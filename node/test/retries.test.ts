import { afterEach, describe, expect, it, vi } from "vitest";
import { PageRush, PageRushConnectionError, PageRushError } from "../src";
import { backoffMs, parseRetryAfter } from "../src/http";
import { BASE, json, mockFetch, mockSleep, problem, setup, TEST_KEY, template, version } from "./helpers";

const pdf = () =>
  new Response(new Uint8Array([37, 80, 68, 70]), {
    status: 200,
    headers: { "Content-Type": "application/pdf", "X-Job-Id": "j" },
  });

describe("retries", () => {
  it("retries a render on 503 after Retry-After, reusing the idempotency key", async () => {
    const { client, calls, delays } = setup([
      problem(503, "RENDER_UNAVAILABLE", {}, { "Retry-After": "2" }),
      pdf(),
    ]);
    const result = await client.templates.render("tpl_1", { data: {} });
    expect(result.job_id).toBe("j");
    expect(calls).toHaveLength(2);
    expect(delays).toEqual([2000]);
    const key = calls[0]!.headers["idempotency-key"];
    expect(key).toBeTruthy();
    expect(calls[1]!.headers["idempotency-key"]).toBe(key);
  });

  it("caps Retry-After at 30 seconds", async () => {
    const { client, delays } = setup([
      problem(429, "RATE_LIMITED", {}, { "Retry-After": "120" }),
      json({ template }),
    ]);
    await client.templates.get("tpl_1");
    expect(delays).toEqual([30_000]);
  });

  it("falls back to backoff when Retry-After is empty or invalid", async () => {
    const { client, delays } = setup([
      problem(503, "RENDER_UNAVAILABLE", {}, { "Retry-After": "" }),
      problem(503, "RENDER_UNAVAILABLE", {}, { "Retry-After": "soon" }),
      json({ template }),
    ]);
    await client.templates.get("tpl_1");
    expect(delays).toHaveLength(2);
    expect(delays[0]).toBeGreaterThanOrEqual(375);
    expect(delays[0]).toBeLessThanOrEqual(625);
    expect(delays[1]).toBeGreaterThanOrEqual(750);
    expect(delays[1]).toBeLessThanOrEqual(1250);
  });

  it.each([429, 502, 503, 504])("retries GET on %i", async (status) => {
    const { client, calls } = setup([problem(status, "X"), json({ template })]);
    await client.templates.get("tpl_1");
    expect(calls).toHaveLength(2);
  });

  it.each([400, 401, 404, 409, 500])("does not retry on %i", async (status) => {
    const { client, calls } = setup([problem(status, "X")]);
    await expect(client.templates.get("tpl_1")).rejects.toMatchObject({ status });
    expect(calls).toHaveLength(1);
  });

  it("does not retry create on 502 or 504", async () => {
    for (const status of [502, 504]) {
      const { client, calls } = setup([problem(status, "X")]);
      await expect(client.templates.create({ name: "a", source: "b" })).rejects.toMatchObject({ status });
      expect(calls).toHaveLength(1);
    }
  });

  it("retries create, createVersion, publish and rename on 429 and 503", async () => {
    const { client, calls } = setup([
      problem(429, "RATE_LIMITED"),
      json({ template, version }, 201),
      problem(503, "X"),
      json({ template, version }, 201),
      problem(503, "X"),
      json({ template }),
      problem(429, "X"),
      json({ template }),
    ]);
    await client.templates.create({ name: "a", source: "b" });
    await client.templates.createVersion("tpl_1", { source: "b" });
    await client.templates.publish("tpl_1", 1);
    await client.templates.rename("tpl_1", "c");
    expect(calls).toHaveLength(8);
  });

  it("does not retry create on a connection error", async () => {
    const { client, calls } = setup([new TypeError("fetch failed")]);
    const err = await client.templates.create({ name: "a", source: "b" }).catch((e: unknown) => e);
    expect(err).toBeInstanceOf(PageRushConnectionError);
    expect(err).toMatchObject({ status: 0, code: "CONNECTION_ERROR" });
    expect(calls).toHaveLength(1);
  });

  it("retries GET, DELETE and render on connection errors", async () => {
    const { client, calls } = setup([
      new TypeError("fetch failed"),
      json({ template }),
      new TypeError("fetch failed"),
      new Response(null, { status: 204 }),
      new TypeError("fetch failed"),
      pdf(),
    ]);
    await client.templates.get("tpl_1");
    await client.templates.delete("tpl_1");
    await client.templates.render("tpl_1", { data: {} });
    expect(calls).toHaveLength(6);
    expect(calls[4]!.headers["idempotency-key"]).toBe(calls[5]!.headers["idempotency-key"]);
  });

  it("stops after maxRetries and throws the last error", async () => {
    const { client, calls, delays } = setup([
      problem(503, "RENDER_UNAVAILABLE"),
      problem(503, "RENDER_UNAVAILABLE"),
      problem(503, "RENDER_UNAVAILABLE", { request_id: "req_last" }),
    ]);
    const err = await client.templates.render("tpl_1", { data: {} }).catch((e: unknown) => e);
    expect(err).toMatchObject({ status: 503, code: "RENDER_UNAVAILABLE", request_id: "req_last" });
    expect(calls).toHaveLength(3);
    expect(delays).toHaveLength(2);
  });

  it("honours maxRetries: 0 on the client and per call", async () => {
    const a = setup([problem(503, "X")], { maxRetries: 0 });
    await expect(a.client.templates.get("tpl_1")).rejects.toMatchObject({ status: 503 });
    expect(a.calls).toHaveLength(1);

    const b = setup([problem(503, "X")]);
    await expect(b.client.templates.get("tpl_1", { maxRetries: 0 })).rejects.toMatchObject({ status: 503 });
    expect(b.calls).toHaveLength(1);
  });
});

