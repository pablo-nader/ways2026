/**
 * De dónde salen los combos y los artículos que los completan al cargar un combo en el POS: de la instantánea local
 * cuando la caja tiene una utilizable (también sin conexión) y, si no, de la API. Las dos fuentes entregan lo mismo, así
 * que ni el modal ni el código `C12` saben cuál están usando.
 */
import { ErrorApi } from '../api/cliente'
import { respetaGranularidad } from '../api/cantidadPorUnidad'
import { clienteDeCombos } from '../api/combos'
import type {
  ArticuloDeInstantanea,
  ArticuloEscaneado,
  ComboCargableDeApi,
  ComboDeInstantanea,
  ComboListado,
  InstantaneaDePos,
} from '../api/tipos'
import { formatearCantidadDeCombo, motivoDeComboNoCargable, type ComboParaCargar, type ComponenteParaCargar } from './cargaDeCombos'
import { conTiempoLimite, TIEMPO_LIMITE_DE_RED_MS } from './tiempoLimite'

/** Tras no encontrar un combo con un código en la API, no se vuelve a preguntar por ese mismo código durante este lapso. */
export const VENTANA_DE_COMBO_AUSENTE_MS = 60_000

export type ComboDeLista = {
  idCombo: number
  codigo: number
  nombre: string
  precio: number
  /** Los componentes en una línea: `Pancho × 1 + Gaseosas × 2`; una cantidad en kilos va con coma (`Jamón × 0,2`). */
  resumen: string
  /** Por qué no se puede cargar; `null` si se puede o si todavía no se sabe (la API lo confirma al elegirlo). */
  motivoNoCargable: string | null
}

export type FuenteDeCombos = {
  origen: 'instantanea' | 'api'
  /** Los combos activos que valen para el punto de venta, por código. */
  listar: () => Promise<ComboDeLista[]>
  /** El combo activo con ese número, o `null` si no hay. */
  buscarPorCodigo: (codigo: number) => Promise<ComboDeLista | null>
  /** El combo con sus candidatos; rechaza si ya no existe. */
  cargar: (idCombo: number) => Promise<ComboParaCargar>
  /**
   * Solo en la fuente con instantánea y conexión: lo que la instantánea sabe, sin ninguna llamada. Permite al llamador mirar
   * sus artículos locales antes de preguntarle a la API (`buscarEnLaApi`). Ausente, `buscarPorCodigo` es el único paso.
   */
  buscarSinRed?: (codigo: number) => Promise<ComboDeLista | null>
  /** Solo junto a `buscarSinRed`: el complemento por la API, con tiempo límite y sin repetir un código ausente en 60 s. */
  buscarEnLaApi?: (codigo: number) => Promise<ComboDeLista | null>
}

function resumenDe(componentes: readonly Pick<ComponenteParaCargar, 'etiqueta' | 'cantidad'>[]): string {
  return componentes.map((c) => `${c.etiqueta} × ${formatearCantidadDeCombo(c.cantidad)}`).join(' + ')
}

function aComboDeLista(combo: ComboParaCargar, motivoNoCargable: string | null): ComboDeLista {
  return {
    idCombo: combo.idCombo,
    codigo: combo.codigo,
    nombre: combo.nombre,
    precio: combo.precio,
    resumen: resumenDe(combo.componentes),
    motivoNoCargable,
  }
}

const ARTICULO_NO_DISPONIBLE = 'artículo no disponible'

// --- API ----------------------------------------------------------------------------------------------------

type ClienteDeCombos = Pick<typeof clienteDeCombos, 'listar' | 'cargable'>

function aListaDesdeApi(combo: ComboListado): ComboDeLista {
  const sinDisponibilidad = combo.componentes.find((c) => !c.disponible)
  return {
    idCombo: combo.id,
    codigo: combo.codigo,
    nombre: combo.nombre,
    precio: combo.precio,
    resumen: resumenDe(
      combo.componentes.map((c) => ({
        etiqueta: c.nombre ?? ARTICULO_NO_DISPONIBLE,
        cantidad: c.cantidad,
      })),
    ),
    motivoNoCargable: sinDisponibilidad ? 'Un componente del combo ya no está disponible.' : null,
  }
}

function aComboParaCargarDesdeApi(combo: ComboCargableDeApi): ComboParaCargar {
  return {
    idCombo: combo.id,
    codigo: combo.codigo,
    nombre: combo.nombre,
    precio: combo.precio,
    componentes: combo.componentes.map((c) => ({
      idArticulo: c.idArticulo,
      idFamilia: c.idFamilia,
      etiqueta: c.nombre ?? ARTICULO_NO_DISPONIBLE,
      cantidad: c.cantidad,
      candidatos: c.candidatos,
    })),
  }
}

/** Combos de `GET /api/combos` (los activos, de todo el tenant o de la empresa del punto de venta) y candidatos de
 * `GET /api/combos/{id}/cargable`. El servidor no filtra el precio: igual que el escaneo, un artículo sin precio se
 * agrega y el carrito lo muestra sin precio. */
