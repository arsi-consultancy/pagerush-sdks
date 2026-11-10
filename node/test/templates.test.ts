import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { type PageRush, PageRushError } from "../src";
import { BASE, job, json, PDF_BYTES, setup, template, version } from "./helpers";

type Client = PageRush;

const UUID_V4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

function pdf(headers: Record<string, string> = {}): Response {
  return new Response(PDF_BYTES, {
    status: 200,
    headers: {
      "Content-Type": "application/pdf",
      "Content-Disposition": 'inline; filename="invoice-42.pdf"',
      "X-Job-Id": "job_9",
      "X-Template-Version": "3",
      "X-Render-Engine": "typst",
      "X-Page-Count": "2",
      "X-Template-Warnings": "1",
      "X-Request-Id": "req_1",
      ...headers,
    },
  });
}

describe("templates", () => {
  it("list sends limit and cursor", async () => {
    const { client, calls } = setup([json({ items: [template], next_cursor: "1_tpl_1" })]);
    const page = await client.templates.list({ limit: 10, cursor: "0_tpl_0" });
    expect(calls[0]!.method).toBe("GET");
    expect(calls[0]!.url).toBe(`${BASE}/v1/templates?limit=10&cursor=0_tpl_0`);
    expect(page).toEqual({ items: [template], next_cursor: "1_tpl_1" });
  });

  it("list omits unset query parameters", async () => {
    const { client, calls } = setup([json({ items: [], next_cursor: null })]);
    await client.templates.list();
    expect(calls[0]!.url).toBe(`${BASE}/v1/templates`);
  });

  it("listAll follows next_cursor across pages", async () => {
    const second = { ...template, id: "tpl_2" };
    const { client, calls } = setup([
      json({ items: [template], next_cursor: "1_tpl_1" }),
      json({ items: [second], next_cursor: null }),
    ]);
    const ids: string[] = [];
    for await (const t of client.templates.listAll({ limit: 1 })) ids.push(t.id);
    expect(ids).toEqual(["tpl_1", "tpl_2"]);
    expect(calls.map((c) => c.url)).toEqual([
      `${BASE}/v1/templates?limit=1`,
      `${BASE}/v1/templates?limit=1&cursor=1_tpl_1`,
    ]);
  });

  it("create posts the body as JSON", async () => {
    const { client, calls } = setup([json({ template, version }, 201)]);
    const params = {
      name: "Invoice",
      source: "Hello",
      files: [{ path: "a.typ", content: "x", encoding: "utf8" as const }],
      sample_data: { name: "Ada" },
    };
    const res = await client.templates.create(params);
    expect(calls[0]!.method).toBe("POST");
    expect(calls[0]!.url).toBe(`${BASE}/v1/templates`);
    expect(calls[0]!.headers["content-type"]).toBe("application/json");
    expect(JSON.parse(calls[0]!.body!)).toEqual(params);
    expect(res).toEqual({ template, version });
  });

  it("get unwraps the template", async () => {
    const { client, calls } = setup([json({ template })]);
    expect(await client.templates.get("tpl_1")).toEqual(template);
    expect(calls[0]!.url).toBe(`${BASE}/v1/templates/tpl_1`);
  });

  it("URL-encodes path segments", async () => {
    const { client, calls } = setup([json({ template }), json(job())]);
    await client.templates.get("a/b?c#d");
    await client.jobs.get("../x y");
    expect(calls[0]!.url).toBe(`${BASE}/v1/templates/a%2Fb%3Fc%23d`);
    expect(calls[1]!.url).toBe(`${BASE}/v1/jobs/..%2Fx%20y`);
  });

  it("rename patches the name and unwraps", async () => {
    const { client, calls } = setup([json({ template: { ...template, name: "New" } })]);
    const t = await client.templates.rename("tpl_1", "New");
    expect(calls[0]!.method).toBe("PATCH");
    expect(calls[0]!.url).toBe(`${BASE}/v1/templates/tpl_1`);
    expect(JSON.parse(calls[0]!.body!)).toEqual({ name: "New" });
    expect(t.name).toBe("New");
  });

  it("delete sends DELETE and resolves on 204", async () => {
    const { client, calls } = setup([new Response(null, { status: 204 })]);
    await expect(client.templates.delete("tpl_1")).resolves.toBeUndefined();
    expect(calls[0]!.method).toBe("DELETE");
    expect(calls[0]!.url).toBe(`${BASE}/v1/templates/tpl_1`);
    expect(calls[0]!.body).toBeUndefined();
  });

  it("createVersion posts to /versions", async () => {
    const { client, calls } = setup([json({ template, version }, 201)]);
    await client.templates.createVersion("tpl_1", { source: "Hi", publish: false });
    expect(calls[0]!.method).toBe("POST");
    expect(calls[0]!.url).toBe(`${BASE}/v1/templates/tpl_1/versions`);
    expect(JSON.parse(calls[0]!.body!)).toEqual({ source: "Hi", publish: false });
  });

  it("getVersion unwraps the version", async () => {
    const { client, calls } = setup([json({ version })]);
    expect(await client.templates.getVersion("tpl_1", 1)).toEqual(version);
    expect(calls[0]!.url).toBe(`${BASE}/v1/templates/tpl_1/versions/1`);
  });

  it("publish posts the version and unwraps", async () => {
    const { client, calls } = setup([json({ template })]);
    expect(await client.templates.publish("tpl_1", 2)).toEqual(template);
    expect(calls[0]!.url).toBe(`${BASE}/v1/templates/tpl_1/publish`);
    expect(JSON.parse(calls[0]!.body!)).toEqual({ version: 2 });
  });
});

