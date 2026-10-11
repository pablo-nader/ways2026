import { costoDeLineaIncluyeIva } from '../../api/compras'
import { redondearImporte } from '../../formato/importes'
import type { PrecargaDeAlta } from '../articulos/useFormularioDeArticulo'

const LARGO_MAXIMO_NOMBRE = 150
const LARGO_MAXIMO_CODIGO_PROVEEDOR = 50

export type DatosDeLineaParaElAlta = {
  descripcion: string
  codigoProveedor: string
  idAlicuotaIva: number | ''
  /** El Importe tipeado en la fila: un costo por unidad (un artículo sin crear no tiene bultos). */
  importe: number | null
  idProveedor: number | null
  discriminaIva: boolean
  preciosFinales: boolean
}

/**
 * Lo que una fila de la compra le adelanta al formulario completo de artículo. El costo se declara por unidad y con o
 * sin IVA según el modo de precios del comprobante, y se redondea a 2 decimales: la fila admite 4, pero
 * `articulos.costo_lista` es `numeric(14,2)` y lo que se ve en el campo tiene que ser lo que se guarda. Todo queda
 * editable en el formulario.
 */
export function precargaDeArticuloDesdeLinea(linea: DatosDeLineaParaElAlta): PrecargaDeAlta {
  const campos: NonNullable<PrecargaDeAlta['campos']> = {
    nombre: linea.descripcion.trim().slice(0, LARGO_MAXIMO_NOMBRE),
    costoLista: linea.importe === null ? '' : String(redondearImporte(linea.importe, 2)),
    costoListaPor: 'Unidad',
    costoListaIncluyeIva: costoDeLineaIncluyeIva(linea.discriminaIva, linea.preciosFinales),
  }
  if (linea.idProveedor !== null) campos.idProveedorHabitual = linea.idProveedor
  if (linea.idAlicuotaIva !== '') campos.idAlicuotaIva = linea.idAlicuotaIva

  return {
    campos,
    codigoProveedor: linea.codigoProveedor.trim().slice(0, LARGO_MAXIMO_CODIGO_PROVEEDOR),
  }
}
