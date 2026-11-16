"""Verify and parse ``callback_url`` deliveries (webhooks).

These functions need no API key, so a receiver can use them without a client::

    from pagerush import webhooks

    event = webhooks.construct_event(raw_body, request_headers, webhook_secret)

The signature covers the timestamp, the ``X-PageRush-Event``, ``X-PageRush-Job-Id`` (or, for
``batch.completed``, ``X-PageRush-Batch-Id``) and ``X-PageRush-Metadata`` headers, and the body.
Always pass the **raw request body bytes** exactly as received: parsing JSON and serialising it
again changes the bytes and the signature will not match.
"""

from __future__ import annotations

import hashlib
import hmac
import json
import re
import time
from dataclasses import dataclass
from typing import Any, Dict, Iterable, List, Optional, Protocol, Sequence, Tuple, Union, cast

from ._errors import WebhookVerificationError
from ._types import Batch

__all__ = [
    "BATCH_COMPLETED",
    "BATCH_ID_HEADER",
    "DEFAULT_TOLERANCE",
    "EVENT_HEADER",
    "JOB_ID_HEADER",
    "METADATA_HEADER",
    "SIGNATURE_HEADER",
    "Event",
    "HeadersLike",
    "WebhookEvent",
    "Webhooks",
    "construct_event",
    "verify",
]

DEFAULT_TOLERANCE = 300
"""Seconds a signature's timestamp may differ from now (5 minutes)."""

SIGNATURE_HEADER = "X-PageRush-Signature"
"""``t=<unix seconds>,v2=<hex HMAC-SHA256>``, with two ``v2`` values during a secret rotation."""

EVENT_HEADER = "X-PageRush-Event"
"""``pdf.succeeded``, ``pdf.failed`` or ``batch.completed``. Signed."""

JOB_ID_HEADER = "X-PageRush-Job-Id"
"""The job id, the same on every delivery attempt. Signed."""

BATCH_ID_HEADER = "X-PageRush-Batch-Id"
"""The batch id on ``batch.completed`` deliveries, in place of the job id. Signed."""

METADATA_HEADER = "X-PageRush-Metadata"
"""The job's or batch's metadata as a JSON object (``pdf.succeeded`` and ``batch.completed``, when
there is metadata). Signed."""

BATCH_COMPLETED = "batch.completed"
"""The event sent when a batch finishes; it carries ``X-PageRush-Batch-Id`` instead of a job id."""

_TIMESTAMP = re.compile(r"[0-9]{1,15}")

Secrets = Union[str, Sequence[str]]
RawBody = Union[bytes, bytearray, memoryview, str]


class HeadersLike(Protocol):
    """Anything with ``items()`` yielding ``(name, value)`` pairs: a ``dict``, Flask/Werkzeug
    ``request.headers``, Starlette ``request.headers``, or ``http.server``'s ``self.headers``."""

    def items(self) -> Iterable[Tuple[str, Any]]: ...


@dataclass
class WebhookEvent:
    """A verified webhook delivery. Every field comes from signed data.

    Attributes:
        event: ``"pdf.succeeded"``, ``"pdf.failed"`` or ``"batch.completed"``.
        job_id: The job id, or ``None`` for ``batch.completed``. Delivery is at-least-once; ignore
            ids you have already handled.
        timestamp: Unix seconds from the signature header.
        pdf: The PDF bytes (``pdf.succeeded`` only), else ``None``.
        failure: The parsed JSON body (``pdf.failed`` only): ``{event, job_id, template_id,
            error: {code, message}, metadata, created_at}``, else ``None``.
        metadata: The job's or batch's metadata, or ``None`` if there is none or it is not a JSON
            object of strings.
        batch_id: The batch id (``batch.completed`` only), else ``None``.
        batch: The body's ``batch`` object, a :class:`pagerush.Batch` without ``items``
            (``batch.completed`` only), else ``None``.
    """

    event: str
    job_id: Optional[str]
    timestamp: int
    pdf: Optional[bytes]
    failure: Optional[Dict[str, Any]]
    metadata: Optional[Dict[str, str]]
    batch_id: Optional[str] = None
    batch: Optional[Batch] = None


Event = WebhookEvent
"""Alias of :class:`WebhookEvent`."""


def _to_bytes(raw_body: RawBody) -> bytes:
    if isinstance(raw_body, str):
        return raw_body.encode("utf-8")
    return bytes(raw_body)


