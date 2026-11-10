from __future__ import annotations

import importlib.metadata
import json
import uuid
from typing import Any

import pytest
from conftest import json_response

import pagerush
from pagerush import HTTPResponse, PageRushConnectionError, PageRushError

TEMPLATE = {
    "id": "tpl_1",
    "name": "Invoice",
    "live_version": 1,
    "latest_version": 1,
    "created_at": "2026-11-09T00:00:00.000Z",
    "updated_at": "2026-11-09T00:00:00.000Z",
}
VERSION = {
    "version": 1,
    "source": "Hello",
    "document": None,
    "files": [],
    "sample_data": None,
    "typst_version": "0.13.1",
    "created_at": "2026-11-09T00:00:00.000Z",
}
JOB = {
    "id": "job_1",
    "status": "queued",
    "mode": "async",
    "region": "in",
    "template_id": "tpl_1",
    "template_version": 3,
    "engine": None,
    "bytes": None,
    "duration_ms": None,
    "error": None,
    "delivery": None,
    "metadata": None,
    "download_url": None,
    "delete_after_download": False,
    "created_at": "2026-11-09T00:00:00.000Z",
    "completed_at": None,
}


def pdf_response(headers: dict[str, str] | None = None) -> HTTPResponse:
    return HTTPResponse(
        status=200,
        headers={
            "Content-Type": "application/pdf",
            "Content-Disposition": 'inline; filename="invoice 42.pdf"',
            "X-Job-Id": "job_9",
            "X-Template-Version": "3",
            "X-Render-Engine": "typst-native",
            "X-Page-Count": "2",
            "X-Template-Warnings": "0",
            "X-Request-Id": "req_1",
            **(headers or {}),
        },
        body=b"%PDF-1.7 fake",
    )


# -- version / configuration ------------------------------------------------------------------


def test_installed_metadata_version_matches() -> None:
    assert importlib.metadata.version("pagerush") == pagerush.__version__ == "0.1.0"


