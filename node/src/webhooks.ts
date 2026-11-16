/** Verify and parse `callback_url` deliveries. @module */

import { WebhookVerificationError } from "./errors";
import { toBytes, utf8Encode, webCrypto } from "./internal";
import type { Batch } from "./types";

/** Raw request body: the exact bytes received. */
export type RawBody = Uint8Array | ArrayBuffer | string;

/** Request headers: a Fetch `Headers`, Node's `req.headers`, or any plain object. Lookup is case-insensitive. */
export type HeadersInput =
  { get(name: string): string | null } | Record<string, string | string[] | undefined | null>;

/** Options for {@link verify} and {@link constructEvent}. */
export interface VerifyOptions {
  /** Maximum age (or future skew) of the signature timestamp, in seconds. Default 300. */
  tolerance?: number | undefined;
  /** Current time in Unix seconds. Defaults to the system clock; set it in tests. */
  now?: number | undefined;
}

/** A verified callback, as returned by {@link constructEvent}. Every field comes from signed data. */
export interface WebhookEvent {
  /** `pdf.succeeded`, `pdf.failed` or `batch.completed` (from `X-PageRush-Event`). */
  event: "pdf.succeeded" | "pdf.failed" | "batch.completed" | (string & {});
  /**
   * `X-PageRush-Job-Id`; null for `batch.completed`. The same on every retry: dedupe on it, delivery
   * is at-least-once.
   */
  job_id: string | null;
  /**
   * `X-PageRush-Batch-Id`, for `batch.completed`; otherwise null. The same on every retry: dedupe on it.
   */
  batch_id: string | null;
  /** The body's `batch` (a {@link Batch} without `items`), for `batch.completed`; otherwise null. */
  batch: Batch | null;
  /** Signature timestamp, Unix seconds. */
  timestamp: number;
  /** The PDF, for `pdf.succeeded`; otherwise null. */
  pdf: Uint8Array | null;
  /** The parsed JSON body, for `pdf.failed`; otherwise null. */
  failure: WebhookFailure | null;
  /**
   * The render's or batch's metadata: from `X-PageRush-Metadata` on `pdf.succeeded` and
   * `batch.completed`, from the body on `pdf.failed`. Null if there is none, or if it is not a JSON
   * object of strings.
   */
  metadata: Record<string, string> | null;
}

/** Body of a `pdf.failed` callback. */
export interface WebhookFailure {
  event: "pdf.failed";
  job_id: string;
  template_id: string | null;
  error: { code: string | null; message: string | null };
  metadata: Record<string, string> | null;
  created_at: string;
}

const DEFAULT_TOLERANCE_S = 300;

/** The headers a delivery is verified against. */
interface SignedHeaders {
  t: string;
  v2: string[];
  event: string;
  /** `X-PageRush-Batch-Id` for `batch.completed`, else `X-PageRush-Job-Id`. */
  id: string;
  /** `X-PageRush-Metadata`, or null when absent. */
  metadata: string | null;
}

function headerOf(headers: HeadersInput, name: string): string | null {
  if (typeof (headers as { get?: unknown }).get === "function") {
    return (headers as { get(n: string): string | null }).get(name);
  }
  const lower = name.toLowerCase();
  for (const [k, v] of Object.entries(headers as Record<string, string | string[] | undefined | null>)) {
    if (k.toLowerCase() !== lower || v == null) continue;
    return Array.isArray(v) ? v.join(", ") : v;
  }
  return null;
}

/** The event whose id header is `X-PageRush-Batch-Id`. */
const BATCH_COMPLETED = "batch.completed";

/**
 * Reads the signature, event, id and metadata headers. The id header is `X-PageRush-Batch-Id` for
 * `batch.completed` and `X-PageRush-Job-Id` otherwise; only that one counts. The signature header
 * is comma-separated `key=value` pairs; unknown keys are ignored.
 */