def _header(headers: HeadersLike, name: str) -> Optional[str]:
    lower = name.lower()
    for key, value in headers.items():
        if str(key).lower() == lower:
            return str(value)
    return None


def _secrets(secret: Secrets) -> List[str]:
    secrets = [secret] if isinstance(secret, str) else list(secret)
    if not secrets:
        raise ValueError("No webhook secret given: pass your project's webhook secret")
    if any(not isinstance(item, str) or not item for item in secrets):
        raise ValueError("Webhook secrets must be non-empty strings (is the environment variable set?)")
    return secrets


def _parse_signature(header: Optional[str]) -> Tuple[int, List[str]]:
    timestamps: List[str] = []
    signatures: List[str] = []
    for pair in (header or "").split(","):
        key, sep, value = pair.strip().partition("=")
        if not sep:
            continue
        key, value = key.strip(), value.strip()
        if key == "t":
            timestamps.append(value)
        elif key == "v2":
            signatures.append(value)
    if len(timestamps) != 1 or not _TIMESTAMP.fullmatch(timestamps[0]):
        raise WebhookVerificationError("header", "Signature header needs exactly one t= of 1-15 digits")
    if not signatures:
        raise WebhookVerificationError("header", "Signature header has no v2= signature")
    return int(timestamps[0]), signatures


def _verify(
    body: bytes, headers: HeadersLike, secret: Secrets, tolerance: int, now: Optional[int]
) -> Tuple[int, str, str, Optional[str]]:
    """Verify a delivery and return ``(timestamp, event, id, metadata header)``.

    The id is the batch id for ``batch.completed`` and the job id otherwise.
    """
    secrets = _secrets(secret)
    timestamp, signatures = _parse_signature(_header(headers, SIGNATURE_HEADER))
    event = _header(headers, EVENT_HEADER)
    if not event:
        raise WebhookVerificationError("header", f"The delivery has no {EVENT_HEADER} header")
    id_header = BATCH_ID_HEADER if event == BATCH_COMPLETED else JOB_ID_HEADER
    delivery_id = _header(headers, id_header)
    if not delivery_id:
        raise WebhookVerificationError("header", f"The delivery has no {id_header} header")
    metadata = _header(headers, METADATA_HEADER)

    current = int(time.time()) if now is None else now
    if abs(current - timestamp) > tolerance:
        raise WebhookVerificationError("timestamp", f"Signature timestamp is more than {tolerance}s from now")

    prefix = f"{timestamp}\n{event}\n{delivery_id}\n{metadata or ''}\n"
    signed = prefix.encode("utf-8") + body
    matched = False
    for key in secrets:
        expected = hmac.new(key.encode("utf-8"), signed, hashlib.sha256).hexdigest().encode("ascii")
        for candidate in signatures:
            # Compare every pair so timing does not reveal which one matched.
            if hmac.compare_digest(expected, candidate.encode("utf-8")):
                matched = True
    if not matched:
        raise WebhookVerificationError("signature", "No signature matches the webhook secret")
    return timestamp, event, delivery_id, metadata


def verify(
    raw_body: RawBody,
    headers: HeadersLike,
    secret: Secrets,
    *,
    tolerance: int = DEFAULT_TOLERANCE,
    now: Optional[int] = None,
) -> int:
    """Check a delivery's ``X-PageRush-Signature`` against its signed headers and raw body.

    The id header is ``X-PageRush-Batch-Id`` when the event is ``batch.completed`` and
    ``X-PageRush-Job-Id`` otherwise; only that one counts.

    Args:
        raw_body: The exact bytes received (a ``str`` is encoded as UTF-8).
        headers: The request headers; names are matched case-insensitively.
        secret: Your webhook secret, or a list of secrets (any match passes).
        tolerance: Maximum seconds between the signature's timestamp and ``now``.
        now: Current Unix time in seconds (defaults to the system clock).

    Returns:
        The verified timestamp (Unix seconds).

    Raises:
        WebhookVerificationError: ``reason`` is ``"header"`` (signature header missing or
            malformed, or no event header or no id header for the event), ``"timestamp"``
            (outside ``tolerance``) or ``"signature"`` (no ``v2`` value matches any secret).
        ValueError: ``secret`` is empty, an empty list, or contains an empty string.
    """
    return _verify(_to_bytes(raw_body), headers, secret, tolerance, now)[0]