def test_missing_api_key(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("PAGERUSH_API_KEY", raising=False)
    with pytest.raises(PageRushError) as info:
        pagerush.PageRush()
    assert info.value.code == "MISSING_API_KEY"


def test_env_fallbacks_and_trailing_slash(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("PAGERUSH_API_KEY", "pk_live_env")
    monkeypatch.setenv("PAGERUSH_BASE_URL", "https://env.test/")
    client = pagerush.PageRush()
    assert client.api_key == "pk_live_env"
    assert client.base_url == "https://env.test"
    assert pagerush.PageRush(base_url="https://x.test///").base_url == "https://x.test"
    monkeypatch.delenv("PAGERUSH_BASE_URL")
    assert pagerush.PageRush().base_url == "https://pagerush.dev"
    assert client.timeout == 60 and client.max_retries == 2


def test_auth_user_agent_accept(make_client: Any) -> None:
    client, t = make_client(json_response(200, {"items": [], "next_cursor": None}), timeout=5)
    client.templates.list()
    req = t.last
    assert req.headers["Authorization"] == "Bearer pk_test_123"
    assert req.headers["User-Agent"] == "pagerush-python/0.1.0"
    assert req.headers["Accept"] == "application/json"
    assert "Content-Type" not in req.headers
    assert req.body is None
    assert req.timeout == 5


# -- templates --------------------------------------------------------------------------------


def test_list_with_query(make_client: Any) -> None:
    client, t = make_client(json_response(200, {"items": [TEMPLATE], "next_cursor": "c1"}))
    page = client.templates.list(limit=10, cursor="abc=")
    assert t.last.method == "GET"
    assert t.last.url == "https://api.test/v1/templates?limit=10&cursor=abc%3D"
    assert page["items"][0]["id"] == "tpl_1"
    assert page["next_cursor"] == "c1"


def test_list_all_follows_cursor(make_client: Any) -> None:
    second = dict(TEMPLATE, id="tpl_2")
    client, t = make_client(
        json_response(200, {"items": [TEMPLATE], "next_cursor": "c1"}),
        json_response(200, {"items": [second], "next_cursor": None}),
    )
    ids = [tpl["id"] for tpl in client.templates.list_all(limit=1)]
    assert ids == ["tpl_1", "tpl_2"]
    assert t.requests[0].url == "https://api.test/v1/templates?limit=1"
    assert t.requests[1].url == "https://api.test/v1/templates?limit=1&cursor=c1"


def test_create(make_client: Any) -> None:
    client, t = make_client(json_response(201, {"template": TEMPLATE, "version": VERSION}))
    logo = pagerush.template_file("logo.png", b"\x89PNG")
    out = client.templates.create(name="Invoice", source="Hello", files=[logo], sample_data={"a": 1})
    assert t.last.method == "POST"
    assert t.last.url == "https://api.test/v1/templates"
    assert t.last.headers["Content-Type"] == "application/json"
    assert t.json_body() == {
        "name": "Invoice",
        "source": "Hello",
        "files": [{"path": "logo.png", "content": "iVBORw==", "encoding": "base64"}],
        "sample_data": {"a": 1},
    }
    assert out["template"]["id"] == "tpl_1"
    assert out["version"]["version"] == 1


def test_get_unwraps_and_encodes_id(make_client: Any) -> None:
    client, t = make_client(json_response(200, {"template": TEMPLATE}))
    tpl = client.templates.get("a/b c")
    assert t.last.url == "https://api.test/v1/templates/a%2Fb%20c"
    assert tpl["name"] == "Invoice"


def test_rename(make_client: Any) -> None:
    client, t = make_client(json_response(200, {"template": dict(TEMPLATE, name="New")}))
    tpl = client.templates.rename("tpl_1", "New")
    assert (t.last.method, t.last.url) == ("PATCH", "https://api.test/v1/templates/tpl_1")
    assert t.json_body() == {"name": "New"}
    assert tpl["name"] == "New"


def test_delete(make_client: Any) -> None:
    client, t = make_client(HTTPResponse(status=204))
    assert client.templates.delete("tpl_1") is None
    assert (t.last.method, t.last.url) == ("DELETE", "https://api.test/v1/templates/tpl_1")


def test_create_version(make_client: Any) -> None:
    client, t = make_client(json_response(201, {"template": TEMPLATE, "version": VERSION}))
    client.templates.create_version("tpl_1", source="Hi", typst_version="0.13.1", publish=False)
    assert (t.last.method, t.last.url) == ("POST", "https://api.test/v1/templates/tpl_1/versions")
    assert t.json_body() == {"source": "Hi", "typst_version": "0.13.1", "publish": False}


def test_get_version_unwraps(make_client: Any) -> None:
    client, t = make_client(json_response(200, {"version": VERSION}))
    v = client.templates.get_version("tpl_1", 1)
    assert (t.last.method, t.last.url) == ("GET", "https://api.test/v1/templates/tpl_1/versions/1")
    assert v["source"] == "Hello"


def test_publish(make_client: Any) -> None:
    client, t = make_client(json_response(200, {"template": dict(TEMPLATE, live_version=2)}))
    tpl = client.templates.publish("tpl_1", 2)
    assert (t.last.method, t.last.url) == ("POST", "https://api.test/v1/templates/tpl_1/publish")
    assert t.json_body() == {"version": 2}
    assert tpl["live_version"] == 2


# -- render -----------------------------------------------------------------------------------


def test_render_200(make_client: Any, tmp_path: Any) -> None:
    client, t = make_client(pdf_response())
    result = client.templates.render(
        "tpl_1",
        data={"name": "Ada"},
        version=3,
        filename="invoice 42",
        metadata={"order": "42"},
        delete_after_download=True,
        callback_url=None,
    )
    req = t.last
    assert (req.method, req.url) == ("POST", "https://api.test/v1/templates/tpl_1/render")
    assert req.headers["Accept"] == "application/pdf, application/json"
    assert "Prefer" not in req.headers
    assert t.json_body() == {
        "data": {"name": "Ada"},
        "version": 3,
        "filename": "invoice 42",
        "metadata": {"order": "42"},
        "delete_after_download": True,
    }
    assert uuid.UUID(req.headers["Idempotency-Key"]).version == 4
    assert result.queued is False
    assert result.pdf == b"%PDF-1.7 fake"
    assert result.job_id == "job_9"
    assert result.template_version == 3
    assert result.engine == "typst-native"
    assert result.page_count == 2
    assert result.warnings == 0
    assert result.request_id == "req_1"
    assert result.filename == "invoice 42.pdf"
    assert result.job is None
    out = tmp_path / "out.pdf"
    result.save(out)
    assert out.read_bytes() == b"%PDF-1.7 fake"


def test_render_200_optional_headers_missing(make_client: Any) -> None:
    resp = HTTPResponse(
        status=200,
        headers={"content-type": "Application/PDF; qs=1", "x-job-id": "job_1", "x-template-version": "1"},
        body=b"%PDF",
    )
    client, _ = make_client(resp)
    result = client.templates.render("tpl_1", data=None)
    assert result.job_id == "job_1" and result.template_version == 1
    assert result.page_count is None and result.warnings is None
    assert result.request_id is None and result.filename is None


def test_render_202(make_client: Any, tmp_path: Any) -> None:
    client, t = make_client(json_response(202, JOB))
    result = client.templates.render(
        "tpl_1",
        data={},
        callback_url="https://example.com/hook",
        prefer_async=True,
        idempotency_key="order-42",
    )
    assert t.last.headers["Prefer"] == "respond-async"
    assert t.last.headers["Idempotency-Key"] == "order-42"
    assert t.json_body()["callback_url"] == "https://example.com/hook"
    assert result.queued is True
    assert result.pdf is None
    assert result.job is not None and result.job["id"] == "job_1"
    assert result.job_id == "job_1"
    assert result.template_version == 3
    assert result.engine is None and result.page_count is None and result.filename is None
    with pytest.raises(ValueError):
        result.save(tmp_path / "x.pdf")


def test_render_idempotency_key_reused_across_retries(make_client: Any, sleeps: Any) -> None:
    client, t = make_client(
        PageRushConnectionError("CONNECTION_ERROR", "reset"),
        json_response(
            502, {"code": "INTERNAL", "title": "Bad gateway", "status": 502, "type": "about:blank"}
        ),
        pdf_response(),
    )
    result = client.templates.render("tpl_1", data={})
    assert result.pdf is not None
    keys = {r.headers["Idempotency-Key"] for r in t.requests}
    assert len(t.requests) == 3 and len(keys) == 1
    assert len(sleeps) == 2


# -- retries ----------------------------------------------------------------------------------


def problem(status: int, code: str, **extra: Any) -> dict[str, Any]:
    return {"type": "about:blank", "title": code.title(), "status": status, "code": code, **extra}


def test_retry_503_with_retry_after(make_client: Any, sleeps: Any) -> None:
    client, t = make_client(
        json_response(503, problem(503, "RENDER_UNAVAILABLE"), {"Retry-After": "7"}),
        json_response(200, {"template": TEMPLATE}),
    )
    client.templates.get("tpl_1")
    assert len(t.requests) == 2
    assert sleeps == [7.0]


def test_retry_after_capped_at_30(make_client: Any, sleeps: Any) -> None:
    client, _ = make_client(
        json_response(429, problem(429, "RATE_LIMITED"), {"Retry-After": "120"}),
        json_response(200, {"template": TEMPLATE}),
    )
    client.templates.get("tpl_1")
    assert sleeps == [30.0]


def test_backoff_without_retry_after(make_client: Any, sleeps: Any) -> None:
    client, _ = make_client(
        json_response(504, problem(504, "INTERNAL")),
        json_response(504, problem(504, "INTERNAL")),
        json_response(200, {"template": TEMPLATE}),
    )
    client.templates.get("tpl_1")
    assert len(sleeps) == 2
    assert 0.375 <= sleeps[0] <= 0.625
    assert 0.75 <= sleeps[1] <= 1.25


def test_backoff_caps_at_8(make_client: Any) -> None:
    client, _ = make_client()
    client._random = lambda: 0.5
    assert client._backoff(1, None) == 0.5
    assert client._backoff(5, None) == 8.0
    assert client._backoff(10, None) == 8.0


def test_create_not_retried_on_502(make_client: Any, sleeps: Any) -> None:
    client, t = make_client(json_response(502, problem(502, "INTERNAL")))
    with pytest.raises(PageRushError) as info:
        client.templates.create(name="x", source="y")
    assert info.value.status == 502
    assert len(t.requests) == 1 and sleeps == []


def test_create_not_retried_on_connection_error(make_client: Any) -> None:
    client, t = make_client(PageRushConnectionError("CONNECTION_ERROR", "reset"))
    with pytest.raises(PageRushConnectionError):
        client.templates.create(name="x", source="y")
    assert len(t.requests) == 1


def test_create_retried_on_429_and_503(make_client: Any) -> None:
    client, t = make_client(
        json_response(429, problem(429, "RATE_LIMITED")),
        json_response(503, problem(503, "RENDER_UNAVAILABLE")),
        json_response(201, {"template": TEMPLATE, "version": VERSION}),
    )
    client.templates.create(name="x", source="y")
    assert len(t.requests) == 3


def test_no_retry_after_max_retries(make_client: Any, sleeps: Any) -> None:
    client, t = make_client(*[json_response(503, problem(503, "RENDER_UNAVAILABLE")) for _ in range(3)])
    with pytest.raises(PageRushError) as info:
        client.jobs.get("job_1")
    assert info.value.code == "RENDER_UNAVAILABLE"
    assert len(t.requests) == 3 and len(sleeps) == 2


def test_max_retries_zero(make_client: Any) -> None:
    client, t = make_client(json_response(503, problem(503, "RENDER_UNAVAILABLE")), max_retries=0)
    with pytest.raises(PageRushError):
        client.jobs.get("job_1")
    assert len(t.requests) == 1


def test_4xx_not_retried(make_client: Any) -> None:
    client, t = make_client(json_response(404, problem(404, "TEMPLATE_NOT_FOUND")))
    with pytest.raises(PageRushError):
        client.templates.get("nope")
    assert len(t.requests) == 1


def test_os_error_mapped_and_retried_for_get(make_client: Any) -> None:
    client, t = make_client(
        TimeoutError("timed out"),
        ConnectionResetError("reset"),
        ConnectionResetError("reset"),
    )
    with pytest.raises(PageRushConnectionError) as info:
        client.jobs.get("job_1")
    assert info.value.code == "CONNECTION_ERROR" and info.value.status == 0
    assert len(t.requests) == 3


def test_timeout_maps_to_timeout_code(make_client: Any) -> None:
    client, _ = make_client(TimeoutError("timed out"), max_retries=0)
    with pytest.raises(PageRushConnectionError) as info:
        client.jobs.get("job_1")
    assert info.value.code == "TIMEOUT"
    assert isinstance(info.value, PageRushError)


# -- errors -----------------------------------------------------------------------------------


def test_problem_json_mapping(make_client: Any) -> None:
    body = problem(
        422,
        "TEMPLATE_COMPILE_FAILED",
        detail="unknown variable: foo",
        errors=[{"path": "main.typ:3:5", "message": "unknown variable: foo"}],
        request_id="req_body",
    )
    client, _ = make_client(json_response(422, body, {"X-Request-Id": "req_header"}))
    with pytest.raises(PageRushError) as info:
        client.templates.render("tpl_1", data={})
    err = info.value
    assert err.status == 422
    assert err.code == "TEMPLATE_COMPILE_FAILED"
    assert err.title == "Template_Compile_Failed"
    assert err.detail == "unknown variable: foo"
    assert err.errors == [{"path": "main.typ:3:5", "message": "unknown variable: foo"}]
    assert err.request_id == "req_body"
    assert err.retry_after is None
    assert err.type == "about:blank"
    assert str(err) == "TEMPLATE_COMPILE_FAILED: unknown variable: foo"


def test_error_message_falls_back_to_title(make_client: Any) -> None:
    client, _ = make_client(json_response(401, problem(401, "INVALID_API_KEY")))
    with pytest.raises(PageRushError) as info:
        client.jobs.get("job_1")
    assert str(info.value) == "INVALID_API_KEY: Invalid_Api_Key"
    assert info.value.errors == []


def test_non_json_error_body(make_client: Any) -> None:
    resp = HTTPResponse(
        status=500, headers={"Content-Type": "text/html", "X-Request-Id": "req_h"}, body=b"<html>oops"
    )
    client, _ = make_client(resp)
    with pytest.raises(PageRushError) as info:
        client.jobs.get("job_1")
    err = info.value
    assert (err.status, err.code, err.request_id) == (500, "UNKNOWN", "req_h")
    assert err.title is None and err.detail is None and err.errors == []
    assert str(err) == "UNKNOWN: HTTP 500"


def test_request_id_falls_back_to_header(make_client: Any) -> None:
    client, _ = make_client(
        json_response(429, problem(429, "QUOTA_EXCEEDED"), {"X-Request-Id": "req_h", "Retry-After": "5"}),
        max_retries=0,
    )
    with pytest.raises(PageRushError) as info:
        client.jobs.get("job_1")
    assert info.value.request_id == "req_h"
    assert info.value.retry_after == 5


# -- jobs -------------------------------------------------------------------------------------


def test_jobs_get(make_client: Any) -> None:
    client, t = make_client(json_response(200, JOB))
    job = client.jobs.get("job_1")
    assert (t.last.method, t.last.url) == ("GET", "https://api.test/v1/jobs/job_1")
    assert job["status"] == "queued"


def test_jobs_wait(make_client: Any, sleeps: Any) -> None:
    client, t = make_client(
        json_response(200, JOB),
        json_response(200, dict(JOB, status="rendering")),
        json_response(200, dict(JOB, status="succeeded")),
    )
    job = client.jobs.wait("job_1", interval=0.25)
    assert job["status"] == "succeeded"
    assert len(t.requests) == 3
    assert sleeps == [0.25, 0.25]


def test_jobs_wait_returns_failed(make_client: Any) -> None:
    failed = dict(JOB, status="failed", error={"code": "RENDER_FAILED", "message": "x"})
    client, _ = make_client(json_response(200, failed))
    assert client.jobs.wait("job_1")["status"] == "failed"


def test_jobs_wait_timeout(make_client: Any, sleeps: Any) -> None:
    client, _ = make_client(*[json_response(200, JOB) for _ in range(4)])
    now = [0.0]
    client._clock = lambda: now[0]
    client._sleep = lambda s: now.__setitem__(0, now[0] + s)
    with pytest.raises(PageRushError) as info:
        client.jobs.wait("job_1", timeout=3, interval=1)
    assert (info.value.status, info.value.code) == (0, "WAIT_TIMEOUT")
    assert not isinstance(info.value, PageRushConnectionError)


def test_jobs_download_without_auth(make_client: Any) -> None:
    signed = "https://files.test/job_1.pdf?sig=abc"
    client, t = make_client(
        json_response(200, dict(JOB, status="succeeded", download_url=signed)),
        HTTPResponse(status=200, headers={"Content-Type": "application/pdf"}, body=b"%PDF-data"),
    )
    assert client.jobs.download("job_1") == b"%PDF-data"
    assert t.requests[0].headers["Authorization"] == "Bearer pk_test_123"
    assert t.last.url == signed
    assert t.last.method == "GET"
    assert "Authorization" not in t.last.headers


def test_jobs_download_expired(make_client: Any) -> None:
    client, t = make_client(json_response(200, dict(JOB, status="succeeded")))
    with pytest.raises(PageRushError) as info:
        client.jobs.download("job_1")
    assert info.value.code == "FILE_EXPIRED"
    assert len(t.requests) == 1


# -- files ------------------------------------------------------------------------------------


def test_template_file_encodings(tmp_path: Any) -> None:
    assert pagerush.template_file("parts/a.TYP", "= Hi") == {
        "path": "parts/a.TYP",
        "content": "= Hi",
        "encoding": "utf8",
    }
    assert pagerush.template_file("d.csv", b"a,b")["encoding"] == "utf8"
    font = pagerush.template_file("fonts/x.ttf", b"\x00\x01")
    assert font == {"path": "fonts/x.ttf", "content": "AAE=", "encoding": "base64"}
    p = tmp_path / "logo.png"
    p.write_bytes(b"\x89PNG")
    assert pagerush.file_from_path(p) == {"path": "logo.png", "content": "iVBORw==", "encoding": "base64"}
    assert pagerush.file_from_path(str(p), "img/l.png")["path"] == "img/l.png"


# -- default transport against a local server ---------------------------------------------------


def test_urllib_transport_roundtrip() -> None:
    import http.server
    import threading

    seen: dict[str, Any] = {}

    class Handler(http.server.BaseHTTPRequestHandler):
        def do_POST(self) -> None:
            length = int(self.headers["Content-Length"])
            seen["body"] = self.rfile.read(length)
            seen["auth"] = self.headers["Authorization"]
            self.send_response(404)
            self.send_header("Content-Type", "application/problem+json")
            self.send_header("X-Request-Id", "req_local")
            body = b'{"type":"about:blank","title":"Not found","status":404,"code":"TEMPLATE_NOT_FOUND"}'
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, *args: Any) -> None:
            pass

    server = http.server.HTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        client = pagerush.PageRush(api_key="pk_test_456", base_url=f"http://127.0.0.1:{server.server_port}")
        with pytest.raises(PageRushError) as info:
            client.templates.render("missing", data={"a": 1})
        assert info.value.code == "TEMPLATE_NOT_FOUND"
        assert info.value.request_id == "req_local"
        assert seen["auth"] == "Bearer pk_test_456"
        assert seen["body"] == b'{"data":{"a":1}}'
    finally:
        server.shutdown()
        server.server_close()


def test_urllib_transport_connection_refused() -> None:
    import socket

    sock = socket.socket()
    sock.bind(("127.0.0.1", 0))
    port = sock.getsockname()[1]
    sock.close()
    client = pagerush.PageRush(api_key="k", base_url=f"http://127.0.0.1:{port}", max_retries=0)
    with pytest.raises(PageRushConnectionError) as info:
        client.jobs.get("job_1")
    assert info.value.code == "CONNECTION_ERROR"


# -- edge cases -------------------------------------------------------------------------------


def test_non_json_2xx_raises_invalid_response(make_client: Any) -> None:
    client, _ = make_client(HTTPResponse(status=200, headers={"X-Request-Id": "req_x"}, body=b"<html>"))
    with pytest.raises(PageRushError) as info:
        client.jobs.get("job_1")
    assert (info.value.status, info.value.code, info.value.request_id) == (200, "INVALID_RESPONSE", "req_x")


@pytest.mark.parametrize(
    ("body", "call"),
    [
        ({"id": "tpl_1"}, lambda c: c.templates.get("tpl_1")),
        ({"template": "nope"}, lambda c: c.templates.rename("tpl_1", "x")),
        ({"template": TEMPLATE}, lambda c: c.templates.create(name="x", source="y")),
        ({"items": []}, lambda c: c.templates.list()),
        ({"template": TEMPLATE}, lambda c: c.templates.get_version("tpl_1", 1)),
        ([JOB], lambda c: c.jobs.get("job_1")),
        ({"status": "queued"}, lambda c: c.jobs.get("job_1")),
    ],
)
def test_missing_wrapper_raises_invalid_response(make_client: Any, body: Any, call: Any) -> None:
    client, t = make_client(json_response(200, body))
    with pytest.raises(PageRushError) as info:
        call(client)
    assert (info.value.status, info.value.code) == (200, "INVALID_RESPONSE")
    assert len(t.requests) == 1


def test_render_200_not_pdf_raises_invalid_response(make_client: Any) -> None:
    client, _ = make_client(json_response(200, {"ok": True}))
    with pytest.raises(PageRushError) as info:
        client.templates.render("tpl_1", data={})
    assert (info.value.status, info.value.code) == (200, "INVALID_RESPONSE")


def test_render_202_without_job_raises_invalid_response(make_client: Any) -> None:
    client, _ = make_client(HTTPResponse(status=202, body=b""))
    with pytest.raises(PageRushError) as info:
        client.templates.render("tpl_1", data={}, prefer_async=True)
    assert (info.value.status, info.value.code) == (202, "INVALID_RESPONSE")


@pytest.mark.parametrize(
    "value", ["", "  ", "soon", "-3", "inf", "-inf", "nan", "1e400", "1_0", "Fri, 99 Foo"]
)
def test_unusable_retry_after_falls_back_to_backoff(make_client: Any, sleeps: Any, value: str) -> None:
    client, _ = make_client(
        json_response(503, problem(503, "RENDER_UNAVAILABLE"), {"Retry-After": value}),
        json_response(200, JOB),
    )
    client.jobs.get("job_1")
    assert 0.375 <= sleeps[0] <= 0.625


@pytest.mark.parametrize(("value", "expected"), [("9" * 400, 31_536_000), ("1.5", 2), ("2.0", 2), ("0", 0)])
def test_retry_after_parsing_and_cap(make_client: Any, sleeps: Any, value: str, expected: int) -> None:
    client, _ = make_client(
        json_response(429, problem(429, "RATE_LIMITED"), {"Retry-After": value}),
        max_retries=0,
    )
    with pytest.raises(PageRushError) as info:
        client.jobs.get("job_1")
    assert info.value.retry_after == expected
    assert client._backoff(1, info.value.retry_after) == min(30.0, expected)


def test_http_date_retry_after(make_client: Any, sleeps: Any) -> None:
    import email.utils
    import time

    when = email.utils.formatdate(time.time() + 10, usegmt=True)
    client, _ = make_client(
        json_response(429, problem(429, "RATE_LIMITED"), {"Retry-After": when}),
        json_response(200, JOB),
    )
    client.jobs.get("job_1")
    assert 8 <= sleeps[0] <= 11


def test_unsafe_methods_not_retried_on_connection_error_or_504(make_client: Any) -> None:
    client, t = make_client(
        PageRushConnectionError("TIMEOUT", "slow"),
        json_response(504, problem(504, "INTERNAL")),
        json_response(502, problem(502, "INTERNAL")),
    )
    with pytest.raises(PageRushConnectionError):
        client.templates.publish("tpl_1", 1)
    with pytest.raises(PageRushError):
        client.templates.rename("tpl_1", "x")
    with pytest.raises(PageRushError):
        client.templates.create_version("tpl_1", source="x")
    assert len(t.requests) == 3


def test_path_segments_encoded(make_client: Any) -> None:
    client, t = make_client(json_response(200, {"version": VERSION}), json_response(200, JOB))
    client.templates.get_version("../x?y#z", 2)
    assert t.requests[0].url == "https://api.test/v1/templates/..%2Fx%3Fy%23z/versions/2"
    client.jobs.get("job/1")
    assert t.last.url == "https://api.test/v1/jobs/job%2F1"


def test_download_error_status_raises(make_client: Any) -> None:
    client, t = make_client(
        json_response(200, dict(JOB, status="succeeded", download_url="https://files.test/x")),
        HTTPResponse(status=403, body=b"<Error>expired</Error>"),
    )
    with pytest.raises(PageRushError) as info:
        client.jobs.download("job_1")
    assert (info.value.status, info.value.code) == (403, "UNKNOWN")
    assert len(t.requests) == 2


def test_error_repr_and_attributes() -> None:
    err = PageRushError(status=400, code="INVALID_DATA", detail="bad")
    assert "INVALID_DATA" in repr(err)
    assert err.message == "INVALID_DATA: bad"


def test_urllib_transport_read_timeout() -> None:
    import http.server
    import threading
    import time

    class Slow(http.server.BaseHTTPRequestHandler):
        def do_GET(self) -> None:
            self.send_response(200)
            self.send_header("Content-Length", "100")
            self.end_headers()
            self.wfile.write(b"{")
            self.wfile.flush()
            time.sleep(1.0)

        def log_message(self, *args: Any) -> None:
            pass

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Slow)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    try:
        client = pagerush.PageRush(
            api_key="pk_test_123",
            base_url=f"http://127.0.0.1:{server.server_port}",
            timeout=0.2,
            max_retries=0,
        )
        with pytest.raises(PageRushConnectionError) as info:
            client.jobs.get("job_1")
        assert info.value.code == "TIMEOUT"
    finally:
        server.shutdown()
        server.server_close()


