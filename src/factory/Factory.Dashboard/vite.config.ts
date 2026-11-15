import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

/**
 * The dashboard, built into factory/dashboard/ (committed, so neither install needs Node; contract,
 * "Dashboard API"). `npm run dev` serves it on its own port and proxies the API and the state files
 * to a running `factory serve` (FACTORY_SERVE_URL, else the Playwright simulator's port).
 */
const SERVE = process.env.FACTORY_SERVE_URL ?? 'http://localhost:4318'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  build: {
    outDir: fileURLToPath(new URL('../../../factory/dashboard', import.meta.url)),
    emptyOutDir: true,
  },
  server: {
    port: Number(process.env.PORT) || 4321,
    strictPort: true,
    proxy: { '/api': SERVE, '/state': SERVE },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    coverage: {
      provider: 'v8',
      include: ['src/**/*.{ts,tsx}'],
      exclude: ['src/main.tsx', 'src/test/**', 'src/**/*.test.{ts,tsx}'],
      thresholds: { lines: 70 },
    },
  },
})
