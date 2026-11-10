import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { fileFromPath, TEXT_EXTENSIONS, templateFile } from "../src";

describe("templateFile", () => {
  it.each(TEXT_EXTENSIONS)("sends %s as utf8", (ext) => {
    expect(templateFile(`dir/file${ext}`, "héllo")).toEqual({
      path: `dir/file${ext}`,
      content: "héllo",
      encoding: "utf8",
    });
  });

  it("matches extensions case-insensitively", () => {
    expect(templateFile("DATA.CSV", "a,b").encoding).toBe("utf8");
  });

  it("decodes bytes for text files", () => {
    expect(templateFile("a.typ", new TextEncoder().encode("= Title")).content).toBe("= Title");
  });

  it("base64-encodes binary files", () => {
    const png = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x00, 0xff]);
    expect(templateFile("logo.png", png)).toEqual({
      path: "logo.png",
      content: "iVBORwD/",
      encoding: "base64",
    });
    expect(templateFile("font.ttf", png.buffer).content).toBe("iVBORwD/");
  });

  it("treats files without an extension as binary", () => {
    expect(templateFile("LICENSE", "MIT")).toEqual({ path: "LICENSE", content: "TUlU", encoding: "base64" });
    expect(templateFile("dir.typ/blob", "MIT").encoding).toBe("base64");
  });

  it("encodes large binaries", () => {
    const big = new Uint8Array(200_000).map((_, i) => i % 256);
    expect(templateFile("big.bin", big).content).toBe(Buffer.from(big).toString("base64"));
  });
});

describe("fileFromPath", () => {
  it("reads a file and defaults the path to its base name", async () => {
    const dir = await mkdtemp(join(tmpdir(), "pagerush-test-"));
    try {
      await writeFile(join(dir, "logo.png"), new Uint8Array([1, 2, 3]));
      await writeFile(join(dir, "header.typ"), "#let x = 1");
      expect(await fileFromPath(join(dir, "logo.png"))).toEqual({
        path: "logo.png",
        content: "AQID",
        encoding: "base64",
      });
      expect(await fileFromPath(join(dir, "header.typ"), "parts/header.typ")).toEqual({
        path: "parts/header.typ",
        content: "#let x = 1",
        encoding: "utf8",
      });
    } finally {
      await rm(dir, { recursive: true, force: true });
    }
  });
});
