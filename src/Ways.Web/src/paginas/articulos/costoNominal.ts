/**
 * Costo nominal de un artículo en el formulario: espejo de `CalculadorDeCostoNominal` del servidor (doc 10 §3).
 * El servidor es quien lo guarda; acá se calcula solo para mostrarlo antes de guardar, con aritmética decimal
 * exacta (`productoRedondeado`) para que la vista previa coincida con el resultado al centavo. Funciones puras,
 * sin React ni fetch (web-descriptor-tests).
 */
import type { AlicuotaIvaListado, CostoListaPor } from '../../api/tipos'
import { productoRedondeado, redondearImporte } from '../../formato/importes'
import { parsearNumeroDecimal } from '../../formato/numeros'
import type { Formulario } from './FormularioArticulo'

/** Lo que entra en el cálculo, tal como está guardado o tal como se está editando. */
export type EntradasDeCosto = {
  costoLista: number | null
  por: CostoListaPor
  incluyeIva: boolean
  unidadesPorBulto: number | null
  /** `null`: la alícuota no está elegida o no se conoce. */
  porcentajeIva: number | null
  descuentoProveedor: number | null
  /** Importe de impuestos internos sin IVA, en la misma base que el costo de lista (unidad o bulto); el IVA no lo grava y
   * solo cuenta con un costo de lista sin IVA. */
  impuestoInterno: number
}

/** Decimales con los que se suma o resta un porcentaje a 100 antes de multiplicar: más que de sobra para un
 * porcentaje tipeado y evita el ruido binario de `100 - 0,1`. */
const DECIMALES_DE_PORCENTAJE = 8

/** Decimales del costo con IVA antes del descuento: el servidor no redondea ahí, y estos sobran para un importe de 2. */
const DECIMALES_INTERMEDIOS = 10

/** `true` si el costo es por bulto y no hay unidades por bulto mayores que cero: el cálculo no es posible. */
export function bultoSinUnidades(por: CostoListaPor, unidadesPorBulto: number | null): boolean {
  return por === 'Bulto' && !(unidadesPorBulto !== null && unidadesPorBulto > 0)
}

/**
 * `true` si alguna entrada que cuenta para el cálculo cambió: el costo de lista, su base, su IVA y el descuento
 * siempre; las unidades por bulto solo mientras la base es bulto, antes o después; el porcentaje de la alícuota
 * y el importe de impuestos internos (cualquiera de los dos) solo mientras el costo se guarda sin IVA, antes o después. Es la misma regla del servidor: si ninguna cambió,
 * el costo nominal guardado se conserva (puede ser el efectivo que dejó una compra).
 */
export function cambiaAlgunaEntrada(anterior: EntradasDeCosto, nueva: EntradasDeCosto): boolean {
  return (
    anterior.costoLista !== nueva.costoLista ||
    anterior.por !== nueva.por ||
    anterior.incluyeIva !== nueva.incluyeIva ||
    anterior.descuentoProveedor !== nueva.descuentoProveedor ||
    (anterior.unidadesPorBulto !== nueva.unidadesPorBulto && (anterior.por === 'Bulto' || nueva.por === 'Bulto')) ||
    ((anterior.porcentajeIva !== nueva.porcentajeIva || anterior.impuestoInterno !== nueva.impuestoInterno) &&
      (!anterior.incluyeIva || !nueva.incluyeIva))
  )
}

/**
 * Costo por unidad con IVA incluido y neto del descuento, a 2 decimales (mitad lejos de cero). `null` si no hay
 * costo de lista, si es por bulto sin unidades por bulto, o si el costo es sin IVA y la alícuota no se conoce.
 */
export function calcularCostoNominal(entradas: EntradasDeCosto): number | null {
  const { costoLista, por, incluyeIva, unidadesPorBulto, porcentajeIva, descuentoProveedor, impuestoInterno } = entradas
  if (costoLista === null || bultoSinUnidades(por, unidadesPorBulto)) return null
  if (!incluyeIva && porcentajeIva === null) return null

  const porUnidad = por === 'Bulto' ? costoLista / unidadesPorBulto! : costoLista
  const factorDeIva = incluyeIva ? 100 : redondearImporte(100 + porcentajeIva!, DECIMALES_DE_PORCENTAJE)
  const factorDeDescuento = redondearImporte(100 - (descuentoProveedor ?? 0), DECIMALES_DE_PORCENTAJE)
  // El IVA no grava al impuesto interno: se suma después de aplicarlo y el descuento rige sobre el total.
  const impuestoPorUnidad = incluyeIva ? 0 : por === 'Bulto' ? impuestoInterno / unidadesPorBulto! : impuestoInterno
  const conIva = incluyeIva ? porUnidad : productoRedondeado([porUnidad, factorDeIva, 0.01], DECIMALES_INTERMEDIOS) + impuestoPorUnidad
  const costo = productoRedondeado([redondearImporte(conIva, DECIMALES_INTERMEDIOS), factorDeDescuento, 0.01])

  return Number.isFinite(costo) ? costo : null
}

