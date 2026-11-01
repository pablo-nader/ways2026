import type { ResultadoDeResolucion } from '../api/tipos'
import { formatearImporte } from '../formato/importes'
import { desgloseDeOferta, textoDeDesglose } from './precioDeLinea'

type Props = {
  cantidad: number
  resultado: ResultadoDeResolucion | undefined
  precioUnitario: number
  tieneDescuento: boolean
}

function moneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

/** Precio unitario del carrito. Con una oferta "solo múltiplos" que no cubre todas las unidades muestra el precio
 * regular y el desglose de lo que se cobra, no el precio de oferta que solo vale para los grupos completos. */
export function PrecioUnitarioDeLinea({ cantidad, resultado, precioUnitario, tieneDescuento }: Props) {
  const desglose = desgloseDeOferta(cantidad, resultado)
  const mostrado = desglose?.precioSinOferta ?? precioUnitario
  const original = resultado?.precioOriginal ?? null
  const tachado = original !== null && original > mostrado
  const aplicadas = resultado?.aplicadas ?? []

  return (
    <>
      {tachado && <div className="text-decoration-line-through text-muted small">{moneda(original)}</div>}
      <div>
        {moneda(mostrado)}
        {tieneDescuento && aplicadas.length > 0 && (
          <span className="badge bg-success ms-1" title={aplicadas.map((a) => a.nombre).join(', ')}>
            {aplicadas.length === 1 ? aplicadas[0].nombre : `${aplicadas[0].nombre} +${aplicadas.length - 1}`}
          </span>
        )}
      </div>
      {desglose && <div className="small text-muted">{textoDeDesglose(desglose)}</div>}
    </>
  )
}
