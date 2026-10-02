// Arma el manifiesto `latest.json` que consulta tauri-plugin-updater.
// Uso: node scripts/generar-latest-json.mjs <version> <instalador.exe> <instalador.exe.sig> <url-del-instalador> <salida>
import { existsSync, readFileSync, writeFileSync } from 'node:fs'

const [version, instalador, firma, url, salida] = process.argv.slice(2)
if (!version || !instalador || !firma || !url || !salida) {
  console.error('Uso: generar-latest-json.mjs <version> <instalador.exe> <instalador.exe.sig> <url-del-instalador> <salida>')
  process.exit(1)
}

if (!/^\d+\.\d+\.\d+$/.test(version)) {
  console.error(`Versión inválida: '${version}'`)
  process.exit(1)
}

const nombreDelInstalador = instalador.replace(/\\/g, '/').split('/').pop()
if (!url.endsWith(`/${encodeURIComponent(nombreDelInstalador)}`)) {
  console.error(`La URL '${url}' no apunta al instalador firmado '${nombreDelInstalador}'.`)
  process.exit(1)
}

if (!existsSync(instalador)) {
  console.error(`No existe el instalador '${instalador}'.`)
  process.exit(1)
}
const signature = readFileSync(firma, 'utf8').trim()
if (signature === '') {
  console.error(`La firma '${firma}' está vacía.`)
  process.exit(1)
}

const manifiesto = {
  version,
  notes: `Ways POS ${version}`,
  pub_date: new Date().toISOString(),
  platforms: {
    'windows-x86_64': { signature, url },
  },
}

writeFileSync(salida, `${JSON.stringify(manifiesto, null, 2)}\n`)
console.log(`latest.json generado para ${version} -> ${url}`)
