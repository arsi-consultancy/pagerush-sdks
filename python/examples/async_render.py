"""Render with prefer_async and handle both outcomes.

The API renders straight away and usually returns the PDF. If that attempt hits a temporary capacity
problem, prefer_async makes it queue the job (HTTP 202) instead of failing with 503; this program then
waits for the job and downloads the PDF.

Run: PAGERUSH_API_KEY=pk_live_... python examples/async_render.py <template_id>
"""

import sys

import pagerush


def main(template_id: str) -> None:
    client = pagerush.PageRush()

    result = client.templates.render(
        template_id,
        data={"name": "World"},
        prefer_async=True,  # Prefer: respond-async: queue instead of failing with 503 when busy
        metadata={"source": "async_render.py"},
    )
    if not result.queued:
        result.save("async.pdf")
        print("Rendered immediately; saved async.pdf")
        return

    assert result.job_id is not None
    print("Queued job", result.job_id)
    job = client.jobs.wait(result.job_id, timeout=120, interval=1)
    if job["status"] == "failed":
        error = job["error"]
        print("Render failed:", error["code"] if error else "unknown", error["message"] if error else "")
        sys.exit(1)

    pdf = client.jobs.download(job["id"])
    with open("async.pdf", "wb") as f:
        f.write(pdf)
    print(f"Saved async.pdf ({len(pdf)} bytes)")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit("usage: python examples/async_render.py <template_id>")
    main(sys.argv[1])
