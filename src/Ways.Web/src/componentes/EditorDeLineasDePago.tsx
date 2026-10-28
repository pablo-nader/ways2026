import { useEffect, useRef } from 'react'
import {
  agregarLineaDePago,
  cambiarLineaDePago,
  diferenciaContraObjetivo,
  MAXIMO_DE_LINEAS_DE_PAGO,
  MAXIMO_DE_REFERENCIA_DE_LINEA,
  quitarLineaDePago,
  totalDeLineasDePago,
  type LineaDePagoEditable,
} from '../api/lineasDePago'
import type { MedioPagoListado } from '../api/tipos'
import { formatearImporte } from '../formato/importes'
import { CampoImporte } from './CampoImporte'

type Props = {
  lineas: readonly LineaDePagoEditable[]
  /** Medios elegibles (sin cuenta corriente: lo decide quien monta el editor). */
  medios: readonly MedioPagoListado[]
  /** Se pasa un reductor en lugar de la lista nueva para que cada cambio construya desde el estado previo. */
  onCambio: (cambios: (prev: LineaDePagoEditable[]) => LineaDePagoEditable[]) => void
  /** Monto a cubrir (p. ej. el saldo de una compra): con él se muestra cuánto falta o sobra y la línea
   * nueva arranca con lo que falta. */
  objetivo?: number | null
  deshabilitado?: boolean
  /** Tope de líneas de este editor (1 = un solo medio, sin "Agregar medio"). */
  maximoDeLineas?: number
}

function textoDeDiferencia(diferencia: number): string {
  if (diferencia > 0) return `Falta ${formatearImporte(diferencia, { simbolo: true })}`
  if (diferencia < 0) return `Sobra ${formatearImporte(-diferencia, { simbolo: true })}`
  return 'Sin diferencia'
}

/**
 * Reparto de un pago entre medios: una fila por línea (medio, importe y referencia opcional), total y, si hay un
 * objetivo, lo que falta o sobra. Presentacional y controlado: el estado vive en quien lo monta.
 */
export function EditorDeLineasDePago({
  lineas,
  medios,
  onCambio,
  objetivo = null,
  deshabilitado = false,
  maximoDeLineas = MAXIMO_DE_LINEAS_DE_PAGO,
}: Props) {
  const total = totalDeLineasDePago(lineas)
  const diferencia = objetivo === null ? null : diferenciaContraObjetivo(objetivo, lineas)
  const alTope = lineas.length >= maximoDeLineas
  const admiteAgregar = maximoDeLineas > 1
  const importesRef = useRef(new Map<number, HTMLInputElement>())
  const agregarRef = useRef<HTMLButtonElement>(null)
  // Destino del foco tras quitar una línea: la fila anterior (su clave) o el botón de agregar.
  const focoPendiente = useRef<number | 'agregar' | null>(null)

  useEffect(() => {
    const destino = focoPendiente.current
    if (destino === null) return
    focoPendiente.current = null
    if (destino === 'agregar') agregarRef.current?.focus()
    else importesRef.current.get(destino)?.focus()
  }, [lineas])

  function agregar() {
    onCambio((prev) => {
      const faltante = objetivo === null ? null : diferenciaContraObjetivo(objetivo, prev)
      return agregarLineaDePago(prev, faltante !== null && faltante > 0 ? faltante : null).slice(0, maximoDeLineas)
    })
  }

  function quitar(indice: number, clave: number) {
    focoPendiente.current = indice > 0 ? lineas[indice - 1].clave : 'agregar'
    onCambio((prev) => quitarLineaDePago(prev, clave))
  }

  return (
    <fieldset disabled={deshabilitado} className="border-0 p-0 m-0">
      <div className="d-flex flex-column gap-2">
        {lineas.map((linea, indice) => {
          const numero = indice + 1
          // Un medio dado de baja (o que no cargó) no puede cambiar el valor de la línea en silencio.
          const medioFaltante = linea.idMedioPago !== '' && !medios.some((m) => m.id === linea.idMedioPago)
          return (
            <div key={linea.clave} className="row g-2 align-items-end" role="group" aria-label={`Línea ${numero}`}>
              <div className="col-md-4">
                {indice === 0 && <div className="form-label small mb-1">Medio de pago</div>}
                <select
                  aria-label={`Medio de la línea ${numero}`}
                  className="form-select"
                  value={linea.idMedioPago}
                  onChange={(e) =>
                    onCambio((prev) =>
                      cambiarLineaDePago(prev, linea.clave, { idMedioPago: e.target.value === '' ? '' : Number(e.target.value) }),
                    )
                  }
                >
                  <option value="">Elegir…</option>
                  {medioFaltante && (
                    <option value={linea.idMedioPago}>
                      {linea.nombreMedioPago ? linea.nombreMedioPago : `Medio #${linea.idMedioPago}`} (no disponible)
                    </option>
                  )}
                  {medios.map((m) => (
                    <option key={m.id} value={m.id}>
                      {m.nombre}
                    </option>
                  ))}
                </select>
              </div>

              <div className="col-md-3">
                {indice === 0 && <div className="form-label small mb-1">Importe</div>}
                <CampoImporte
                  ref={(el) => {
                    if (el) importesRef.current.set(linea.clave, el)
                    else importesRef.current.delete(linea.clave)
                  }}
                  aria-label={`Importe de la línea ${numero}`}
                  className="form-control"
                  valor={linea.importe}
                  onChange={(importe) => onCambio((prev) => cambiarLineaDePago(prev, linea.clave, { importe }))}
                />
              </div>

              <div className="col-md-4">
                {indice === 0 && <div className="form-label small mb-1">Referencia (opcional)</div>}
                <input
                  type="text"
                  aria-label={`Referencia de la línea ${numero}`}
                  className="form-control"
                  maxLength={MAXIMO_DE_REFERENCIA_DE_LINEA}
                  value={linea.referencia}
                  onChange={(e) => onCambio((prev) => cambiarLineaDePago(prev, linea.clave, { referencia: e.target.value }))}
                />
              </div>

              <div className="col-md-1 text-end">
                <button
                  type="button"
                  className="btn btn-outline-secondary"
                  aria-label={`Quitar línea ${numero}`}
                  title="Quitar línea"
                  disabled={lineas.length <= 1}
                  onClick={() => quitar(indice, linea.clave)}
                >
                  ×
                </button>
              </div>
            </div>
          )
        })}
      </div>

      <div className="d-flex flex-wrap justify-content-between align-items-center gap-2 mt-2">
        {admiteAgregar ? (
          <button ref={agregarRef} type="button" className="btn btn-sm btn-outline-primary" disabled={alTope} onClick={agregar}>
            Agregar medio
          </button>
        ) : (
          <span />
        )}
        <div className="text-end" aria-live="polite">
          <span className="fw-semibold">Total: {formatearImporte(total, { simbolo: true })}</span>
          {diferencia !== null && (
            <span className={`ms-3 ${diferencia === 0 ? 'text-muted' : diferencia > 0 ? 'text-warning-emphasis' : 'text-danger'}`}>
              {textoDeDiferencia(diferencia)}
            </span>
          )}
        </div>
      </div>
      {alTope && admiteAgregar && <div className="form-text">Un pago admite hasta {maximoDeLineas} líneas.</div>}
    </fieldset>
  )
}
