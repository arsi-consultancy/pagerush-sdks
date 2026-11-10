/** HTTP transport with timeouts, retries, redirects and error mapping. @internal @module */

import { type ErrorDetail, PageRushConnectionError, PageRushError } from "./errors";
import { abortReason, sleepUnlessAborted, utf8Decode, utf8Encode } from "./internal";
import { VERSION } from "./version";

/** Per-call options accepted by every API method. */
export interface RequestOptions {
  /** Cancels the call, including any retry wait. The promise rejects with the signal's reason. */
  signal?: AbortSignal | undefined;
  /** Timeout per HTTP attempt for this call, in milliseconds. Overrides the client's `timeout`. */
  timeout?: number | undefined;
  /** Retries after the first attempt for this call. Overrides the client's `maxRetries`. */
  maxRetries?: number | undefined;
}

/**
 * Which failures a request may be retried on.
 * - `safe`: GET and DELETE: connection errors, timeouts, 429, 502, 503, 504.
 * - `render`: render POSTs, which always carry an idempotency key: as `safe`, plus 409 with code
 *   `CONFLICT` (the server is still rendering an earlier attempt with the same key).
 * - `unsafe`: other POST/PATCH: only 429 and 503, which the server sends before doing anything.
 */
export type RetryPolicy = "safe" | "render" | "unsafe";

/** A sleep that resolves after `ms` and should stop early, clearing its timer, when `signal` aborts. */
export type Sleep = (ms: number, signal?: AbortSignal) => Promise<void>;

/** Transport settings. The API key is held separately, in a private field of {@link HttpClient}. */
export interface HttpConfig {
  baseUrl: string;
  timeout: number;
  maxRetries: number;
  fetch: typeof fetch;
  sleep: Sleep;
}

export interface HttpRequest {
  method: "GET" | "POST" | "PATCH" | "DELETE";
  /** Path below the base URL (starting with `/`), or an absolute URL. */
  path: string;
  query?: Record<string, string | number | undefined> | undefined;
  body?: unknown;
  headers?: Record<string, string> | undefined;
  accept?: string | undefined;
  retry: RetryPolicy;
  /** Send the Authorization header. Default true. Never set for signed download links. */
  auth?: boolean | undefined;
  options?: RequestOptions | undefined;
}

export interface HttpResponse {
  status: number;
  headers: Headers;
  body: Uint8Array;
}

const SAFE_STATUSES = new Set([429, 502, 503, 504]);
const UNSAFE_STATUSES = new Set([429, 503]);
const MAX_RETRY_AFTER_S = 30;
const MAX_REDIRECTS = 5;
const REDIRECT_STATUSES = new Set([301, 302, 303, 307, 308]);

/**
 * Seconds from a Retry-After header (delta-seconds or HTTP date), or null if absent or unparseable.
 * A very long value may be `Infinity`; callers cap the wait.
 */
export function parseRetryAfter(value: string | null, now = Date.now()): number | null {
  const v = value?.trim();
  if (!v) return null;
  if (/^\d+(\.\d+)?$/.test(v)) return Number(v);
  // An HTTP date always names the weekday and month; this also stops Date.parse reading "-1" as a year.
  if (!/[a-z]/i.test(v)) return null;
  const date = Date.parse(v);
  if (Number.isNaN(date)) return null;
  return Math.max(0, (date - now) / 1000);
}

/** Backoff before retry `n` (1-based), in ms: min(8, 0.5 * 2^(n-1)) s with ±25 % jitter. */
export function backoffMs(n: number, random: () => number = Math.random): number {
  const base = Math.min(8, 0.5 * 2 ** (n - 1));
  return base * (0.75 + random() * 0.5) * 1000;
}

/** A URL without its query string or fragment, safe to put in messages (signed links carry secrets there). */
export function redactUrl(url: string): string {
  try {
    const u = new URL(url);
    return `${u.origin}${u.pathname}`;
  } catch {
    return "(invalid URL)";
  }
}

/** A {@link PageRushError} with code `VALIDATION_FAILED` and status 0, raised before anything is sent. */
export function validationError(title: string, cause?: unknown): PageRushError {
  return new PageRushError({ status: 0, code: "VALIDATION_FAILED", title, cause });
}