describe("timeouts and cancellation", () => {
  /** A fetch that never answers until its signal aborts. */
  const hang = () =>
    mockFetch([
      (call) =>
        new Promise<Response>((_, reject) => {
          call.signal!.addEventListener("abort", () => reject(new DOMException("aborted", "AbortError")));
        }),
    ]);

  it("throws TIMEOUT when an attempt exceeds the timeout", async () => {
    const { fetch } = hang();
    const client = new PageRush({ apiKey: TEST_KEY, baseUrl: BASE, fetch, timeout: 20, maxRetries: 0 });
    const err = await client.templates.get("tpl_1").catch((e: unknown) => e);
    expect(err).toBeInstanceOf(PageRushConnectionError);
    expect(err).toMatchObject({ status: 0, code: "TIMEOUT" });
  });

  it("applies the timeout to reading the body", async () => {
    const { fetch } = mockFetch([
      (call) => {
        const body = new ReadableStream<Uint8Array>({
          start(controller) {
            controller.enqueue(new TextEncoder().encode("{"));
            call.signal!.addEventListener("abort", () =>
              controller.error(new DOMException("aborted", "AbortError")),
            );
          },
        });
        return new Response(body, { status: 200 });
      },
    ]);
    const client = new PageRush({ apiKey: TEST_KEY, baseUrl: BASE, fetch, timeout: 20, maxRetries: 0 });
    await expect(client.templates.get("tpl_1")).rejects.toMatchObject({ code: "TIMEOUT" });
  });

  it("uses a per-call timeout", async () => {
    const { fetch } = hang();
    const client = new PageRush({ apiKey: TEST_KEY, baseUrl: BASE, fetch, timeout: 60_000, maxRetries: 0 });
    await expect(client.templates.get("tpl_1", { timeout: 10 })).rejects.toMatchObject({ code: "TIMEOUT" });
  });

  it("retries a GET after a timeout", async () => {
    const { fetch: hanging } = hang();
    const { fetch, calls } = mockFetch([
      (call) => hanging(call.url, { signal: call.signal ?? null }),
      json({ template }),
    ]);
    const { sleep } = mockSleep();
    const client = new PageRush({ apiKey: TEST_KEY, baseUrl: BASE, fetch, sleep, timeout: 10 });
    await expect(client.templates.get("tpl_1")).resolves.toEqual(template);
    expect(calls).toHaveLength(2);
  });

  it("rejects with the signal's reason when aborted mid-request, without retrying", async () => {
    const { fetch, calls } = hang();
    const client = new PageRush({ apiKey: TEST_KEY, baseUrl: BASE, fetch });
    const controller = new AbortController();
    const pending = client.templates.get("tpl_1", { signal: controller.signal });
    controller.abort(new Error("user cancelled"));
    await expect(pending).rejects.toThrow("user cancelled");
    expect(calls).toHaveLength(1);
  });

  it("does not send a request when the signal is already aborted", async () => {
    const { fetch, calls } = mockFetch([]);
    const client = new PageRush({ apiKey: TEST_KEY, baseUrl: BASE, fetch });
    const err = await client.templates.get("tpl_1", { signal: AbortSignal.abort() }).catch((e: unknown) => e);
    expect((err as Error).name).toBe("AbortError");
    expect(calls).toHaveLength(0);
  });

  it("cancels a retry wait", async () => {
    const { fetch, calls } = mockFetch([problem(503, "X", {}, { "Retry-After": "30" })]);
    const controller = new AbortController();
    const sleep = () => new Promise<void>(() => controller.abort(new Error("stop")));
    const client = new PageRush({ apiKey: TEST_KEY, baseUrl: BASE, fetch, sleep });
    await expect(client.templates.get("tpl_1", { signal: controller.signal })).rejects.toThrow("stop");
    expect(calls).toHaveLength(1);
  });
});

