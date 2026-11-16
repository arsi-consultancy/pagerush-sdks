import { afterEach, describe, expect, it, vi } from "vitest";
import { type Batch, PageRush, PageRushConnectionError, PageRushError } from "../src";
import {
  BASE,
  batch,
  batchItem,
  json,
  mockFetch,
  PDF_BYTES,
  problem,
  setup,
  TEST_KEY,
  ZIP_BYTES,
} from "./helpers";

const UUID_V4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

const zip = (headers: Record<string, string> = {}) =>
  new Response(ZIP_BYTES, { status: 200, headers: { "Content-Type": "application/zip", ...headers } });

describe("batches.create", () => {
  it("posts the items and options to the template's batches path", async () => {
    const { client, calls } = setup([json(batch(), 202)]);
    const result = await client.batches.create("tpl/1", {
      items: [{ data: { name: "Ada" }, filename: "ada.pdf" }, { data: { name: "Grace" } }],
      version: 2,
      metadata: { run: "nov" },
      delete_after_download: true,
      callback_url: "https://example.com/hook",
    });
    expect(result).toEqual(batch());
    expect(calls[0]!.method).toBe("POST");
    expect(calls[0]!.url).toBe(`${BASE}/v1/templates/tpl%2F1/batches`);
    expect(calls[0]!.headers.authorization).toBe(`Bearer ${TEST_KEY}`);
    expect(calls[0]!.headers["content-type"]).toBe("application/json");
    expect(calls[0]!.headers.accept).toBe("application/json");
    expect(JSON.parse(calls[0]!.body!)).toEqual({
      items: [{ data: { name: "Ada" }, filename: "ada.pdf" }, { data: { name: "Grace" } }],
      version: 2,
      metadata: { run: "nov" },
      delete_after_download: true,
      callback_url: "https://example.com/hook",
    });
  });

  it("sends only the items when no options are given, and no SDK-only fields", async () => {
    const { client, calls } = setup([json(batch(), 202)]);
    await client.batches.create("tpl_1", { items: [{ data: 1 }], idempotencyKey: "run-1" });
    expect(JSON.parse(calls[0]!.body!)).toEqual({ items: [{ data: 1 }] });
    expect(calls[0]!.headers["idempotency-key"]).toBe("run-1");
  });

  it("generates an idempotency key and reuses it across retries", async () => {
    const { client, calls, delays } = setup([
      problem(503, "RENDER_UNAVAILABLE", {}, { "Retry-After": "1" }),
      new TypeError("fetch failed"),
      json(batch(), 202),
    ]);
    await expect(client.batches.create("tpl_1", { items: [{ data: {} }] })).resolves.toMatchObject({
      id: "bat_1",
    });
    expect(calls).toHaveLength(3);
    expect(delays[0]).toBe(1000);
    const key = calls[0]!.headers["idempotency-key"];
    expect(key).toMatch(UUID_V4);
    expect(calls[1]!.headers["idempotency-key"]).toBe(key);
    expect(calls[2]!.headers["idempotency-key"]).toBe(key);
  });

  it("uses a new key for each call", async () => {
    const { client, calls } = setup([json(batch(), 202), json(batch(), 202)]);
    await client.batches.create("tpl_1", { items: [{ data: {} }] });
    await client.batches.create("tpl_1", { items: [{ data: {} }] });
    expect(calls[0]!.headers["idempotency-key"]).not.toBe(calls[1]!.headers["idempotency-key"]);
  });

  it.each([429, 502, 504])("retries on %i", async (status) => {
    const { client, calls } = setup([problem(status, "X"), json(batch(), 202)]);
    await client.batches.create("tpl_1", { items: [{ data: {} }] });
    expect(calls).toHaveLength(2);
  });

  it.each([
    ["IDEMPOTENCY_CONFLICT", 409, "IDEMPOTENCY_CONFLICT"],
    ["409 CONFLICT", 409, "CONFLICT"],
    ["QUOTA_EXCEEDED", 402, "QUOTA_EXCEEDED"],
    ["VALIDATION_FAILED", 422, "VALIDATION_FAILED"],
  ])("never retries %s", async (_name, status, code) => {
    const { client, calls } = setup([problem(status, code)]);
    await expect(client.batches.create("tpl_1", { items: [{ data: {} }] })).rejects.toMatchObject({
      status,
      code,
    });
    expect(calls).toHaveLength(1);
  });

  it("stops after maxRetries", async () => {
    const { client, calls } = setup([
      new TypeError("fetch failed"),
      new TypeError("fetch failed"),
      new TypeError("fetch failed"),
    ]);
    const err = await client.batches.create("tpl_1", { items: [{ data: {} }] }).catch((e: unknown) => e);
    expect(err).toBeInstanceOf(PageRushConnectionError);
    expect(calls).toHaveLength(3);
  });

  it("rejects a bad idempotency key before sending anything", async () => {
    const { client, calls } = setup([]);
    await expect(
      client.batches.create("tpl_1", { items: [{ data: {} }], idempotencyKey: "has space" }),
    ).rejects.toMatchObject({ status: 0, code: "VALIDATION_FAILED" });
    expect(calls).toHaveLength(0);
  });

  it("rejects item data JSON can't represent before sending anything", async () => {
    const { client, calls } = setup([]);
    await expect(
      client.batches.create("tpl_1", { items: [{ data: { total: Number.NaN } }] }),
    ).rejects.toMatchObject({ status: 0, code: "VALIDATION_FAILED" });
    expect(calls).toHaveLength(0);
  });

  it("throws INVALID_RESPONSE for a 202 that isn't a batch", async () => {
    const { client } = setup([json({ ok: true }, 202)]);
    await expect(client.batches.create("tpl_1", { items: [{ data: {} }] })).rejects.toMatchObject({
      status: 202,
      code: "INVALID_RESPONSE",
    });
  });
});