def test_urllib_transport_drops_auth_on_cross_host_redirect() -> None:
    import http.server
    import threading

    seen: dict[str, Any] = {}

    class Target(http.server.BaseHTTPRequestHandler):
        def do_GET(self) -> None:
            seen["auth"] = self.headers.get("Authorization")
            body = json.dumps(dict(JOB, id="job_r")).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, *args: Any) -> None:
            pass

    target = http.server.HTTPServer(("127.0.0.1", 0), Target)

    class Redirect(http.server.BaseHTTPRequestHandler):
        def do_GET(self) -> None:
            seen["first_auth"] = self.headers.get("Authorization")
            self.send_response(302)
            self.send_header("Location", f"http://127.0.0.1:{target.server_port}/elsewhere")
            self.send_header("Content-Length", "0")
            self.end_headers()

        def log_message(self, *args: Any) -> None:
            pass

    origin = http.server.HTTPServer(("127.0.0.1", 0), Redirect)
    servers = [target, origin]
    for server in servers:
        threading.Thread(target=server.serve_forever, daemon=True).start()
    try:
        client = pagerush.PageRush(api_key="pk_test_123", base_url=f"http://127.0.0.1:{origin.server_port}")
        assert client.jobs.get("job_1")["id"] == "job_r"
        assert seen["first_auth"] == "Bearer pk_test_123"
        assert seen["auth"] is None
    finally:
        for server in servers:
            server.shutdown()
            server.server_close()


