import path from "node:path";
import { fileURLToPath, URL } from "node:url";
import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";
import tailwindcss from "@tailwindcss/vite";

// GitHub Pages serves the repo's docs/ folder at /muse-chief-relay/.
// Relative asset URLs so the built client works at /muse-chief-relay/muse/
// and at the root of any static host (python -m http.server, npm run preview).
//
// The default outDir is the committed fallback at docs/muse/, which is built
// with VITE_WATCH_CHANNEL and VITE_RELAY_URL unset. The Pages workflow sets MUSE_BUILD_OUTDIR to
// a runner temp directory so the secret-bearing build is uploaded and never
// written into the checkout. A relative override is resolved from the cwd.
const defaultOutDir = fileURLToPath(new URL("../../docs/muse", import.meta.url));
const override = process.env.MUSE_BUILD_OUTDIR && process.env.MUSE_BUILD_OUTDIR.trim();
const outDir = override ? path.resolve(override) : defaultOutDir;

export default defineConfig({
  plugins: [vue(), tailwindcss()],
  base: "./",
  build: {
    outDir,
    emptyOutDir: true,
    sourcemap: false,
  },
});
