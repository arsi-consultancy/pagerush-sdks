"""Exceptions raised by the SDK."""

from __future__ import annotations

from typing import Any, Dict, List, Optional, Tuple, Type

from ._types import ErrorItem

__all__ = ["PageRushConnectionError", "PageRushError", "WebhookVerificationError"]


def _rebuild(cls: Type[Exception], message: str, state: Dict[str, Any]) -> Exception:
    """Recreate an SDK exception from its state (used by pickle)."""
    error = cls.__new__(cls)
    Exception.__init__(error, message)
    error.__dict__.update(state)
    return error


class _PicklableError(Exception):
    """Base for SDK exceptions whose ``__init__`` takes keyword-only or extra arguments.

    The default ``BaseException`` pickling calls ``cls(*args)``, which does not fit those
    signatures, so the attributes are restored directly instead.
    """

    def __reduce__(self) -> Tuple[Any, ...]:
        return (_rebuild, (type(self), str(self), dict(self.__dict__)))


class PageRushError(_PicklableError):
    """An error response from the PageRush API, or a failure to reach it.

    Every non-2xx response raises this. The fields mirror the API's ``application/problem+json``
    body.

    Attributes:
        status: HTTP status code (``0`` when no response was received).
        code: Stable machine-readable code, e.g. ``"TEMPLATE_COMPILE_FAILED"``. ``"UNKNOWN"`` when
            the response body was not a problem document.
        title: Short summary from the body, or ``None``.
        detail: Longer explanation from the body, or ``None``.
        errors: ``[{"path": ..., "message": ...}]``. On ``TEMPLATE_COMPILE_FAILED`` these are the
            Typst diagnostics. May be empty.
        request_id: The request's id (body ``request_id``, else the ``X-Request-Id`` header). Quote
            it when you contact support.
        retry_after: Seconds from the ``Retry-After`` header, or ``None``.
        type: The problem ``type`` URI, or ``None``.
    """

    status: int
    code: str
    title: Optional[str]
    detail: Optional[str]
    errors: List[ErrorItem]
    request_id: Optional[str]
    retry_after: Optional[int]
    type: Optional[str]
    message: str

    def __init__(
        self,
        *,
        status: int,
        code: str,
        title: Optional[str] = None,
        detail: Optional[str] = None,
        errors: Optional[List[ErrorItem]] = None,
        request_id: Optional[str] = None,
        retry_after: Optional[int] = None,
        type: Optional[str] = None,
        message: Optional[str] = None,
    ) -> None:
        self.status = status
        self.code = code
        self.title = title
        self.detail = detail
        self.errors = list(errors) if errors else []
        self.request_id = request_id
        self.retry_after = retry_after
        self.type = type
        if message is None:
            message = f"{code}: {detail or title or f'HTTP {status}'}"
        self.message = message
        super().__init__(message)

    def __repr__(self) -> str:
        return (
            f"{type(self).__name__}(status={self.status!r}, code={self.code!r}, "
            f"message={self.message!r}, request_id={self.request_id!r})"
        )


class PageRushConnectionError(PageRushError):
    """The API could not be reached: a network failure (code ``CONNECTION_ERROR``) or a timeout
    (code ``TIMEOUT``). ``status`` is always ``0``."""

    def __init__(self, code: str, message: str) -> None:
        super().__init__(status=0, code=code, message=f"{code}: {message}", detail=message)


class WebhookVerificationError(_PicklableError):
    """A webhook delivery failed signature verification.

    Attributes:
        reason: ``"header"`` (signature, event or job id header missing or malformed, or a
            ``pdf.failed`` body that is not a JSON object), ``"timestamp"`` (outside the
            tolerance) or ``"signature"`` (no ``v2`` value matches any of your secrets).
    """

    reason: str
    message: str

    def __init__(self, reason: str, message: str) -> None:
        self.reason = reason
        self.message = message
        super().__init__(message)


def validation_error(message: str) -> PageRushError:
    """A problem found before any request was sent: status 0, code ``VALIDATION_FAILED``."""
    return PageRushError(status=0, code="VALIDATION_FAILED", message=f"VALIDATION_FAILED: {message}")
