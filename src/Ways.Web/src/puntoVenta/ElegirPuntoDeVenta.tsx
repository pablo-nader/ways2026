import type { PuntoVentaListado } from '../api/tipos'

type Props = {
  puntosVenta: PuntoVentaListado[]
  /** Id del punto de venta activo, si ya hay uno: se marca y se muestra como "(actual)". */
  actual?: number | null
  alElegir: (id: number) => void
}

export function ElegirPuntoDeVenta({ puntosVenta, actual = null, alElegir }: Props) {
  return (
    <div className="d-flex align-items-center justify-content-center min-vh-100 p-3">
      <div className="card rounded-0 w-100" style={{ maxWidth: 480 }}>
        <div className="card-body">
          <h1 className="h3 text-center mb-4">Elegí el punto de venta</h1>

          <ul className="list-unstyled mb-0">
            {puntosVenta.map((puntoVenta) => {
              const esActual = puntoVenta.id === actual

              return (
                <li key={puntoVenta.id} className="mb-2">
                  <button
                    type="button"
                    className="btn btn-outline-dark btn-lg w-100 rounded-0 text-start"
                    aria-current={esActual ? 'true' : undefined}
                    onClick={() => alElegir(puntoVenta.id)}
                  >
                    {esActual ? `${puntoVenta.nombre} (actual)` : puntoVenta.nombre}
                    {puntoVenta.domicilio && (
                      <small className="d-block text-muted">{puntoVenta.domicilio}</small>
                    )}
                  </button>
                </li>
              )
            })}
          </ul>
        </div>
      </div>
    </div>
  )
}
