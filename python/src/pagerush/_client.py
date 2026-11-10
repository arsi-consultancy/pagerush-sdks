"""The API client and its request/retry loop."""

from __future__ import annotations

import email.utils
import json
import math
import os
import random
import re
import time
import urllib.parse
from typing import Any, Callable, Dict, List, Literal, Optional, Tuple

from ._errors import PageRushConnectionError, PageRushError, validation_error
from ._http import HTTPRequest, HTTPResponse, Transport, is_timeout, urllib_transport
from ._resources import Jobs, Templates
from ._types import ErrorItem
from ._version import __version__
from .webhooks import Webhooks

__all__ = ["DEFAULT_BASE_URL", "PageRush"]

DEFAULT_BASE_URL = "https://pagerush.dev"
"""The API host used when neither ``base_url`` nor ``PAGERUSH_BASE_URL`` is set."""

USER_AGENT = f"pagerush-python/{__version__}"

_RETRYABLE_STATUS = frozenset({429, 502, 503, 504})
_RETRYABLE_STATUS_UNSAFE = frozenset({429, 503})
_MAX_RETRY_AFTER = 30.0
_MAX_RETRY_AFTER_VALUE = 86_400 * 365
_DELTA_SECONDS = re.compile(r"([0-9]+)(?:\.([0-9]+))?")
_BAD_KEY_CHARS = re.compile(r"[\s\x00-\x1f\x7f]")

RetryPolicy = Literal["idempotent", "render", "unsafe"]
"""How a request may be retried: ``idempotent`` (GET, DELETE), ``render`` (POST with an
idempotency key) or ``unsafe`` (other POST and PATCH: only when the server rejected it up front)."""


def _parse_retry_after(value: Optional[str]) -> Optional[int]:
    """Seconds from a ``Retry-After`` header (delta-seconds or an HTTP date).

    Returns ``None`` when the header is missing or unparsable. Very large values are capped at a
    year so they never overflow.
    """
    if value is None:
        return None
    value = value.strip()
    match = _DELTA_SECONDS.fullmatch(value)
    if match:
        whole, fraction = match.groups()
        seconds = int(whole) if len(whole) <= 12 else _MAX_RETRY_AFTER_VALUE
        if fraction and fraction.strip("0"):
            seconds += 1
        return min(seconds, _MAX_RETRY_AFTER_VALUE)
    try:
        when = email.utils.parsedate_to_datetime(value)
    except (TypeError, ValueError, IndexError, OverflowError):
        return None
    if when is None:  # pragma: no cover - Python 3.9 returns None on some bad input
        return None
    try:
        delta = math.ceil(when.timestamp() - time.time())
    except (OverflowError, ValueError, OSError):
        return None
    return min(max(0, delta), _MAX_RETRY_AFTER_VALUE)


def _encode_json(value: Any) -> bytes:
    try:
        return json.dumps(value, ensure_ascii=False, allow_nan=False, separators=(",", ":")).encode("utf-8")
    except (TypeError, ValueError) as err:
        raise validation_error(f"The request body cannot be encoded as JSON ({err})") from None


def _error_from_response(response: HTTPResponse) -> PageRushError:
    status = response.status
    header_request_id = response.header("X-Request-Id")
    retry_after = _parse_retry_after(response.header("Retry-After"))
    body: Any = None
    try:
        body = json.loads(response.body.decode("utf-8")) if response.body else None
    except (ValueError, UnicodeDecodeError):
        body = None
    if not isinstance(body, dict) or not isinstance(body.get("code"), str):
        return PageRushError(
            status=status, code="UNKNOWN", request_id=header_request_id, retry_after=retry_after
        )

    def text(key: str) -> Optional[str]:
        value = body.get(key)
        return value if isinstance(value, str) else None

    errors: List[ErrorItem] = []
    raw_errors = body.get("errors")
    if isinstance(raw_errors, list):
        for item in raw_errors:
            if isinstance(item, dict):
                errors.append({"path": str(item.get("path", "")), "message": str(item.get("message", ""))})
    return PageRushError(
        status=status,
        code=body["code"],
        title=text("title"),
        detail=text("detail"),
        errors=errors,
        request_id=text("request_id") or header_request_id,
        retry_after=retry_after,
        type=text("type"),
    )


