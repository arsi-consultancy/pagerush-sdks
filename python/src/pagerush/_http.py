"""The transport layer: one HTTP attempt, swappable for tests."""

from __future__ import annotations

import http.client
import socket
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass, field
from typing import IO, Callable, Dict, Mapping, Optional

from ._errors import PageRushConnectionError

__all__ = ["HTTPRequest", "HTTPResponse", "Transport", "is_timeout", "redact_url", "urllib_transport"]


def redact_url(url: str) -> str:
    """Return ``url`` without its query string and fragment, which may hold a signature."""
    try:
        parts = urllib.parse.urlsplit(url)
    except ValueError:
        return "<invalid URL>"
    return urllib.parse.urlunsplit((parts.scheme, parts.netloc, parts.path, "", ""))


@dataclass(repr=False)
class HTTPRequest:
    """One HTTP attempt the client wants to make.

    Its ``repr`` hides the ``Authorization`` header and the URL's query string.
    """

    method: str
    url: str
    headers: Dict[str, str]
    body: Optional[bytes]
    timeout: float

    def __repr__(self) -> str:
        headers = {k: ("<redacted>" if k.lower() == "authorization" else v) for k, v in self.headers.items()}
        size = None if self.body is None else len(self.body)
        return (
            f"HTTPRequest(method={self.method!r}, url={redact_url(self.url)!r}, headers={headers!r}, "
            f"body=<{size} bytes>, timeout={self.timeout!r})"
        )


@dataclass
class HTTPResponse:
    """A response from a transport. Any status code is a response, not an exception."""

    status: int
    headers: Mapping[str, str] = field(default_factory=dict)
    body: bytes = field(default=b"", repr=False)

    def header(self, name: str) -> Optional[str]:
        """Return a header value, looked up case-insensitively, or ``None``."""
        lower = name.lower()
        for key, value in self.headers.items():
            if key.lower() == lower:
                return value
        return None


Transport = Callable[[HTTPRequest], HTTPResponse]
"""Sends one :class:`HTTPRequest` and returns an :class:`HTTPResponse` for any status code.

On a network failure it raises :class:`~pagerush.PageRushConnectionError` (or any ``OSError``,
which the client maps to one; ``TimeoutError`` maps to code ``TIMEOUT``). If nothing could be
sent (an invalid URL or header value) it raises ``ValueError``, which the client reports as
``VALIDATION_FAILED`` without the original message.
"""


def is_timeout(error: BaseException) -> bool:
    """Whether an exception from the network layer is a timeout."""
    if isinstance(error, (socket.timeout, TimeoutError)):
        return True
    return isinstance(error, urllib.error.URLError) and isinstance(
        error.reason, (socket.timeout, TimeoutError)
    )


class _RedirectHandler(urllib.request.HTTPRedirectHandler):
    """Follows redirects, but never sends the API key to another host or from https to http."""

    def redirect_request(
        self,
        req: urllib.request.Request,
        fp: IO[bytes],
        code: int,
        msg: str,
        headers: http.client.HTTPMessage,
        newurl: str,
    ) -> Optional[urllib.request.Request]:
        new = super().redirect_request(req, fp, code, msg, headers, newurl)
        if new is not None:
            old, target = urllib.parse.urlsplit(req.full_url), urllib.parse.urlsplit(new.full_url)
            downgraded = old.scheme == "https" and target.scheme != "https"
            if downgraded or old.netloc.lower() != target.netloc.lower():
                new.remove_header("Authorization")
        return new


_OPENER = urllib.request.build_opener(_RedirectHandler)


def urllib_transport(request: HTTPRequest) -> HTTPResponse:
    """The default transport, built on :mod:`urllib.request`.

    ``request.timeout`` applies to connecting and to each read of the response, so a stalled
    connection raises ``TIMEOUT``. Redirects are followed, without the ``Authorization`` header
    when they lead to another host or from https to http. Error messages leave out the URL's query
    string, which for a signed download link holds the signature.

    Raises:
        PageRushConnectionError: The request failed on the network or timed out.
        ValueError: The URL or a header value is invalid; nothing was sent.
    """
    req = urllib.request.Request(
        request.url, data=request.body, headers=request.headers, method=request.method
    )
    try:
        try:
            with _OPENER.open(req, timeout=request.timeout) as resp:
                body = resp.read()
                headers = {k.lower(): v for k, v in resp.headers.items()}
                return HTTPResponse(status=resp.status, headers=headers, body=body)
        except urllib.error.HTTPError as err:
            try:
                body = err.read()
            finally:
                err.close()
            headers = {k.lower(): v for k, v in (err.headers or {}).items()}
            return HTTPResponse(status=err.code, headers=headers, body=body)
    except http.client.InvalidURL:
        raise ValueError(f"Invalid URL: {redact_url(request.url)}") from None
    except (OSError, http.client.HTTPException) as err:
        if is_timeout(err):
            raise PageRushConnectionError("TIMEOUT", f"Request timed out after {request.timeout}s") from err
        reason = err.reason if isinstance(err, urllib.error.URLError) else err
        raise PageRushConnectionError(
            "CONNECTION_ERROR", f"Could not reach {redact_url(request.url)}: {reason}"
        ) from err