describe("batches.get", () => {
  it("gets a batch with its items", async () => {
    const body = batch({
      status: "partial",
      items: [batchItem(), batchItem({ index: 1, status: "failed" })],
    });
    const { client, calls } = setup([json(body)]);
    const result: Batch = await client.batches.get("bat_1");
    expect(result).toEqual(body);
    expect(result.items).toHaveLength(2);
    expect(calls[0]!.method).toBe("GET");
    expect(calls[0]!.url).toBe(`${BASE}/v1/batches/bat_1`);
    expect(calls[0]!.body).toBeUndefined();
  });

  it("URL-encodes the id", async () => {
    const { client, calls } = setup([json(batch())]);
    await client.batches.get("a/b?c");
    expect(calls[0]!.url).toBe(`${BASE}/v1/batches/a%2Fb%3Fc`);
  });

  it("throws NOT_FOUND", async () => {
    const { client } = setup([problem(404, "NOT_FOUND")]);
    await expect(client.batches.get("bat_x")).rejects.toMatchObject({ status: 404, code: "NOT_FOUND" });
  });
});

describe("batches.wait", () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("polls until the batch has finished, every 2 seconds by default", async () => {
    const { client, calls, delays } = setup([
      json(batch()),
      json(batch({ status: "rendering" })),
      json(batch({ status: "succeeded", succeeded: 2 })),
    ]);
    const done = await client.batches.wait("bat_1");
    expect(done.status).toBe("succeeded");
    expect(calls).toHaveLength(3);
    expect(delays).toEqual([2000, 2000]);
  });

  it.each(["partial", "failed"])("returns a %s batch instead of throwing", async (status) => {
    const { client } = setup([json(batch({ status }))]);
    await expect(client.batches.wait("bat_1")).resolves.toMatchObject({ status });
  });

  it("throws WAIT_TIMEOUT after the timeout, 600 seconds by default", async () => {
    let now = 1_000_000;
    vi.spyOn(Date, "now").mockImplementation(() => now);
    const { fetch, calls } = mockFetch(
      Array.from({ length: 400 }, () => json(batch({ status: "rendering" }))),
    );
    const sleep = async (ms: number) => {
      now += ms;
    };
    const client = new PageRush({ apiKey: TEST_KEY, baseUrl: BASE, fetch, sleep });
    const err = (await client.batches.wait("bat_1").catch((e: unknown) => e)) as PageRushError | undefined;
    expect(err).toBeInstanceOf(PageRushError);
    expect(err).toMatchObject({ status: 0, code: "WAIT_TIMEOUT" });
    expect(now).toBe(1_600_000);
    expect(calls).toHaveLength(301);
  });

  it("honours a custom timeout and interval", async () => {
    let now = 0;
    vi.spyOn(Date, "now").mockImplementation(() => now);
    const { fetch } = mockFetch(Array.from({ length: 10 }, () => json(batch({ status: "rendering" }))));
    const sleep = async (ms: number) => {
      now += ms;
    };
    const client = new PageRush({ apiKey: TEST_KEY, baseUrl: BASE, fetch, sleep });
    await expect(client.batches.wait("bat_1", { timeout: 2500, interval: 1000 })).rejects.toMatchObject({
      code: "WAIT_TIMEOUT",
    });
    expect(now).toBe(2500);
  });

  it("stops when the signal aborts", async () => {
    const controller = new AbortController();
    const { fetch } = mockFetch([json(batch()), json(batch())]);
    const sleep = async () => {
      controller.abort(new Error("cancelled"));
    };
    const client = new PageRush({ apiKey: TEST_KEY, baseUrl: BASE, fetch, sleep });
    await expect(client.batches.wait("bat_1", { signal: controller.signal })).rejects.toThrow("cancelled");
  });

  it("clears the poll timer when the signal aborts", async () => {
    vi.useFakeTimers();
    try {
      const controller = new AbortController();
      const { fetch } = mockFetch([json(batch())]);
      const client = new PageRush({ apiKey: TEST_KEY, baseUrl: BASE, fetch });
      const pending = client.batches
        .wait("bat_1", { interval: 60_000, signal: controller.signal })
        .catch((e: unknown) => e);
      await vi.advanceTimersByTimeAsync(0);
      controller.abort(new Error("stop"));
      await expect(pending).resolves.toMatchObject({ message: "stop" });
      expect(vi.getTimerCount()).toBe(0);
    } finally {
      vi.useRealTimers();
    }
  });
});

