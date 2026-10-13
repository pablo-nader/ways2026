import type { UnidadVenta } from '../api/tipos'
import { formatearPrecio } from './carrito'

/** Cartelito "Oferta"; el nombre de la oferta va de `title` y como texto para lectores de pantalla. */
export function EtiquetaDeOferta({ oferta }: { oferta: string | undefined }) {
  return (
    <span className="publico-etiqueta-oferta" title={oferta}>
      Oferta
      {oferta !== undefined && <span className="publico-solo-lectura">: {oferta}</span>}
    </span>
  )
}

/** El precio de lista tachado; "Antes" lo dice para quien no ve el tachado. */
export function PrecioAnterior({ precioOriginal }: { precioOriginal: number }) {
  return (
    <s className="publico-precio-anterior">
      <span className="publico-solo-lectura">Antes </span>
      {formatearPrecio(precioOriginal)}
    </s>
  )
}

/**
 * Precio de un artículo en una tarjeta. Sin `precioOriginal` es solo el precio; con él (una oferta lo
 * baja) suma la etiqueta "Oferta" y el precio de lista tachado antes del final.
 */
export function PrecioConOferta({
  precio,
  precioOriginal,
  oferta,
  unidadVenta,
}: {
  precio: number
  precioOriginal?: number
  oferta?: string
  unidadVenta: UnidadVenta
}) {
  return (
    <>
      {precioOriginal !== undefined && (
        <>
          <EtiquetaDeOferta oferta={oferta} /> <PrecioAnterior precioOriginal={precioOriginal} />{' '}
        </>
      )}
      <span className="publico-precio">
        {precioOriginal !== undefined && <span className="publico-solo-lectura">Ahora </span>}
        {formatearPrecio(precio)}
      </span>
      {unidadVenta === 'Peso' && <span className="publico-articulo-unidad"> por kg</span>}
    </>
  )
}
