#!/usr/bin/env node
// Sincroniza el build de Ways.Web (`pos.html` + sus assets) dentro de `ui/`, el frontendDist de
// Tauri (ver `tauri.conf.json`, `build.frontendDist`). Se ejecuta antes de `tauri dev`/`tauri
// build` via `beforeDevCommand`/`beforeBuildCommand`.
//
// `ui/index.html`, `ui/setup.js` y `ui/styles.css` son la pagina de configuracion, escrita a mano
// -- este script nunca los toca ni los borra (nunca aparecen en `dist/`, Ways.Web no los conoce).
// `dist/index.html` (la app completa para navegador) tampoco se copia a proposito: la app de
// escritorio solo necesita `pos.html`; copiar el `index.html` de la app completa pisaria la
// pagina de configuracion con la app entera.
//
// Todas las rutas se resuelven relativas a este archivo (nunca a `process.cwd()`), para no
// depender de desde donde el CLI de Tauri decide invocar este script.
import { execSync } from 'node:child_process'
import { cpSync, readdirSync, rmSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const directorioDeEsteScript = path.dirname(fileURLToPath(import.meta.url))
const directorioWaysWeb = path.resolve(directorioDeEsteScript, '..', '..', 'Ways.Web')
const directorioDist = path.join(directorioWaysWeb, 'dist')
const directorioUi = path.resolve(directorioDeEsteScript, '..', 'ui')

console.log('[sync-frontend-local] compilando Ways.Web...')
// `execSync` (comando único, siempre vía shell) en vez de `execFileSync` con un array de args y
// `shell: true`: Node advierte (DEP0190) que esa combinación no escapa los args, sin efecto
// práctico acá (no hay entrada externa) pero evitable sin costo con `execSync`.
execSync('npm run build', { cwd: directorioWaysWeb, stdio: 'inherit' })

for (const entrada of readdirSync(directorioDist)) {
  if (entrada === 'index.html') continue

  const origen = path.join(directorioDist, entrada)
  const destino = path.join(directorioUi, entrada)
  rmSync(destino, { recursive: true, force: true })
  cpSync(origen, destino, { recursive: true })
}

console.log('[sync-frontend-local] listo: pos.html y sus assets quedaron en ui/')
