"""A callback_url receiver using only the standard library (http.server).

Run: PAGERUSH_WEBHOOK_SECRET=whsec_... python examples/webhook_receiver.py
Then render with callback_url pointing at a public https URL that forwards to this port.
"""

import os
import re
from http.server import BaseHTTPRequestHandler, HTTPServer
from typing import Set

from pagerush import WebhookVerificationError, webhooks

SECRET = os.environ.get("PAGERUSH_WEBHOOK_SECRET", "")
PORT = int(os.environ.get("PORT", "8000"))

# Delivery is at-least-once. Use a database in production; a set is enough for a demo.
handled_jobs: Set[str] = set()


# Job ids are checked before they are used in a file name.
JOB_ID = re.compile(r"[A-Za-z0-9_-]{1,64}")


class Handler(BaseHTTPRequestHandler):
    def do_POST(self) -> None:
        # Read the raw bytes. Never parse and re-serialise the body before verifying it.
        raw_body = self.rfile.read(int(self.headers.get("Content-Length", "0")))
        try:
            event = webhooks.construct_event(raw_body, self.headers, SECRET)
        except WebhookVerificationError as err:
            self.send_response(400)
            self.end_headers()
            self.wfile.write(f"invalid signature ({err.reason})".encode())
            return
        if not JOB_ID.fullmatch(event.job_id):
            self.send_response(400)
            self.end_headers()
            self.wfile.write(b"unexpected job id")
            return

        if event.job_id not in handled_jobs:
            try:
                if event.event == "pdf.succeeded" and event.pdf is not None:
                    path = f"{event.job_id}.pdf"
                    with open(path, "wb") as f:
                        f.write(event.pdf)
                    print("Saved", path, "metadata:", event.metadata)
                elif event.event == "pdf.failed" and event.failure is not None:
                    print("Job failed:", event.job_id, event.failure.get("error"))
            except OSError as err:
                # Not recorded as handled, so PageRush delivers it again later.
                print("Could not handle", event.job_id, err)
                self.send_response(500)
                self.end_headers()
                return
            # Record the job only after the work succeeded.
            handled_jobs.add(event.job_id)

        self.send_response(200)
        self.end_headers()


if __name__ == "__main__":
    if not SECRET:
        raise SystemExit("Set PAGERUSH_WEBHOOK_SECRET")
    print(f"Listening on http://127.0.0.1:{PORT}")
    HTTPServer(("", PORT), Handler).serve_forever()
