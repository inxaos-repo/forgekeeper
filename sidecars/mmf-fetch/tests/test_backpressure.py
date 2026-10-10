"""Issue #57: a slow client must not make the sidecar buffer the whole download in RAM."""
import os
import socket
import subprocess
import sys
import threading
import time

import httpx
import pytest
import uvicorn

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import app as fetch_app  # noqa: E402

_REAL_HOST_ALLOWED = fetch_app.host_allowed

SIZE = 384 * 1024 * 1024  # 384 MiB upstream body


def _free_port():
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    p = s.getsockname()[1]
    s.close()
    return p


def _rss_mib():
    with open("/proc/self/status") as f:
        for line in f:
            if line.startswith("VmRSS:"):
                return int(line.split()[1]) / 1024
    raise RuntimeError("no VmRSS")


@pytest.fixture(scope="module")
def upstream(tmp_path_factory):
    d = tmp_path_factory.mktemp("up")
    with open(d / "big.bin", "wb") as f:
        f.truncate(SIZE)  # sparse: no disk/RAM cost on the server side
    port = _free_port()
    p = subprocess.Popen([sys.executable, "-m", "http.server", str(port), "--bind", "127.0.0.1",
                          "--directory", str(d)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    for _ in range(50):
        try:
            socket.create_connection(("127.0.0.1", port), 0.2).close()
            break
        except OSError:
            time.sleep(0.1)
    yield f"http://127.0.0.1:{port}"
    p.kill()


@pytest.fixture(scope="module")
def sidecar():
    fetch_app.host_allowed = lambda h: True  # test-only: allow the local upstream
    port = _free_port()
    server = uvicorn.Server(uvicorn.Config(fetch_app.app, host="127.0.0.1", port=port, log_level="warning"))
    t = threading.Thread(target=server.run, daemon=True)
    t.start()
    while not server.started:
        time.sleep(0.05)
    yield f"http://127.0.0.1:{port}"
    server.should_exit = True
    t.join(5)


def test_real_allowlist():
    assert _REAL_HOST_ALLOWED("www.myminifactory.com")
    assert _REAL_HOST_ALLOWED("dl.myminifactory.com.")
    assert not _REAL_HOST_ALLOWED("myminifactory.com.evil.net")
    assert not _REAL_HOST_ALLOWED("example.com")


def test_slow_client_memory_is_bounded(upstream, sidecar):
    base = _rss_mib()
    peak = base
    got = 0
    with httpx.Client(timeout=60) as c:
        with c.stream("GET", f"{sidecar}/fetch", headers={"X-Fetch-Url": f"{upstream}/big.bin"}) as r:
            assert r.status_code == 200
            assert int(r.headers["content-length"]) == SIZE
            for i, chunk in enumerate(r.iter_bytes(1024 * 1024)):
                got += len(chunk)
                if i < 40:
                    time.sleep(0.05)  # slow consumer for the first ~2s: upstream would race ahead
                if i % 16 == 0:
                    peak = max(peak, _rss_mib())
    assert got == SIZE
    # Old AsyncSession(stream=True) path grew by ~the whole body (384 MiB). Bounded queue: a few MiB.
    assert peak - base < 96, f"RSS grew {peak - base:.0f} MiB while streaming {SIZE >> 20} MiB"


def test_head_and_404(upstream, sidecar):
    with httpx.Client(timeout=30) as c:
        r = c.head(f"{sidecar}/fetch", headers={"X-Fetch-Url": f"{upstream}/big.bin"})
        assert r.status_code == 200 and r.headers["content-length"] == str(SIZE)
        r = c.get(f"{sidecar}/fetch", headers={"X-Fetch-Url": f"{upstream}/nope"})
        assert r.status_code == 404


def test_upstream_unreachable_is_502(sidecar):
    with httpx.Client(timeout=60) as c:
        r = c.get(f"{sidecar}/fetch", headers={"X-Fetch-Url": f"http://127.0.0.1:{_free_port()}/x"})
        assert r.status_code == 502 and r.headers["x-fetch-proxy-error"] == "1"


@pytest.mark.skipif(os.environ.get("FETCH_LIVE_TEST") != "1", reason="set FETCH_LIVE_TEST=1 (hits myminifactory.com)")
def test_live_fingerprint_not_challenged():
    """Regression for the #60 follow-up: hand-rolled curl options got a Cloudflare 403 challenge.

    Anonymous API calls must reach the origin (401 JSON), never `cf-mitigated: challenge`.
    """
    import asyncio

    async def go():
        up = fetch_app.Upstream("GET", "https://www.myminifactory.com/api/v2/objects/824787",
                                {"Accept": "application/json"}, None)
        await up.start()
        async for _ in up.chunks():
            pass
        return up

    up = asyncio.run(go())
    h = {k.lower(): v for k, v in up.resp_headers}
    assert h.get("cf-mitigated") != "challenge", up.status
    assert "json" in h.get("content-type", ""), (up.status, h.get("content-type"))
