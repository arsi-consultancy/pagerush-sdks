from __future__ import annotations

import json
from typing import Any, Dict, List, Optional, Union

import pytest

import pagerush
from pagerush import HTTPRequest, HTTPResponse


def json_response(status: int, body: Any, headers: Optional[Dict[str, str]] = None) -> HTTPResponse:
    content_type = "application/problem+json" if status >= 400 else "application/json"
    return HTTPResponse(
        status=status,
        headers={"Content-Type": content_type, **(headers or {})},
        body=json.dumps(body).encode(),
    )


Reply = Union[HTTPResponse, BaseException]


class FakeTransport:
    """Returns queued replies in order and records every request."""

    def __init__(self, *replies: Reply) -> None:
        self.replies: List[Reply] = list(replies)
        self.requests: List[HTTPRequest] = []

    def __call__(self, request: HTTPRequest) -> HTTPResponse:
        self.requests.append(request)
        if not self.replies:
            raise AssertionError(f"unexpected request {request.method} {request.url}")
        reply = self.replies.pop(0)
        if isinstance(reply, BaseException):
            raise reply
        return reply

    @property
    def last(self) -> HTTPRequest:
        return self.requests[-1]

    def json_body(self, index: int = -1) -> Any:
        body = self.requests[index].body
        assert body is not None
        return json.loads(body)


class Sleeps(list):  # type: ignore[type-arg]
    def __call__(self, seconds: float) -> None:
        self.append(seconds)


@pytest.fixture
def sleeps() -> Sleeps:
    return Sleeps()


@pytest.fixture
def make_client(sleeps: Sleeps, monkeypatch: pytest.MonkeyPatch) -> Any:
    monkeypatch.delenv("PAGERUSH_API_KEY", raising=False)
    monkeypatch.delenv("PAGERUSH_BASE_URL", raising=False)

    def make(*replies: Reply, **kwargs: Any) -> tuple[pagerush.PageRush, FakeTransport]:
        transport = FakeTransport(*replies)
        client = pagerush.PageRush(
            api_key=kwargs.pop("api_key", "pk_test_123"),
            base_url=kwargs.pop("base_url", "https://api.test"),
            transport=transport,
            sleep=sleeps,
            **kwargs,
        )
        return client, transport

    return make
