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

import logging
import os
import re
from urllib.parse import urlsplit

from curl_cffi.requests import AsyncSession
from starlette.applications import Starlette
from starlette.requests import Request
from starlette.responses import PlainTextResponse, Response, StreamingResponse
from starlette.routing import Route

IMPERSONATE = os.environ.get("FETCH_IMPERSONATE", "chrome")
TIMEOUT = float(os.environ.get("FETCH_TIMEOUT_SECONDS", "3600"))
CONNECT_TIMEOUT = float(os.environ.get("FETCH_CONNECT_TIMEOUT_SECONDS", "30"))
ALLOWED_SUFFIX = "myminifactory.com"
CHUNK = 256 * 1024

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


_session: AsyncSession | None = None


def session() -> AsyncSession:
    global _session
    if _session is None:
        _session = AsyncSession(impersonate=IMPERSONATE, max_clients=16)
    return _session


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

    try:
        resp = await session().request(
            method, url, headers=headers, data=body or None,
            allow_redirects=False, stream=True,
            timeout=(CONNECT_TIMEOUT, TIMEOUT),
        )
    except Exception as e:  # noqa: BLE001 - message only, no headers
        log.warning("%s %s -> upstream error %s", method, safe_target(url), type(e).__name__)
        return proxy_error(502, f"upstream error: {type(e).__name__}")

    out_headers = []
    for k, v in resp.headers.multi_items() if hasattr(resp.headers, "multi_items") else resp.headers.items():
        if k.lower() in RESP_DROP:
            continue
        out_headers.append((k, v))
    # Keep the upstream size when the body was not re-encoded (lets the plugin verify sizes).
    if "content-encoding" not in {k.lower() for k in resp.headers.keys()}:
        cl = resp.headers.get("content-length")
        if cl and re.fullmatch(r"\d+", cl):
            out_headers.append(("Content-Length", cl))

    log.info("%s %s -> %s %s", method, safe_target(url), resp.status_code,
             resp.headers.get("content-type", "-"))

    if method == "HEAD":
        await resp.aclose()
        r = Response(status_code=resp.status_code)
        r.raw_headers = [(k.lower().encode("latin-1"), v.encode("latin-1")) for k, v in out_headers]
        return r

    async def body_iter():
        try:
            async for chunk in resp.aiter_content(chunk_size=CHUNK):
                if chunk:
                    yield chunk
        finally:
            await resp.aclose()

    r = StreamingResponse(body_iter(), status_code=resp.status_code)
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
