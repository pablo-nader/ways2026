import type { PercepcionFormulario } from '../api/compras'
import type { TipoDePercepcion } from '../api/tipos'
import { CampoImporte } from '../componentes/CampoImporte'
import { formatearImporte } from '../formato/importes'

const ETIQUETA_DE_PERCEPCION: Record<TipoDePercepcion, string> = {
  iibb: 'Percepción IIBB',
  iva: 'Percepción IVA',
}

type Props = {
  filas: PercepcionFormulario[]
  /** Con `onCambiar` las filas son editables; sin él, de solo lectura. */
  onCambiar?: (tipo: TipoDePercepcion, cambios: Partial<Pick<PercepcionFormulario, 'baseImponible' | 'alicuota' | 'importe'>>) => void
  onQuitar?: (tipo: TipoDePercepcion) => void
  /** Tipos que todavía se pueden agregar a mano. */
  agregables?: TipoDePercepcion[]
  onAgregar?: (tipo: TipoDePercepcion) => void
  disabled?: boolean
}

function moneda(valor: number | null): string {
  return formatearImporte(valor, { simbolo: true })
}

/** Percepciones del comprobante: base imponible, alícuota e importe por tipo. El importe es el que
 * imprimió el proveedor: se puede corregir sin que nada lo vuelva a pisar. Mientras la fila es
 * automática (`automatica`) se deriva del neto gravado y de la alícuota de la empresa. */
export function PercepcionesDeCompra({ filas, onCambiar, onQuitar, agregables = [], onAgregar, disabled = false }: Props) {
  const editable = onCambiar !== undefined
  if (filas.length === 0 && (!editable || agregables.length === 0)) return null

  return (
    <div className="mb-3">
      {filas.length > 0 && (
        <div className="table-responsive">
          <table className="table table-sm table-bordered align-middle w-auto mb-1" aria-label="Percepciones">
            <thead>
              <tr>
                <th>Percepción</th>
                <th className="text-end">Base imponible</th>
                <th className="text-end">Alícuota %</th>
                <th className="text-end">Importe</th>
                {editable && <th></th>}
              </tr>
            </thead>
            <tbody>
              {filas.map((fila) => {
                const etiqueta = ETIQUETA_DE_PERCEPCION[fila.tipo]
                return (
                  <tr key={fila.tipo}>
                    <td>
                      {etiqueta}
                      {editable && fila.automatica && <span className="badge text-bg-light ms-1">Sugerida</span>}
                    </td>
                    {editable ? (
                      <>
                        <td>
                          <CampoImporte
                            aria-label={`Base imponible ${etiqueta}`}
                            className="form-control form-control-sm text-end"
                            valor={fila.baseImponible}
                            disabled={disabled}
                            onChange={(valor) => onCambiar(fila.tipo, { baseImponible: valor })}
                          />
                        </td>
                        <td>
                          <CampoImporte
                            aria-label={`Alícuota ${etiqueta}`}
                            className="form-control form-control-sm text-end"
                            decimales={3}
                            valor={fila.alicuota}
                            disabled={disabled}
                            onChange={(valor) => onCambiar(fila.tipo, { alicuota: valor })}
                          />
                        </td>
                        <td>
                          <CampoImporte
                            aria-label={`Importe ${etiqueta}`}
                            className="form-control form-control-sm text-end"
                            valor={fila.importe}
                            disabled={disabled}
                            onChange={(valor) => onCambiar(fila.tipo, { importe: valor })}
                          />
                        </td>
                        <td>
                          <button
                            type="button"
                            className="btn btn-outline-danger btn-sm"
                            disabled={disabled}
                            onClick={() => onQuitar?.(fila.tipo)}
                          >
                            Quitar
                          </button>
                        </td>
                      </>
                    ) : (
                      <>
                        <td className="text-end">{moneda(fila.baseImponible)}</td>
                        <td className="text-end">{fila.alicuota === null ? '—' : `${fila.alicuota}%`}</td>
                        <td className="text-end">{moneda(fila.importe)}</td>
                      </>
                    )}
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
      {editable && filas.length > 0 && (
        <div className="small text-muted mb-1">
          El importe es el que figura en la factura del proveedor: corregilo si difiere del propuesto.
        </div>
      )}
      {editable && agregables.length > 0 && (
        <div className="d-flex gap-2">
          {agregables.map((tipo) => (
            <button
              key={tipo}
              type="button"
              className="btn btn-outline-secondary btn-sm"
              disabled={disabled}
              onClick={() => onAgregar?.(tipo)}
            >
              + {ETIQUETA_DE_PERCEPCION[tipo]}
            </button>
          ))}
        </div>
      )}
    </div>
  )
}
