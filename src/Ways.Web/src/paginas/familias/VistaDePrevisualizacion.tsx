import type { CambiosDeUnArticulo, PrevisualizacionDeAgrupacion } from '../../api/tipos'
import { CAMPOS_COMPARTIDOS, describirEstadoDePrecios } from '../articulos/familia'
import type { ArticuloElegido } from './SelectorDeArticulos'
import { mensajeDeProblema } from './mensajes'
import { formatearValorCompartido, type NombresDeCatalogo } from './valoresCompartidos'

type Props = {
  previsualizacion: PrevisualizacionDeAgrupacion
  /** Los artículos que se eligieron, para nombrar a cada uno por su código y su nombre. */
  articulos: ReadonlyMap<number, ArticuloElegido>
  nombres: NombresDeCatalogo
  /** Nombre de cada lista de precios, por id. */
  listas: ReadonlyMap<number, string>
}

/**
 * Lo que pasaría si se agruparan los artículos elegidos (doc 10 §3, "Alinear"), tal como lo informa
 * `POST /api/familias/previsualizacion`: por artículo, los campos compartidos y los precios por lista que
 * cambian, de lo actual a lo que toma de la referencia; y, aparte, lo que impediría agrupar, en el orden en que
 * el pedido real lo rechazaría. Es una foto: el pedido real decide con lo que lee bajo sus locks.
 */
export function VistaDePrevisualizacion({ previsualizacion, articulos, nombres, listas }: Props) {
  const { articulos: cambios, problemas } = previsualizacion

  return (
    <div>
      {problemas.length > 0 && (
        <div className="alert alert-danger" role="alert">
          <p className="mb-1">
            <strong>No se puede agrupar así.</strong> Corregí lo siguiente y volvé a previsualizar:
          </p>
          <ul className="mb-0">
            {problemas.map((problema, i) => (
              <li key={`${problema.codigo}-${problema.idArticulo ?? 'x'}-${problema.idListaPrecio ?? 'x'}-${i}`}>
                {mensajeDeProblema(problema.codigo, problema.mensaje)}
              </li>
            ))}
          </ul>
        </div>
      )}

      {cambios.length === 0 && problemas.length === 0 && <p className="text-muted">No hay artículos para agrupar.</p>}

      {cambios.map((c) => (
        <CambiosDelArticulo key={c.idArticulo} cambios={c} articulos={articulos} nombres={nombres} listas={listas} />
      ))}
    </div>
  )
}

function nombreDelArticulo(id: number, articulos: ReadonlyMap<number, ArticuloElegido>): string {
  const articulo = articulos.get(id)
  return articulo === undefined ? `Artículo ${id}` : `${articulo.codigoInterno} — ${articulo.nombre}`
}

function CambiosDelArticulo({
  cambios,
  articulos,
  nombres,
  listas,
}: {
  cambios: CambiosDeUnArticulo
  articulos: ReadonlyMap<number, ArticuloElegido>
  nombres: NombresDeCatalogo
  listas: ReadonlyMap<number, string>
}) {
  const sinCambios = cambios.campos.length === 0 && cambios.precios.length === 0

  return (
    <section className="mb-3" aria-label={nombreDelArticulo(cambios.idArticulo, articulos)}>
      <h6 className="mb-1">{nombreDelArticulo(cambios.idArticulo, articulos)}</h6>

      {sinCambios && <p className="text-muted small mb-0">Ya está alineado con la referencia: no cambia nada.</p>}

      {cambios.campos.length > 0 && (
        <div className="table-responsive">
          <table className="table table-sm table-bordered align-middle mb-2">
            <thead>
              <tr>
                <th>Campo</th>
                <th>Actual</th>
                <th>Pasa a</th>
              </tr>
            </thead>
            <tbody>
              {cambios.campos.map((columna) => {
                const campo = CAMPOS_COMPARTIDOS.find((c) => c.columna === columna)
                return (
                  <tr key={columna}>
                    <td>{campo?.etiqueta ?? columna}</td>
                    <td>{campo === undefined ? '—' : formatearValorCompartido(campo.clave, cambios.actual, nombres)}</td>
                    <td>{campo === undefined ? '—' : formatearValorCompartido(campo.clave, cambios.nuevo, nombres)}</td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}

      {cambios.precios.length > 0 && (
        <div className="table-responsive">
          <table className="table table-sm table-bordered align-middle mb-0">
            <thead>
              <tr>
                <th>Lista de precios</th>
                <th>Actual</th>
                <th>Pasa a</th>
              </tr>
            </thead>
            <tbody>
              {cambios.precios.map((precio) => (
                <tr key={precio.idListaPrecio}>
                  <td>{listas.get(precio.idListaPrecio) ?? `Lista ${precio.idListaPrecio}`}</td>
                  <td>{describirEstadoDePrecios(precio.actual)}</td>
                  <td>{describirEstadoDePrecios(precio.nuevo)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  )
}
