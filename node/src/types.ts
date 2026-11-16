/**
 * Request and response shapes for the PageRush `/v1` API. Field names are the API's own
 * snake_case names, exactly as they appear on the wire.
 *
 * @module
 */

/** Any JSON value. */
export type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue };

/** A file in a template project other than `main.typ`: another `.typ` module, an image, a font, data. */
export interface TemplateFile {
  /** Relative path from `main.typ`, e.g. `parts/header.typ` or `logo.png`. No leading `/`, no `..`. */
  path: string;
  /** File contents: plain text for `utf8`, base64 for `base64`. */
  content: string;
  /** `utf8` for text files, `base64` for binary files (images, fonts). The API defaults to `utf8`. */
  encoding?: "utf8" | "base64" | undefined;
}

/** One entry in {@link Template.versions}. */
export interface TemplateVersionSummary {
  version: number;
  /** True for the version renders use by default. */
  live: boolean;
  /** Stored size of the source and files, in bytes. */
  bytes: number;
  file_count: number;
  has_sample_data: boolean;
  /** Typst release this version renders with. */
  typst_version: string;
  created_at: string;
}

/** A template: a named, versioned Typst project. */
export interface Template {
  id: string;
  name: string;
  /** The version renders use unless they ask for one. Null until a version is published. */
  live_version: number | null;
  latest_version: number;
  /** ISO 8601 timestamp. */
  created_at: string;
  /** ISO 8601 timestamp. */
  updated_at: string;
  /** Version history, newest first. Only present on {@link Templates.get}. */
  versions?: TemplateVersionSummary[] | undefined;
}

/** One version of a template: its source and files. */
export interface TemplateVersion {
  version: number;
  /** The version's `main.typ`. */
  source: string;
  /** The visual-editor document this version was made from, or null. */
  document: JsonValue | null;
  files: TemplateFile[];
  /** Example data for dashboard test renders, or null. */
  sample_data: JsonValue | null;
  /** Typst release this version renders with. */
  typst_version: string;
  created_at: string;
}

/** Returned by {@link Templates.create} and {@link Templates.createVersion}. */
export interface TemplateWithVersion {
  template: Template;
  version: TemplateVersion;
}

/** One page of {@link Templates.list}. */
export interface TemplatePage {
  items: Template[];
  /** Pass as `cursor` to get the next page. Null on the last page. */
  next_cursor: string | null;
}

/** Parameters for {@link Templates.list}. */
export interface ListTemplatesParams {
  /** Page size, 1-100. The API defaults to 50. */
  limit?: number | undefined;
  /** `next_cursor` from the previous page. */
  cursor?: string | undefined;
}

/** Fields shared by template creation and new versions. Send `source` or `document`, not both. */
export interface TemplateContent {
  /** The template's `main.typ`. Send this or `document`. */
  source?: string | undefined;
  /** A template from the visual editor. Send this or `source`. */
  document?: Record<string, JsonValue> | undefined;
  /** Other files the template imports or reads. Build entries with {@link templateFile}. */
  files?: TemplateFile[] | undefined;
  /** Example data for dashboard test renders. Not used by API renders. */
  sample_data?: JsonValue | null | undefined;
  /** Typst release to render with. Defaults to the server's current release (new templates) or the previous version's (new versions). */
  typst_version?: string | undefined;
}

/** Parameters for {@link Templates.create}. */
export interface CreateTemplateParams extends TemplateContent {
  /** Template name, 1-120 characters. */
  name: string;
}

/** Parameters for {@link Templates.createVersion}. */
export interface CreateVersionParams extends TemplateContent {
  /** Make this version the live one. The API defaults to `true`. */
  publish?: boolean | undefined;
}

/** Job status. Known values are listed; future values come through as plain strings. */
export type JobStatus = "queued" | "rendering" | "succeeded" | "failed" | (string & {});

/** A render job. */
export interface Job {
  id: string;
  status: JobStatus;
  /** `sync` or `async`. */
  mode: "sync" | "async" | (string & {});
  /** Region the job rendered in and its PDF is stored in. */
  region: string;
  /** Null once the template has been deleted. */
  template_id: string | null;
  /** The template version that was rendered. */
  template_version: number | null;
  /** The compiler that produced the PDF. */
  engine: string | null;
  bytes: number | null;
  duration_ms: number | null;
  /** Set when `status` is `failed`. */
  error: { code: string | null; message: string | null } | null;
  /** Callback delivery state, when the render had a `callback_url`. */
  delivery: {
    status: "pending" | "delivered" | "failed" | (string & {});
    attempts: number;
    error: string | null;
  } | null;
  metadata: Record<string, string> | null;
  /**
   * Signed link to the stored PDF, valid for up to 1 hour. Null once the file has expired, or has
   * been delivered or downloaded when `delete_after_download` is set. Use {@link Jobs.download}.
   */
  download_url: string | null;
  delete_after_download: boolean;
  created_at: string;
  completed_at: string | null;
}