describe("parseRetryAfter and backoffMs", () => {
  it("parses seconds and HTTP dates", () => {
    expect(parseRetryAfter("3")).toBe(3);
    expect(parseRetryAfter(" 1.5 ")).toBe(1.5);
    expect(parseRetryAfter(null)).toBeNull();
    expect(parseRetryAfter("")).toBeNull();
    expect(parseRetryAfter("-1")).toBeNull();
    const now = Date.parse("2026-01-01T00:00:00Z");
    expect(parseRetryAfter("Thu, 01 Jan 2026 00:00:05 GMT", now)).toBe(5);
    expect(parseRetryAfter("Wed, 31 Dec 2025 23:59:00 GMT", now)).toBe(0);
  });

  it("doubles from 0.5 s up to 8 s with ±25 % jitter", () => {
    expect([1, 2, 3, 4, 5, 6].map((n) => backoffMs(n, () => 0.5))).toEqual([
      500, 1000, 2000, 4000, 8000, 8000,
    ]);
    expect(backoffMs(1, () => 0)).toBe(375);
    expect(backoffMs(1, () => 1)).toBe(625);
  });
});

describe("errors", () => {
  it("maps problem+json", async () => {
    const { client } = setup([
      problem(
        422,
        "TEMPLATE_COMPILE_FAILED",
        {
          type: "https://pagerush.dev/errors/template-compile-failed",
          title: "Template failed to compile",
          detail: "unknown variable: foo",
          errors: [{ path: "main.typ:3:5", message: "unknown variable: foo" }],
          request_id: "req_body",
        },
        { "X-Request-Id": "req_header" },
      ),
    ]);
    const err = await client.templates.render("tpl_1", { data: {} }).catch((e: unknown) => e);
    expect(err).toBeInstanceOf(PageRushError);
    expect(err).not.toBeInstanceOf(PageRushConnectionError);
    const e = err as PageRushError;
    expect(e.name).toBe("PageRushError");
    expect(e.status).toBe(422);
    expect(e.code).toBe("TEMPLATE_COMPILE_FAILED");
    expect(e.title).toBe("Template failed to compile");
    expect(e.detail).toBe("unknown variable: foo");
    expect(e.errors).toEqual([{ path: "main.typ:3:5", message: "unknown variable: foo" }]);
    expect(e.request_id).toBe("req_body");
    expect(e.retry_after).toBeNull();
    expect(e.type).toBe("https://pagerush.dev/errors/template-compile-failed");
    expect(e.message).toBe("TEMPLATE_COMPILE_FAILED: unknown variable: foo");
  });

  it("uses the title when there is no detail, and exposes retry_after", async () => {
    const { client } = setup(
      [problem(429, "RATE_LIMITED", { title: "Too many requests" }, { "Retry-After": "7" })],
      {
        maxRetries: 0,
      },
    );
    const err = (await client.templates.get("tpl_1").catch((e: unknown) => e)) as PageRushError;
    expect(err.message).toBe("RATE_LIMITED: Too many requests");
    expect(err.retry_after).toBe(7);
  });

  it("maps a non-JSON body to UNKNOWN and falls back to the X-Request-Id header", async () => {
    const { client } = setup([
      new Response("<html>Bad gateway</html>", { status: 500, headers: { "X-Request-Id": "req_header" } }),
    ]);
    const err = (await client.templates.get("tpl_1").catch((e: unknown) => e)) as PageRushError;
    expect(err.status).toBe(500);
    expect(err.code).toBe("UNKNOWN");
    expect(err.title).toBe("");
    expect(err.detail).toBeNull();
    expect(err.errors).toEqual([]);
    expect(err.request_id).toBe("req_header");
    expect(err.type).toBeNull();
    expect(err.message).toBe("UNKNOWN: HTTP 500");
  });

  it("maps JSON without a code to UNKNOWN and ignores malformed errors entries", async () => {
    const { client } = setup([json({ title: "Nope", errors: [null, "x", { path: "a" }] }, 400)]);
    const err = (await client.templates.get("tpl_1").catch((e: unknown) => e)) as PageRushError;
    expect(err.code).toBe("UNKNOWN");
    expect(err.message).toBe("UNKNOWN: Nope");
    expect(err.errors).toEqual([{ path: "a", message: "" }]);
    expect(err.request_id).toBeNull();
  });

  it("throws INVALID_RESPONSE for a non-JSON 2xx body", async () => {
    const { client } = setup([
      new Response("OK", { status: 200, headers: { "Content-Type": "text/plain", "X-Request-Id": "req_1" } }),
    ]);
    const err = (await client.jobs.get("job_1").catch((e: unknown) => e)) as PageRushError;
    expect(err).toBeInstanceOf(PageRushError);
    expect(err).toMatchObject({ status: 200, code: "INVALID_RESPONSE", request_id: "req_1" });
    expect(err.message).toMatch(/text\/plain/);
  });

  it("wraps the underlying network error as cause", async () => {
    const cause = new TypeError("getaddrinfo ENOTFOUND");
    const { client } = setup([cause], { maxRetries: 0 });
    const err = (await client.jobs.get("job_1").catch((e: unknown) => e)) as PageRushConnectionError;
    expect(err.name).toBe("PageRushConnectionError");
    expect(err.cause).toBe(cause);
    expect(err.message).toBe(`CONNECTION_ERROR: Could not connect to ${BASE}`);
  });

  it("never copies the underlying message, which may quote header values", async () => {
    const cause = new TypeError(`Headers.append: "Bearer ${TEST_KEY}" is an invalid header value.`);
    const { client, calls } = setup([cause, cause, cause]);
    const err = (await client.jobs.get("job_1").catch((e: unknown) => e)) as PageRushConnectionError;
    expect(err.code).toBe("CONNECTION_ERROR");
    expect(err.message).not.toContain(TEST_KEY);
    expect(String(err)).not.toContain(TEST_KEY);
    expect(calls).toHaveLength(3);
  });
});