# -- input validation and secrets --------------------------------------------------------------


def test_api_key_is_trimmed(make_client: Any) -> None:
    client, t = make_client(json_response(200, JOB), api_key="  pk_test_123\n")
    client.jobs.get("job_1")
    assert t.last.headers["Authorization"] == "Bearer pk_test_123"


@pytest.mark.parametrize("key", ["pk_test_1 23", "pk_test_\n123", "pk_test_\x00", "pk_test_\x7f"])
def test_api_key_with_bad_characters_rejected_without_echo(key: str) -> None:
    with pytest.raises(PageRushError) as info:
        pagerush.PageRush(api_key=key)
    assert (info.value.status, info.value.code) == (0, "VALIDATION_FAILED")
    assert "pk_test" not in str(info.value) and "pk_test" not in repr(info.value)


def test_client_repr_hides_key() -> None:
    client = pagerush.PageRush(api_key="pk_test_secret_123")
    assert "pk_test_secret_123" not in repr(client)


def test_transport_value_error_is_validation_error_without_details(make_client: Any) -> None:
    client, t = make_client(ValueError("Invalid header value b'Bearer pk_test_123'"))
    with pytest.raises(PageRushError) as info:
        client.jobs.get("job_1")
    err = info.value
    assert (err.status, err.code) == (0, "VALIDATION_FAILED")
    assert not isinstance(err, PageRushConnectionError)
    assert "pk_test_123" not in str(err) and err.__cause__ is None and err.__suppress_context__
    assert len(t.requests) == 1


