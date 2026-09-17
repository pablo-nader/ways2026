/** URL del instalador de Ways POS (stage-desktop-pos): apunta al asset publicado en el último
 * GitHub Release del repo público `pablo-nader/ways2026`. `VITE_URL_DESCARGA_POS` permite
 * apuntar a otro origen (por ejemplo, un build de prueba) sin tocar código. */
export const URL_DESCARGA_POS_POR_DEFECTO =
  'https://github.com/pablo-nader/ways2026/releases/latest/download/WaysPOS-setup.exe'

/** Función pura para poder testearla sin depender de `import.meta.env` en el test. */
export function urlDescargaPos(env: { VITE_URL_DESCARGA_POS?: string } = import.meta.env): string {
  return env.VITE_URL_DESCARGA_POS || URL_DESCARGA_POS_POR_DEFECTO
}
