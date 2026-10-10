# mmf-fetch sidecar

Loopback-only relay that re-issues MyMiniFactory requests through
[curl_cffi](https://github.com/lexiforest/curl_cffi) with `impersonate=chrome`, so they carry a
browser TLS/HTTP2 fingerprint and pass Cloudflare (plain .NET HttpClient gets `403 cf-mitigated`).

Enable in the MMF plugin by setting `FETCH_PROXY_URL=http://127.0.0.1:8199`.

- `<METHOD> /fetch` with header `X-Fetch-Url: <absolute MMF url>`; other headers + body are forwarded.
- Upstream status/headers returned, body streamed (multi-GB safe). Redirects are **not** followed —
  the plugin follows them itself and strips credentials before leaving `*.myminifactory.com`.
- Only `*.myminifactory.com` targets are allowed (403 + `X-Fetch-Proxy-Error: 1` otherwise).
- Never logs cookies, auth headers, or query strings. `GET /healthz` → `ok`.

Env: `FETCH_BIND` (127.0.0.1), `FETCH_PORT` (8199), `FETCH_IMPERSONATE` (chrome),
`FETCH_TIMEOUT_SECONDS` (3600), `FETCH_CONNECT_TIMEOUT_SECONDS` (30), `FETCH_QUEUE_CHUNKS` (256).

Memory: each transfer runs curl on its own thread with a bounded chunk queue; when the client
is slower than upstream, curl's write callback blocks (TCP backpressure), so RSS stays flat
regardless of file size. (curl_cffi `AsyncSession(stream=True)` uses an unbounded queue and
buffered whole downloads in RAM -> OOMKilled, #57.) Tests: `pytest sidecars/mmf-fetch/tests`.