/** Options for {@link Templates.render}. */
export interface RenderOptions {
  /** Any JSON value. The template reads it with `#let data = json("data.json")`. */
  data: unknown;
  /** Render this version instead of the live one. */
  version?: number | undefined;
  /** File name for Content-Disposition. Letters, numbers, spaces, `-`, `.`, `_`. */
  filename?: string | undefined;
  /** Up to 20 string pairs (at most 4 KB as JSON), echoed on the job and in the callback. */
  metadata?: Record<string, string> | undefined;
  /** Delete the PDF once you have it instead of keeping it for 24 hours. Defaults to the project setting. */
  delete_after_download?: boolean | undefined;
  /** Queue the render and POST the signed PDF to this https URL when it is ready. */
  callback_url?: string | undefined;
  /**
   * Sent as `Idempotency-Key`: 1-255 characters from `A-Z a-z 0-9 _ - : .`; anything else throws
   * `VALIDATION_FAILED` before the request is sent. If omitted, the SDK generates a random UUID v4 and reuses it across
   * retries of this call, so a retry never renders or charges twice.
   */
  idempotencyKey?: string | undefined;
  /**
   * Send `Prefer: respond-async`. The server still renders straight away, but if that attempt fails
   * for a temporary reason (e.g. no capacity), it queues the render and answers 202 with a job
   * instead of an error. Check {@link RenderResult.queued}.
   */
  preferAsync?: boolean | undefined;
}

/** Result of {@link Templates.render}. */
export interface RenderResult {
  /** True when the server answered 202 and the PDF will be ready later. */
  queued: boolean;
  /** The PDF bytes (200), or null when queued. */
  pdf: Uint8Array | null;
  /** The queued job (202), or null. */
  job: Job | null;
  /** `X-Job-Id` (200) or `job.id` (202). */
  job_id: string;
  /** `X-Template-Version` (200) or `job.template_version` (202). */
  template_version: number | null;
  /** `X-Render-Engine`, or null. */
  engine: string | null;
  /** `X-Page-Count`, or null. */
  page_count: number | null;
  /** `X-Template-Warnings` (number of Typst warnings), or null. */
  warnings: number | null;
  /** `X-Request-Id`, or null. */
  request_id: string | null;
  /** File name from `Content-Disposition`, or null. */
  filename: string | null;
  /**
   * Writes the PDF to a local file (Node, Bun and Deno). Throws if the render was queued.
   */
  save(path: string): Promise<void>;
}

/** Options for {@link Jobs.wait} and {@link Batches.wait}. */
export interface WaitOptions {
  /** Give up after this many milliseconds. Default 120000 for `jobs.wait`, 600000 for `batches.wait`. */
  timeout?: number | undefined;
  /** Poll every this many milliseconds. Default 1000 for `jobs.wait`, 2000 for `batches.wait`. */
  interval?: number | undefined;
  /** Stops waiting (and any request in flight). The promise rejects with the signal's reason. */
  signal?: AbortSignal | undefined;
}

/** One document in a batch: the data for one render. */
export interface BatchItemInput {
  /** Any JSON value. The template reads it with `#let data = json("data.json")`. */
  data: unknown;
  /** File name for this PDF in the ZIP. Letters, numbers, spaces, `-`, `.`, `_`. */
  filename?: string | undefined;
}

/** Options for {@link Batches.create}. */
export interface CreateBatchOptions {
  /** The documents to render: 1-1,000 items. The SDK sends them as one batch; it never splits them. */
  items: BatchItemInput[];
  /** Render this version instead of the live one. */
  version?: number | undefined;
  /** Up to 20 string pairs (at most 4 KB as JSON), echoed on the batch and in the callback. */
  metadata?: Record<string, string> | undefined;
  /** Delete the PDFs once the ZIP has been downloaded instead of keeping them. Defaults to the project setting. */
  delete_after_download?: boolean | undefined;
  /** POST a signed `batch.completed` notice to this https URL when the batch has finished. */
  callback_url?: string | undefined;
  /**
   * Sent as `Idempotency-Key`: 1-255 characters from `A-Z a-z 0-9 _ - : .`; anything else throws
   * `VALIDATION_FAILED` before the request is sent. If omitted, the SDK generates a random UUID v4 and reuses it across
   * retries of this call, so a retry never creates a second batch.
   */
  idempotencyKey?: string | undefined;
}

/**
 * Batch status. `succeeded`, `partial` (some items failed) and `failed` are final. Known values are
 * listed; future values come through as plain strings.
 */
export type BatchStatus = "queued" | "rendering" | "succeeded" | "partial" | "failed" | (string & {});

/** One document of a batch, as returned by {@link Batches.get}. */
export interface BatchItem {
  /** Position in the `items` you sent, from 0. */
  index: number;
  /** The render job for this item. */
  job_id: string;
  status: JobStatus;
  /** The PDF's file name in the ZIP. */
  filename: string | null;
  /** Size of the PDF, once rendered. */
  bytes: number | null;
  /** Renders this item took (counted against your quota). */
  renders: number | null;
  /** Set when `status` is `failed`. */
  error: { code: string | null; message: string | null } | null;
}

/** A batch: many renders of one template, collected into a ZIP. */
export interface Batch {
  id: string;
  status: BatchStatus;
  /** Null once the template has been deleted. */
  template_id: string | null;
  /** The template version every item renders with. */
  template_version: number | null;
  /** Number of items in the batch. */
  item_count: number;
  /** Items rendered so far. */
  succeeded: number;
  /** Items that failed so far. */
  failed: number;
  /**
   * Where the ZIP of finished PDFs is served. It needs your API key: download it with
   * {@link Batches.downloadZip}.
   */
  zip_url: string | null;
  /** When the PDFs are deleted (ISO 8601), or null. */
  expires_at: string | null;
  metadata: Record<string, string> | null;
  delete_after_download: boolean;
  /** Callback delivery state, when the batch had a `callback_url`. */
  delivery: {
    status: "pending" | "delivered" | "failed" | (string & {});
    attempts: number;
    error: string | null;
  } | null;
  created_at: string;
  finished_at: string | null;
  /** The batch's documents. Only present on {@link Batches.get}. */
  items?: BatchItem[] | undefined;
}
