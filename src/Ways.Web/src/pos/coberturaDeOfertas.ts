/**
 * Ofertas "solo múltiplos" en el POS offline: puerto a TypeScript, sin React ni I/O, de
 * `Ways.Domain.Ofertas.CoberturaDeOfertas`. No evalúa ofertas (eso lo hizo el servidor al armar la instantánea): con las
 * ofertas que ya aplicaron a cada línea, calcula su descuento de oferta como IMPORTE DE LÍNEA. Duplicado a propósito,
 * como `motorDeCombos.ts`: un cambio en la regla del servidor tiene que replicarse acá a mano, y
 * `coberturaDeOfertas.test.ts` espeja caso por caso `CoberturaDeOfertasTests`.
 *
 * Una oferta común cubre todas las unidades de la línea. Una "solo múltiplos" (`multiploDe` = N) de artículo, grupo o
 * categoría cubre `N × floor(unidades / N)` de su propia línea. Una de familia (`porFamilia`) cuenta los grupos sobre la
 * suma de las unidades de todas las líneas que la llevan y reparte las unidades cubiertas en el orden de las líneas: las
 * que quedan fuera de un grupo completo caen en las últimas. Cada unidad suma los descuentos unitarios de las ofertas
 * que la cubren, con el tope del precio original; las coberturas quedan anidadas (las unidades cubiertas son las
 * primeras de la línea).
 *
 * Aritmética exacta, como el `decimal` del servidor: cada número entra por su representación decimal más corta y todo
 * se opera en `bigint` a una escala común; el resultado sale con todos sus decimales (sin redondear: el neto de la línea
 * se redondea una sola vez, al peso, en `calcularTotalesDeLinea`).
 */
import type { OfertaAplicada } from '../api/tipos'

/** `unidades` son las que cobra la oferta, sin signo: la cantidad de la línea menos las que consumió un combo. */
export type LineaConOfertas = { unidades: number; precioOriginal: number; aplicadas: readonly OfertaAplicada[] }

/** `true` si alguna oferta aplicada es "solo múltiplos": solo entonces la línea necesita `descuentosDeLinea`. */
export function tieneSoloMultiplos(aplicadas: readonly OfertaAplicada[]): boolean {
  return aplicadas.some((a) => a.multiploDe != null)
}

/** El descuento por unidad de la resolución entera (`PrecioConOfertas.DescuentoUnitario` del servidor): la suma de los
 * descuentos aplicados, entre cero y el precio original. */
export function descuentoUnitarioDeAplicadas(precioOriginal: number, aplicadas: readonly OfertaAplicada[]): number {
  const escala = escalaComun([precioOriginal, ...aplicadas.map((a) => a.descuentoUnitario)])
  const suma = aplicadas.reduce((total, a) => total + escalar(a.descuentoUnitario, escala), 0n)
  return desescalar(acotar(suma, escalar(precioOriginal, escala)), escala)
}

/** El descuento de oferta de cada línea de `lineas`, en el mismo orden, sin signo. */
export function descuentosDeLinea(lineas: readonly LineaConOfertas[]): number[] {
  const escala = escalaComun(
    lineas.flatMap((l) => [l.unidades, l.precioOriginal, ...l.aplicadas.flatMap((a) => [a.descuentoUnitario, a.multiploDe ?? 0])]),
  )
  const restantesPorFamilia = unidadesCubiertasPorOfertaDeFamilia(lineas, escala)

  return lineas.map((linea) => {
    const unidades = maximo(escalar(linea.unidades, escala), 0n)
    const cubiertas = linea.aplicadas.map((aplicada) => {
      if (aplicada.multiploDe == null) return unidades
      if (aplicada.porFamilia) {
        const restantes = restantesPorFamilia.get(aplicada.idOferta) ?? 0n
        const cubiertasDeLaLinea = minimo(restantes, unidades)
        restantesPorFamilia.set(aplicada.idOferta, restantes - cubiertasDeLaLinea)
        return cubiertasDeLaLinea
      }
      return enGruposCompletos(unidades, escalar(aplicada.multiploDe, escala))
    })
    return desescalar(descuentoEscalonado(linea, cubiertas, escala), 2 * escala)
  })
}

