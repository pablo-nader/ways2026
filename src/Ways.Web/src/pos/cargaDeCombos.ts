/**
 * Cargar un combo en el carrito del POS: puro y sin DOM. Un combo nunca es una línea de venta: cargarlo agrega las
 * mismas líneas de artículo que se agregarían escaneándolas por separado, y es el carrito el que después detecta el
 * combo por sí mismo. Acá se decide QUÉ artículos y qué cantidad de cada uno (unidades, o kilos con hasta tres decimales
 * si el artículo se vende por peso); agregarlas es del camino de escaneo de la pantalla.
 */
import { DECIMALES_DE_PESO, esCantidadValida, respetaGranularidad } from '../api/cantidadPorUnidad'
import type { ArticuloEscaneado } from '../api/tipos'
import { redondearImporte } from '../formato/importes'

/** Un componente listo para cargar. `candidatos` es vacía cuando no se puede vender; con uno solo, ese es el artículo; con
 * varios (una familia), el cajero reparte la cantidad entre ellos. Las dos fuentes (la instantánea y la API) dejan como
 * candidatos solo los artículos cuya unidad de venta admite `cantidad`. */
export type ComponenteParaCargar = {
  idArticulo: number | null
  idFamilia: number | null
  /** Nombre del artículo o de la familia, o el rótulo que arma la fuente cuando no lo conoce. */
  etiqueta: string
  cantidad: number
  candidatos: ArticuloEscaneado[]
}

export type ComboParaCargar = {
  idCombo: number
  codigo: number
  nombre: string
  precio: number
  componentes: ComponenteParaCargar[]
}

/** Qué cantidad de cada miembro eligió el cajero, por índice de componente y por `idArticulo`. */
export type EleccionDeMiembros = Readonly<Record<number, Readonly<Record<number, number>>>>

export type ResolucionDeCombo = { ok: true; lineas: ArticuloEscaneado[] } | { ok: false; motivo: string }

const PATRON_DE_CODIGO_DE_COMBO = /^[cC]([1-9]\d{0,5})$/

/**
 * El número de combo que tipeó el cajero (`C12`, `c7`), o `null` si la entrada no es un código de combo: `C` y de uno a
 * seis dígitos sin ceros a la izquierda, o sea un valor entre 1 y 999999 (el mismo rango que valida el servidor). Solo el
 * texto canónico es un código de combo: `C012` o `C001` pueden ser el código de un artículo con relleno de ceros. No
 * recorta: recibe la entrada ya recortada. Cualquier otra cosa (`C0`, `C012`, `C1000000`, `C12X`, `3*C12`) no es un combo
 * y sigue por la búsqueda de artículos.
 */
export function parsearCodigoDeCombo(entrada: string): number | null {
  const coincidencia = PATRON_DE_CODIGO_DE_COMBO.exec(entrada)
  return coincidencia ? Number(coincidencia[1]) : null
}

/** Rótulo del código de un combo: `C12`. */
export function rotuloDeCodigoDeCombo(codigo: number): string {
  return `C${codigo}`
}

/** Cantidad para mostrar: coma decimal, sin ceros de más (`2`, `0,2`). */
export function formatearCantidadDeCombo(cantidad: number): string {
  return String(cantidad).replace('.', ',')
}

/** Por qué un componente no se puede cargar, o `null` si se puede (aunque haya que elegir entre varios miembros). La
 * cantidad tiene que ser positiva y con a lo sumo los decimales de una cantidad por peso; con un solo candidato, que es
 * el artículo que se va a cargar con toda la cantidad, tiene que ser además la que admite su unidad de venta. */
export function motivoDeComponenteNoCargable(componente: ComponenteParaCargar): string | null {
  const unidadVenta = componente.candidatos.length === 1 ? componente.candidatos[0].unidadVenta : 'Peso'
  if (!esCantidadValida(unidadVenta, componente.cantidad)) return `La cantidad de «${componente.etiqueta}» no es válida.`
  if (componente.candidatos.length === 0) return `No hay artículos disponibles para «${componente.etiqueta}».`
  return null
}

/** Por qué el combo no se puede cargar, o `null` si se puede: el motivo del primer componente que no se puede vender. */
export function motivoDeComboNoCargable(combo: ComboParaCargar): string | null {
  if (combo.componentes.length === 0) return 'El combo no tiene componentes.'
  for (const componente of combo.componentes) {
    const motivo = motivoDeComponenteNoCargable(componente)
    if (motivo !== null) return motivo
  }
  return null
}

/** Los índices de los componentes entre cuyos miembros hay que elegir (los que tienen más de un candidato). */
export function componentesAElegir(combo: ComboParaCargar): number[] {
  return combo.componentes.flatMap((componente, indice) => (componente.candidatos.length > 1 ? [indice] : []))
}

/** Cantidad repartida entre los miembros de un componente, exacta a tres decimales (0,1 + 0,2 es 0,3). */
export function totalElegido(elecciones: EleccionDeMiembros, indiceDeComponente: number): number {
  const suma = Object.values(elecciones[indiceDeComponente] ?? {}).reduce((total, cantidad) => total + cantidad, 0)
  return redondearImporte(suma, DECIMALES_DE_PESO)
}

/**
 * Las líneas de artículo con las que se carga el combo, en el orden de sus componentes y cada una con su cantidad. Un
 * componente con un solo candidato se carga entero con ese artículo; con varios, la cantidad se reparte según
 * `elecciones` y el total elegido tiene que ser exactamente la del componente. Todo o nada: si algo no cierra (un
 * componente sin artículos, una elección que falta, un total distinto, un artículo que no es candidato, una cantidad que
 * la unidad de venta de ese miembro no admite) no devuelve ninguna línea, de modo que el carrito nunca queda con un combo
 * a medias.
 */
export function resolverLineasDeCombo(combo: ComboParaCargar, elecciones: EleccionDeMiembros = {}): ResolucionDeCombo {
  const lineas: ArticuloEscaneado[] = []

  for (const [indice, componente] of combo.componentes.entries()) {
    const motivo = motivoDeComponenteNoCargable(componente)
    if (motivo !== null) return { ok: false, motivo }

    if (componente.candidatos.length === 1) {
      lineas.push({
        ...componente.candidatos[0],
        cantidad: componente.cantidad,
      })
      continue
    }

    const elegidas = elecciones[indice]
    if (elegidas === undefined)
      return {
        ok: false,
        motivo: `Falta elegir los artículos de «${componente.etiqueta}».`,
      }

    const candidatosPorId = new Map(componente.candidatos.map((c) => [c.idArticulo, c]))
    for (const [id, cantidad] of Object.entries(elegidas)) {
      const candidato = candidatosPorId.get(Number(id))
      if (candidato === undefined || !respetaGranularidad(candidato.unidadVenta, cantidad) || cantidad < 0) {
        return {
          ok: false,
          motivo: `La elección de «${componente.etiqueta}» no es válida.`,
        }
      }
    }

    if (totalElegido(elecciones, indice) !== componente.cantidad) {
      return {
        ok: false,
        motivo: `Elegí exactamente ${formatearCantidadDeCombo(componente.cantidad)} para «${componente.etiqueta}».`,
      }
    }

    for (const candidato of componente.candidatos) {
      const cantidad = elegidas[candidato.idArticulo] ?? 0
      if (cantidad > 0) lineas.push({ ...candidato, cantidad })
    }
  }

  return { ok: true, lineas }
}
