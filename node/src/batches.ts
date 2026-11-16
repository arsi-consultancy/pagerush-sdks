/** Batch endpoints. @module */

import { PageRushError } from "./errors";
import { type HttpClient, idempotencyKey, invalidResponse, type RequestOptions } from "./http";
import { sleepUnlessAborted } from "./internal";
import type { Batch, CreateBatchOptions, WaitOptions } from "./types";

const enc = encodeURIComponent;

const DONE = new Set(["succeeded", "partial", "failed"]);

/** `client.batches`: render many documents from one template and download them as a ZIP. */
export class Batches {
  /** @internal */
  constructor(private readonly http: HttpClient) {}

  /**
   * Queues a batch: one render of the template per item. The server answers at once (202) with the
   * queued batch; use {@link Batches.wait} and {@link Batches.downloadZip}, or a `callback_url`.
   *
   * An `Idempotency-Key` is always sent (a random UUID if you don't pass one) and reused across
   * retries, so a retry never creates a second batch. The server refuses a batch your quota and
   * credits can't cover (402 `QUOTA_EXCEEDED`) and a fourth unfinished batch (429 `CONCURRENCY_LIMIT`).
   *
   * @example
   * ```ts
   * const batch = await client.batches.create("tpl_123", {
   *   items: customers.map((c) => ({ data: c, filename: `${c.id}.pdf` })),
   * });
   * ```
   */
  async create(templateId: string, params: CreateBatchOptions, options?: RequestOptions): Promise<Batch> {
    const { idempotencyKey: key, ...rest } = params;
    const body: Record<string, unknown> = { items: rest.items };
    if (rest.version !== undefined) body.version = rest.version;
    if (rest.metadata !== undefined) body.metadata = rest.metadata;
    if (rest.delete_after_download !== undefined) body.delete_after_download = rest.delete_after_download;
    if (rest.callback_url !== undefined) body.callback_url = rest.callback_url;
    const headers = { "Idempotency-Key": await idempotencyKey(key) };
    return this.http.object<Batch>(
      {
        method: "POST",
        path: `/v1/templates/${enc(templateId)}/batches`,
        body,
        headers,
        retry: "safe",
        options,
      },
      ["id", "status"],
    );
  }

  /** Gets a batch with its `items`. Throws NOT_FOUND if it doesn't exist in this key's project. */
  get(id: string, options?: RequestOptions): Promise<Batch> {
    return this.http.object<Batch>(
      { method: "GET", path: `/v1/batches/${enc(id)}`, retry: "safe", options },
      ["id", "status"],
    );
  }

  /**
   * Polls a batch until its status is `succeeded`, `partial` or `failed`, and returns it. A partial
   * or failed batch is returned, not thrown: check `batch.status`, `batch.failed` and each item's `error`.
   *
   * @param options - `timeout` (ms, default 600000), `interval` (ms, default 2000) and `signal`.
   *   The per-request `timeout` of each poll stays the client's.
   * @throws {@link PageRushError} with code `WAIT_TIMEOUT` if the batch is still running after `timeout`.
   */
  async wait(id: string, options: WaitOptions = {}): Promise<Batch> {
    const timeout = options.timeout ?? 600_000;
    const interval = options.interval ?? 2_000;
    const deadline = Date.now() + timeout;
    for (;;) {
      const batch = await this.get(id, { signal: options.signal });
      if (DONE.has(batch.status)) return batch;
      const remaining = deadline - Date.now();
      if (remaining <= 0) {
        throw new PageRushError({
          status: 0,
          code: "WAIT_TIMEOUT",
          title: `Batch ${id} was still ${batch.status} after ${timeout} ms`,
        });
      }
      await sleepUnlessAborted(this.http.config.sleep, Math.min(interval, remaining), options.signal);
    }
  }

  /**
   * Downloads a finished batch's PDFs as one ZIP. Unlike a job's signed `download_url`, this link
   * needs the API key, so the SDK sends it.
   *
   * @throws {@link PageRushError} with status 409 and code `CONFLICT` while the batch is still
   *   rendering (`retry_after` says when to try again), status 410 and code `FILE_EXPIRED` when no
   *   PDFs are left, and code `INVALID_RESPONSE` if a 200 response is not `application/zip`.
   */
  async downloadZip(id: string, options?: RequestOptions): Promise<Uint8Array> {
    const res = await this.http.request({
      method: "GET",
      path: `/v1/batches/${enc(id)}/zip`,
      accept: "application/zip",
      retry: "safe",
      options,
    });
    const contentType = res.headers.get("Content-Type") ?? "";
    if (!/^application\/zip\b/i.test(contentType.trim())) {
      throw invalidResponse(res, `Expected application/zip but got ${contentType || "no content type"}`);
    }
    return res.body;
  }
}
