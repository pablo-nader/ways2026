/**
 * Cómo se muestra un valor compartido de una familia (doc 10 §3): lo que lee el detalle de una familia
 * (`ValoresCompartidosDeLaFamilia`) y lo que cambia una agrupación (`CambiosDeUnArticulo`). Los ids de catálogo
 * se muestran con el nombre de su fila; si el nombre no se conoce (el catálogo no cargó) se muestra el código, y
 * un id que el servidor presenta como `null` —una fila dada de baja— es "Sin asignar". Funciones puras, sin React
 * ni fetch (web-descriptor-tests).
 */
import { UNIDADES_VENTA } from '../../api/tipos'
import type { ValoresCompartidosDeLaFamilia } from '../../api/tipos'
import { formatearImporte } from '../../formato/importes'
import type { ClaveCompartida } from '../articulos/familia'

const SIN_VALOR = '—'
const SIN_ASIGNAR = 'Sin asignar'

/** El nombre de cada fila de los catálogos que un valor compartido referencia, por id. */
export type NombresDeCatalogo = {
  areas: ReadonlyMap<number, string>
  categorias: ReadonlyMap<number, string>
  grupos: ReadonlyMap<number, string>
  proveedores: ReadonlyMap<number, string>
  alicuotas: ReadonlyMap<number, string>
}

export function nombresDeCatalogoVacios(): NombresDeCatalogo {
  return { areas: new Map(), categorias: new Map(), grupos: new Map(), proveedores: new Map(), alicuotas: new Map() }
}

export function mapaDeNombres<T extends { id: number }>(items: readonly T[], nombre: (item: T) => string): Map<number, string> {
  return new Map(items.map((item) => [item.id, nombre(item)]))
}

function nombreDeCatalogo(nombres: ReadonlyMap<number, string>, id: number | null): string {
  if (id === null) return SIN_ASIGNAR
  return nombres.get(id) ?? `#${id}`
}

function siONo(valor: boolean): string {
  return valor ? 'Sí' : 'No'
}

/** El valor de `clave` en `valores`, listo para mostrar. */
export function formatearValorCompartido(clave: ClaveCompartida, valores: ValoresCompartidosDeLaFamilia, nombres: NombresDeCatalogo): string {
  switch (clave) {
    case 'idArea':
      return nombreDeCatalogo(nombres.areas, valores.idArea)
    case 'idCategoria':
      return nombreDeCatalogo(nombres.categorias, valores.idCategoria)
    case 'idGrupo':
      return nombreDeCatalogo(nombres.grupos, valores.idGrupo)
    case 'idProveedorHabitual':
      return nombreDeCatalogo(nombres.proveedores, valores.idProveedorHabitual)
    case 'idAlicuotaIva':
      return nombreDeCatalogo(nombres.alicuotas, valores.idAlicuotaIva)
    case 'unidadVenta':
      return UNIDADES_VENTA.find((u) => u.valor === valores.unidadVenta)?.etiqueta ?? valores.unidadVenta
    case 'unidadesPorBulto':
      return valores.unidadesPorBulto === null ? SIN_VALOR : String(valores.unidadesPorBulto)
    case 'esProducto':
      return siONo(valores.esProducto)
    case 'controlaLote':
      return siONo(valores.controlaLote)
    case 'acumulaEnVenta':
      return siONo(valores.acumulaEnVenta)
    case 'costoLista':
      return formatearImporte(valores.costoLista, { simbolo: true })
    case 'descuentoProveedor':
      return valores.descuentoProveedor === null ? SIN_VALOR : `${valores.descuentoProveedor}%`
    case 'costoNominal':
      return formatearImporte(valores.costoNominal, { simbolo: true })
  }
}
