export type EstadoDeClave = 'agregada' | 'cambiada' | 'sin_cambio'

export type ComparacionDeClave = {
  clave: string
  valorAnterior: unknown
  valorNuevo: unknown
  estado: EstadoDeClave
}

function sonIguales(a: unknown, b: unknown): boolean {
  if (a === b) return true
  if (a === null || b === null) return false
  return JSON.stringify(a) === JSON.stringify(b)
}

/**
 * Compara `valor_anterior`/`valor_nuevo` clave por clave para `PanelDeCambio`
 * (stage-14-auditoria-trazabilidad, Slice 7; design: "Web composition — Auditoria.tsx", Panel de
 * detalle). `anterior` es `null` para las acciones "hecho puro" (`usuario.password`,
 * `usuario.alta`) — TODAS las claves de `nuevo` se muestran como `'agregada'` en ese caso.
 *
 * Invariante del dominio (`RegistroDeAuditoria`, Slice 1): las claves de `anterior` son SIEMPRE
 * un subconjunto de las de `nuevo` — nunca hay una clave "solo en anterior" que mostrar, así que
 * este helper solo itera sobre las claves de `nuevo`.
 *
 * mutation target (slice 7, tasks.md fila única de esta slice): una clave presente SOLO en
 * `nuevo` (ausente en `anterior`, o `anterior` entero `null`) DEBE marcarse `'agregada'`, nunca
 * `'sin_cambio'` — el test colocado (`compararPayloads.test.ts`) es el discriminador.
 */
export function compararPayloads(
  anterior: Record<string, unknown> | null,
  nuevo: Record<string, unknown>,
): ComparacionDeClave[] {
  return Object.keys(nuevo).map((clave) => {
    const valorNuevo = nuevo[clave]

    if (anterior === null || !(clave in anterior)) {
      return { clave, valorAnterior: undefined, valorNuevo, estado: 'agregada' }
    }

    const valorAnterior = anterior[clave]
    return {
      clave,
      valorAnterior,
      valorNuevo,
      estado: sonIguales(valorAnterior, valorNuevo) ? 'sin_cambio' : 'cambiada',
    }
  })
}
