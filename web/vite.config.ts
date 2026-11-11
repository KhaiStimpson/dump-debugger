import { defineConfig, type Plugin } from 'vite';
import react from '@vitejs/plugin-react';

// Built assets are served by DumpDebugger.Host from a WebView2 virtual host, so paths stay
// relative and everything is bundled locally (no CDN, works offline).

/** Lock the shipped page down. Dev (HMR) needs inline scripts, so this only applies to builds. */
const csp = (): Plugin => ({
  name: 'dd-csp',
  apply: 'build',
  transformIndexHtml: (html) =>
    html.replace(
      '<meta charset="utf-8" />',
      `<meta charset="utf-8" />\n    <meta http-equiv="Content-Security-Policy" content="default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; script-src 'self'; worker-src 'self' blob:" />`,
    ),
});

export default defineConfig({
  plugins: [react(), csp()],
  base: './',
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    target: 'es2022',
    chunkSizeWarningLimit: 2000,
  },
  server: { port: 5173, strictPort: true },
  test: { environment: 'node', include: ['src/**/*.test.ts'] },
} as never);