/** El costo de lista visto sin IVA y con IVA a partir del que se cargó. El que no se cargó se deriva con el
 * porcentaje de la alícuota; sin alícuota queda `null`. */
export function costoDeListaSinYConIva(
  costoLista: number | null,
  incluyeIva: boolean,
  porcentajeIva: number | null,
): { sinIva: number | null; conIva: number | null } {
  if (costoLista === null) return { sinIva: null, conIva: null }
  if (incluyeIva) {
    return {
      sinIva: porcentajeIva === null ? null : redondearImporte((costoLista * 100) / (100 + porcentajeIva)),
      conIva: costoLista,
    }
  }

  return {
    sinIva: costoLista,
    conIva:
      porcentajeIva === null
        ? null
        : productoRedondeado([costoLista, redondearImporte(100 + porcentajeIva, DECIMALES_DE_PORCENTAJE), 0.01]),
  }
}

function numeroOVacio(texto: string): number | null {
  const limpio = texto.trim()
  return limpio === '' ? null : (parsearNumeroDecimal(limpio) ?? Number.NaN)
}

/** Lo que el formulario toma de un artículo para decidir si sus entradas de costo cambiaron. */
export type CostoGuardado = Pick<
  Formulario,
  | 'costoLista'
  | 'costoListaPor'
  | 'costoListaIncluyeIva'
  | 'unidadesPorBulto'
  | 'descuentoProveedor'
  | 'impuestoInterno'
  | 'idAlicuotaIva'
>

export function porcentajeDeAlicuota(alicuotas: readonly AlicuotaIvaListado[], idAlicuota: number | ''): number | null {
  return alicuotas.find((a) => a.id === idAlicuota)?.porcentaje ?? null
}

export function entradasDeCosto(f: CostoGuardado, alicuotas: readonly AlicuotaIvaListado[]): EntradasDeCosto {
  return {
    costoLista: numeroOVacio(f.costoLista),
    por: f.costoListaPor,
    incluyeIva: f.costoListaIncluyeIva,
    unidadesPorBulto: numeroOVacio(f.unidadesPorBulto),
    porcentajeIva: porcentajeDeAlicuota(alicuotas, f.idAlicuotaIva),
    descuentoProveedor: numeroOVacio(f.descuentoProveedor) ?? 0,
    impuestoInterno: numeroOVacio(f.impuestoInterno) ?? 0,
  }
}

/**
 * El costo nominal que muestra el formulario. Quien no ve los costos no recibe ninguno y no se le calcula. Con el alta
 * bloqueada a los valores de una familia (`bloqueadoPorFamilia`) se muestra el `costoNominal` del formulario: el de la
 * referencia de la familia una vez aplicados sus valores (puede ser el efectivo de una compra, y es el que el servidor
 * guarda), y mientras esos valores no llegaron, el que el formulario ya tuviera (vacío en un alta recién abierta).
 * Con un artículo cargado cuyas entradas no cambiaron se muestra el guardado; en un alta sin familia o apenas cambia
 * una entrada, el que daría el servidor al guardar.
 */
export function costoNominalDelFormulario(
  f: Formulario,
  alicuotas: readonly AlicuotaIvaListado[],
  bloqueadoPorFamilia: boolean,
): number | null {
  if (f.costosOcultos) return null
  if (bloqueadoPorFamilia) return numeroOVacio(f.costoNominal)

  const actual = entradasDeCosto(f, alicuotas)
  if (f.costoGuardado !== null && !cambiaAlgunaEntrada(entradasDeCosto(f.costoGuardado, alicuotas), actual)) {
    return numeroOVacio(f.costoNominal)
  }

  return calcularCostoNominal(actual)
}
