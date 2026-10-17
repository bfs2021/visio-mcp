import { defineConfig } from 'vite';

// The host serves the build output at https://app.agentic-diagram.invalid/ and reserves /assets/<sha256> for image bytes,
// so bundles go to /app/.
export default defineConfig({
  base: './',
  build: { target: 'es2022', outDir: 'dist', assetsDir: 'app', sourcemap: false },
});
