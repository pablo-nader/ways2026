/**
 * De dónde salen los combos y los artículos que los completan al cargar un combo en el POS: de la instantánea local
 * cuando la caja tiene una utilizable (también sin conexión) y, si no, de la API. Las dos fuentes entregan lo mismo, así
 * que ni el modal ni el código `C12` saben cuál están usando.
 */
import { clienteDeCombos } from '../api/combos'
import type {
  ArticuloDeInstantanea,
  ArticuloEscaneado,
  ComboCargableDeApi,
  ComboDeInstantanea,
  ComboListado,
  InstantaneaDePos,
} from '../api/tipos'
import { motivoDeComboNoCargable, type ComboParaCargar, type ComponenteParaCargar } from './cargaDeCombos'

export type ComboDeLista = {
  idCombo: number
  codigo: number
  nombre: string
  precio: number
  /** Los componentes en una línea: `Pancho × 1 + Gaseosas × 2`. */
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
}

function resumenDe(componentes: readonly Pick<ComponenteParaCargar, 'etiqueta' | 'cantidad'>[]): string {
  return componentes.map((c) => `${c.etiqueta} × ${c.cantidad}`).join(' + ')
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
export function fuenteDesdeApi(idEmpresa: number | null, cliente: ClienteDeCombos = clienteDeCombos): FuenteDeCombos {
  const listar = async () =>
    (await cliente.listar())
      .filter((c) => c.activo && (c.idEmpresa === null || c.idEmpresa === idEmpresa))
      .sort((a, b) => a.codigo - b.codigo)
      .map(aListaDesdeApi)

  return {
    origen: 'api',
    listar,
    buscarPorCodigo: async (codigo) => (await listar()).find((c) => c.codigo === codigo) ?? null,
    cargar: async (idCombo) => aComboParaCargarDesdeApi(await cliente.cargable(idCombo, idEmpresa)),
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
 * Combos de la instantánea y sus candidatos, calculados sobre la instantánea misma. Un candidato es un artículo de unidad
 * de venta `Unidad` (una unidad ausente es `Peso`, como en el motor) y, con una lista de precios conocida, con precio en
 * ella: la misma regla con que el carrito offline decide qué líneas puede cotizar. Sin lista (`null`, el cliente todavía
 * no se resolvió) no se filtra por precio. Los miembros de una familia se buscan por el `idFamilia` de cada artículo.
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
    const vendibles = instantanea.articulos.filter(
      (a) => a.unidadVenta === 'Unidad' && (idListaPrecio === null || a.preciosPorLista.some((p) => p.idListaPrecio === idListaPrecio)),
    )
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
            candidatos: vendible ? [aCandidato(vendible)] : [],
          }
        }
        const candidatos = c.idFamilia != null ? (porFamilia.get(c.idFamilia) ?? []) : []
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

/** La instantánea utilizable manda, también con conexión (la caja vende contra ella); sin instantánea, la API. */
export function elegirFuenteDeCombos(params: {
  instantanea: InstantaneaDePos | null
  idListaPrecio: number | null
  idEmpresa: number | null
}): FuenteDeCombos {
  return params.instantanea ? fuenteDesdeInstantanea(params.instantanea, params.idListaPrecio) : fuenteDesdeApi(params.idEmpresa)
}