describe("409 CONFLICT on render", () => {
  it("retries a render after Retry-After, with the same idempotency key", async () => {
    const { client, calls, delays } = setup([problem(409, "CONFLICT", {}, { "Retry-After": "2" }), pdf()]);
    await expect(client.templates.render("tpl_1", { data: {} })).resolves.toMatchObject({ job_id: "j" });
    expect(delays).toEqual([2000]);
    expect(calls[1]!.headers["idempotency-key"]).toBe(calls[0]!.headers["idempotency-key"]);
  });

  it("never retries IDEMPOTENCY_CONFLICT", async () => {
    const { client, calls } = setup([problem(409, "IDEMPOTENCY_CONFLICT")]);
    await expect(client.templates.render("tpl_1", { data: {} })).rejects.toMatchObject({
      status: 409,
      code: "IDEMPOTENCY_CONFLICT",
    });
    expect(calls).toHaveLength(1);
  });

  it("does not retry 409 CONFLICT outside render", async () => {
    const a = setup([problem(409, "CONFLICT")]);
    await expect(a.client.templates.create({ name: "a", source: "b" })).rejects.toMatchObject({
      status: 409,
    });
    expect(a.calls).toHaveLength(1);
    const b = setup([problem(409, "CONFLICT")]);
    await expect(b.client.templates.get("tpl_1")).rejects.toMatchObject({ status: 409 });
    expect(b.calls).toHaveLength(1);
  });
});