class PageRush:
    """Client for the PageRush API (``/v1``).

    Example::

        import pagerush

        client = pagerush.PageRush()  # reads PAGERUSH_API_KEY
        created = client.templates.create(
            name="Invoice",
            source='#let data = json("data.json")\\nInvoice for #data.name',
        )
        result = client.templates.render(created["template"]["id"], data={"name": "Ada"})
        result.save("invoice.pdf")

    Args:
        api_key: Project key (``pk_live_...``). Defaults to the ``PAGERUSH_API_KEY`` environment
            variable. Raises :class:`PageRushError` (code ``MISSING_API_KEY``) if neither is set.
        base_url: API host. Defaults to ``PAGERUSH_BASE_URL``, else ``https://pagerush.dev``.
        timeout: Seconds per HTTP attempt (default 60).
        max_retries: Retries after the first attempt (default 2, so up to 3 attempts).
        transport: Sends one HTTP attempt. Defaults to :func:`pagerush.urllib_transport`. Inject
            your own for tests or custom networking.
        sleep: Called with seconds to wait between retries and polls. Defaults to ``time.sleep``.

    Attributes:
        templates: :class:`pagerush.Templates`.
        jobs: :class:`pagerush.Jobs`.
        webhooks: :class:`pagerush.Webhooks` (the same functions as :mod:`pagerush.webhooks`).
    """

    def __init__(
        self,
        api_key: Optional[str] = None,
        *,
        base_url: Optional[str] = None,
        timeout: float = 60.0,
        max_retries: int = 2,
        transport: Optional[Transport] = None,
        sleep: Optional[Callable[[float], None]] = None,
    ) -> None:
        key = (api_key or os.environ.get("PAGERUSH_API_KEY") or "").strip()
        if not key:
            raise PageRushError(
                status=0,
                code="MISSING_API_KEY",
                message="MISSING_API_KEY: pass api_key or set the PAGERUSH_API_KEY environment variable",
            )
        if _BAD_KEY_CHARS.search(key):
            raise validation_error("The API key contains spaces or control characters")
        if max_retries < 0:
            raise ValueError("max_retries must be 0 or more")
        self.api_key = key
        self.base_url = (base_url or os.environ.get("PAGERUSH_BASE_URL") or DEFAULT_BASE_URL).rstrip("/")
        self.timeout = timeout
        self.max_retries = max_retries
        self._transport: Transport = urllib_transport if transport is None else transport
        self._sleep: Callable[[float], None] = time.sleep if sleep is None else sleep
        self._clock: Callable[[], float] = time.monotonic
        self._random: Callable[[], float] = random.random
        self.templates = Templates(self)
        self.jobs = Jobs(self)
        self.webhooks = Webhooks()

    def __repr__(self) -> str:
        return f"PageRush(base_url={self.base_url!r})"

    # -- internals -------------------------------------------------------------------------------

    def _backoff(self, retry: int, retry_after: Optional[int]) -> float:
        if retry_after is not None:
            return min(float(retry_after), _MAX_RETRY_AFTER)
        base = min(8.0, 0.5 * 2.0 ** (retry - 1))
        return base * (0.75 + 0.5 * self._random())

    def _request(
        self,
        method: str,
        path: str,
        *,
        json_body: Any = None,
        has_body: bool = False,
        query: Optional[Dict[str, Any]] = None,
        headers: Optional[Dict[str, str]] = None,
        accept: str = "application/json",
        retry: RetryPolicy = "idempotent",
        absolute: bool = False,
        auth: bool = True,
    ) -> HTTPResponse:
        """Send a request with retries and return the 2xx response, or raise PageRushError."""
        url = path if absolute else self.base_url + path
        if query:
            pairs = {k: str(v) for k, v in query.items() if v is not None}
            if pairs:
                url += "?" + urllib.parse.urlencode(pairs)
        req_headers: Dict[str, str] = {"User-Agent": USER_AGENT, "Accept": accept}
        if auth:
            req_headers["Authorization"] = f"Bearer {self.api_key}"
        body: Optional[bytes] = None
        if has_body:
            body = _encode_json(json_body)
            req_headers["Content-Type"] = "application/json"
        if headers:
            req_headers.update(headers)

        retries = 0
        while True:
            request = HTTPRequest(
                method=method, url=url, headers=dict(req_headers), body=body, timeout=self.timeout
            )
            error: PageRushError
            try:
                response = self._transport(request)
            except PageRushConnectionError as err:
                error = err
            except ValueError:
                # Raised before anything was sent (e.g. an invalid header value). The original
                # message can contain header values such as the API key, so it is not kept.
                raise validation_error(
                    "The request could not be sent (invalid URL or header value)"
                ) from None
            except OSError as err:
                code = "TIMEOUT" if is_timeout(err) else "CONNECTION_ERROR"
                error = PageRushConnectionError(code, str(err) or type(err).__name__)
                error.__cause__ = err
            else:
                if 200 <= response.status < 300:
                    return response
                error = _error_from_response(response)

            if retries < self.max_retries and self._retryable(error, retry):
                retries += 1
                self._sleep(self._backoff(retries, error.retry_after))
                continue
            raise error

    @staticmethod
    def _retryable(error: PageRushError, policy: RetryPolicy) -> bool:
        if policy == "unsafe":
            return not isinstance(error, PageRushConnectionError) and error.status in _RETRYABLE_STATUS_UNSAFE
        if isinstance(error, PageRushConnectionError):
            return True
        if policy == "render" and error.status == 409 and error.code == "CONFLICT":
            # The server is still rendering an earlier attempt with the same idempotency key.
            return True
        return error.status in _RETRYABLE_STATUS

    @staticmethod
    def _invalid_response(response: HTTPResponse, detail: str) -> PageRushError:
        return PageRushError(
            status=response.status,
            code="INVALID_RESPONSE",
            detail=detail,
            request_id=response.header("X-Request-Id"),
        )

    def _json_object(self, response: HTTPResponse, required: Tuple[str, ...] = ()) -> Dict[str, Any]:
        """Parse a 2xx JSON object body that has every key in ``required``.

        Raises:
            PageRushError: Code ``INVALID_RESPONSE`` if the body is not such an object.
        """
        try:
            data = json.loads(response.body.decode("utf-8"))
        except (ValueError, UnicodeDecodeError) as err:
            raise self._invalid_response(response, "The response body was not valid JSON") from err
        if not isinstance(data, dict):
            raise self._invalid_response(response, "The response body was not a JSON object")
        missing = [key for key in required if key not in data]
        if missing:
            raise self._invalid_response(response, f"The response body has no {', '.join(missing)}")
        return data

    def _unwrap(self, response: HTTPResponse, key: str) -> Dict[str, Any]:
        """Return the object under ``key`` in a 2xx JSON body, e.g. ``{"template": {...}}``."""
        value = self._json_object(response, (key,))[key]
        if not isinstance(value, dict):
            raise self._invalid_response(response, f"The response body's {key} is not an object")
        return value