export type FuenteDeCombosDeApi = FuenteDeCombos & {
  /** Como `buscarPorCodigo`, pero para sondear sin congelar la caja: vence a los `TIEMPO_LIMITE_DE_RED_MS` (cuenta como
   * lectura fallida) y un código que la API no tiene no se vuelve a preguntar durante `VENTANA_DE_COMBO_AUSENTE_MS`. */
  sondearPorCodigo: (codigo: number) => Promise<ComboDeLista | null>
  /** Olvida los códigos ausentes: el siguiente sondeo vuelve a preguntar. */
  olvidarAusentes: () => void
}

export function fuenteDesdeApi(
  idEmpresa: number | null,
  cliente: ClienteDeCombos = clienteDeCombos,
  ahora: () => number = Date.now,
): FuenteDeCombosDeApi {
  // La lista se guarda en memoria mientras viva la fuente (la vida de la pantalla): `listar` (el buscador al abrirse) siempre
  // la vuelve a pedir y `buscarPorCodigo` solo cuando el código no está en lo guardado, una vez, y recién entonces decide.
  let guardada: ComboDeLista[] | null = null
  const ausentes = new Map<number, number>()

  const leer = async () => {
    const lista = (await cliente.listar())
      .filter((c) => c.activo && (c.idEmpresa === null || c.idEmpresa === idEmpresa))
      .sort((a, b) => a.codigo - b.codigo)
      .map(aListaDesdeApi)
    guardada = lista
    return lista
  }

  const buscarPorCodigo = async (codigo: number) => {
    const enGuardada = guardada?.find((c) => c.codigo === codigo)
    if (enGuardada && enGuardada.motivoNoCargable === null) return enGuardada
    if (enGuardada) {
      // Lo guardado puede estar viejo: un combo que figura como no cargable se vuelve a leer una vez antes de rechazarlo, y
      // si esa lectura falla se queda con lo que ya sabía.
      try {
        return (await leer()).find((c) => c.codigo === codigo) ?? null
      } catch {
        return enGuardada
      }
    }
    return (await leer()).find((c) => c.codigo === codigo) ?? null
  }

  return {
    origen: 'api',
    listar: leer,
    buscarPorCodigo,
    cargar: async (idCombo) => {
      try {
        return aComboParaCargarDesdeApi(await cliente.cargable(idCombo, idEmpresa))
      } catch (e) {
        // Un 404 sobre un combo guardado: ya no existe, así que no puede seguir fallando desde la memoria.
        if (e instanceof ErrorApi && e.estado === 404 && guardada) guardada = guardada.filter((c) => c.idCombo !== idCombo)
        throw e
      }
    },
    sondearPorCodigo: async (codigo) => {
      const desde = ausentes.get(codigo)
      if (desde !== undefined && ahora() - desde < VENTANA_DE_COMBO_AUSENTE_MS) return null
      const combo = await conTiempoLimite(() => buscarPorCodigo(codigo), TIEMPO_LIMITE_DE_RED_MS)
      if (combo === null) ausentes.set(codigo, ahora())
      else ausentes.delete(codigo)
      return combo
    },
    olvidarAusentes: () => ausentes.clear(),
  }
}

// --- Instantánea --------------------------------------------------------------------------------------------

function aCandidato(articulo: ArticuloDeInstantanea): ArticuloEscaneado {
  return {
    idArticulo: articulo.idArticulo,
    codigoInterno: articulo.codigoInterno,
    nombre: articulo.nombre,
    codigoBarra: null,
    cantidad: 1,
    acumulaEnVenta: articulo.acumulaEnVenta ?? true,
    unidadVenta: articulo.unidadVenta,
  }
}

function etiquetaDeFamilia(candidatos: readonly ArticuloEscaneado[]): string {
  if (candidatos.length === 0) return 'Familia sin artículos disponibles'
  const nombres = candidatos.slice(0, 3).map((c) => c.nombre)
  return candidatos.length > 3 ? `${nombres.join(' / ')} / …` : nombres.join(' / ')
}

/**
 * Combos de la instantánea y sus candidatos, calculados sobre la instantánea misma. Un candidato de un componente es un
 * artículo cuya unidad de venta admite la cantidad del componente (por unidad, solo una cantidad entera; por peso, o con la
 * unidad ausente, también kilos con decimales: la regla de `api/cantidadPorUnidad`) y, con una lista
 * de precios conocida, con precio en ella: la misma regla con que el carrito offline decide qué líneas puede cotizar. Sin
 * lista (`null`, el cliente todavía no se resolvió) no se filtra por precio. Los miembros de una familia se buscan por el
 * `idFamilia` de cada artículo.
 */
