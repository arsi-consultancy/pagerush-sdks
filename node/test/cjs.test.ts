import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { createRequire } from "node:module";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { Script } from "node:vm";
import { build } from "tsup";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import config from "../tsup.config";
import type * as Sdk from "../src";

/**
 * Builds the CommonJS bundle and loads it the way Jest's CommonJS runtime does: wrapped in a
 * function and run through `vm.Script` with no dynamic-import callback. Native `import()` throws
 * ERR_VM_DYNAMIC_IMPORT_CALLBACK_MISSING there, so the bundle must reach Node built-ins through
 * `require` instead.
 */
describe("CommonJS bundle under a vm.Script loader", () => {
  let dir: string;
  let sdk: typeof Sdk;

  beforeAll(async () => {
    dir = await mkdtemp(join(tmpdir(), "pagerush-cjs-"));
    await build({
      ...config,
      format: ["cjs"],
      outDir: dir,
      dts: false,
      sourcemap: false,
      clean: false,
      silent: true,
    });
    const file = join(dir, "index.cjs");
    const code = await readFile(file, "utf8");
    const wrapper = new Script(`(function (exports, require, module, __filename, __dirname) {${code}\n})`, {
      filename: file,
    }).runInThisContext() as (...args: unknown[]) => void;
    const mod = { exports: {} as typeof Sdk };
    wrapper(mod.exports, createRequire(file), mod, file, dirname(file));
    sdk = mod.exports;
  }, 30_000);

  afterAll(async () => {
    await rm(dir, { recursive: true, force: true });
  });

  it("reads files with fileFromPath", async () => {
    const path = join(dir, "logo.png");
    await writeFile(path, new Uint8Array([1, 2, 3]));
    await expect(sdk.fileFromPath(path)).resolves.toEqual({
      path: "logo.png",
      content: "AQID",
      encoding: "base64",
    });
  });

  it("saves a rendered PDF", async () => {
    const fetch = async () =>
      new Response(new TextEncoder().encode("%PDF-1.7"), {
        status: 200,
        headers: { "Content-Type": "application/pdf", "X-Job-Id": "job_1" },
      });
    const client = new sdk.PageRush({ apiKey: "pk_test_123", baseUrl: "https://api.example.test", fetch });
    const result = await client.templates.render("tpl_1", { data: {} });
    const out = join(dir, "out.pdf");
    await result.save(out);
    expect(await readFile(out, "utf8")).toBe("%PDF-1.7");
  });

  it("verifies webhooks", async () => {
    await expect(
      sdk.verify(
        "x",
        {
          "x-pagerush-signature": "t=1,v2=00",
          "x-pagerush-event": "pdf.succeeded",
          "x-pagerush-job-id": "job_1",
        },
        "whsec_test",
        { now: 1 },
      ),
    ).rejects.toMatchObject({
      reason: "signature",
    });
  });
});
