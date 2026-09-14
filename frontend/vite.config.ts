/// <reference types="vitest/config" />
import https from 'node:https'
import react from '@vitejs/plugin-react'
import basicSsl from '@vitejs/plugin-basic-ssl'
import { defineConfig } from 'vite'

// A long-running E2E session (many sequential real round-trips against the dev proxy) can hit a
// known Node http-proxy/http-agent issue where a pooled keep-alive socket to the backend goes
// stale and the next request on it hangs forever instead of erroring — the ASP.NET Core host
// itself stays healthy throughout (confirmed via direct requests), only the proxied connection
// wedges. A non-keep-alive agent opens a fresh socket per proxied request instead of reusing a
// pool, trading a little latency for not silently hanging under sustained E2E load.
const proxyAgent = new https.Agent({ keepAlive: false, rejectUnauthorized: false })

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), basicSsl()],
  server: {
    https: {},
    // Same-origin cookie auth (ADR-0009 §1) needs the browser to see the API under the SAME
    // origin as the frontend in dev too — the dev server proxies /api and /health through to
    // the ASP.NET Core host instead of the browser calling it cross-origin.
    proxy: {
      '/api': {
        target: 'https://localhost:7246',
        changeOrigin: true,
        secure: false, // the local dev HTTPS certificate is self-signed
        agent: proxyAgent,
      },
      '/health': {
        target: 'https://localhost:7246',
        changeOrigin: true,
        secure: false,
        agent: proxyAgent,
      },
    },
  },
  // `vite preview` serves the production build the E2E suite actually runs against — the same
  // same-origin proxy is needed here too, and this path avoids the dev server's per-module
  // transform/HMR pipeline entirely for a long, sustained sequential E2E session.
  preview: {
    https: {},
    proxy: {
      '/api': {
        target: 'https://localhost:7246',
        changeOrigin: true,
        secure: false,
        agent: proxyAgent,
      },
      '/health': {
        target: 'https://localhost:7246',
        changeOrigin: true,
        secure: false,
        agent: proxyAgent,
      },
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    css: true,
  },
})