function readSignedHeaders(headers: HeadersInput): SignedHeaders {
  const fail = (what: string) => new WebhookVerificationError("header", what);
  if (typeof headers !== "object" || headers === null) throw fail("Missing request headers");
  const signature = headerOf(headers, "X-PageRush-Signature");
  if (!signature) throw fail("Missing X-PageRush-Signature header");
  const ts: string[] = [];
  const v2: string[] = [];
  for (const raw of signature.split(",")) {
    const pair = raw.trim();
    const eq = pair.indexOf("=");
    if (eq <= 0) continue;
    const key = pair.slice(0, eq).trim();
    const value = pair.slice(eq + 1).trim();
    if (key === "t") ts.push(value);
    else if (key === "v2") v2.push(value);
  }
  if (ts.length !== 1 || !/^\d{1,15}$/.test(ts[0]!) || v2.length === 0) {
    throw fail("Malformed X-PageRush-Signature header");
  }
  const event = headerOf(headers, "X-PageRush-Event");
  if (!event) throw fail("Missing X-PageRush-Event header");
  const idHeader = event === BATCH_COMPLETED ? "X-PageRush-Batch-Id" : "X-PageRush-Job-Id";
  const id = headerOf(headers, idHeader);
  if (!id) throw fail(`Missing ${idHeader} header`);
  return { t: ts[0]!, v2, event, id, metadata: headerOf(headers, "X-PageRush-Metadata") };
}

/**
 * The secrets as a non-empty list of non-empty strings. A missing or empty secret is a setup
 * mistake, not a bad delivery, so it throws a TypeError rather than a verification error.
 */
function secretList(secrets: string | readonly string[]): readonly string[] {
  const list = typeof secrets === "string" ? [secrets] : secrets;
  if (!Array.isArray(list) || list.length === 0 || list.some((s) => typeof s !== "string" || s === "")) {
    throw new TypeError(
      "PageRush webhooks: the webhook secret is missing or empty. Pass your project's secret (e.g. from PAGERUSH_WEBHOOK_SECRET).",
    );
  }
  return list;
}

