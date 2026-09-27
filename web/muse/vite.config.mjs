import { fileURLToPath, URL } from "node:url";
import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";
import tailwindcss from "@tailwindcss/vite";

// GitHub Pages serves the repo's docs/ folder at /muse-chief-relay/.
// Relative asset URLs so the built client works at /muse-chief-relay/muse/
// and at the root of any static host (python -m http.server, npm run preview).
export default defineConfig({
  plugins: [vue(), tailwindcss()],
  base: "./",
  build: {
    outDir: fileURLToPath(new URL("../../docs/muse", import.meta.url)),
    emptyOutDir: true,
    sourcemap: false,
  },
});