@pytest.mark.parametrize("key", ["", "has space", "line\n", "a" * 256, "émoji", "a/b"])
def test_invalid_idempotency_key_sends_nothing(make_client: Any, key: str) -> None:
    client, t = make_client()
    with pytest.raises(PageRushError) as info:
        client.templates.render("tpl_1", data={}, idempotency_key=key)
    assert (info.value.status, info.value.code) == (0, "VALIDATION_FAILED")
    assert t.requests == []


def test_valid_idempotency_key_characters(make_client: Any) -> None:
    client, t = make_client(pdf_response())
    client.templates.render("tpl_1", data={}, idempotency_key="Order_42-a:b." + "x" * 242)
    assert len(t.last.headers["Idempotency-Key"]) == 255


@pytest.mark.parametrize("data", [float("nan"), {"x": float("inf")}, {1, 2}, b"bytes", "\ud800"])
def test_unencodable_body_is_validation_error(make_client: Any, data: Any) -> None:
    client, t = make_client()
    with pytest.raises(PageRushError) as info:
        client.templates.render("tpl_1", data=data)
    assert (info.value.status, info.value.code) == (0, "VALIDATION_FAILED")
    assert t.requests == []


def test_render_retries_409_conflict_with_retry_after(make_client: Any, sleeps: Any) -> None:
    client, t = make_client(
        json_response(409, problem(409, "CONFLICT"), {"Retry-After": "2"}),
        pdf_response(),
    )
    assert client.templates.render("tpl_1", data={}).pdf is not None
    assert sleeps == [2.0]
    assert t.requests[0].headers["Idempotency-Key"] == t.requests[1].headers["Idempotency-Key"]


