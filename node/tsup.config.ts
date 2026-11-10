import { defineConfig } from "tsup";

export default defineConfig({
  entry: ["src/index.ts"],
  format: ["esm", "cjs"],
  dts: true,
  sourcemap: true,
  clean: true,
  target: "es2022",
  platform: "neutral",
  treeshake: true,
  esbuildOptions(options, context) {
    options.define = { ...options.define, __PAGERUSH_CJS__: context.format === "cjs" ? "true" : "false" };
  },
});
