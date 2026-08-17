import { compararPayloads } from './compararPayloads'

type PropsPanelDeCambio = {
  valorAnterior: Record<string, unknown> | null
  valorNuevo: Record<string, unknown>
}

/** Exportada para el test colocado (`PanelDeCambio.test.tsx`, `web-descriptor-tests`: todo
 * formatter/mapper puro nuevo lleva su propio test unitario, sin DOM). */
export function formatearValor(valor: unknown): string {
  if (valor === undefined) return '—'
  if (valor === null) return 'null'
  if (typeof valor === 'object') return JSON.stringify(valor)
  return String(valor)
}

/**
 * Panel de detalle expandible de `Auditoria.tsx` (stage-14-auditoria-trazabilidad, Slice 7;
 * design: "Web composition — Auditoria.tsx", Panel de detalle) — pre-aprobado como corte si la
 * slice desborda (los payloads siguen llegando por el export). Renderiza `valor_anterior`/
 * `valor_nuevo` clave por clave vía `compararPayloads`, con `data-testid` propio por lado
 * (`panel-cambio-anterior-<clave>` / `panel-cambio-nuevo-<clave>`) para que los tests apunten a
 * un lado sin ambigüedad. Una clave agregada muestra "—" del lado anterior (design literal:
 * "—→ valor") — estética más allá de los testids está exenta (Testing Strategy, fila "Exempt").
 */
export function PanelDeCambio({ valorAnterior, valorNuevo }: PropsPanelDeCambio) {
  const comparaciones = compararPayloads(valorAnterior, valorNuevo)

  return (
    <table className="table table-sm table-borderless mb-0">
      <thead>
        <tr>
          <th>Clave</th>
          <th>Valor anterior</th>
          <th>Valor nuevo</th>
        </tr>
      </thead>
      <tbody>
        {comparaciones.map((c) => (
          <tr key={c.clave} className={c.estado === 'sin_cambio' ? undefined : 'table-warning'}>
            <td className="text-muted">{c.clave}</td>
            <td data-testid={`panel-cambio-anterior-${c.clave}`}>
              {c.estado === 'agregada' ? '—' : formatearValor(c.valorAnterior)}
            </td>
            <td data-testid={`panel-cambio-nuevo-${c.clave}`}>{formatearValor(c.valorNuevo)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}
