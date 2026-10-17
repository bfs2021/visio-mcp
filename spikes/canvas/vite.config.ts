import { defineConfig } from 'vite';
import { resolve } from 'node:path';

// `vite` (dev, used by the Playwright probe) serves index.html; `vite build` also emits webview.html for the WPF/WebView2 host.
export default defineConfig({
  base: './',
  build: {
    target: 'es2022',
    assetsDir: 'app', // /assets/<sha256> is reserved for host-served images
    rollupOptions: { input: { index: resolve(import.meta.dirname, 'index.html'), webview: resolve(import.meta.dirname, 'webview.html') } },
  },
});
