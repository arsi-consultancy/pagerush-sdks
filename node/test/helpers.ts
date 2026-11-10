import { PageRush, type PageRushOptions } from "../src";

/** A request the mock `fetch` received. */
export interface RecordedCall {
  url: string;
  method: string;
  headers: Record<string, string>;
  body: string | undefined;
  signal: AbortSignal | undefined;
  redirect: RequestRedirect | undefined;
}

/** What the mock answers with: a Response, a function making one, or an Error to throw. */
export type MockReply = Response | Error | ((call: RecordedCall) => Response | Promise<Response>);

/** A `fetch` that answers from a queue and records every call. */
export function mockFetch(replies: MockReply[]) {
  const calls: RecordedCall[] = [];
  const queue = [...replies];
  const fetch = async (input: RequestInfo | URL, init: RequestInit = {}): Promise<Response> => {
    const headers: Record<string, string> = {};
    new Headers(init.headers).forEach((v, k) => {
      headers[k] = v;
    });
    const body = init.body instanceof Uint8Array ? new TextDecoder().decode(init.body) : undefined;
    const call: RecordedCall = {
      url: String(input),
      method: init.method ?? "GET",
      headers,
      body,
      signal: init.signal ?? undefined,
      redirect: init.redirect,
    };
    calls.push(call);
    const reply = queue.shift();
    if (reply === undefined) throw new Error(`Unexpected request: ${call.method} ${call.url}`);
    if (reply instanceof Error) throw reply;
    return typeof reply === "function" ? reply(call) : reply;
  };
  return { fetch: fetch as typeof globalThis.fetch, calls };
}

/** A sleep that resolves at once and records the requested delays. */
export function mockSleep() {
  const delays: number[] = [];
  const sleep = async (ms: number) => {
    delays.push(ms);
  };
  return { sleep, delays };
}

export const TEST_KEY = "pk_test_123";
export const BASE = "https://api.example.test";

/** A client wired to a mock fetch and an instant sleep. */
export function setup(replies: MockReply[], options: PageRushOptions = {}) {
  const { fetch, calls } = mockFetch(replies);
  const { sleep, delays } = mockSleep();
  const client = new PageRush({ apiKey: TEST_KEY, baseUrl: BASE, fetch, sleep, ...options });
  return { client, calls, delays };
}

export function json(body: unknown, status = 200, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json", ...headers },
  });
}

export function problem(
  status: number,
  code: string,
  extra: Record<string, unknown> = {},
  headers: Record<string, string> = {},
): Response {
  return new Response(JSON.stringify({ type: "about:blank", title: code, status, code, ...extra }), {
    status,
    headers: { "Content-Type": "application/problem+json", ...headers },
  });
}

export const template = {
  id: "tpl_1",
  name: "Invoice",
  live_version: 1,
  latest_version: 1,
  created_at: "2026-01-01T00:00:00.000Z",
  updated_at: "2026-01-01T00:00:00.000Z",
};

export const version = {
  version: 1,
  source: "Hello",
  document: null,
  files: [],
  sample_data: null,
  typst_version: "0.14.0",
  created_at: "2026-01-01T00:00:00.000Z",
};

export function job(overrides: Record<string, unknown> = {}) {
  return {
    id: "job_1",
    status: "queued",
    mode: "async",
    region: "us",
    template_id: "tpl_1",
    template_version: 3,
    engine: null,
    bytes: null,
    duration_ms: null,
    error: null,
    delivery: null,
    metadata: null,
    download_url: null,
    delete_after_download: false,
    created_at: "2026-01-01T00:00:00.000Z",
    completed_at: null,
    ...overrides,
  };
}

export const PDF_BYTES = new TextEncoder().encode("%PDF-1.7\n%%EOF\n");
