from __future__ import annotations

import base64
import hashlib
import hmac
import json
import pickle
from email.message import Message
from pathlib import Path
from typing import Any, Dict, Optional

import pytest

import pagerush
from pagerush import WebhookVerificationError, webhooks

VECTORS = json.loads((Path(__file__).resolve().parent / "fixtures" / "webhooks.json").read_text())
CASES = VECTORS["cases"]
SECRET = "whsec_test_secret"


@pytest.mark.parametrize("case", CASES, ids=[c["name"] for c in CASES])
def test_vectors(case: Dict[str, Any]) -> None:
    body = base64.b64decode(case["body_base64"])
    headers = case["headers"]
    secrets = case["secrets"]
    kwargs = {"tolerance": case["tolerance_seconds"], "now": case["now"]}
    if case["valid"]:
        timestamp = webhooks.verify(body, headers, secrets, **kwargs)
        if len(secrets) == 1:
            webhooks.verify(body, headers, secrets[0], **kwargs)
        lowered = {k.lower(): v for k, v in headers.items()}
        event = webhooks.construct_event(body, lowered, secrets, **kwargs)
        assert event.timestamp == timestamp
        assert event.event == headers["X-PageRush-Event"]
        assert event.job_id == headers["X-PageRush-Job-Id"]
        if event.event == "pdf.succeeded":
            assert event.pdf == body and event.failure is None
            if "X-PageRush-Metadata" in headers:
                assert event.metadata == json.loads(headers["X-PageRush-Metadata"])
            else:
                assert event.metadata is None
        else:
            assert event.pdf is None and event.failure == json.loads(body)
    else:
        with pytest.raises(WebhookVerificationError) as info:
            webhooks.verify(body, headers, secrets, **kwargs)
        assert info.value.reason == case["reason"]
        with pytest.raises(WebhookVerificationError) as info:
            webhooks.construct_event(body, headers, secrets, **kwargs)
        assert info.value.reason == case["reason"]


def test_vectors_cover_unicode_metadata() -> None:
    case = CASES[0]
    event = webhooks.construct_event(
        base64.b64decode(case["body_base64"]), case["headers"], case["secrets"], now=case["now"]
    )
    assert event.metadata == {"order": "42", "amount": "₹1,250"}


def sign(
    body: bytes,
    t: int,
    event: str = "pdf.succeeded",
    job_id: str = "job_1",
    metadata: Optional[str] = None,
    secret: str = SECRET,
) -> Dict[str, str]:
    signed = f"{t}\n{event}\n{job_id}\n{metadata or ''}\n".encode() + body
    digest = hmac.new(secret.encode(), signed, hashlib.sha256).hexdigest()
    headers = {
        "X-PageRush-Signature": f"t={t},v2={digest}",
        "X-PageRush-Event": event,
        "X-PageRush-Job-Id": job_id,
    }
    if metadata is not None:
        headers["X-PageRush-Metadata"] = metadata
    return headers


def test_verify_returns_timestamp_and_accepts_str_body() -> None:
    body = '{"a":"é"}'
    assert webhooks.verify(body, sign(body.encode(), 100), SECRET, now=100) == 100


def test_verify_default_now_uses_clock() -> None:
    with pytest.raises(WebhookVerificationError) as info:
        webhooks.verify(b"%PDF-", sign(b"%PDF-", 100), SECRET)
    assert info.value.reason == "timestamp"


@pytest.mark.parametrize("missing", ["X-PageRush-Signature", "X-PageRush-Event", "X-PageRush-Job-Id"])
def test_verify_missing_required_header(missing: str) -> None:
    headers = sign(b"x", 1)
    del headers[missing]
    with pytest.raises(WebhookVerificationError) as info:
        webhooks.verify(b"x", headers, SECRET, now=1)
    assert info.value.reason == "header"


@pytest.mark.parametrize(
    "header", ["t=abc,v2=00", "t=-5,v2=00", "garbage", "t=,v2=00", "t=1,v2", "t=1,v1=00"]
)
def test_verify_malformed_signature_header(header: str) -> None:
    headers = dict(sign(b"x", 1), **{"X-PageRush-Signature": header})
    with pytest.raises(WebhookVerificationError) as info:
        webhooks.verify(b"x", headers, SECRET, now=1)
    assert info.value.reason == "header"


