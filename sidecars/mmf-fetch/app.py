"""Forgekeeper MMF fetch sidecar.

A tiny loopback-only HTTP relay that re-issues MyMiniFactory requests with
curl_cffi (impersonate=chrome) so they present a real browser TLS/HTTP2
fingerprint and get past Cloudflare's fingerprint check.

Protocol (used by MmfFetchProxyHandler in the plugin):
  <METHOD> /fetch
    X-Fetch-Url: https://www.myminifactory.com/...   (required, absolute)
    <any other headers>  -> forwarded upstream (Cookie, Authorization, User-Agent, Accept, ...)
    <body>               -> forwarded upstream
  Response: upstream status + headers, body streamed chunk by chunk (never buffered).
  Redirects are NEVER followed: 3xx comes back with its Location so the plugin
  decides (it strips credentials before leaving *.myminifactory.com).

  GET /healthz -> 200 "ok"

Errors produced by the sidecar itself carry `X-Fetch-Proxy-Error: 1`.
Never logs cookies, auth headers, or query strings.
"""
from __future__ import annotations

import asyncio
import logging
import os
import queue
import re
import threading
from urllib.parse import urlsplit

from curl_cffi import Curl, CurlInfo, CurlOpt
from curl_cffi.curl import CURL_WRITEFUNC_ERROR
from curl_cffi.requests.utils import set_curl_options
from starlette.applications import Starlette
from starlette.requests import Request
from starlette.responses import PlainTextResponse, Response, StreamingResponse
from starlette.routing import Route

IMPERSONATE = os.environ.get("FETCH_IMPERSONATE", "chrome")
TIMEOUT = float(os.environ.get("FETCH_TIMEOUT_SECONDS", "3600"))
CONNECT_TIMEOUT = float(os.environ.get("FETCH_CONNECT_TIMEOUT_SECONDS", "30"))
ALLOWED_SUFFIX = "myminifactory.com"
CHUNK = 256 * 1024
# Max body chunks buffered between curl and the client (issue #57). curl's write callback
# blocks when this is full, which stops reading the socket -> TCP backpressure upstream.
# Memory per in-flight download is bounded to roughly QUEUE_CHUNKS * curl buffer (~16 KiB..CHUNK).
QUEUE_CHUNKS = int(os.environ.get("FETCH_QUEUE_CHUNKS", "256"))
_END = object()

log = logging.getLogger("mmf-fetch")

# Hop-by-hop / transport headers we never forward in either direction.
HOP = {
    "connection", "keep-alive", "proxy-authenticate", "proxy-authorization", "te", "trailer",
    "trailers", "transfer-encoding", "upgrade", "host", "content-length",
}
# Request headers curl_cffi must own (impersonation sets them) or that are ours.
REQ_DROP = HOP | {"accept-encoding"}
RESP_DROP = HOP | {"content-encoding"}  # curl decodes the body, so the encoding no longer applies


def host_allowed(host: str | None) -> bool:
    if not host:
        return False
    h = host.lower().rstrip(".")
    return h == ALLOWED_SUFFIX or h.endswith("." + ALLOWED_SUFFIX)


def safe_target(url: str) -> str:
    """host + path only, for logs (drops query: download URLs carry signed tokens)."""
    p = urlsplit(url)
    return f"{p.scheme}://{p.hostname}{p.path}"


def proxy_error(status: int, msg: str) -> Response:
    return PlainTextResponse(msg, status_code=status, headers={"X-Fetch-Proxy-Error": "1"})


