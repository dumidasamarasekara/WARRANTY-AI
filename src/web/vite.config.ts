/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// The API URL comes from Aspire service discovery when run by the AppHost (T039); the fallback
// matches the API's local https launch profile.
const apiUrl =
  process.env.services__api__https__0 ?? process.env.services__api__http__0 ?? 'https://localhost:7443'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: true,
    // Tenant claimant channels are resolved by the API from the Host header (research R9).
    allowedHosts: ['localhost', 'aurora.localhost', 'borealis.localhost'],
    proxy: {
      '/api': {
        target: apiUrl,
        // Keep the original Host header so the API can map aurora.localhost / borealis.localhost.
        changeOrigin: false,
        secure: false,
      },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./tests/setup.ts'],
    include: ['tests/**/*.test.{ts,tsx}', 'src/**/*.test.{ts,tsx}'],
    css: { modules: { classNameStrategy: 'non-scoped' } },
  },
})
