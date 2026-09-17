/// <reference types="vitest/config" />
import { fileURLToPath } from 'node:url'
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
    // stage-desktop-pos: build multi-página — `pos.html` es el POS lite que sirve el shell de
    // Tauri (`src/Ways.Desktop`), `index.html` sigue siendo la app completa. El proxy de arriba
    // sigue funcionando para las dos: Vite en dev sirve cualquier `.html` de la raíz del proyecto.
    rollupOptions: {
      input: {
        index: fileURLToPath(new URL('./index.html', import.meta.url)),
        pos: fileURLToPath(new URL('./pos.html', import.meta.url)),
      },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    // `css: true` (etapa 18, slice 1): sin esto Vitest reemplaza cualquier import de `.css` —
    // incluido `?raw` — por un string vacío, y el test estructural de la named page
    // (`etiquetas.css`, mutation target 1) necesita el texto real del stylesheet.
    css: true,
  },
})