/** Constant-time string comparison (the length check leaks only the length). */
function safeEqual(a: string, b: string): boolean {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

async function hmacHex(secret: string, message: Uint8Array<ArrayBuffer>): Promise<string> {
  const { subtle } = await webCrypto();
  const key = await subtle.importKey("raw", utf8Encode(secret), { name: "HMAC", hash: "SHA-256" }, false, [
    "sign",
  ]);
  const sig = new Uint8Array(await subtle.sign("HMAC", key, message));
  return Array.from(sig, (b) => b.toString(16).padStart(2, "0")).join("");
}

/** Does the work of {@link verify} and returns the verified headers. */
async function verifyDelivery(
  rawBody: RawBody,
  headers: HeadersInput,
  secrets: string | readonly string[],
  options: VerifyOptions,
): Promise<SignedHeaders> {
  const list = secretList(secrets);
  const signed = readSignedHeaders(headers);
  const tolerance = options.tolerance ?? DEFAULT_TOLERANCE_S;
  const now = options.now ?? Math.floor(Date.now() / 1000);
  if (Math.abs(now - Number(signed.t)) > tolerance) {
    throw new WebhookVerificationError(
      "timestamp",
      "X-PageRush-Signature timestamp is outside the tolerance",
    );
  }
  const body = toBytes(rawBody);
  const prefix = utf8Encode(`${signed.t}\n${signed.event}\n${signed.id}\n${signed.metadata ?? ""}\n`);
  const message = new Uint8Array(prefix.length + body.length);
  message.set(prefix, 0);
  message.set(body, prefix.length);

  let matched = false;
  for (const secret of list) {
    const expected = await hmacHex(secret, message);
    // Check every pair, without stopping early, so timing doesn't reveal which one matched.
    for (const candidate of signed.v2) matched = safeEqual(expected, candidate) || matched;
  }
  if (!matched) {
    throw new WebhookVerificationError("signature", "No X-PageRush-Signature v2 matches the secret");
  }
  return signed;
}

/**
 * Checks that a callback was sent by PageRush and has not been changed. The signature covers the
 * timestamp, the event, id (`X-PageRush-Job-Id`, or `X-PageRush-Batch-Id` for `batch.completed`)
 * and metadata headers, and the raw body.
 *
 * Pass every secret you currently accept (e.g. new and old during a rotation); any match passes.
 *
 * @param rawBody - The exact bytes received. Do not re-serialise parsed JSON: any change breaks the signature.
 * @param headers - The request headers (Fetch `Headers` or Node `req.headers`).
 * @param secrets - Your project's webhook secret, or several.
 * @throws {@link WebhookVerificationError} with `reason` `header` (a required header is missing or
 *   malformed), `timestamp` (outside the tolerance) or `signature` (no match).
 * @throws TypeError if no secret is given or a secret is an empty string.
 *
 * @example
 * ```ts
 * await webhooks.verify(rawBody, req.headers, process.env.PAGERUSH_WEBHOOK_SECRET!);
 * ```
 */
export async function verify(
  rawBody: RawBody,
  headers: HeadersInput,
  secrets: string | readonly string[],
  options: VerifyOptions = {},
): Promise<void> {
  await verifyDelivery(rawBody, headers, secrets, options);
}

/** A plain object whose values are all strings, or null. */
function stringMap(value: unknown): Record<string, string> | null {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return null;
  const entries = Object.entries(value);
  return entries.every(([, v]) => typeof v === "string") ? Object.fromEntries(entries) : null;
}

/** Parses the metadata header (ASCII JSON), or null if it is absent or not an object of strings. */
function parseMetadata(raw: string | null): Record<string, string> | null {
  if (!raw) return null;
  try {
    return stringMap(JSON.parse(raw));
  } catch {
    return null;
  }
}

/**
 * Verifies a callback (see {@link verify}) and parses it. Every field of the result comes from
 * signed data.
 *
 * @param rawBody - The exact bytes received.
 * @param headers - The request headers (Fetch `Headers` or Node `req.headers`).
 * @param secrets - Your project's webhook secret, or several.
 * @throws {@link WebhookVerificationError} if verification fails, or with reason `header` if a
 *   `pdf.failed` body is not a JSON object, or a `batch.completed` body is not a JSON object with a
 *   `batch` object.
 * @throws TypeError if no secret is given or a secret is an empty string.
 *
 * @example
 * ```ts
 * const event = await webhooks.constructEvent(rawBody, req.headers, secret);
 * if (event.event === "pdf.succeeded") await savePdf(event.job_id!, event.pdf!);
 * if (event.event === "batch.completed") await fetchZip(event.batch_id!);
 * ```
 */
export async function constructEvent(
  rawBody: RawBody,
  headers: HeadersInput,
  secrets: string | readonly string[],
  options: VerifyOptions = {},
): Promise<WebhookEvent> {
  const signed = await verifyDelivery(rawBody, headers, secrets, options);
  const body = toBytes(rawBody);
  let pdf: Uint8Array | null = null;
  let failure: WebhookFailure | null = null;
  let batch: Batch | null = null;
  let metadata: Record<string, string> | null = null;
  if (signed.event === "pdf.failed") {
    const parsed = parseJsonObject(body);
    if (!parsed) throw new WebhookVerificationError("header", "pdf.failed body is not a JSON object");
    failure = parsed as unknown as WebhookFailure;
    metadata = stringMap(failure.metadata);
  } else if (signed.event === "pdf.succeeded") {
    pdf = body;
    metadata = parseMetadata(signed.metadata);
  } else if (signed.event === BATCH_COMPLETED) {
    const inner = parseJsonObject(body)?.batch;
    if (typeof inner !== "object" || inner === null || Array.isArray(inner)) {
      throw new WebhookVerificationError(
        "header",
        "batch.completed body is not a JSON object with a batch object",
      );
    }
    batch = inner as Batch;
    metadata = parseMetadata(signed.metadata);
  }
  const isBatch = signed.event === BATCH_COMPLETED;
  return {
    event: signed.event,
    job_id: isBatch ? null : signed.id,
    batch_id: isBatch ? signed.id : null,
    batch,
    timestamp: Number(signed.t),
    pdf,
    failure,
    metadata,
  };
}

/** Parses a UTF-8 JSON body that must be an object; null if it is anything else. */
function parseJsonObject(body: Uint8Array): Record<string, unknown> | null {
  let parsed: unknown;
  try {
    parsed = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(body));
  } catch {
    return null;
  }
  return typeof parsed === "object" && parsed !== null && !Array.isArray(parsed)
    ? (parsed as Record<string, unknown>)
    : null;
}

/**
 * Webhook helpers. Also available as `client.webhooks`; using them does not need an API key.
 */
export const webhooks = { verify, constructEvent } as const;
