import {
  etiquetaDeResultado,
  resumenOrdenado,
  textoDeImporte,
  textoDePeriodo,
  totalesDelResumen,
  type ConciliacionImportada,
} from '../../api/conciliacionMercadoPago'

type Props = {
  archivo: File | null
  /** La subida está en vuelo: archivo y botón quedan inertes. */
  subiendo: boolean
  error: string
  resultado: ConciliacionImportada | null
  onElegir: (archivo: File | null) => void
  onSubir: () => void
}

/** Subida del reporte (Admin) y, tras importarlo, su resumen y las filas que no se pudieron leer. */
export function SeccionImportar({ archivo, subiendo, error, resultado, onElegir, onSubir }: Props) {
  const totales = resultado ? totalesDelResumen(resultado.conciliacion.resumen) : null

  return (
    <section aria-label="Importar reporte">
      <p className="text-muted small">
        Subí el reporte de transacciones de Mercado Pago (CSV, hasta 10 MB). Cada fila se coteja con los cobros de la empresa y los cobros
        conciliados guardan la comisión y el neto reales.
      </p>
      <div className="row g-2 align-items-end mb-3">
        <div className="col-md-6">
          <label className="form-label" htmlFor="conciliacion-archivo">
            Archivo del reporte
          </label>
          <input
            key={resultado?.conciliacion.id ?? 'sin-importar'}
            id="conciliacion-archivo"
            type="file"
            accept=".csv,text/csv"
            className="form-control"
            disabled={subiendo}
            onChange={(e) => onElegir(e.target.files?.[0] ?? null)}
          />
        </div>
        <div className="col-auto">
          <button type="button" className="btn btn-primary" disabled={subiendo || archivo === null} onClick={onSubir}>
            {subiendo ? 'Importando…' : 'Importar'}
          </button>
        </div>
      </div>

      {error && (
        <div className="alert alert-danger py-2 px-3" role="alert">
          {error}
        </div>
      )}

      {resultado && totales && (
        <div aria-label="Resultado de la importación" role="region">
          <h6>
            {resultado.conciliacion.nombreArchivo ?? 'Reporte importado'} · {textoDePeriodo(resultado.conciliacion.fechaDesde, resultado.conciliacion.fechaHasta)}
          </h6>
          <p className="small text-muted mb-2">
            {resultado.conciliacion.cantidadFilas} filas, {resultado.conciliacion.cantidadInvalidas} inválidas.
            {resultado.retirosYaPropuestos > 0 &&
              ` ${resultado.retirosYaPropuestos} retiro(s) ya estaban propuestos por un reporte anterior y no se volvieron a proponer.`}
          </p>
          <div className="table-responsive">
            <table className="table table-sm w-auto">
              <thead>
                <tr>
                  <th>Resultado</th>
                  <th className="text-end">Filas</th>
                  <th className="text-end">Importe</th>
                  <th className="text-end">Comisión real</th>
                  <th className="text-end">Neto</th>
                </tr>
              </thead>
              <tbody>
                {resumenOrdenado(resultado.conciliacion.resumen).map((r) => (
                  <tr key={r.resultado}>
                    <td>{etiquetaDeResultado(r.resultado)}</td>
                    <td className="text-end">{r.cantidad}</td>
                    <td className="text-end">{textoDeImporte(r.importe)}</td>
                    <td className="text-end">{textoDeImporte(r.comision)}</td>
                    <td className="text-end">{textoDeImporte(r.neto)}</td>
                  </tr>
                ))}
              </tbody>
              <tfoot>
                <tr>
                  <th>Total</th>
                  <th className="text-end">{totales.cantidad}</th>
                  <th className="text-end">{textoDeImporte(totales.importe)}</th>
                  <th className="text-end">{textoDeImporte(totales.comision)}</th>
                  <th className="text-end">{textoDeImporte(totales.neto)}</th>
                </tr>
              </tfoot>
            </table>
          </div>

          {resultado.invalidas.length > 0 && (
            <div className="mt-3">
              <h6>Filas que no se pudieron leer</h6>
              <ul className="small">
                {resultado.invalidas.map((invalida) => (
                  <li key={invalida.numeroLinea}>
                    Línea {invalida.numeroLinea}: {invalida.motivo}
                  </li>
                ))}
              </ul>
              {resultado.invalidasTruncado && (
                <p className="small text-muted">Hay más filas inválidas que las listadas: se muestran las primeras.</p>
              )}
            </div>
          )}
        </div>
      )}
    </section>
  )
}
