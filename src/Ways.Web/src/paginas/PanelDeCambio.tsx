import { useMemo } from 'react'
import { EncabezadoOrdenable, useOrdenamiento, type ColumnasOrdenables } from '../componentes/grilla'
import { compararPayloads, type ComparacionDeClave } from './compararPayloads'

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

function textoAnterior(c: ComparacionDeClave): string {
  return c.estado === 'agregada' ? '—' : formatearValor(c.valorAnterior)
}

const COLUMNAS: ColumnasOrdenables<ComparacionDeClave> = {
  clave: { valor: (c) => c.clave },
  anterior: { valor: textoAnterior },
  nuevo: { valor: (c) => formatearValor(c.valorNuevo) },
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
  const comparaciones = useMemo(() => compararPayloads(valorAnterior, valorNuevo), [valorAnterior, valorNuevo])
  const { filas, orden, alternar } = useOrdenamiento(comparaciones, COLUMNAS)

  return (
    <table className="table table-sm table-borderless mb-0">
      <thead>
        <tr>
          <EncabezadoOrdenable columna="clave" orden={orden} onAlternar={alternar}>
            Clave
          </EncabezadoOrdenable>
          <EncabezadoOrdenable columna="anterior" orden={orden} onAlternar={alternar}>
            Valor anterior
          </EncabezadoOrdenable>
          <EncabezadoOrdenable columna="nuevo" orden={orden} onAlternar={alternar}>
            Valor nuevo
          </EncabezadoOrdenable>
        </tr>
      </thead>
      <tbody>
        {filas.map((c) => (
          <tr key={c.clave} className={c.estado === 'sin_cambio' ? undefined : 'table-warning'}>
            <td className="text-muted">{c.clave}</td>
            <td data-testid={`panel-cambio-anterior-${c.clave}`}>
              {textoAnterior(c)}
            </td>
            <td data-testid={`panel-cambio-nuevo-${c.clave}`}>{formatearValor(c.valorNuevo)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}