describe("unusable 2xx responses", () => {
  it.each([
    ["get without the template wrapper", (c: Client) => c.templates.get("tpl_1"), json(template)],
    ["get with a null template", (c: Client) => c.templates.get("tpl_1"), json({ template: null })],
    ["rename without the wrapper", (c: Client) => c.templates.rename("tpl_1", "x"), json({})],
    ["publish with an array body", (c: Client) => c.templates.publish("tpl_1", 1), json([template])],
    ["getVersion without the wrapper", (c: Client) => c.templates.getVersion("tpl_1", 1), json(version)],
    ["list without items", (c: Client) => c.templates.list(), json({ data: [] })],
    [
      "create without the version",
      (c: Client) => c.templates.create({ name: "a", source: "b" }),
      json({ template }, 201),
    ],
    [
      "createVersion with an empty body",
      (c: Client) => c.templates.createVersion("tpl_1", { source: "b" }),
      new Response(null, { status: 201 }),
    ],
    ["jobs.get with a non-job body", (c: Client) => c.jobs.get("job_1"), json({ ok: true })],
  ])("%s throws INVALID_RESPONSE", async (_name, call, reply) => {
    const { client } = setup([reply]);
    const err = await call(client).catch((e: unknown) => e);
    expect(err).toBeInstanceOf(PageRushError);
    expect(err).toMatchObject({ code: "INVALID_RESPONSE", status: reply.status });
  });
});

describe("templates.render", () => {
  it("returns the PDF and parses headers on 200", async () => {
    const { client, calls } = setup([pdf()]);
    const result = await client.templates.render("tpl_1", {
      data: { name: "Ada" },
      version: 3,
      filename: "invoice-42.pdf",
      metadata: { order: "42" },
      delete_after_download: true,
      callback_url: undefined,
    });
    const call = calls[0]!;
    expect(call.method).toBe("POST");
    expect(call.url).toBe(`${BASE}/v1/templates/tpl_1/render`);
    expect(call.headers.accept).toBe("application/pdf, application/json");
    expect(call.headers["idempotency-key"]).toMatch(UUID_V4);
    expect(call.headers.prefer).toBeUndefined();
    expect(JSON.parse(call.body!)).toEqual({
      data: { name: "Ada" },
      version: 3,
      filename: "invoice-42.pdf",
      metadata: { order: "42" },
      delete_after_download: true,
    });
    expect(result).toMatchObject({
      queued: false,
      job: null,
      job_id: "job_9",
      template_version: 3,
      engine: "typst",
      page_count: 2,
      warnings: 1,
      request_id: "req_1",
      filename: "invoice-42.pdf",
    });
    expect(result.pdf).toEqual(PDF_BYTES);
  });

  it("returns nulls for missing optional headers", async () => {
    const { client } = setup([
      new Response(PDF_BYTES, {
        status: 200,
        headers: { "Content-Type": "application/pdf", "X-Job-Id": "job_9", "X-Page-Count": "n/a" },
      }),
    ]);
    const result = await client.templates.render("tpl_1", { data: null });
    expect(result).toMatchObject({
      template_version: null,
      engine: null,
      page_count: null,
      warnings: null,
      request_id: null,
      filename: null,
    });
  });

  it("sends a given idempotency key and Prefer: respond-async", async () => {
    const { client, calls } = setup([json(job(), 202)]);
    await client.templates.render("tpl_1", { data: {}, idempotencyKey: "order-42", preferAsync: true });
    expect(calls[0]!.headers["idempotency-key"]).toBe("order-42");
    expect(calls[0]!.headers.prefer).toBe("respond-async");
  });

  it("returns the job on 202", async () => {
    const queued = job({ metadata: { order: "42" } });
    const { client } = setup([json(queued, 202)]);
    const result = await client.templates.render("tpl_1", {
      data: {},
      callback_url: "https://example.test/hook",
    });
    expect(result).toMatchObject({
      queued: true,
      pdf: null,
      job: queued,
      job_id: "job_1",
      template_version: 3,
      engine: null,
      page_count: null,
      warnings: null,
      request_id: null,
      filename: null,
    });
  });

  it("generates a fresh key per call", async () => {
    const { client, calls } = setup([pdf(), pdf()]);
    await client.templates.render("tpl_1", { data: {} });
    await client.templates.render("tpl_1", { data: {} });
    expect(calls[0]!.headers["idempotency-key"]).not.toBe(calls[1]!.headers["idempotency-key"]);
  });

  it("throws INVALID_RESPONSE if a 200 has no X-Job-Id", async () => {
    const { client } = setup([
      new Response(PDF_BYTES, { status: 200, headers: { "Content-Type": "application/pdf" } }),
    ]);
    await expect(client.templates.render("tpl_1", { data: {} })).rejects.toMatchObject({
      status: 200,
      code: "INVALID_RESPONSE",
    });
  });

  it.each([
    ["JSON", "application/json"],
    ["HTML", "text/html; charset=utf-8"],
    ["no content type", null],
  ])("throws INVALID_RESPONSE if a 200 render is %s, not a PDF", async (_name, type) => {
    const headers: Record<string, string> = { "X-Job-Id": "job_9", "X-Request-Id": "req_1" };
    if (type) headers["Content-Type"] = type;
    const { client, calls } = setup([new Response("<html>login</html>", { status: 200, headers })]);
    const err = await client.templates.render("tpl_1", { data: {} }).catch((e: unknown) => e);
    expect(err).toBeInstanceOf(PageRushError);
    expect(err).toMatchObject({ status: 200, code: "INVALID_RESPONSE", request_id: "req_1" });
    expect(calls).toHaveLength(1);
  });

  it("accepts a PDF content type with parameters", async () => {
    const { client } = setup([pdf({ "Content-Type": "Application/PDF; charset=binary" })]);
    await expect(client.templates.render("tpl_1", { data: {} })).resolves.toMatchObject({ queued: false });
  });

  it("throws INVALID_RESPONSE if a 202 body is not JSON", async () => {
    const { client } = setup([new Response("<html>", { status: 202 })]);
    const err = await client.templates.render("tpl_1", { data: {} }).catch((e: unknown) => e);
    expect(err).toBeInstanceOf(PageRushError);
    expect(err).toMatchObject({ status: 202, code: "INVALID_RESPONSE" });
  });

  it("throws INVALID_RESPONSE if a 202 body is not a job", async () => {
    const { client } = setup([json({ queued: true }, 202)]);
    await expect(client.templates.render("tpl_1", { data: {} })).rejects.toMatchObject({
      status: 202,
      code: "INVALID_RESPONSE",
    });
  });

  it("save writes the PDF, and refuses when queued", async () => {
    const dir = await mkdtemp(join(tmpdir(), "pagerush-test-"));
    try {
      const { client } = setup([pdf(), json(job(), 202)]);
      const done = await client.templates.render("tpl_1", { data: {} });
      await done.save(join(dir, "out.pdf"));
      expect(new Uint8Array(await readFile(join(dir, "out.pdf")))).toEqual(PDF_BYTES);

      const queued = await client.templates.render("tpl_1", { data: {} });
      await expect(queued.save(join(dir, "queued.pdf"))).rejects.toThrow(/queued as job job_1/);
    } finally {
      await rm(dir, { recursive: true, force: true });
    }
  });
});

