"""Optional smoke test against the real API. Runs only when PAGERUSH_API_KEY is set."""

from __future__ import annotations

import os

import pytest

import pagerush

pytestmark = pytest.mark.skipif(not os.environ.get("PAGERUSH_API_KEY"), reason="PAGERUSH_API_KEY not set")


def test_live_create_render_delete() -> None:
    client = pagerush.PageRush()
    created = client.templates.create(
        name="pagerush-python smoke test",
        source='#let data = json("data.json")\nHello #data.name',
    )
    template_id = created["template"]["id"]
    try:
        result = client.templates.render(template_id, data={"name": "World"})
        assert result.pdf is not None and result.pdf.startswith(b"%PDF")
    finally:
        client.templates.delete(template_id)
