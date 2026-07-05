import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// The build output lands directly in the backend's wwwroot so `dotnet run` serves the SPA.
// Dev server proxies API/hub/static routes to the running backend on :5000.
export default defineConfig({
  plugins: [vue()],
  build: {
    outDir: '../backend/Tubelet/wwwroot',
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5000',
      '/cache': 'http://localhost:5000',
      '/media': 'http://localhost:5000',
      '/hub': { target: 'http://localhost:5000', ws: true },
    },
  },
})