const LONE_SURROGATE = /[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/;

/**
 * Serialises a request body. Unlike plain `JSON.stringify`, values JSON cannot represent (NaN,
 * Infinity, BigInt, functions, symbols, strings with unpaired surrogates, cycles) raise
 * `VALIDATION_FAILED` instead of being silently changed or throwing a raw exception. `undefined`
 * properties are left out, as usual.
 */
export function encodeJson(value: unknown): string {
  const fail = (what: string, key: string) =>
    validationError(`Request body can't be sent as JSON: ${what}${key ? ` at "${key}"` : ""}`);
  let text: string;
  try {
    text = JSON.stringify(value, function (this: unknown, key: string, v: unknown) {
      if (LONE_SURROGATE.test(key)) throw fail("a key is not valid UTF-8", "");
      switch (typeof v) {
        case "number":
          if (!Number.isFinite(v)) throw fail(String(v), key);
          break;
        case "bigint":
        case "function":
        case "symbol":
          throw fail(`a ${typeof v}`, key);
        case "string":
          if (LONE_SURROGATE.test(v)) throw fail("a string that is not valid UTF-8", key);
          break;
      }
      return v;
    });
  } catch (err) {
    if (err instanceof PageRushError) throw err;
    throw validationError("Request body can't be sent as JSON (it may contain a cycle)", err);
  }
  return text;
}

export class HttpClient {
  readonly #apiKey: string;

  constructor(
    apiKey: string,
    readonly config: HttpConfig,
  ) {
    this.#apiKey = apiKey;
  }

  /** Sends a request, retrying per the policy, and throws {@link PageRushError} on non-2xx. */
  async request(req: HttpRequest): Promise<HttpResponse> {
    const url = this.url(req.path, req.query);
    const headers: Record<string, string> = {
      "User-Agent": `pagerush-node/${VERSION}`,
      Accept: req.accept ?? "application/json",
      ...req.headers,
    };
    if (req.auth !== false) headers.Authorization = `Bearer ${this.#apiKey}`;
    let body: Uint8Array<ArrayBuffer> | undefined;
    if (req.body !== undefined) {
      headers["Content-Type"] = "application/json";
      body = utf8Encode(encodeJson(req.body));
    }
    const signal = req.options?.signal;
    const timeout = req.options?.timeout ?? this.config.timeout;
    const maxRetries = req.options?.maxRetries ?? this.config.maxRetries;
    const statuses = req.retry === "unsafe" ? UNSAFE_STATUSES : SAFE_STATUSES;

    for (let retries = 0; ; retries++) {
      const canRetry = retries < maxRetries;
      let res: HttpResponse;
      try {
        res = await this.attempt(url, req.method, headers, body, timeout, signal);
      } catch (err) {
        if (canRetry && req.retry !== "unsafe" && err instanceof PageRushConnectionError) {
          await sleepUnlessAborted(this.config.sleep, backoffMs(retries + 1), signal);
          continue;
        }
        throw err;
      }
      if (res.status >= 200 && res.status < 300) return res;
      const error = errorFromResponse(res);
      const retryable =
        statuses.has(res.status) ||
        (req.retry === "render" && res.status === 409 && error.code === "CONFLICT");
      if (!canRetry || !retryable) throw error;
      const delay =
        error.retry_after !== null
          ? Math.min(error.retry_after, MAX_RETRY_AFTER_S) * 1000
          : backoffMs(retries + 1);
      await sleepUnlessAborted(this.config.sleep, delay, signal);
    }
  }

  /**
   * Sends a request whose response is a JSON object with all of `fields`.
   *
   * @throws {@link PageRushError} with code `INVALID_RESPONSE` if the body is not such an object.
   */
  async object<T>(req: HttpRequest, fields: readonly string[]): Promise<T> {
    const res = await this.request(req);
    const body = parseJsonBody<unknown>(res);
    const missing = isObject(body) ? fields.filter((f) => !(f in body)) : fields;
    if (!isObject(body) || missing.length > 0) {
      throw invalidResponse(res, `Expected a JSON object with ${fields.map((f) => `"${f}"`).join(", ")}`);
    }
    return body as T;
  }

  /**
   * Sends a request whose response wraps an object in `{ [key]: ... }`, and returns that object.
   *
   * @throws {@link PageRushError} with code `INVALID_RESPONSE` if the wrapper is missing.
   */
  async unwrap<T>(req: HttpRequest, key: string): Promise<T> {
    return unwrapJsonBody<T>(await this.request(req), key);
  }

  /** Keeps the transport's settings, and never the API key, out of `console.log` output. */
  [Symbol.for("nodejs.util.inspect.custom")](): string {
    return `HttpClient { baseUrl: '${this.config.baseUrl}' }`;
  }

  /** Keeps the API key out of `JSON.stringify`. */
  toJSON(): { baseUrl: string } {
    return { baseUrl: this.config.baseUrl };
  }

  private url(path: string, query?: HttpRequest["query"]): string {
    const url = /^https?:\/\//i.test(path) ? new URL(path) : new URL(this.config.baseUrl + path);
    for (const [k, v] of Object.entries(query ?? {})) {
      if (v !== undefined) url.searchParams.set(k, String(v));
    }
    return url.toString();
  }

  /**
   * One HTTP attempt, following redirects. The timeout covers redirects and reading the whole body.
   * Redirects are followed by hand so the Authorization header is never sent to another origin or
   * over plain http.
   */
  private async attempt(
    url: string,
    method: string,
    headers: Record<string, string>,
    body: Uint8Array<ArrayBuffer> | undefined,
    timeout: number,
    signal: AbortSignal | undefined,
  ): Promise<HttpResponse> {
    if (signal?.aborted) throw abortReason(signal);
    const controller = new AbortController();
    let timedOut = false;
    const timer = setTimeout(() => {
      timedOut = true;
      controller.abort();
    }, timeout);
    const onAbort = () => controller.abort();
    signal?.addEventListener("abort", onAbort, { once: true });
    const origin = new URL(url).origin;
    try {
      let current = url;
      let currentMethod = method;
      let currentBody = body;
      let currentHeaders = headers;
      for (let redirects = 0; ; redirects++) {
        const init: RequestInit = {
          method: currentMethod,
          headers: currentHeaders,
          signal: controller.signal,
          redirect: "manual",
        };
        if (currentBody !== undefined) init.body = currentBody;
        const res = await this.config.fetch(current, init);
        const location = res.headers.get("Location");
        if (REDIRECT_STATUSES.has(res.status) && location !== null && redirects < MAX_REDIRECTS) {
          await res.body?.cancel();
          const next = new URL(location, current);
          if (next.protocol !== "https:" && next.protocol !== "http:") break;
          if (next.origin !== origin || next.protocol !== "https:") {
            const { Authorization: _dropped, ...rest } = currentHeaders;
            currentHeaders = rest;
          }
          // 303, and 301/302 after a POST, switch to GET without a body, as browsers do.
          if (
            res.status === 303 ||
            ((res.status === 301 || res.status === 302) && currentMethod === "POST")
          ) {
            currentMethod = "GET";
            currentBody = undefined;
            const { "Content-Type": _type, ...rest } = currentHeaders;
            currentHeaders = rest;
          }
          current = next.toString();
          continue;
        }
        const bytes = new Uint8Array(await res.arrayBuffer());
        return { status: res.status, headers: res.headers, body: bytes };
      }
      throw new PageRushConnectionError("CONNECTION_ERROR", `Redirect to an unsupported URL from ${origin}`);
    } catch (err) {
      if (signal?.aborted) throw abortReason(signal);
      if (err instanceof PageRushConnectionError) throw err;
      if (timedOut) {
        throw new PageRushConnectionError(
          "TIMEOUT",
          `Request to ${origin} timed out after ${timeout} ms`,
          err,
        );
      }
      // The underlying message is left out on purpose: some runtimes quote header values in it.
      // The original error stays available as `cause`.
      throw new PageRushConnectionError("CONNECTION_ERROR", `Could not connect to ${origin}`, err);
    } finally {
      clearTimeout(timer);
      signal?.removeEventListener("abort", onAbort);
    }
  }
}

/** A {@link PageRushError} with code `INVALID_RESPONSE` for a 2xx response the SDK cannot use. */
export function invalidResponse(res: HttpResponse, title: string, cause?: unknown): PageRushError {
  return new PageRushError({
    status: res.status,
    code: "INVALID_RESPONSE",
    title,
    request_id: res.headers.get("X-Request-Id"),
    cause,
  });
}

/** Parses a 2xx JSON body; throws `INVALID_RESPONSE` if it isn't JSON. */
export function parseJsonBody<T>(res: HttpResponse): T {
  try {
    return JSON.parse(utf8Decode(res.body)) as T;
  } catch (err) {
    const type = res.headers.get("Content-Type") ?? "no content type";
    throw invalidResponse(res, `Expected a JSON response but got ${type}`, err);
  }
}

/** Parses a 2xx JSON body of the form `{ [key]: object }` and returns the inner object. */
export function unwrapJsonBody<T>(res: HttpResponse, key: string): T {
  const body = parseJsonBody<unknown>(res);
  const inner = isObject(body) ? body[key] : undefined;
  if (!isObject(inner)) throw invalidResponse(res, `Expected a JSON object with a "${key}" field`);
  return inner as T;
}

/** True for a plain JSON object (not null, not an array). */
export function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/** Maps a non-2xx response to a {@link PageRushError}. */
export function errorFromResponse(res: HttpResponse): PageRushError {
  let problem: Record<string, unknown> | null = null;
  try {
    const parsed: unknown = JSON.parse(utf8Decode(res.body));
    if (isObject(parsed)) problem = parsed;
  } catch {
    // Not JSON: an HTML error page from a proxy, say.
  }
  const str = (v: unknown) => (typeof v === "string" ? v : null);
  const rawErrors = problem?.errors;
  const errors: ErrorDetail[] = Array.isArray(rawErrors)
    ? rawErrors
        .filter((e): e is Record<string, unknown> => !!e && typeof e === "object")
        .map((e) => ({ path: String(e.path ?? ""), message: String(e.message ?? "") }))
    : [];
  return new PageRushError({
    status: res.status,
    code: str(problem?.code) ?? "UNKNOWN",
    title: str(problem?.title) ?? "",
    detail: str(problem?.detail),
    errors,
    request_id: str(problem?.request_id) ?? res.headers.get("X-Request-Id"),
    retry_after: parseRetryAfter(res.headers.get("Retry-After")),
    type: str(problem?.type),
  });
}