describe("validation before sending", () => {
  it.each(["", "has space", "slash/inside", "é", "x".repeat(256), "line\nbreak"])(
    "rejects idempotencyKey %j without a request",
    async (idempotencyKey) => {
      const { client, calls } = setup([]);
      const err = await client.templates
        .render("tpl_1", { data: {}, idempotencyKey })
        .catch((e: unknown) => e);
      expect(err).toBeInstanceOf(PageRushError);
      expect(err).toMatchObject({ status: 0, code: "VALIDATION_FAILED" });
      expect(calls).toHaveLength(0);
    },
  );

  it("accepts every character the server allows", async () => {
    const { client, calls } = setup([pdf()]);
    const key = `Az09_-:.${"k".repeat(247)}`;
    await client.templates.render("tpl_1", { data: {}, idempotencyKey: key });
    expect(calls[0]!.headers["idempotency-key"]).toBe(key);
  });

  const cyclic: Record<string, unknown> = {};
  cyclic.self = cyclic;

  it.each([
    ["NaN", { total: Number.NaN }],
    ["Infinity", { total: Number.POSITIVE_INFINITY }],
    ["a BigInt", { total: 10n }],
    ["a function", { fn: () => 1 }],
    ["a symbol", { s: Symbol("x") }],
    ["an unpaired surrogate", { name: "bad \ud800 text" }],
    ["an unpaired surrogate in a key", { ["k\udc00"]: 1 }],
    ["a cycle", cyclic],
  ])("rejects data containing %s without a request", async (_name, data) => {
    const { client, calls } = setup([]);
    const err = await client.templates.render("tpl_1", { data }).catch((e: unknown) => e);
    expect(err).toBeInstanceOf(PageRushError);
    expect(err).toMatchObject({ status: 0, code: "VALIDATION_FAILED" });
    expect(calls).toHaveLength(0);
  });

  it("leaves out undefined properties and keeps valid surrogate pairs", async () => {
    const { client, calls } = setup([pdf()]);
    await client.templates.render("tpl_1", { data: { a: undefined, emoji: "😀" } });
    expect(JSON.parse(calls[0]!.body!)).toEqual({ data: { emoji: "😀" } });
  });
});
