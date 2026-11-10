/** The PageRush client. @module */

import { HttpClient, type Sleep } from "./http";
import { defaultSleep, env } from "./internal";
import { Jobs } from "./jobs";
import { Templates } from "./templates";
import { webhooks } from "./webhooks";

/** Options for {@link PageRush}. */
export interface PageRushOptions {
  /** Project API key (`pk_live_...`). Defaults to the `PAGERUSH_API_KEY` environment variable. */
  apiKey?: string | undefined;
  /** API origin. Defaults to `PAGERUSH_BASE_URL`, else `https://pagerush.dev`. A trailing `/` is removed. */
  baseUrl?: string | undefined;
  /** Timeout per HTTP attempt, in milliseconds. Default 60000. */
  timeout?: number | undefined;
  /** Retries after the first attempt (so up to `maxRetries + 1` attempts). Default 2. */
  maxRetries?: number | undefined;
  /** The `fetch` to use. Defaults to the global `fetch`. Inject one for tests or proxies. */
  fetch?: typeof fetch | undefined;
  /**
   * Waits between retries and between {@link Jobs.wait} polls. Inject one to make tests fast. It
   * receives the call's `AbortSignal`, if any, and should stop early when that aborts.
   */
  sleep?: Sleep | undefined;
}

function isHttpUrl(value: string): boolean {
  try {
    const { protocol } = new URL(value);
    return protocol === "https:" || protocol === "http:";
  } catch {
    return false;
  }
}

/** Default API origin. */
export const DEFAULT_BASE_URL = "https://pagerush.dev";

/**
 * Client for the PageRush `/v1` API.
 *
 * @example
 * ```ts
 * import { PageRush } from "pagerush";
 *
 * const client = new PageRush(); // reads PAGERUSH_API_KEY
 * const { template } = await client.templates.create({
 *   name: "Hello",
 *   source: '#let data = json("data.json")\nHello #data.name',
 * });
 * const result = await client.templates.render(template.id, { data: { name: "Ada" } });
 * await result.save("hello.pdf");
 * ```
 */
export class PageRush {
  /** Create, version, publish and render templates. */
  readonly templates: Templates;
  /** Check on renders and download their PDFs. */
  readonly jobs: Jobs;
  /** Verify and parse callback deliveries. The same functions as the top-level `webhooks` export. */
  readonly webhooks = webhooks;
  /** The resolved base URL, without a trailing slash. */
  readonly baseUrl: string;

  /**
   * @throws TypeError if there is no API key (and `PAGERUSH_API_KEY` is not set), if the key contains
   *   spaces or control characters, or if the base URL is not an http(s) URL.
   */
  constructor(options: PageRushOptions = {}) {
    const apiKey = (options.apiKey ?? env("PAGERUSH_API_KEY") ?? "").trim();
    if (!apiKey) {
      throw new TypeError(
        "PageRush: no API key. Pass { apiKey } or set the PAGERUSH_API_KEY environment variable.",
      );
    }
    // The key goes into a header, so reject anything a header can't carry. Never echo the key.
    if (!/^[\x21-\x7e]+$/.test(apiKey)) {
      throw new TypeError(
        "PageRush: the API key contains spaces or control characters. Check how it is loaded.",
      );
    }
    this.baseUrl = (options.baseUrl ?? env("PAGERUSH_BASE_URL") ?? DEFAULT_BASE_URL).replace(/\/+$/, "");
    if (!isHttpUrl(this.baseUrl)) {
      throw new TypeError(`PageRush: baseUrl must be an http(s) URL, got "${this.baseUrl}"`);
    }
    if (!options.fetch && typeof globalThis.fetch !== "function") {
      throw new Error("PageRush: no global fetch. Use Node 18+ or pass { fetch }.");
    }
    const http = new HttpClient(apiKey, {
      baseUrl: this.baseUrl,
      timeout: options.timeout ?? 60_000,
      maxRetries: options.maxRetries ?? 2,
      // Called through globalThis: Workers and browsers throw "Illegal invocation" on a detached fetch.
      fetch: options.fetch ?? ((input, init) => globalThis.fetch(input, init)),
      sleep: options.sleep ?? defaultSleep,
    });
    this.templates = new Templates(http);
    this.jobs = new Jobs(http);
  }

  /** What `console.log(client)` shows. The API key is never included. */
  [Symbol.for("nodejs.util.inspect.custom")](): string {
    return `PageRush { baseUrl: '${this.baseUrl}' }`;
  }

  /** What `JSON.stringify(client)` produces. The API key is never included. */
  toJSON(): { baseUrl: string } {
    return { baseUrl: this.baseUrl };
  }
}
