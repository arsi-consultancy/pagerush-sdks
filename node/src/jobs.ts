/** Job endpoints. @module */

import { PageRushError } from "./errors";
import { sleepUnlessAborted } from "./internal";
import type { HttpClient, RequestOptions } from "./http";
import type { Job, WaitOptions } from "./types";

const DONE = new Set(["succeeded", "failed"]);

/** `client.jobs`: check on renders and download their PDFs. */
export class Jobs {
  /** @internal */
  constructor(private readonly http: HttpClient) {}

  /** Gets a job. Throws NOT_FOUND if it doesn't exist in this key's project. */
  get(id: string, options?: RequestOptions): Promise<Job> {
    return this.http.object<Job>(
      { method: "GET", path: `/v1/jobs/${encodeURIComponent(id)}`, retry: "safe", options },
      ["id", "status"],
    );
  }

  /**
   * Polls a job until its status is `succeeded` or `failed`, and returns it. A failed job is
   * returned, not thrown: check `job.status` and `job.error`.
   *
   * @param options - `timeout` (ms, default 120000), `interval` (ms, default 1000) and `signal`.
   *   The per-request `timeout` of each poll stays the client's.
   * @throws {@link PageRushError} with code `WAIT_TIMEOUT` if the job is still running after `timeout`.
   */
  async wait(id: string, options: WaitOptions = {}): Promise<Job> {
    const timeout = options.timeout ?? 120_000;
    const interval = options.interval ?? 1_000;
    const deadline = Date.now() + timeout;
    for (;;) {
      const job = await this.get(id, { signal: options.signal });
      if (DONE.has(job.status)) return job;
      const remaining = deadline - Date.now();
      if (remaining <= 0) {
        throw new PageRushError({
          status: 0,
          code: "WAIT_TIMEOUT",
          title: `Job ${id} was still ${job.status} after ${timeout} ms`,
        });
      }
      await sleepUnlessAborted(this.http.config.sleep, Math.min(interval, remaining), options.signal);
    }
  }

  /**
   * Downloads a finished job's PDF through its signed `download_url`. The API key is not sent to that link.
   *
   * @throws {@link PageRushError} with code `FILE_EXPIRED` if the job has no `download_url` (not
   * finished, failed, expired after 24 hours, or already delivered with `delete_after_download`).
   */
  async download(id: string, options?: RequestOptions): Promise<Uint8Array> {
    const job = await this.get(id, options);
    if (!job.download_url) {
      throw new PageRushError({
        status: 0,
        code: "FILE_EXPIRED",
        title: `Job ${id} has no PDF to download (status ${job.status})`,
      });
    }
    const url = new URL(job.download_url, this.http.config.baseUrl).toString();
    const res = await this.http.request({
      method: "GET",
      path: url,
      accept: "application/pdf",
      auth: false,
      retry: "safe",
      options,
    });
    return res.body;
  }
}
