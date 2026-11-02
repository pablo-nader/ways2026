/**
 * De dónde salen los artículos del buscador del POS y su precio de vista previa: de la instantánea local cuando la caja
 * tiene una (también sin conexión) y, si no, de la API. El modal no sabe cuál está usando.
 */
import { clienteDeArticulos } from '../api/articulos'
import { clienteDeOfertas } from '../api/ofertas'
import { obtenerOfertasVigentes, type OfertasVigentesDeArticulo } from '../api/ofertasVigentes'
import type { ArticuloListado, InstantaneaDePos, ResultadoDeResolucion } from '../api/tipos'
import { normalizarParaBuscar } from '../formato/texto'
import { preciosVigentesDeLasLineas, todasLasLineasTienenPrecioOffline } from './instantaneaOffline'
import { clienteAdmitidoOffline } from './reglasOffline'

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
type ClienteDeLaVenta = Parameters<typeof clienteAdmitidoOffline>[0]

const SIN_PRECIO = { precioOriginal: null, precioFinal: null, descuentoUnitario: 0, aplicadas: [] }

function resolverPorLinea(ofertas: ClienteDeOfertas, idsArticulos: number[], idListaPrecio: number, idEmpresa: number | null) {
  return ofertas.resolver(
    idsArticulos.map((idArticulo) => ({ idArticulo, idEmpresa, idListaPrecio, cantidad: CANTIDAD_DE_VISTA_PREVIA })),
    'PorLinea',
  )
}

export function fuenteDeArticulosDesdeApi(
  articulos: ClienteDeArticulos = clienteDeArticulos,
  ofertas: ClienteDeOfertas = clienteDeOfertas,
  vigentes: typeof obtenerOfertasVigentes = obtenerOfertasVigentes,
): FuenteDeArticulos {
  return {
    origen: 'api',
    buscar: async (termino) => (await articulos.listar(termino, false, undefined, { soloVendibles: true })).items,
    resolverPrecios: (idsArticulos, idListaPrecio, idEmpresa) => resolverPorLinea(ofertas, idsArticulos, idListaPrecio, idEmpresa),
    ofertasVigentes: (idsArticulos, idEmpresa, idListaPrecio) => vigentes({ idsArticulos, idEmpresa, idListaPrecio }),
  }
}

/**
 * La instantánea solo trae artículos activos y a la venta, así que no hace falta filtrar la venta. Busca por nombre, código
 * interno y códigos de barra, sin mayúsculas ni acentos, ordenado por nombre y con el tope de una página del servidor. A
 * diferencia de la API, no trae el costo (siempre `null`) ni las ofertas vigentes. El precio sale de
 * `preciosVigentesDeLasLineas`, el mismo cálculo del carrito offline, así que coincide con el de escanear el artículo.
 *
 * El precio sigue la regla del carrito para cada fila, como si fuera una venta de esa sola línea: sale de la instantánea si
 * el cliente de la venta es admitido offline y el artículo tiene precio en su lista; si no, el carrito cobraría el precio del
 * servidor, así que esas filas se resuelven con la API en una sola llamada. Si esa llamada falla, quedan sin precio y las
 * locales lo conservan.
 */
export function fuenteDeArticulosDesdeInstantanea(
  instantanea: InstantaneaDePos,
  cliente: ClienteDeLaVenta,
  ofertas: ClienteDeOfertas = clienteDeOfertas,
): FuenteDeArticulos {
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
    resolverPrecios: async (idsArticulos, idListaPrecio, idEmpresa) => {
      const admitido = clienteAdmitidoOffline(cliente, instantanea)
      const esLocal = (idArticulo: number) => admitido && todasLasLineasTienenPrecioOffline([{ idArticulo }], instantanea, idListaPrecio)
      const remotos = idsArticulos.filter((id) => !esLocal(id))
      let delServidor: ResultadoDeResolucion[] = []
      if (remotos.length > 0) {
        try {
          delServidor = await resolverPorLinea(ofertas, remotos, idListaPrecio, idEmpresa)
        } catch {
          delServidor = []
        }
      }
      return idsArticulos.map((idArticulo) => {
        if (!esLocal(idArticulo)) return delServidor.find((r) => r.idArticulo === idArticulo) ?? { idArticulo, idListaPrecio, ...SIN_PRECIO }
        const [precios] = preciosVigentesDeLasLineas([{ idArticulo, cantidad: CANTIDAD_DE_VISTA_PREVIA }], instantanea, idListaPrecio)
        return { idArticulo, idListaPrecio, ...(precios ?? SIN_PRECIO) }
      })
    },
  }
}

/** La instantánea manda cuando hay una utilizable; sin ella, la API. */
export function elegirFuenteDeArticulos(
  instantanea: InstantaneaDePos | null,
  cliente: ClienteDeLaVenta,
  api: FuenteDeArticulos = fuenteDeArticulosDesdeApi(),
): FuenteDeArticulos {
  return instantanea ? fuenteDeArticulosDesdeInstantanea(instantanea, cliente) : api
}