describe("extreme Retry-After values", () => {
  it.each([
    ["a large number", "99999999"],
    ["a number too large for a double", "9".repeat(400)],
    ["a date far in the future", "Fri, 31 Dec 9999 23:59:59 GMT"],
  ])("caps %s at 30 seconds", async (_name, value) => {
    const { client, delays } = setup([problem(503, "X", {}, { "Retry-After": value }), json({ template })]);
    await client.templates.get("tpl_1");
    expect(delays).toEqual([30_000]);
  });

  it("falls back to backoff for an unparseable value", async () => {
    const { client, delays } = setup([problem(503, "X", {}, { "Retry-After": "1e400" }), json({ template })]);
    await client.templates.get("tpl_1");
    expect(delays[0]).toBeLessThanOrEqual(625);
  });
});

describe("cancellation leaves no timers running", () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  it("clears the retry wait when the signal aborts", async () => {
    vi.useFakeTimers();
    const { fetch } = mockFetch([problem(503, "X", {}, { "Retry-After": "30" })]);
    const client = new PageRush({ apiKey: TEST_KEY, baseUrl: BASE, fetch });
    const controller = new AbortController();
    const pending = client.templates.get("tpl_1", { signal: controller.signal });
    const settled = pending.catch((e: unknown) => e);
    await vi.waitFor(() => expect(vi.getTimerCount()).toBe(1));
    controller.abort(new Error("stop"));
    expect(await settled).toMatchObject({ message: "stop" });
    expect(vi.getTimerCount()).toBe(0);
  });

  it("clears the jobs.wait poll timer when the signal aborts", async () => {
    vi.useFakeTimers();
    const { fetch } = mockFetch([json({ id: "job_1", status: "queued" })]);
    const client = new PageRush({ apiKey: TEST_KEY, baseUrl: BASE, fetch });
    const controller = new AbortController();
    const settled = client.jobs
      .wait("job_1", { interval: 60_000, signal: controller.signal })
      .catch((e: unknown) => e);
    await vi.waitFor(() => expect(vi.getTimerCount()).toBe(1));
    controller.abort(new Error("stop"));
    expect(await settled).toMatchObject({ message: "stop" });
    expect(vi.getTimerCount()).toBe(0);
  });
});

describe("redirects", () => {
  const redirect = (status: number, location: string) =>
    new Response(null, { status, headers: { Location: location } });

  it("keeps the Authorization header on a same-origin redirect", async () => {
    const { client, calls } = setup([redirect(307, "/v1/templates/tpl_2"), json({ template })]);
    await client.templates.get("tpl_1");
    expect(calls[1]!.url).toBe(`${BASE}/v1/templates/tpl_2`);
    expect(calls[1]!.headers.authorization).toBe(`Bearer ${TEST_KEY}`);
  });

  it.each([
    ["another host", "https://elsewhere.example.test/v1/templates/tpl_1"],
    ["plain http on the same host", BASE.replace("https:", "http:") + "/v1/templates/tpl_1"],
  ])("drops the Authorization header on a redirect to %s", async (_name, location) => {
    const { client, calls } = setup([redirect(302, location), json({ template })]);
    await client.templates.get("tpl_1");
    expect(calls[0]!.headers.authorization).toBe(`Bearer ${TEST_KEY}`);
    expect(calls[1]!.url).toBe(location);
    expect(calls[1]!.headers.authorization).toBeUndefined();
  });

  it("asks fetch not to follow redirects itself", async () => {
    const { client, calls } = setup([json({ template })]);
    await client.templates.get("tpl_1");
    expect(calls[0]!.redirect).toBe("manual");
  });

  it("switches to GET without a body on 303", async () => {
    const { client, calls } = setup([redirect(303, "/v1/templates/tpl_1"), json({ template })]);
    await client.templates.rename("tpl_1", "x");
    expect(calls[0]!.method).toBe("PATCH");
    expect(calls[1]!.method).toBe("GET");
    expect(calls[1]!.body).toBeUndefined();
    expect(calls[1]!.headers["content-type"]).toBeUndefined();
  });

  it("stops after 5 redirects", async () => {
    const loop = Array.from({ length: 6 }, () => redirect(307, "/v1/templates/tpl_1"));
    const { client, calls } = setup(loop);
    await expect(client.templates.get("tpl_1")).rejects.toMatchObject({ status: 307 });
    expect(calls).toHaveLength(6);
  });
});
