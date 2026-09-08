/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    // Same-origin cookie auth (ADR-0009 §1) needs the browser to see the API under the SAME
    // origin as the frontend in dev too — the dev server proxies /api and /health through to
    // the ASP.NET Core host instead of the browser calling it cross-origin.
    proxy: {
      '/api': {
        target: 'https://localhost:7246',
        changeOrigin: true,
        secure: false, // the local dev HTTPS certificate is self-signed
      },
      '/health': {
        target: 'https://localhost:7246',
        changeOrigin: true,
        secure: false,
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
