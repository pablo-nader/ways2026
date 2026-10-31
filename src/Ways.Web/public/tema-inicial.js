// Aplica el tema guardado antes del primer pintado para que el modo claro no parpadee en oscuro.
// La clave es la misma de CLAVE_DE_TEMA en src/tema/tema.ts; este archivo no pasa por el bundler.
try {
  if (localStorage.getItem('ways.tema') === 'claro') document.documentElement.dataset.bsTheme = 'light'
} catch {
  // Sin almacenamiento disponible queda el tema oscuro por defecto del HTML.
}