export function fuenteDesdeInstantanea(instantanea: InstantaneaDePos, idListaPrecio: number | null): FuenteDeCombos {
  // Los índices se arman la primera vez que se usa la fuente: una caja con miles de artículos no los paga al renderizar.
  let indices: {
    porId: Map<number, ArticuloDeInstantanea>
    todosPorId: Map<number, ArticuloDeInstantanea>
    porFamilia: Map<number, ArticuloEscaneado[]>
  } | null = null
  const obtenerIndices = () => {
    if (indices) return indices
    const vendibles = instantanea.articulos.filter((a) => idListaPrecio === null || a.preciosPorLista.some((p) => p.idListaPrecio === idListaPrecio))
    const porFamilia = new Map<number, ArticuloEscaneado[]>()
    for (const articulo of [...vendibles].sort((a, b) => a.nombre.localeCompare(b.nombre, 'es') || a.idArticulo - b.idArticulo)) {
      if (articulo.idFamilia == null) continue
      porFamilia.set(articulo.idFamilia, [...(porFamilia.get(articulo.idFamilia) ?? []), aCandidato(articulo)])
    }
    indices = {
      porId: new Map(vendibles.map((a) => [a.idArticulo, a])),
      todosPorId: new Map(instantanea.articulos.map((a) => [a.idArticulo, a])),
      porFamilia,
    }
    return indices
  }

  const armar = (combo: ComboDeInstantanea): ComboParaCargar => {
    const { porId, todosPorId, porFamilia } = obtenerIndices()
    return {
      idCombo: combo.idCombo,
      codigo: combo.codigo,
      nombre: combo.nombre,
      precio: combo.precio,
      componentes: combo.componentes.map((c) => {
        if (c.idArticulo != null) {
          const vendible = porId.get(c.idArticulo)
          return {
            idArticulo: c.idArticulo,
            idFamilia: null,
            etiqueta: vendible?.nombre ?? todosPorId.get(c.idArticulo)?.nombre ?? ARTICULO_NO_DISPONIBLE,
            cantidad: c.cantidad,
            candidatos: vendible && respetaGranularidad(vendible.unidadVenta, c.cantidad) ? [aCandidato(vendible)] : [],
          }
        }
        const candidatos = (c.idFamilia != null ? (porFamilia.get(c.idFamilia) ?? []) : []).filter((m) => respetaGranularidad(m.unidadVenta, c.cantidad))
        return {
          idArticulo: null,
          idFamilia: c.idFamilia ?? null,
          etiqueta: etiquetaDeFamilia(candidatos),
          cantidad: c.cantidad,
          candidatos,
        }
      }),
    }
  }

  const combos = [...(instantanea.combos ?? [])].sort((a, b) => a.codigo - b.codigo)
  const aLista = (combo: ComboDeInstantanea) => {
    const armado = armar(combo)
    return aComboDeLista(armado, motivoDeComboNoCargable(armado))
  }

  return {
    origen: 'instantanea',
    listar: async () => combos.map(aLista),
    buscarPorCodigo: async (codigo) => {
      const combo = combos.find((c) => c.codigo === codigo)
      return combo ? aLista(combo) : null
    },
    cargar: async (idCombo) => {
      const combo = combos.find((c) => c.idCombo === idCombo)
      if (!combo) throw new Error('El combo ya no está en la copia local.')
      return armar(combo)
    },
  }
}

/**
 * La instantánea utilizable manda, también con conexión (la caja vende contra ella); sin instantánea, la API. Con conexión
 * la instantánea no es la última palabra: un combo creado después de la última sincronización no está en ella, así que una
 * búsqueda por código que la instantánea no resuelve prueba la API antes de rendirse, y una instantánea sin la clave
 * `combos` (anterior a los combos) lista los de la API. Sin conexión: solo la instantánea, ninguna llamada. La lista del
 * buscador no mezcla fuentes: es la de la instantánea cuando trae `combos` y, si no, la de la API.
 */
export function elegirFuenteDeCombos(params: {
  instantanea: InstantaneaDePos | null
  idListaPrecio: number | null
  idEmpresa: number | null
  enLinea?: boolean
  /** La fuente de la API de la pantalla, para que conserve su lista entre una lectura y otra. */
  api?: FuenteDeCombosDeApi
}): FuenteDeCombos {
  const api = params.api ?? fuenteDesdeApi(params.idEmpresa)
  if (!params.instantanea) return api

  const local = fuenteDesdeInstantanea(params.instantanea, params.idListaPrecio)
  if (params.enLinea === false) return local

  const idsLocales = new Set((params.instantanea.combos ?? []).map((c) => c.idCombo))
  return {
    origen: local.origen,
    listar: params.instantanea.combos === undefined ? api.listar : local.listar,
    buscarPorCodigo: async (codigo) => (await local.buscarPorCodigo(codigo)) ?? (await api.sondearPorCodigo(codigo)),
    buscarSinRed: local.buscarPorCodigo,
    buscarEnLaApi: api.sondearPorCodigo,
    cargar: (idCombo) => (idsLocales.has(idCombo) ? local.cargar(idCombo) : api.cargar(idCombo)),
  }
}
