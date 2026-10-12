import { tieneDesgloseDeCierre } from '../api/caja'
import type { ResumenDeCierrePorRetiro } from '../api/tipos'
import { formatearImporte } from '../formato/importes'

type Props = {
  cierre: ResumenDeCierrePorRetiro
}

function formatearMoneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

/**
 * Ventas de un turno cerrado por medio de pago, con los subtotales y el total general tal como los
 * calculó el servidor (`GET .../resumen-de-cierre`): acá no se suma ni se clasifica nada. Un
 * resumen anterior al desglose no trae los subtotales y se muestra solo con las filas y el total
 * general, sin inventarlos.
 */
export function VentasPorMedioDeCierre({ cierre }: Props) {
  const traeSubtotales = tieneDesgloseDeCierre(cierre)

  return (
    <>
      <h6>Ventas por medio de pago</h6>
      <div className="table-responsive mb-3">
        <table className="table table-sm table-striped table-bordered align-middle" data-testid="ventas-por-medio">
          <thead>
            <tr>
              <th>Medio</th>
              <th className="text-end">Importe</th>
            </tr>
          </thead>
          <tbody>
            {cierre.ventasPorMedio.map((v) => (
              <tr key={v.idMedioPago}>
                <td>{v.nombre}</td>
                <td className="text-end">{formatearMoneda(v.importe)}</td>
              </tr>
            ))}
            {cierre.ventasPorMedio.length === 0 && (
              <tr>
                <td colSpan={2} className="text-center text-muted">
                  Sin ventas en el turno.
                </td>
              </tr>
            )}
          </tbody>
          <tfoot>
            {traeSubtotales && (
              <>
                <tr>
                  <td>Total digitales</td>
                  <td className="text-end">{formatearMoneda(cierre.totalDigitales)}</td>
                </tr>
                <tr>
                  <td>Total efectivo</td>
                  <td className="text-end">{formatearMoneda(cierre.totalEfectivo)}</td>
                </tr>
              </>
            )}
            <tr className="fw-bold">
              <td>Total general</td>
              <td className="text-end">{formatearMoneda(cierre.totalVentas)}</td>
            </tr>
          </tfoot>
        </table>
        {traeSubtotales && cierre.ventasACuentaCorriente !== 0 && (
          <p className="small text-muted mb-0" data-testid="fiado-en-cuenta-corriente">
            Fiado en cuenta corriente (no incluido en el total): {formatearMoneda(cierre.ventasACuentaCorriente)}
          </p>
        )}
      </div>
    </>
  )
}
