/**
 * Error types thrown by the SDK.
 *
 * @module
 */

/** Error codes the API is known to return. The API may add more; `code` is always a plain string. */
export const ErrorCodes = {
  CONFLICT: "CONFLICT",
  UNAUTHENTICATED: "UNAUTHENTICATED",
  FORBIDDEN: "FORBIDDEN",
  URL_NOT_ALLOWED: "URL_NOT_ALLOWED",
  INVALID_OUTPUT: "INVALID_OUTPUT",
  /** The server rejected the request, or (status 0) the SDK did before sending it. */
  VALIDATION_FAILED: "VALIDATION_FAILED",
  INVALID_API_KEY: "INVALID_API_KEY",
  PROJECT_DISABLED: "PROJECT_DISABLED",
  QUOTA_EXCEEDED: "QUOTA_EXCEEDED",
  CONCURRENCY_LIMIT: "CONCURRENCY_LIMIT",
  RATE_LIMITED: "RATE_LIMITED",
  IDEMPOTENCY_CONFLICT: "IDEMPOTENCY_CONFLICT",
  REQUEST_TOO_LARGE: "REQUEST_TOO_LARGE",
  TEMPLATE_NOT_FOUND: "TEMPLATE_NOT_FOUND",
  TEMPLATE_NOT_PUBLISHED: "TEMPLATE_NOT_PUBLISHED",
  INVALID_DATA: "INVALID_DATA",
  TEMPLATE_COMPILE_FAILED: "TEMPLATE_COMPILE_FAILED",
  RENDER_TIMEOUT: "RENDER_TIMEOUT",
  RENDER_FAILED: "RENDER_FAILED",
  RENDER_UNAVAILABLE: "RENDER_UNAVAILABLE",
  FILE_EXPIRED: "FILE_EXPIRED",
  NOT_FOUND: "NOT_FOUND",
  INTERNAL: "INTERNAL",
  /** SDK-side: an error response's body was not problem+json. */
  UNKNOWN: "UNKNOWN",
  /** SDK-side: a 2xx response the SDK could not use (not JSON, missing fields, or a render that is not a PDF). */
  INVALID_RESPONSE: "INVALID_RESPONSE",
  /** SDK-side: the request could not reach the server. */
  CONNECTION_ERROR: "CONNECTION_ERROR",
  /** SDK-side: an HTTP attempt took longer than the client's `timeout`. */
  TIMEOUT: "TIMEOUT",
  /** SDK-side: {@link Jobs.wait} gave up before the job finished. */
  WAIT_TIMEOUT: "WAIT_TIMEOUT",
} as const;

/** One problem in an error response. On TEMPLATE_COMPILE_FAILED these are Typst diagnostics. */
export interface ErrorDetail {
  path: string;
  message: string;
}

/** Fields of a {@link PageRushError}. */
export interface PageRushErrorInit {
  status: number;
  code: string;
  title?: string;
  detail?: string | null;
  errors?: ErrorDetail[];
  request_id?: string | null;
  retry_after?: number | null;
  type?: string | null;
  cause?: unknown;
}

/**
 * Thrown for every non-2xx API response, and (as {@link PageRushConnectionError}) when the server
 * cannot be reached.
 *
 * @example
 * ```ts
 * try {
 *   await client.templates.render(id, { data });
 * } catch (err) {
 *   if (err instanceof PageRushError && err.code === "TEMPLATE_COMPILE_FAILED") {
 *     for (const e of err.errors) console.error(`${e.path}: ${e.message}`);
 *   }
 * }
 * ```
 */
export class PageRushError extends Error {
  /** HTTP status, or 0 when there was no response. */
  readonly status: number;
  /**
   * Machine-readable code from the response body. `UNKNOWN` if an error body was not problem+json;
   * `INVALID_RESPONSE` if a 2xx response could not be used.
   */
  readonly code: string;
  /** Short summary from the response body. */
  readonly title: string;
  /** Longer explanation from the response body, or null. */
  readonly detail: string | null;
  /** Field problems, or Typst diagnostics on TEMPLATE_COMPILE_FAILED. May be empty. */
  readonly errors: ErrorDetail[];
  /** Request id for support: body `request_id`, else the `X-Request-Id` header, else null. */
  readonly request_id: string | null;
  /** Seconds from the `Retry-After` header, or null. */
  readonly retry_after: number | null;
  /** Problem type URI from the response body, or null. */
  readonly type: string | null;

  constructor(init: PageRushErrorInit) {
    const detail = init.detail ?? null;
    const title = init.title ?? "";
    super(
      `${init.code}: ${detail || title || `HTTP ${init.status}`}`,
      init.cause === undefined ? undefined : { cause: init.cause },
    );
    this.name = "PageRushError";
    this.status = init.status;
    this.code = init.code;
    this.title = title;
    this.detail = detail;
    this.errors = init.errors ?? [];
    this.request_id = init.request_id ?? null;
    this.retry_after = init.retry_after ?? null;
    this.type = init.type ?? null;
  }
}

/**
 * The request never got a response: DNS, TLS or connection failure (`code` CONNECTION_ERROR), or the
 * attempt exceeded the client's `timeout` (`code` TIMEOUT). `status` is 0.
 */
export class PageRushConnectionError extends PageRushError {
  constructor(code: "CONNECTION_ERROR" | "TIMEOUT", message: string, cause?: unknown) {
    super({ status: 0, code, title: message, cause });
    this.name = "PageRushConnectionError";
  }
}

/** Why a webhook failed verification. */
export type WebhookVerificationReason = "header" | "timestamp" | "signature";

/**
 * Thrown by {@link webhooks.verify} and {@link webhooks.constructEvent} when a callback is not
 * authentic. Respond 400 and do not process the body.
 */
export class WebhookVerificationError extends Error {
  /**
   * `header`: a required header is missing or malformed, or a `pdf.failed` body is not a JSON object.
   * `timestamp`: outside the tolerance.
   * `signature`: no v2 signature matches any of your secrets.
   */
  readonly reason: WebhookVerificationReason;

  constructor(reason: WebhookVerificationReason, message: string) {
    super(message);
    this.name = "WebhookVerificationError";
    this.reason = reason;
  }
}
