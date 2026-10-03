import { CampoImporte } from '../componentes/CampoImporte'
import { formatearImporte } from '../formato/importes'

export type FilaDeDesgloseDeIva = {
  idAlicuotaIva: number
  porcentaje: number
  neto: number
  iva: number
  /** Solo en el editor: lo que sale del neto, para mostrar contra lo impreso. */
  ivaCalculado?: number
  fueraDeTolerancia?: boolean
}

type Props = {
  filas: FilaDeDesgloseDeIva[]
  nombrePorAlicuota: Record<number, string>
  /** Con `onCambiarIva` el IVA de cada alícuota es editable (override de redondeo); sin él, es de
   * solo lectura. */
  onCambiarIva?: (idAlicuotaIva: number, iva: number | null) => void
  disabled?: boolean
}

function moneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

/** Desglose de IVA de una compra que discrimina IVA: neto e IVA por alícuota. En el editor el IVA
 * se puede corregir para que coincida con el impreso en el comprobante del proveedor cuando el
 * redondeo difiere; el servidor acepta una diferencia de hasta un peso. */
export function DesgloseDeIvaDeCompra({ filas, nombrePorAlicuota, onCambiarIva, disabled = false }: Props) {
  if (filas.length === 0) return null

  return (
    <div className="table-responsive mb-3">
      <table className="table table-sm table-bordered align-middle w-auto mb-1" aria-label="Desglose de IVA">
        <thead>
          <tr>
            <th>Alícuota</th>
            <th className="text-end">Neto</th>
            <th className="text-end">IVA</th>
          </tr>
        </thead>
        <tbody>
          {filas.map((fila) => {
            const nombre = nombrePorAlicuota[fila.idAlicuotaIva] ?? `${fila.porcentaje}%`
            return (
              <tr key={fila.idAlicuotaIva}>
                <td>{nombre}</td>
                <td className="text-end">{moneda(fila.neto)}</td>
                <td className="text-end">
                  {onCambiarIva ? (
                    <>
                      <CampoImporte
                        aria-label={`IVA impreso ${nombre}`}
                        className={`form-control form-control-sm text-end${fila.fueraDeTolerancia ? ' is-invalid' : ''}`}
                        valor={fila.iva}
                        disabled={disabled}
                        onChange={(valor) => onCambiarIva(fila.idAlicuotaIva, valor)}
                      />
                      {fila.fueraDeTolerancia && (
                        <div className="small text-danger">
                          Difiere más de {moneda(1)} del calculado ({moneda(fila.ivaCalculado ?? 0)}).
                        </div>
                      )}
                      {!fila.fueraDeTolerancia && fila.ivaCalculado !== undefined && fila.iva !== fila.ivaCalculado && (
                        <div className="small text-muted">Calculado: {moneda(fila.ivaCalculado)}</div>
                      )}
                    </>
                  ) : (
                    moneda(fila.iva)
                  )}
                </td>
              </tr>
            )
          })}
        </tbody>
      </table>
      {onCambiarIva && (
        <div className="small text-muted">Corregí el IVA si el comprobante del proveedor redondea distinto.</div>
      )}
    </div>
  )
}