def test_render_does_not_retry_idempotency_conflict(make_client: Any) -> None:
    client, t = make_client(json_response(409, problem(409, "IDEMPOTENCY_CONFLICT")))
    with pytest.raises(PageRushError) as info:
        client.templates.render("tpl_1", data={})
    assert info.value.code == "IDEMPOTENCY_CONFLICT"
    assert len(t.requests) == 1


def test_conflict_not_retried_outside_render(make_client: Any) -> None:
    client, t = make_client(
        json_response(409, problem(409, "CONFLICT")), json_response(409, problem(409, "CONFLICT"))
    )
    with pytest.raises(PageRushError):
        client.templates.create(name="x", source="y")
    with pytest.raises(PageRushError):
        client.jobs.get("job_1")
    assert len(t.requests) == 2


@pytest.mark.parametrize(
    "error",
    [
        PageRushError(
            status=422, code="X", errors=[{"path": "a", "message": "b"}], request_id="r", retry_after=3
        ),
        PageRushConnectionError("TIMEOUT", "slow"),
    ],
)
def test_errors_pickle(error: PageRushError) -> None:
    import pickle

    copy = pickle.loads(pickle.dumps(error))
    assert type(copy) is type(error)
    assert copy.__dict__ == error.__dict__
    assert str(copy) == str(error)


