import type { VentaPendienteDeCobro } from '../api/tipos'
import { fechaHoraATexto } from '../formato/fechas'
import { formatearImporte } from '../formato/importes'
import { CampoImporte } from './CampoImporte'
import { repartirFifo, totalAplicado, validarAplicaciones, type ImportesPorVenta } from './cobros/aplicaciones'

type Props = {
  /** Prefijo de los ids de los inputs, para que dos editores en pantalla no choquen. */
  prefijoId: string
  ventas: VentaPendienteDeCobro[]
  importes: ImportesPorVenta
  onCambiar: (importes: ImportesPorVenta) => void
  /** Lo máximo que se puede aplicar entre todas las ventas (lo cobrado del pago o lo disponible del recibo). */
  tope: number
  disabled?: boolean
  /** Rótulo de lo que sobra del tope. */
  rotuloRestante?: string
}

function moneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

/**
 * Lista las ventas con saldo pendiente con un importe editable por venta, el botón que reparte el tope entre las más
 * viejas y el resumen «Aplicado / restante». El estado vive en el llamador; acá solo se edita y se muestra el primer
 * motivo de rechazo local (el llamador repite la misma validación antes de enviar).
 */
export function EditorDeAplicacionesACobrar({
  prefijoId,
  ventas,
  importes,
  onCambiar,
  tope,
  disabled = false,
  rotuloRestante = 'A favor',
}: Props) {
  const aplicado = totalAplicado(ventas, importes)
  const restante = Math.round((tope - aplicado) * 100) / 100
  const rechazo = validarAplicaciones(ventas, importes, tope)

  return (
    <div data-testid={`${prefijoId}-editor`}>
      <div className="d-flex justify-content-between align-items-center mb-2">
        <div className="fw-semibold">Aplicar a ventas</div>
        <button
          type="button"
          className="btn btn-outline-secondary btn-sm"
          disabled={disabled || tope <= 0}
          onClick={() => onCambiar(repartirFifo(ventas, tope))}
        >
          Aplicar a las más viejas
        </button>
      </div>

      <div className="table-responsive">
        <table className="table table-sm table-bordered align-middle mb-2">
          <thead>
            <tr>
              <th>Venta</th>
              <th>Fecha</th>
              <th className="text-end">Saldo pendiente</th>
              <th style={{ width: '10rem' }}>Importe a aplicar</th>
            </tr>
          </thead>
          <tbody>
            {ventas.map((venta) => (
              <tr key={venta.id}>
                <td>{venta.numeroVisible}</td>
                <td>{fechaHoraATexto(venta.fecha)}</td>
                <td className="text-end">{moneda(venta.saldoPendiente)}</td>
                <td>
                  <CampoImporte
                    id={`${prefijoId}-venta-${venta.id}`}
                    aria-label={`Importe a aplicar a la venta ${venta.numeroVisible}`}
                    className="form-control form-control-sm"
                    valor={importes[venta.id] ?? null}
                    disabled={disabled}
                    onChange={(valor) => onCambiar({ ...importes, [venta.id]: valor })}
                  />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {rechazo && (
        <div className="text-danger small mb-2" role="alert">
          {rechazo}
        </div>
      )}

      <div className="row g-3">
        <div className="col-6">
          <div className="small text-muted">Aplicado</div>
          <div data-testid={`${prefijoId}-aplicado`}>{moneda(aplicado)}</div>
        </div>
        <div className="col-6">
          <div className="small text-muted">{rotuloRestante}</div>
          <div data-testid={`${prefijoId}-restante`}>{moneda(restante)}</div>
        </div>
      </div>
    </div>
  )
}
