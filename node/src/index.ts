/**
 * Official Node.js / TypeScript SDK for the PageRush PDF API.
 *
 * @packageDocumentation
 */

export { DEFAULT_BASE_URL, PageRush, type PageRushOptions } from "./client";
export {
  type ErrorDetail,
  ErrorCodes,
  PageRushConnectionError,
  PageRushError,
  type PageRushErrorInit,
  WebhookVerificationError,
  type WebhookVerificationReason,
} from "./errors";
export { fileFromPath, TEXT_EXTENSIONS, templateFile } from "./files";
export type { RequestOptions, Sleep } from "./http";
export { Jobs } from "./jobs";
export { Templates } from "./templates";
export type * from "./types";
export { VERSION } from "./version";
export {
  constructEvent,
  type HeadersInput,
  type RawBody,
  type VerifyOptions,
  verify,
  type WebhookEvent,
  type WebhookFailure,
  webhooks,
} from "./webhooks";
