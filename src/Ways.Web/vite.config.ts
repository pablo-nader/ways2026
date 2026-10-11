/// <reference types="vitest/config" />
import { fileURLToPath } from 'node:url'
import { defineConfig } from 'vite'
import type { Plugin, PreviewServer, ViteDevServer } from 'vite'
import react from '@vitejs/plugin-react'
import { reescribirUrlDeDesarrollo } from './src/config/entradasDeVite.ts'

// En producción la API sirve cada entrada por prefijo (`/admin`, `/online`, el resto la landing);
// en desarrollo y en `vite preview` lo hace este plugin con la misma regla.
function entradasPorPrefijo(): Plugin {
  const reescribir = (servidor: ViteDevServer | PreviewServer) => {
    servidor.middlewares.use((req, _res, next) => {
      if (req.url) req.url = reescribirUrlDeDesarrollo(req.url)
      next()
    })
  }

  return { name: 'ways-entradas-por-prefijo', configureServer: reescribir, configurePreviewServer: reescribir }
}

// En producción el front lo sirve la propia API desde wwwroot, así que no hay CORS.
// En desarrollo se proxea /api al Kestrel local.
export default defineConfig({
  plugins: [react(), entradasPorPrefijo()],
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
    // Build multi-página: `index.html` es la landing del comercio, `admin.html` la app completa
    // (back office y POS web, bajo `/admin`), `online.html` la tienda (bajo `/online`) y `pos.html`
    // el POS lite que sirve el shell de Tauri (`src/Ways.Desktop`). El proxy de arriba vale para
    // todas: Vite en dev sirve cualquier `.html` de la raíz del proyecto.
    rollupOptions: {
      input: {
        index: fileURLToPath(new URL('./index.html', import.meta.url)),
        admin: fileURLToPath(new URL('./admin.html', import.meta.url)),
        online: fileURLToPath(new URL('./online.html', import.meta.url)),
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
