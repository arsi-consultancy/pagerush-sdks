/** Runtime helpers that work in Node, Deno, Bun and Cloudflare Workers. @internal @module */

const encoder = new TextEncoder();
const decoder = new TextDecoder();

export const utf8Encode = (s: string): Uint8Array<ArrayBuffer> => encoder.encode(s);
export const utf8Decode = (b: Uint8Array): string => decoder.decode(b);

/** Normalises any accepted binary input to a Uint8Array without copying where possible. */
export function toBytes(input: string | Uint8Array | ArrayBuffer | ArrayBufferView): Uint8Array {
  if (typeof input === "string") return utf8Encode(input);
  if (input instanceof Uint8Array) return input;
  if (input instanceof ArrayBuffer) return new Uint8Array(input);
  if (ArrayBuffer.isView(input)) return new Uint8Array(input.buffer, input.byteOffset, input.byteLength);
  throw new TypeError("Expected a string, Uint8Array, Buffer or ArrayBuffer");
}

export function base64Encode(bytes: Uint8Array): string {
  let binary = "";
  const chunk = 0x8000;
  for (let i = 0; i < bytes.length; i += chunk) {
    binary += String.fromCharCode(...bytes.subarray(i, i + chunk));
  }
  return btoa(binary);
}

/** Set to `true` by the build for the CommonJS bundle and `false` for ESM; undefined in tests run from source. */
declare const __PAGERUSH_CJS__: boolean | undefined;

/**
 * Imports a Node built-in only when it is needed.
 *
 * The CommonJS build uses `require`, because a native `import()` fails in CommonJS loaders that run
 * modules through `vm.Script` (Jest, for one). The ESM build uses `import()` with a non-literal
 * specifier, so bundlers for edge runtimes (Workers, browsers) don't try to resolve it.
 */
export async function importNode<T>(specifier: string): Promise<T> {
  if (typeof __PAGERUSH_CJS__ !== "undefined" && __PAGERUSH_CJS__ && typeof require === "function") {
    return require(specifier) as T;
  }
  return (await import(/* @vite-ignore */ /* webpackIgnore: true */ specifier)) as T;
}

/**
 * Web Crypto: the global `crypto`, or `node:crypto`'s `webcrypto` on Node 18, where the global is
 * only available behind a flag.
 */
export async function webCrypto(): Promise<Crypto> {
  const g = (globalThis as { crypto?: Crypto }).crypto;
  if (g?.subtle) return g;
  const mod = await importNode<{ webcrypto: Crypto }>("node:crypto");
  return mod.webcrypto;
}

/** A random UUID v4. */
export async function uuidv4(): Promise<string> {
  return (await webCrypto()).randomUUID();
}

/** Reads an environment variable in Node, Bun or Deno; undefined elsewhere (e.g. Workers). */
export function env(name: string): string | undefined {
  const g = globalThis as {
    process?: { env?: Record<string, string | undefined> };
    Deno?: { env?: { get(k: string): string | undefined } };
  };
  try {
    const v = g.process?.env?.[name] ?? g.Deno?.env?.get(name);
    return v === "" ? undefined : v;
  } catch {
    return undefined; // Deno without --allow-env throws.
  }
}

/** The reason a signal was aborted. */
export function abortReason(signal: AbortSignal): unknown {
  return signal.reason ?? new DOMException("This operation was aborted", "AbortError");
}

/** Waits `ms`. If `signal` aborts first, the timer is cleared and the promise rejects with its reason. */
export function defaultSleep(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(abortReason(signal));
      return;
    }
    const onAbort = () => {
      clearTimeout(timer);
      reject(abortReason(signal!));
    };
    const timer = setTimeout(() => {
      signal?.removeEventListener("abort", onAbort);
      resolve();
    }, ms);
    signal?.addEventListener("abort", onAbort, { once: true });
  });
}

/**
 * Runs `sleep(ms, signal)`, and rejects as soon as `signal` aborts even if a custom `sleep` ignores
 * the signal.
 */
export function sleepUnlessAborted(
  sleep: (ms: number, signal?: AbortSignal) => Promise<void>,
  ms: number,
  signal: AbortSignal | undefined,
): Promise<void> {
  if (!signal) return sleep(ms);
  if (signal.aborted) return Promise.reject(abortReason(signal));
  return new Promise((resolve, reject) => {
    const onAbort = () => reject(abortReason(signal));
    signal.addEventListener("abort", onAbort, { once: true });
    sleep(ms, signal).then(
      () => {
        signal.removeEventListener("abort", onAbort);
        resolve();
      },
      (err: unknown) => {
        signal.removeEventListener("abort", onAbort);
        reject(err);
      },
    );
  });
}

/** Writes bytes to a local file through `node:fs/promises` (Node, Bun, Deno). */
export async function writeLocalFile(path: string, bytes: Uint8Array): Promise<void> {
  const fs = await importNode<{ writeFile(p: string, d: Uint8Array): Promise<void> }>("node:fs/promises");
  await fs.writeFile(path, bytes);
}
