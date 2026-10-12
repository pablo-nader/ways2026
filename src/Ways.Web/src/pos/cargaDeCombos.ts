/**
 * Cargar un combo en el carrito del POS: puro y sin DOM. Un combo nunca es una línea de venta: cargarlo agrega las
 * mismas líneas de artículo que se agregarían escaneándolas por separado, y es el carrito el que después detecta el
 * combo por sí mismo. Acá se decide QUÉ artículos y cuántas unidades; agregarlas es del camino de escaneo de la pantalla.
 */
import type { ArticuloEscaneado } from '../api/tipos'

/** Un componente listo para cargar. `candidatos` es vacía cuando no se puede vender; con uno solo, ese es el artículo; con
 * varios (una familia), el cajero reparte la cantidad entre ellos. */
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

/** Qué unidades de cada miembro eligió el cajero, por índice de componente y por `idArticulo`. */
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

function esCantidadEnteraPositiva(cantidad: number): boolean {
  return Number.isInteger(cantidad) && cantidad > 0
}

/** Por qué un componente no se puede cargar, o `null` si se puede (aunque haya que elegir entre varios miembros). */
export function motivoDeComponenteNoCargable(componente: ComponenteParaCargar): string | null {
  if (!esCantidadEnteraPositiva(componente.cantidad)) return `La cantidad de «${componente.etiqueta}» no es válida.`
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

/** Unidades repartidas entre los miembros de un componente. */
export function totalElegido(elecciones: EleccionDeMiembros, indiceDeComponente: number): number {
  return Object.values(elecciones[indiceDeComponente] ?? {}).reduce((suma, cantidad) => suma + cantidad, 0)
}

/**
 * Las líneas de artículo con las que se carga el combo, en el orden de sus componentes y cada una con su cantidad. Un
 * componente con un solo candidato se carga entero con ese artículo; con varios, la cantidad se reparte según
 * `elecciones` y el total elegido tiene que ser exactamente la del componente. Todo o nada: si algo no cierra (un
 * componente sin artículos, una elección que falta, un total distinto, un artículo que no es candidato, una cantidad que
 * no es entera) no devuelve ninguna línea, de modo que el carrito nunca queda con un combo a medias.
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

    const idsDeCandidatos = new Set(componente.candidatos.map((c) => c.idArticulo))
    for (const [id, cantidad] of Object.entries(elegidas)) {
      if (!idsDeCandidatos.has(Number(id)) || !Number.isInteger(cantidad) || cantidad < 0) {
        return {
          ok: false,
          motivo: `La elección de «${componente.etiqueta}» no es válida.`,
        }
      }
    }

    if (totalElegido(elecciones, indice) !== componente.cantidad) {
      return {
        ok: false,
        motivo: `Elegí exactamente ${componente.cantidad} para «${componente.etiqueta}».`,
      }
    }

    for (const candidato of componente.candidatos) {
      const cantidad = elegidas[candidato.idArticulo] ?? 0
      if (cantidad > 0) lineas.push({ ...candidato, cantidad })
    }
  }

  return { ok: true, lineas }
}
