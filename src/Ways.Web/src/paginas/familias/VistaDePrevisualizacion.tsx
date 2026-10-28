import { EncabezadoOrdenable, useOrdenamiento, type ColumnasOrdenables } from '../../componentes/grilla'
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

type FilaDeCampo = { columna: string; etiqueta: string; actual: string; nuevo: string }
type FilaDePrecio = { idListaPrecio: number; nombre: string; actual: string; nuevo: string }

const COLUMNAS_DE_CAMPOS: ColumnasOrdenables<FilaDeCampo> = {
  campo: { valor: (f) => f.etiqueta },
  actual: { valor: (f) => f.actual },
  nuevo: { valor: (f) => f.nuevo },
}

const COLUMNAS_DE_PRECIOS: ColumnasOrdenables<FilaDePrecio> = {
  lista: { valor: (f) => f.nombre },
  actual: { valor: (f) => f.actual },
  nuevo: { valor: (f) => f.nuevo },
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
  // Una columna que el cliente no conoce se muestra por su nombre: el servidor dice que cambia aunque acá no se pueda decir
  // de qué a qué. Un valor conocido cuyos dos lados se muestran igual no es un cambio para quien lo mira (dos filas de
  // catálogo dadas de baja se ven "Sin asignar", dos importes que difieren por debajo del centavo se ven iguales): no se
  // lista.
  const filasDeCampos = cambios.campos.flatMap((columna): FilaDeCampo[] => {
    const campo = CAMPOS_COMPARTIDOS.find((c) => c.columna === columna)
    if (campo === undefined) return [{ columna, etiqueta: columna, actual: '—', nuevo: '—' }]

    const actual = formatearValorCompartido(campo.clave, cambios.actual, nombres)
    const nuevo = formatearValorCompartido(campo.clave, cambios.nuevo, nombres)

    return actual === nuevo ? [] : [{ columna, etiqueta: campo.etiqueta, actual, nuevo }]
  })
  const filasDePrecios = cambios.precios.flatMap((precio): FilaDePrecio[] => {
    const actual = describirEstadoDePrecios(precio.actual)
    const nuevo = describirEstadoDePrecios(precio.nuevo)

    return actual === nuevo
      ? []
      : [{ idListaPrecio: precio.idListaPrecio, nombre: listas.get(precio.idListaPrecio) ?? `Lista ${precio.idListaPrecio}`, actual, nuevo }]
  })
  const ordenCampos = useOrdenamiento(filasDeCampos, COLUMNAS_DE_CAMPOS)
  const ordenPrecios = useOrdenamiento(filasDePrecios, COLUMNAS_DE_PRECIOS)
  const sinCambios = filasDeCampos.length === 0 && filasDePrecios.length === 0

  return (
    <section className="mb-3" aria-label={nombreDelArticulo(cambios.idArticulo, articulos)}>
      <h6 className="mb-1">{nombreDelArticulo(cambios.idArticulo, articulos)}</h6>

      {sinCambios && <p className="text-muted small mb-0">Ya está alineado con la referencia: no cambia nada.</p>}

      {filasDeCampos.length > 0 && (
        <div className="table-responsive">
          <table className="table table-sm table-bordered align-middle mb-2">
            <thead>
              <tr>
                <EncabezadoOrdenable columna="campo" orden={ordenCampos.orden} onAlternar={ordenCampos.alternar}>
                  Campo
                </EncabezadoOrdenable>
                <EncabezadoOrdenable columna="actual" orden={ordenCampos.orden} onAlternar={ordenCampos.alternar}>
                  Actual
                </EncabezadoOrdenable>
                <EncabezadoOrdenable columna="nuevo" orden={ordenCampos.orden} onAlternar={ordenCampos.alternar}>
                  Pasa a
                </EncabezadoOrdenable>
              </tr>
            </thead>
            <tbody>
              {ordenCampos.filas.map((fila) => (
                <tr key={fila.columna}>
                  <td>{fila.etiqueta}</td>
                  <td>{fila.actual}</td>
                  <td>{fila.nuevo}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {filasDePrecios.length > 0 && (
        <div className="table-responsive">
          <table className="table table-sm table-bordered align-middle mb-0">
            <thead>
              <tr>
                <EncabezadoOrdenable columna="lista" orden={ordenPrecios.orden} onAlternar={ordenPrecios.alternar}>
                  Lista de precios
                </EncabezadoOrdenable>
                <EncabezadoOrdenable columna="actual" orden={ordenPrecios.orden} onAlternar={ordenPrecios.alternar}>
                  Actual
                </EncabezadoOrdenable>
                <EncabezadoOrdenable columna="nuevo" orden={ordenPrecios.orden} onAlternar={ordenPrecios.alternar}>
                  Pasa a
                </EncabezadoOrdenable>
              </tr>
            </thead>
            <tbody>
              {ordenPrecios.filas.map((precio) => (
                <tr key={precio.idListaPrecio}>
                  <td>{precio.nombre}</td>
                  <td>{precio.actual}</td>
                  <td>{precio.nuevo}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  )
}