def _string_map(value: Any) -> Optional[Dict[str, str]]:
    """``value`` if it is a JSON object of strings, else ``None``."""
    if isinstance(value, dict) and all(isinstance(v, str) for v in value.values()):
        return {str(k): v for k, v in value.items()}
    return None


def _json_body(body: bytes) -> Any:
    """The parsed JSON body, or ``None`` if it is not valid UTF-8 JSON."""
    try:
        return json.loads(body.decode("utf-8"))
    except ValueError:
        return None


def _metadata_header(value: Optional[str]) -> Optional[Dict[str, str]]:
    """The ``X-PageRush-Metadata`` header as a JSON object of strings, else ``None``."""
    if not value:
        return None
    try:
        return _string_map(json.loads(value))
    except ValueError:
        return None


def construct_event(
    raw_body: RawBody,
    headers: HeadersLike,
    secret: Secrets,
    *,
    tolerance: int = DEFAULT_TOLERANCE,
    now: Optional[int] = None,
) -> WebhookEvent:
    """Verify a delivery and parse it into a :class:`WebhookEvent`.

    ``metadata`` comes from ``X-PageRush-Metadata`` on ``pdf.succeeded`` and ``batch.completed``,
    and from the body on ``pdf.failed``. For ``batch.completed``, ``batch_id`` and ``batch`` are set
    and ``job_id`` is ``None``. An unknown event is returned as is, with ``pdf`` and ``failure`` set
    to ``None``.

    Args:
        raw_body: The exact bytes received.
        headers: The request headers; names are matched case-insensitively.
        secret: Your webhook secret, or a list of secrets.
        tolerance: Maximum age of the signature in seconds.
        now: Current Unix time in seconds (defaults to the system clock).

    Raises:
        WebhookVerificationError: As for :func:`verify`, and ``reason`` ``"header"`` if a
            ``pdf.failed`` body is not a JSON object, or a ``batch.completed`` body is not a JSON
            object with a ``batch`` object.
        ValueError: ``secret`` is empty, an empty list, or contains an empty string.
    """
    body = _to_bytes(raw_body)
    timestamp, event, delivery_id, metadata_header = _verify(body, headers, secret, tolerance, now)
    job_id: Optional[str] = delivery_id
    pdf: Optional[bytes] = None
    failure: Optional[Dict[str, Any]] = None
    metadata: Optional[Dict[str, str]] = None
    batch_id: Optional[str] = None
    batch: Optional[Batch] = None

    if event == "pdf.failed":
        parsed = _json_body(body)
        if not isinstance(parsed, dict):
            raise WebhookVerificationError("header", "A pdf.failed body must be a JSON object")
        failure = parsed
        metadata = _string_map(parsed.get("metadata"))
    elif event == "pdf.succeeded":
        pdf = body
        metadata = _metadata_header(metadata_header)
    elif event == BATCH_COMPLETED:
        parsed = _json_body(body)
        if not isinstance(parsed, dict) or not isinstance(parsed.get("batch"), dict):
            raise WebhookVerificationError(
                "header", "A batch.completed body must be a JSON object with a batch object"
            )
        job_id = None
        batch_id = delivery_id
        batch = cast(Batch, parsed["batch"])
        metadata = _metadata_header(metadata_header)

    return WebhookEvent(
        event=event,
        job_id=job_id,
        timestamp=timestamp,
        pdf=pdf,
        failure=failure,
        metadata=metadata,
        batch_id=batch_id,
        batch=batch,
    )


class Webhooks:
    """Webhook helpers on a client (``client.webhooks``). Same as the :mod:`pagerush.webhooks`
    module functions; no API key is used."""

    def verify(
        self,
        raw_body: RawBody,
        headers: HeadersLike,
        secret: Secrets,
        *,
        tolerance: int = DEFAULT_TOLERANCE,
        now: Optional[int] = None,
    ) -> int:
        """See :func:`pagerush.webhooks.verify`."""
        return verify(raw_body, headers, secret, tolerance=tolerance, now=now)

    def construct_event(
        self,
        raw_body: RawBody,
        headers: HeadersLike,
        secret: Secrets,
        *,
        tolerance: int = DEFAULT_TOLERANCE,
        now: Optional[int] = None,
    ) -> WebhookEvent:
        """See :func:`pagerush.webhooks.construct_event`."""
        return construct_event(raw_body, headers, secret, tolerance=tolerance, now=now)
