/** Helpers for building template file entries. @module */

import { base64Encode, importNode, toBytes, utf8Decode } from "./internal";
import type { TemplateFile } from "./types";

/** Extensions sent as `utf8`. Everything else (images, fonts, PDFs) is sent as `base64`. */
export const TEXT_EXTENSIONS: readonly string[] = [
  ".typ",
  ".txt",
  ".csv",
  ".json",
  ".svg",
  ".xml",
  ".md",
  ".yml",
  ".yaml",
  ".bib",
];

function isText(path: string): boolean {
  const name = path.slice(path.lastIndexOf("/") + 1).toLowerCase();
  const dot = name.lastIndexOf(".");
  return dot >= 0 && TEXT_EXTENSIONS.includes(name.slice(dot));
}

/**
 * Builds a `{ path, content, encoding }` entry for {@link CreateTemplateParams.files}.
 *
 * The encoding comes from the extension of `path`: `utf8` for `.typ .txt .csv .json .svg .xml .md
 * .yml .yaml .bib`, `base64` for everything else. A string `contents` is taken as text (its UTF-8
 * bytes are base64-encoded for binary extensions); pass a `Uint8Array`/`Buffer` for binary files.
 *
 * @param path - Path in the template project, relative to `main.typ`, e.g. `assets/logo.png`.
 * @param contents - The file's contents.
 *
 * @example
 * ```ts
 * import { readFile } from "node:fs/promises";
 * const files = [
 *   templateFile("parts/header.typ", "#let header = [ACME Ltd]"),
 *   templateFile("logo.png", await readFile("logo.png")),
 * ];
 * ```
 */
export function templateFile(path: string, contents: string | Uint8Array | ArrayBuffer): TemplateFile {
  if (isText(path)) {
    return {
      path,
      content: typeof contents === "string" ? contents : utf8Decode(toBytes(contents)),
      encoding: "utf8",
    };
  }
  return { path, content: base64Encode(toBytes(contents)), encoding: "base64" };
}

/**
 * Reads a local file and builds a template file entry with {@link templateFile}. Node, Bun and Deno
 * only (it uses `node:fs/promises`).
 *
 * @param localPath - File on disk to read.
 * @param targetPath - Path in the template project. Defaults to the file's base name.
 *
 * @example
 * ```ts
 * const logo = await fileFromPath("./assets/logo.png", "logo.png");
 * ```
 */
export async function fileFromPath(localPath: string, targetPath?: string): Promise<TemplateFile> {
  const fs = await importNode<{ readFile(p: string): Promise<Uint8Array> }>("node:fs/promises");
  const bytes = await fs.readFile(localPath);
  const name = targetPath ?? localPath.split(/[\\/]/).pop() ?? localPath;
  return templateFile(name, bytes);
}