function unidadesCubiertasPorOfertaDeFamilia(lineas: readonly LineaConOfertas[], escala: number): Map<number, bigint> {
  const porOferta = new Map<number, { multiplo: bigint; unidades: bigint }>()
  for (const linea of lineas) {
    for (const aplicada of linea.aplicadas) {
      if (aplicada.multiploDe == null || !aplicada.porFamilia) continue
      const acumulado = porOferta.get(aplicada.idOferta) ?? { multiplo: escalar(aplicada.multiploDe, escala), unidades: 0n }
      porOferta.set(aplicada.idOferta, { multiplo: acumulado.multiplo, unidades: acumulado.unidades + maximo(escalar(linea.unidades, escala), 0n) })
    }
  }
  return new Map([...porOferta].map(([idOferta, { multiplo, unidades }]) => [idOferta, enGruposCompletos(unidades, multiplo)]))
}

/** `multiplo × floor(unidades / multiplo)`; las dos cantidades no negativas y a la misma escala. */
function enGruposCompletos(unidades: bigint, multiplo: bigint): bigint {
  return multiplo <= 0n ? unidades : multiplo * (unidades / multiplo)
}

/** Recorre las coberturas de menor a mayor: cada tramo de unidades lleva la suma de los descuentos de las ofertas que lo
 * cubren, con el tope del precio original por unidad. Devuelve el descuento a escala `2 × escala`. */
function descuentoEscalonado(linea: LineaConOfertas, cubiertas: bigint[], escala: number): bigint {
  const precioOriginal = escalar(linea.precioOriginal, escala)
  const niveles = [...new Set(cubiertas.filter((c) => c > 0n))].sort((a, b) => (a < b ? -1 : a > b ? 1 : 0))
  let descuento = 0n
  let anterior = 0n
  for (const nivel of niveles) {
    let unitario = 0n
    cubiertas.forEach((c, i) => {
      if (c >= nivel) unitario += escalar(linea.aplicadas[i].descuentoUnitario, escala)
    })
    descuento += (nivel - anterior) * acotar(unitario, precioOriginal)
    anterior = nivel
  }
  return descuento
}

const PATRON_DE_NUMERO_DECIMAL = /^(-?)(\d+)(?:\.(\d+))?(?:e([+-]?\d+))?$/i

function aDecimalExacto(valor: number): { mantisa: bigint; escala: number } {
  const [, signo, enteros, fraccion = '', exponente = '0'] = PATRON_DE_NUMERO_DECIMAL.exec(String(valor)) ?? []
  if (enteros === undefined) return { mantisa: 0n, escala: 0 }
  return { mantisa: BigInt(`${signo}${enteros}${fraccion}`), escala: fraccion.length - Number(exponente) }
}

function escalaComun(valores: readonly number[]): number {
  return Math.max(0, ...valores.filter(Number.isFinite).map((v) => aDecimalExacto(v).escala))
}

function escalar(valor: number, escala: number): bigint {
  if (!Number.isFinite(valor)) return 0n
  const { mantisa, escala: propia } = aDecimalExacto(valor)
  return propia <= escala ? mantisa * 10n ** BigInt(escala - propia) : mantisa / 10n ** BigInt(propia - escala)
}

/** Número más cercano a `valor / 10^escala`, sin pasar por una división en punto flotante del bigint. */
function desescalar(valor: bigint, escala: number): number {
  const negativo = valor < 0n
  const digitos = (negativo ? -valor : valor).toString().padStart(escala + 1, '0')
  const corte = digitos.length - escala
  const texto = escala === 0 ? digitos : `${digitos.slice(0, corte)}.${digitos.slice(corte)}`
  return Number(`${negativo ? '-' : ''}${texto}`)
}

function acotar(valor: bigint, tope: bigint): bigint {
  return valor < 0n ? 0n : valor > tope ? tope : valor
}

function minimo(a: bigint, b: bigint): bigint {
  return a < b ? a : b
}

function maximo(a: bigint, b: bigint): bigint {
  return a > b ? a : b
}
