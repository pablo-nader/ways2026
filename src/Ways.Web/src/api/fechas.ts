/** Límites de día con el offset local explícito, para los filtros `desde`/`hasta` que el servidor
 * enlaza como `DateTimeOffset` y compara contra un `timestamptz`. */
function desplazamientoUtcLocal(anio: number, mes: number, dia: number): string {
  const minutos = new Date(anio, mes - 1, dia).getTimezoneOffset()
  const signo = minutos > 0 ? '-' : '+'
  const minutosAbsolutos = Math.abs(minutos)
  const horas = String(Math.floor(minutosAbsolutos / 60)).padStart(2, '0')
  const restoMinutos = String(minutosAbsolutos % 60).padStart(2, '0')
  return `${signo}${horas}:${restoMinutos}`
}

export function fechaIsoConOffset(fechaIso: string, horaLimite: string): string {
  const [anio, mes, dia] = fechaIso.split('-').map(Number)
  return `${fechaIso}T${horaLimite}${desplazamientoUtcLocal(anio, mes, dia)}`
}