@pytest.mark.parametrize("t", ["1" * 16, "0" * 400, "1e3", "+5", "1 2"])
def test_verify_timestamp_must_be_1_to_15_digits(t: str) -> None:
    headers = dict(sign(b"x", 1), **{"X-PageRush-Signature": f"t={t},v2=00"})
    with pytest.raises(WebhookVerificationError) as info:
        webhooks.verify(b"x", headers, SECRET, now=1)
    assert info.value.reason == "header"


def test_verify_15_digit_timestamp_is_parsed() -> None:
    t = 999_999_999_999_999
    with pytest.raises(WebhookVerificationError) as info:
        webhooks.verify(b"x", sign(b"x", t), SECRET, now=1)
    assert info.value.reason == "timestamp"
    assert webhooks.verify(b"x", sign(b"x", t), SECRET, now=t) == t


@pytest.mark.parametrize("secret", ["", [], [SECRET, ""]])
def test_empty_secret_is_a_value_error(secret: Any) -> None:
    with pytest.raises(ValueError, match="secret"):
        webhooks.verify(b"x", sign(b"x", 1), secret, now=1)
    with pytest.raises(ValueError, match="secret"):
        webhooks.construct_event(b"x", sign(b"x", 1), secret, now=1)


def test_verify_non_ascii_signature_fails_cleanly() -> None:
    headers = dict(sign(b"x", 1), **{"X-PageRush-Signature": "t=1,v2=é"})
    with pytest.raises(WebhookVerificationError) as info:
        webhooks.verify(b"x", headers, SECRET, now=1)
    assert info.value.reason == "signature"


def test_construct_event_succeeded_case_insensitive_headers() -> None:
    body = b"%PDF-1.7 data"
    headers = {k.upper(): v for k, v in sign(body, 1000, metadata='{"order":"42"}').items()}
    event = webhooks.construct_event(body, headers, SECRET, now=1001)
    assert event == pagerush.WebhookEvent(
        event="pdf.succeeded",
        job_id="job_1",
        timestamp=1000,
        pdf=body,
        failure=None,
        metadata={"order": "42"},
    )


def test_construct_event_failed_with_email_message_headers() -> None:
    payload = {
        "event": "pdf.failed",
        "job_id": "job_2",
        "template_id": "tpl_1",
        "error": {"code": "TEMPLATE_COMPILE_FAILED", "message": "unknown variable: foo"},
        "metadata": {"order": "7"},
        "created_at": "2026-01-01T00:00:00.000Z",
    }
    body = json.dumps(payload).encode()
    headers = Message()
    for key, value in sign(body, 50, event="pdf.failed", job_id="job_2").items():
        headers[key] = value
    event = pagerush.PageRush(api_key="pk_test_123").webhooks.construct_event(
        body, headers, ["whsec_old", SECRET], now=50
    )
    assert event.event == "pdf.failed"
    assert event.job_id == "job_2"
    assert event.pdf is None
    assert event.failure == payload
    assert event.metadata == {"order": "7"}


@pytest.mark.parametrize("body", [b"not json", b"[1]", b'"text"', b"\xff\xfe"])
def test_construct_event_failed_body_not_object_is_header_error(body: bytes) -> None:
    headers = sign(body, 1, event="pdf.failed")
    with pytest.raises(WebhookVerificationError) as info:
        webhooks.construct_event(body, headers, SECRET, now=1)
    assert info.value.reason == "header"


@pytest.mark.parametrize("value", ["[1]", "not json", '{"a": 1}', '"text"', "{"])
def test_construct_event_metadata_not_object_of_strings_is_none(value: str) -> None:
    body = b"%PDF-"
    event = webhooks.construct_event(body, sign(body, 1, metadata=value), SECRET, now=1)
    assert event.metadata is None


def test_construct_event_failed_body_metadata_not_strings_is_none() -> None:
    body = json.dumps({"event": "pdf.failed", "job_id": "j", "metadata": {"a": 1}}).encode()
    event = webhooks.construct_event(body, sign(body, 1, event="pdf.failed", job_id="j"), SECRET, now=1)
    assert event.metadata is None


def test_construct_event_unknown_event_keeps_name() -> None:
    body = b"{}"
    event = webhooks.construct_event(body, sign(body, 1, event="pdf.future"), SECRET, now=1)
    assert (event.event, event.job_id, event.pdf, event.failure) == ("pdf.future", "job_1", None, None)


def test_webhook_error_pickles() -> None:
    err = pickle.loads(pickle.dumps(WebhookVerificationError("timestamp", "too old")))
    assert (err.reason, err.message, str(err)) == ("timestamp", "too old", "too old")
