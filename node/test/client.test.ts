import { readFileSync } from "node:fs";
import { inspect } from "node:util";
import { afterEach, describe, expect, it, vi } from "vitest";
import { DEFAULT_BASE_URL, PageRush, VERSION, webhooks } from "../src";
import { json, mockFetch, TEST_KEY, template } from "./helpers";

describe("version", () => {
  it("matches package.json", () => {
    const pkg = JSON.parse(readFileSync(new URL("../package.json", import.meta.url), "utf8")) as {
      version: string;
    };
    expect(VERSION).toBe(pkg.version);
  });
});

describe("PageRush constructor", () => {
  afterEach(() => {
    vi.unstubAllEnvs();
  });

  it("sends auth, user agent and accept headers", async () => {
    const { fetch, calls } = mockFetch([json({ template })]);
    const client = new PageRush({ apiKey: TEST_KEY, fetch });
    await client.templates.get("tpl_1");
    expect(calls[0]!.url).toBe(`${DEFAULT_BASE_URL}/v1/templates/tpl_1`);
    expect(calls[0]!.headers.authorization).toBe(`Bearer ${TEST_KEY}`);
    expect(calls[0]!.headers["user-agent"]).toBe(`pagerush-node/${VERSION}`);
    expect(calls[0]!.headers.accept).toBe("application/json");
    expect(calls[0]!.headers["content-type"]).toBeUndefined();
  });

  it("reads the API key and base URL from the environment", async () => {
    vi.stubEnv("PAGERUSH_API_KEY", "pk_test_env");
    vi.stubEnv("PAGERUSH_BASE_URL", "https://env.example.test/");
    const { fetch, calls } = mockFetch([json({ template })]);
    const client = new PageRush({ fetch });
    expect(client.baseUrl).toBe("https://env.example.test");
    await client.templates.get("tpl_1");
    expect(calls[0]!.url).toBe("https://env.example.test/v1/templates/tpl_1");
    expect(calls[0]!.headers.authorization).toBe("Bearer pk_test_env");
  });

  it("prefers explicit options over the environment", () => {
    vi.stubEnv("PAGERUSH_BASE_URL", "https://env.example.test");
    const client = new PageRush({ apiKey: TEST_KEY, baseUrl: "https://opt.example.test///" });
    expect(client.baseUrl).toBe("https://opt.example.test");
  });

  it("throws without an API key", () => {
    vi.stubEnv("PAGERUSH_API_KEY", "");
    expect(() => new PageRush()).toThrow(/PAGERUSH_API_KEY/);
  });

  it("exposes the webhook helpers", () => {
    expect(new PageRush({ apiKey: TEST_KEY }).webhooks).toBe(webhooks);
  });
});

describe("API key handling", () => {
  afterEach(() => {
    vi.unstubAllEnvs();
  });

  it("never shows the key in console.log or JSON.stringify output", () => {
    const client = new PageRush({ apiKey: TEST_KEY, baseUrl: "https://api.example.test" });
    const outputs = [
      inspect(client, { depth: Infinity, showHidden: true }),
      inspect(client.templates, { depth: Infinity, showHidden: true }),
      inspect(client.jobs, { depth: Infinity, showHidden: true }),
      JSON.stringify(client),
      JSON.stringify(client.templates),
      JSON.stringify(client.jobs),
      String(client),
    ];
    for (const out of outputs) expect(out).not.toContain(TEST_KEY);
    expect(inspect(client)).toBe("PageRush { baseUrl: 'https://api.example.test' }");
    expect(JSON.stringify(client)).toBe('{"baseUrl":"https://api.example.test"}');
  });

  it("trims whitespace around the key", async () => {
    vi.stubEnv("PAGERUSH_API_KEY", `  ${TEST_KEY}\r\n`);
    const { fetch, calls } = mockFetch([json({ template })]);
    await new PageRush({ fetch }).templates.get("tpl_1");
    expect(calls[0]!.headers.authorization).toBe(`Bearer ${TEST_KEY}`);
  });

  it.each([
    ["an inner space", "pk_test 123"],
    ["a newline", "pk_test\n123"],
    ["a NUL byte", "pk_test\u0000123"],
    ["a non-ASCII character", "pk_test_123é"],
  ])("rejects a key with %s without echoing it", (_name, key) => {
    let error: unknown;
    try {
      new PageRush({ apiKey: key });
    } catch (err) {
      error = err;
    }
    expect(error).toBeInstanceOf(TypeError);
    expect((error as Error).message).toMatch(/spaces or control characters/);
    expect((error as Error).message).not.toContain("pk_test");
  });

  it("treats a blank key as missing", () => {
    expect(() => new PageRush({ apiKey: "   " })).toThrow(/no API key/);
  });

  it.each(["ftp://example.test", "example.test", "not a url", "https://"])(
    "rejects baseUrl %s",
    (baseUrl) => {
      expect(() => new PageRush({ apiKey: TEST_KEY, baseUrl })).toThrow(TypeError);
    },
  );
});