def test_reprs_hide_secrets_and_bytes() -> None:
    request = pagerush.HTTPRequest(
        method="GET",
        url="https://files.test/x.pdf?sig=SECRETSIG",
        headers={"Authorization": "Bearer pk_test_123", "Accept": "application/pdf"},
        body=b"{}",
        timeout=1.0,
    )
    text = repr(request)
    assert "pk_test_123" not in text and "SECRETSIG" not in text and "<redacted>" in text
    assert "%PDF" not in repr(HTTPResponse(status=200, body=b"%PDF-1.7"))
    result = pagerush.RenderResult(queued=False, pdf=b"%PDF-1.7", job_id="j", template_version=1)
    assert "%PDF" not in repr(result)


def test_connection_error_message_strips_query() -> None:
    import socket

    sock = socket.socket()
    sock.bind(("127.0.0.1", 0))
    port = sock.getsockname()[1]
    sock.close()
    request = pagerush.HTTPRequest("GET", f"http://127.0.0.1:{port}/x.pdf?sig=SECRETSIG", {}, None, 1.0)
    with pytest.raises(PageRushConnectionError) as info:
        pagerush.urllib_transport(request)
    assert "SECRETSIG" not in str(info.value)
    assert f"127.0.0.1:{port}/x.pdf" in str(info.value)


