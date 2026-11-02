/**
 * De dónde salen los artículos del buscador del POS y su precio de vista previa: de la instantánea local cuando la caja
 * tiene una (también sin conexión) y, si no, de la API. El modal no sabe cuál está usando.
 */
import { clienteDeArticulos } from '../api/articulos'
import { clienteDeOfertas } from '../api/ofertas'
import { obtenerOfertasVigentes, type OfertasVigentesDeArticulo } from '../api/ofertasVigentes'
import type { ArticuloListado, InstantaneaDePos, ResultadoDeResolucion } from '../api/tipos'
import { normalizarParaBuscar } from '../formato/texto'
import { preciosVigentesDeLasLineas } from './instantaneaOffline'

/** Mismo tamaño de página que `GET /api/articulos` (25 por defecto). */
export const TOPE_DE_RESULTADOS_DE_ARTICULOS = 25

/** Cantidad con la que se previsualiza el precio: la de una línea agregada sin multiplicador. */
const CANTIDAD_DE_VISTA_PREVIA = 1

export type ArticuloDeBusqueda = Pick<ArticuloListado, 'id' | 'codigoInterno' | 'nombre' | 'acumulaEnVenta' | 'unidadVenta' | 'costoNominal'>

export type FuenteDeArticulos = {
  origen: 'instantanea' | 'api'
  /** Los artículos vendibles cuyo nombre, código interno o código de barras contiene el texto. */
  buscar: (termino: string) => Promise<ArticuloDeBusqueda[]>
  /** El precio de cada artículo a cantidad 1 en la lista pedida; sin precio vigente, `precioFinal` es `null`. */
  resolverPrecios: (idsArticulos: number[], idListaPrecio: number, idEmpresa: number | null) => Promise<ResultadoDeResolucion[]>
  /** Solo en la API: la instantánea no trae las ofertas vigentes, así que sin ella no hay marcas. */
  ofertasVigentes?: (idsArticulos: number[], idEmpresa: number, idListaPrecio: number) => Promise<OfertasVigentesDeArticulo[]>
}

type ClienteDeArticulos = Pick<typeof clienteDeArticulos, 'listar'>
type ClienteDeOfertas = Pick<typeof clienteDeOfertas, 'resolver'>

export function fuenteDeArticulosDesdeApi(
  articulos: ClienteDeArticulos = clienteDeArticulos,
  ofertas: ClienteDeOfertas = clienteDeOfertas,
  vigentes: typeof obtenerOfertasVigentes = obtenerOfertasVigentes,
): FuenteDeArticulos {
  return {
    origen: 'api',
    buscar: async (termino) => (await articulos.listar(termino, false, undefined, { soloVendibles: true })).items,
    resolverPrecios: (idsArticulos, idListaPrecio, idEmpresa) =>
      ofertas.resolver(
        idsArticulos.map((idArticulo) => ({ idArticulo, idEmpresa, idListaPrecio, cantidad: CANTIDAD_DE_VISTA_PREVIA })),
        'PorLinea',
      ),
    ofertasVigentes: (idsArticulos, idEmpresa, idListaPrecio) => vigentes({ idsArticulos, idEmpresa, idListaPrecio }),
  }
}

/**
 * La instantánea solo trae artículos activos y a la venta, así que no hace falta filtrar la venta. Busca por nombre, código
 * interno y códigos de barra, sin mayúsculas ni acentos, ordenado por nombre y con el tope de una página del servidor. A
 * diferencia de la API, no trae el costo (siempre `null`) ni las ofertas vigentes. El precio sale de
 * `preciosVigentesDeLasLineas`, el mismo cálculo del carrito offline, así que coincide con el de escanear el artículo.
 */
export function fuenteDeArticulosDesdeInstantanea(instantanea: InstantaneaDePos): FuenteDeArticulos {
  return {
    origen: 'instantanea',
    buscar: async (termino) => {
      const buscado = normalizarParaBuscar(termino).trim()
      const coincide = (texto: string) => normalizarParaBuscar(texto).includes(buscado)
      return instantanea.articulos
        .filter((a) => coincide(a.nombre) || coincide(a.codigoInterno) || a.codigosBarra.some(coincide))
        .sort((a, b) => a.nombre.localeCompare(b.nombre, 'es') || a.idArticulo - b.idArticulo)
        .slice(0, TOPE_DE_RESULTADOS_DE_ARTICULOS)
        .map((a) => ({
          id: a.idArticulo,
          codigoInterno: a.codigoInterno,
          nombre: a.nombre,
          acumulaEnVenta: a.acumulaEnVenta ?? true,
          unidadVenta: a.unidadVenta ?? 'Peso',
          costoNominal: null,
        }))
    },
    resolverPrecios: async (idsArticulos, idListaPrecio) =>
      idsArticulos.map((idArticulo) => {
        const [precios] = preciosVigentesDeLasLineas([{ idArticulo, cantidad: CANTIDAD_DE_VISTA_PREVIA }], instantanea, idListaPrecio)
        return { idArticulo, idListaPrecio, ...(precios ?? { precioOriginal: null, precioFinal: null, descuentoUnitario: 0, aplicadas: [] }) }
      }),
  }
}

/** La instantánea manda cuando hay una utilizable; sin ella, la API. */
export function elegirFuenteDeArticulos(instantanea: InstantaneaDePos | null, api: FuenteDeArticulos = fuenteDeArticulosDesdeApi()): FuenteDeArticulos {
  return instantanea ? fuenteDeArticulosDesdeInstantanea(instantanea) : api
}
