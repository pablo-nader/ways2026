import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// En producción el front lo sirve la propia API desde wwwroot, así que no hay CORS.
// En desarrollo se proxea /api al Kestrel local.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://localhost:5080',
        changeOrigin: true,
      },
    },
  },
  build: {
    outDir: 'dist',
    sourcemap: true,
  },
})