@pytest.mark.parametrize(
    ("old", "new", "keeps_auth"),
    [
        ("https://api.test/a", "https://api.test/b", True),
        ("http://api.test/a", "https://api.test/b", True),
        ("https://api.test/a", "http://api.test/b", False),
        ("https://api.test/a", "https://other.test/b", False),
        ("https://api.test/a", "https://api.test:8443/b", False),
    ],
)
def test_redirect_handler_auth_rules(old: str, new: str, keeps_auth: bool) -> None:
    import http.client
    import io
    import urllib.request

    from pagerush._http import _RedirectHandler

    req = urllib.request.Request(old, headers={"Authorization": "Bearer pk_test_123"})
    out = _RedirectHandler().redirect_request(req, io.BytesIO(), 302, "Found", http.client.HTTPMessage(), new)
    assert out is not None
    assert out.has_header("Authorization") is keeps_auth


def test_invalid_url_is_validation_error() -> None:
    client = pagerush.PageRush(api_key="pk_test_123", base_url="http://exa mple.test", max_retries=1)
    with pytest.raises(PageRushError) as info:
        client.jobs.get("job_1")
    assert (info.value.code, info.value.status) == ("VALIDATION_FAILED", 0)
    assert not isinstance(info.value, PageRushConnectionError)


def test_template_file_non_utf8_text_falls_back_to_base64() -> None:
    entry = pagerush.template_file("data.csv", b"\xff\xfe")
    assert entry == {"path": "data.csv", "content": "//4=", "encoding": "base64"}
