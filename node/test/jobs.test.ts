import { afterEach, describe, expect, it, vi } from "vitest";
import { type Job, PageRush, PageRushError } from "../src";
import { BASE, job, json, mockFetch, PDF_BYTES, problem, setup, TEST_KEY } from "./helpers";

describe("jobs.get", () => {
  it("gets a job", async () => {
    const { client, calls } = setup([json(job())]);
    expect(await client.jobs.get("job_1")).toEqual(job());
    expect(calls[0]!.method).toBe("GET");
    expect(calls[0]!.url).toBe(`${BASE}/v1/jobs/job_1`);
  });

  it("throws NOT_FOUND", async () => {
    const { client } = setup([problem(404, "NOT_FOUND")]);
    await expect(client.jobs.get("job_x")).rejects.toMatchObject({ status: 404, code: "NOT_FOUND" });
  });
});

describe("jobs.wait", () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("polls until the job succeeds", async () => {
    const { client, calls, delays } = setup([
      json(job()),
      json(job({ status: "rendering" })),
      json(job({ status: "succeeded" })),
    ]);
    const done = await client.jobs.wait("job_1", { interval: 250 });
    expect(done.status).toBe("succeeded");
    expect(calls).toHaveLength(3);
    expect(delays).toEqual([250, 250]);
  });

  it("returns a failed job instead of throwing", async () => {
    const failed = job({ status: "failed", error: { code: "TEMPLATE_COMPILE_FAILED", message: "boom" } });
    const { client } = setup([json(failed)]);
    await expect(client.jobs.wait("job_1")).resolves.toEqual(failed);
  });

  it("throws WAIT_TIMEOUT after the timeout", async () => {
    let now = 1_000_000;
    vi.spyOn(Date, "now").mockImplementation(() => now);
    const { fetch } = mockFetch(Array.from({ length: 10 }, () => json(job({ status: "rendering" }))));
    const sleep = async (ms: number) => {
      now += ms;
    };
    const client = new PageRush({ apiKey: TEST_KEY, baseUrl: BASE, fetch, sleep });
    const err = (await client.jobs
      .wait("job_1", { timeout: 2500, interval: 1000 })
      .catch((e: unknown) => e)) as PageRushError | undefined;
    expect(err).toBeInstanceOf(PageRushError);
    expect(err).toMatchObject({ status: 0, code: "WAIT_TIMEOUT" });
    expect(now).toBe(1_002_500); // slept 1000 + 1000 + 500, never past the deadline
  });

  it("stops when the signal aborts", async () => {
    const controller = new AbortController();
    const { fetch } = mockFetch([json(job()), json(job())]);
    const sleep = async () => {
      controller.abort(new Error("cancelled"));
    };
    const client = new PageRush({ apiKey: TEST_KEY, baseUrl: BASE, fetch, sleep });
    await expect(client.jobs.wait("job_1", { signal: controller.signal })).rejects.toThrow("cancelled");
  });

  it("propagates API errors", async () => {
    const { client } = setup([problem(401, "INVALID_API_KEY")]);
    await expect(client.jobs.wait("job_1")).rejects.toMatchObject({ code: "INVALID_API_KEY" });
  });
});

describe("jobs.download", () => {
  const signed = "https://files.example.test/files/job_1?expires=1&sig=abc";

  it("downloads from download_url without the Authorization header", async () => {
    const { client, calls } = setup([
      json(job({ status: "succeeded", download_url: signed })),
      new Response(PDF_BYTES, { status: 200, headers: { "Content-Type": "application/pdf" } }),
    ]);
    const bytes = await client.jobs.download("job_1");
    expect(bytes).toBeInstanceOf(Uint8Array);
    expect(bytes).toEqual(PDF_BYTES);
    expect(calls[0]!.headers.authorization).toBe(`Bearer ${TEST_KEY}`);
    expect(calls[1]!.url).toBe(signed);
    expect(calls[1]!.headers.authorization).toBeUndefined();
    expect(calls[1]!.headers["user-agent"]).toMatch(/^pagerush-node\//);
  });

  it("resolves a relative download_url against the base URL", async () => {
    const { client, calls } = setup([
      json(job({ status: "succeeded", download_url: "/files/job_1?sig=abc" })),
      new Response(PDF_BYTES, { status: 200 }),
    ]);
    await client.jobs.download("job_1");
    expect(calls[1]!.url).toBe(`${BASE}/files/job_1?sig=abc`);
  });

  it("throws FILE_EXPIRED when there is no download_url", async () => {
    const { client, calls } = setup([json(job({ status: "succeeded", download_url: null }))]);
    const err = (await client.jobs.download("job_1").catch((e: unknown) => e)) as PageRushError;
    expect(err).toBeInstanceOf(PageRushError);
    expect(err.code).toBe("FILE_EXPIRED");
    expect(calls).toHaveLength(1);
  });

  it("maps an error from the signed link", async () => {
    const { client } = setup([
      json(job({ status: "succeeded", download_url: signed })),
      problem(410, "FILE_EXPIRED"),
    ]);
    await expect(client.jobs.download("job_1")).rejects.toMatchObject({ status: 410, code: "FILE_EXPIRED" });
  });
});

describe("signed download links in errors", () => {
  const signed = "https://files.example.test/files/job_1?expires=1&sig=secret_signature";

  it("leaves the query string out of connection error messages", async () => {
    const { client } = setup([
      json(job({ status: "succeeded", download_url: signed })),
      new TypeError(`fetch failed: ${signed}`),
      new TypeError(`fetch failed: ${signed}`),
      new TypeError(`fetch failed: ${signed}`),
    ]);
    const err = (await client.jobs.download("job_1").catch((e: unknown) => e)) as PageRushError;
    expect(err.code).toBe("CONNECTION_ERROR");
    expect(err.message).toContain("https://files.example.test");
    expect(err.message).not.toContain("secret_signature");
  });

  it("leaves the query string out of HTTP error messages", async () => {
    const { client } = setup([
      json(job({ status: "succeeded", download_url: signed })),
      new Response(`<Error>Denied ${signed}</Error>`, { status: 403 }),
    ]);
    const err = (await client.jobs.download("job_1").catch((e: unknown) => e)) as PageRushError;
    expect(err.status).toBe(403);
    expect(err.message).not.toContain("secret_signature");
  });

  it("types job.error with nullable fields", () => {
    const failed: Job = job({ status: "failed", error: { code: null, message: null } }) as Job;
    expect(failed.error).toEqual({ code: null, message: null });
  });
});