class Upstream:
    """One upstream transfer on a dedicated thread with a bounded body queue.

    curl_cffi's AsyncSession(stream=True) pushes every received chunk into an *unbounded*
    asyncio.Queue, so when the consumer (plugin writing to NFS) is slower than MMF's CDN the
    whole file accumulates in RAM -> OOMKilled (issue #57). Here the write callback blocks on
    a bounded queue instead, so curl stops reading and memory stays flat.
    """

    def __init__(self, method: str, url: str, headers: dict[str, str], body: bytes | None):
        self.method, self.url, self.headers, self.body = method, url, headers, body
        self.q: queue.Queue = queue.Queue(maxsize=QUEUE_CHUNKS)
        self.ready = threading.Event()
        self.abort = threading.Event()
        self.status = 0
        self.resp_headers: list[tuple[str, str]] = []
        self.error: str | None = None
        self._hdr_block: list[tuple[str, str]] = []
        self._status_line = 0
        self.thread = threading.Thread(target=self._run, daemon=True)

    # -- curl callbacks (curl thread) --
    def _on_header(self, line: bytes):
        text = line.decode("latin-1").rstrip("\r\n")
        if text.startswith("HTTP/"):
            self._hdr_block = []
            try:
                self._status_line = int(text.split()[1])
            except (IndexError, ValueError):
                self._status_line = 0
        elif text == "":
            if self._status_line >= 200 or self._status_line == 0 and self._hdr_block:
                self.status = self._status_line
                self.resp_headers = self._hdr_block
                self.ready.set()
        elif ":" in text:
            k, v = text.split(":", 1)
            self._hdr_block.append((k.strip(), v.strip()))
        return len(line)

    def _on_body(self, chunk: bytes):
        self.ready.set()
        while not self.abort.is_set():
            try:
                self.q.put(chunk, timeout=0.5)
                return len(chunk)
            except queue.Full:
                continue
        return CURL_WRITEFUNC_ERROR

    def _run(self):
        c = Curl()
        try:
            # Use curl_cffi's own option builder so the TLS/HTTP2/header fingerprint is exactly
            # what AsyncSession(impersonate=...) sends; hand-rolled setopt ordering (impersonate
            # first, then ACCEPT_ENCODING/HTTPHEADER) got Cloudflare-challenged. Only the body and
            # header sinks are ours.
            set_curl_options(
                c, self.method, self.url,
                params_list=[None, None],
                data=self.body or None,
                headers_list=[None, self.headers],
                cookies_list=[None, None],
                proxies_list=[None, None],
                verify_list=[True, None],
                timeout=(CONNECT_TIMEOUT, TIMEOUT),
                allow_redirects=False,
                impersonate=IMPERSONATE,
                content_callback=self._on_body,
            )
            c.setopt(CurlOpt.HEADERFUNCTION, self._on_header)
            c.setopt(CurlOpt.WRITEFUNCTION, self._on_body)
            c.perform()
            if not self.ready.is_set():
                self.status = int(c.getinfo(CurlInfo.RESPONSE_CODE) or 0)
                self.resp_headers = self._hdr_block
        except Exception as e:  # noqa: BLE001 - type only, never headers/URL query
            if not self.abort.is_set():
                self.error = type(e).__name__
        finally:
            c.close()
            self.ready.set()
            while True:  # deliver end marker even if consumer is slow; give up if aborted
                try:
                    self.q.put(_END, timeout=0.5)
                    break
                except queue.Full:
                    if self.abort.is_set():
                        break

    # -- async side --
    async def start(self):
        self.thread.start()
        await asyncio.to_thread(self.ready.wait)

    async def chunks(self):
        try:
            while True:
                item = await asyncio.to_thread(self.q.get)
                if item is _END:
                    if self.error:
                        log.warning("%s %s -> upstream error mid-body %s",
                                    self.method, safe_target(self.url), self.error)
                    return
                yield item
        finally:
            self.abort.set()


async def healthz(_: Request) -> Response:
    return PlainTextResponse("ok")


async def fetch(request: Request) -> Response:
    url = request.headers.get("x-fetch-url", "")
    parts = urlsplit(url)
    if parts.scheme not in ("http", "https") or not parts.hostname:
        return proxy_error(400, "X-Fetch-Url must be an absolute http(s) URL")
    if not host_allowed(parts.hostname):
        log.warning("refused non-allowlisted host %s", parts.hostname)
        return proxy_error(403, "target host not allowlisted")

    headers = {}
    for k, v in request.headers.items():
        lk = k.lower()
        if lk in REQ_DROP or lk.startswith("x-fetch-"):
            continue
        headers[k] = v

    method = request.method.upper()
    body = await request.body() if method not in ("GET", "HEAD") else None

    up = Upstream(method, url, headers, body)
    await up.start()
    if up.error and up.status == 0:
        log.warning("%s %s -> upstream error %s", method, safe_target(url), up.error)
        up.abort.set()
        return proxy_error(502, f"upstream error: {up.error}")

    names = {k.lower() for k, _ in up.resp_headers}
    out_headers = [(k, v) for k, v in up.resp_headers if k.lower() not in RESP_DROP]
    # Keep the upstream size when the body was not re-encoded (lets the plugin verify sizes).
    if "content-encoding" not in names:
        cl = next((v for k, v in up.resp_headers if k.lower() == "content-length"), None)
        if cl and re.fullmatch(r"\d+", cl):
            out_headers.append(("Content-Length", cl))

    ctype = next((v for k, v in up.resp_headers if k.lower() == "content-type"), "-")
    log.info("%s %s -> %s %s", method, safe_target(url), up.status, ctype)

    if method == "HEAD":
        up.abort.set()
        r = Response(status_code=up.status)
        r.raw_headers = [(k.lower().encode("latin-1"), v.encode("latin-1")) for k, v in out_headers]
        return r

    r = StreamingResponse(up.chunks(), status_code=up.status)
    r.raw_headers = [(k.lower().encode("latin-1"), v.encode("latin-1")) for k, v in out_headers]
    return r


METHODS = ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"]
app = Starlette(routes=[
    Route("/healthz", healthz, methods=["GET"]),
    Route("/fetch", fetch, methods=METHODS),
])


if __name__ == "__main__":
    import uvicorn

    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")
    uvicorn.run(
        app,
        host=os.environ.get("FETCH_BIND", "127.0.0.1"),
        port=int(os.environ.get("FETCH_PORT", "8199")),
        access_log=False,  # uvicorn's access log would print the path; ours is header-based and scrubbed
        log_level="info",
    )
