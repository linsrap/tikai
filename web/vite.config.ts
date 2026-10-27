import react from '@vitejs/plugin-react'
import { configDefaults, defineConfig } from 'vitest/config'

// The API runs on http://localhost:5080 in development (src/Tikai.Api launchSettings).
const api = 'http://localhost:5080'

export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': api,
      '/hubs': { target: api, ws: true },
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    // Browser tests run with Playwright (npm run e2e).
    exclude: [...configDefaults.exclude, 'e2e/**'],
  },
})
