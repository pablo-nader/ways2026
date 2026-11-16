import { atributosDeCampoDeCantidad, restriccionDeCantidad } from '../api/cantidadPorUnidad'
import type { LineaDevolvible } from '../api/devoluciones'
import { formatearImporte } from '../formato/importes'
import { formatearNumeroDecimal } from '../formato/numeros'
import { evaluarCantidad, unidadDeLinea } from './devolucion'

type Props = {
  lineas: readonly LineaDevolvible[]
  /** Lo tipeado en cada línea, por posición. */
  textos: Readonly<Record<number, string>>
  /** Crédito calculado por el servidor para la selección vigente, por posición; sin entrada mientras no hay vista previa. */
  creditos: Readonly<Record<number, number>>
  bloqueado: boolean
  alCambiarCantidad: (posicion: number, texto: string) => void
}

/** Las líneas del ticket de origen con lo vendido, lo ya devuelto y el campo de cantidad a devolver. Solo muestra y avisa: la
 * selección y su cálculo viven en el contenedor. */
export function TablaDeLineasDeDevolucion({ lineas, textos, creditos, bloqueado, alCambiarCantidad }: Props) {
  return (
    <div className="table-responsive">
      <table className="table table-sm table-striped table-bordered align-middle mb-0">
        <thead>
          <tr>
            <th>Artículo</th>
            <th className="text-end">Vendido</th>
            <th className="text-end">Ya devuelto</th>
            <th className="text-end">Se puede devolver</th>
            <th style={{ width: 160 }}>Cantidad a devolver</th>
            <th className="text-end">Crédito</th>
          </tr>
        </thead>
        <tbody>
          {lineas.map((linea, posicion) => {
            const texto = textos[posicion] ?? ''
            const { error } = evaluarCantidad(linea, texto)
            const idError = `devolucion-error-${posicion}`
            const sinNada = linea.cantidadDevolvible <= 0
            return (
              <tr key={posicion}>
                <td>
                  {linea.descripcion}
                  {linea.codigoBarra !== null && <div className="text-muted small">{linea.codigoBarra}</div>}
                </td>
                <td className="text-end">{formatearNumeroDecimal(linea.cantidadVendida)}</td>
                <td className="text-end">{formatearNumeroDecimal(linea.cantidadDevuelta)}</td>
                <td className="text-end">{formatearNumeroDecimal(linea.cantidadDevolvible)}</td>
                <td>
                  <input
                    className={`form-control form-control-sm text-end${error !== null ? ' is-invalid' : ''}`}
                    aria-label={`Cantidad a devolver de ${linea.descripcion}`}
                    aria-invalid={error !== null}
                    aria-describedby={error !== null ? idError : undefined}
                    {...atributosDeCampoDeCantidad(restriccionDeCantidad(unidadDeLinea(linea)))}
                    value={texto}
                    disabled={bloqueado || sinNada}
                    onChange={(evento) => alCambiarCantidad(posicion, evento.target.value)}
                  />
                  {error !== null && (
                    <div id={idError} className="invalid-feedback d-block">
                      {error}
                    </div>
                  )}
                </td>
                <td className="text-end">{creditos[posicion] === undefined ? '—' : formatearImporte(creditos[posicion], { simbolo: true })}</td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}