describe("batches.downloadZip", () => {
  it("downloads the ZIP with the Authorization header", async () => {
    const { client, calls } = setup([zip()]);
    const bytes = await client.batches.downloadZip("bat_1");
    expect(bytes).toBeInstanceOf(Uint8Array);
    expect(bytes).toEqual(ZIP_BYTES);
    expect(calls).toHaveLength(1);
    expect(calls[0]!.method).toBe("GET");
    expect(calls[0]!.url).toBe(`${BASE}/v1/batches/bat_1/zip`);
    expect(calls[0]!.headers.authorization).toBe(`Bearer ${TEST_KEY}`);
    expect(calls[0]!.headers.accept).toBe("application/zip");
  });

  it("accepts a content type with parameters", async () => {
    const { client } = setup([zip({ "Content-Type": "application/zip; charset=binary" })]);
    await expect(client.batches.downloadZip("bat_1")).resolves.toEqual(ZIP_BYTES);
  });

  it.each([
    ["JSON", json(batch())],
    ["a PDF", new Response(PDF_BYTES, { status: 200, headers: { "Content-Type": "application/pdf" } })],
    ["no content type", new Response(ZIP_BYTES, { status: 200, headers: { "Content-Type": "" } })],
  ])("rejects a 200 that is %s with INVALID_RESPONSE", async (_name, reply) => {
    const { client } = setup([reply]);
    const err = (await client.batches.downloadZip("bat_1").catch((e: unknown) => e)) as PageRushError;
    expect(err).toBeInstanceOf(PageRushError);
    expect(err).toMatchObject({ status: 200, code: "INVALID_RESPONSE" });
  });

  it("raises 409 CONFLICT with retry_after while the batch is rendering, without retrying", async () => {
    const { client, calls } = setup([problem(409, "CONFLICT", {}, { "Retry-After": "5" })]);
    await expect(client.batches.downloadZip("bat_1")).rejects.toMatchObject({
      status: 409,
      code: "CONFLICT",
      retry_after: 5,
    });
    expect(calls).toHaveLength(1);
  });

  it("raises 410 FILE_EXPIRED when no PDFs are left", async () => {
    const { client } = setup([problem(410, "FILE_EXPIRED")]);
    await expect(client.batches.downloadZip("bat_1")).rejects.toMatchObject({
      status: 410,
      code: "FILE_EXPIRED",
    });
  });

  it("retries on 503 like any GET", async () => {
    const { client, calls } = setup([problem(503, "X"), zip()]);
    await expect(client.batches.downloadZip("bat_1")).resolves.toEqual(ZIP_BYTES);
    expect(calls).toHaveLength(2);
  });
});
