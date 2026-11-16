import { ErrorApi } from '../../api/cliente'
import { diaEnZona } from '../../formato/fechas'
import type { Granularidad, PuntoDeValoracion } from '../../api/tipos'
import type { PuntoDeGraficoMultiple, SerieDeGrafico } from '../../componentes/graficos/series'

export type Variacion = {
  /** Último punto menos el primero, redondeado a centavos para que el ruido binario no invente un cambio. */
  monto: number
  /** Sobre el valor inicial; `null` si el inicial es cero (no hay base contra la cual medir). */
  porcentaje: number | null
}

export type Sentido = 'sube' | 'baja' | 'igual'

function aCentavos(valor: number): number {
  return Math.round(valor * 100) / 100
}

export function variacion(inicial: number, final: number): Variacion {
  const monto = aCentavos(final - inicial)
  const porcentaje = aCentavos(inicial) === 0 ? null : (monto / Math.abs(inicial)) * 100
  return { monto, porcentaje }
}

export function sentidoDe(monto: number): Sentido {
  const centavos = aCentavos(monto)
  return centavos > 0 ? 'sube' : centavos < 0 ? 'baja' : 'igual'
}

export type MedidaDeValoracion =
  | 'totalACosto'
  | 'mercaderiaCosto'
  | 'mercaderiaVenta'
  | 'efectivo'
  | 'cuentas'

export const MEDIDAS_DE_VALORACION: { clave: MedidaDeValoracion; titulo: string }[] = [
  { clave: 'totalACosto', titulo: 'Total a costo' },
  { clave: 'mercaderiaCosto', titulo: 'Mercadería a costo' },
  { clave: 'mercaderiaVenta', titulo: 'Mercadería a precio de venta' },
  { clave: 'efectivo', titulo: 'Efectivo' },
  { clave: 'cuentas', titulo: 'Cuentas' },
]

export type TarjetaDeValoracion = {
  clave: MedidaDeValoracion
  titulo: string
  valor: number
  variacion: Variacion
}

/** Una tarjeta por medida: valor del último punto y variación contra el primero del rango. */
export function armarTarjetas(puntos: readonly PuntoDeValoracion[]): TarjetaDeValoracion[] {
  if (puntos.length === 0) return []
  const primero = puntos[0]
  const ultimo = puntos[puntos.length - 1]
  return MEDIDAS_DE_VALORACION.map(({ clave, titulo }) => ({
    clave,
    titulo,
    valor: ultimo[clave],
    variacion: variacion(primero[clave], ultimo[clave]),
  }))
}

/**
 * La frase que responde "¿tengo más dinero o más mercadería?": compara el dinero (efectivo + cuentas) y la mercadería a
 * costo del último punto contra el primero. `null` si hay menos de dos puntos (no hay contra qué comparar).
 */
export function fraseDeValoracion(puntos: readonly PuntoDeValoracion[]): string | null {
  if (puntos.length < 2) return null
  const primero = puntos[0]
  const ultimo = puntos[puntos.length - 1]
  const dinero = sentidoDe(ultimo.dinero - primero.dinero)
  const mercaderia = sentidoDe(ultimo.mercaderiaCosto - primero.mercaderiaCosto)

  if (dinero === 'igual' && mercaderia === 'igual') return 'No hubo cambios en el dinero ni en la mercadería.'
  if (mercaderia === 'igual') return dinero === 'sube' ? 'Tenés más dinero y la mercadería no cambió.' : 'Tenés menos dinero y la mercadería no cambió.'
  if (dinero === 'igual') return mercaderia === 'sube' ? 'Tenés más mercadería y el dinero no cambió.' : 'Tenés menos mercadería y el dinero no cambió.'
  if (dinero === 'sube' && mercaderia === 'baja') return 'Tenés más dinero, pero bajó la mercadería.'
  if (dinero === 'baja' && mercaderia === 'sube') return 'Hay menos dinero y más mercadería.'
  if (dinero === 'sube') return 'Subieron tanto el dinero como la mercadería.'
  return 'Bajaron tanto el dinero como la mercadería.'
}

/**
 * Etiqueta del eje x: día y semana muestran "dd/MM", mes muestra "MM/aaaa". El servidor manda cada punto como instante
 * UTC, así que el día se toma en la zona horaria de la empresa: el corte del 31/10 a las 23:59 locales llega ya en noviembre.
 */
export function etiquetaDePunto(fecha: string, granularidad: Granularidad, zonaHoraria: string): string {
  const { anio, mes, dia } = diaEnZona(fecha, zonaHoraria)
  return granularidad === 'Mes' ? `${mes}/${anio}` : `${dia}/${mes}`
}

/** Nombre del cierre del primer período, que es la base de las variaciones: "cierre de 01/2026" o "cierre del 31/01". */
export function descripcionDelCierreInicial(
  puntos: readonly PuntoDeValoracion[],
  granularidad: Granularidad,
  zonaHoraria: string,
): string {
  if (puntos.length === 0) return ''
  const etiqueta = etiquetaDePunto(puntos[0].fecha, granularidad, zonaHoraria)
  return granularidad === 'Mes' ? `cierre de ${etiqueta}` : `cierre del ${etiqueta}`
}

export const SERIES_DE_EVOLUCION: SerieDeGrafico[] = [
  { clave: 'mercaderiaCosto', nombre: 'Mercadería a costo', color: '#f7941d' },
  { clave: 'dinero', nombre: 'Dinero', color: '#2a9d8f' },
  { clave: 'totalACosto', nombre: 'Total a costo', color: '#6c8ebf' },
  { clave: 'mercaderiaVenta', nombre: 'Mercadería a precio de venta', color: '#b07cc6', punteada: true },
]

export const SERIES_DE_COMPOSICION: SerieDeGrafico[] = [
  { clave: 'mercaderiaCosto', nombre: 'Mercadería a costo', color: '#f7941d' },
  { clave: 'efectivo', nombre: 'Efectivo', color: '#2a9d8f' },
  { clave: 'cuentas', nombre: 'Cuentas', color: '#6c8ebf' },
]

export function aPuntosDeGrafico(
  puntos: readonly PuntoDeValoracion[],
  granularidad: Granularidad,
  zonaHoraria: string,
): PuntoDeGraficoMultiple[] {
  return puntos.map((p) => ({
    etiqueta: etiquetaDePunto(p.fecha, granularidad, zonaHoraria),
    mercaderiaCosto: p.mercaderiaCosto,
    mercaderiaVenta: p.mercaderiaVenta,
    efectivo: p.efectivo,
    cuentas: p.cuentas,
    dinero: p.dinero,
    totalACosto: p.totalACosto,
  }))
}

export type AvisoDeCobertura = { clave: string; texto: string; importe?: number }

function plural(cantidad: number, singular: string, pluralTexto: string): string {
  return cantidad === 1 ? singular : pluralTexto
}

/**
 * Avisos de cobertura del último punto (costo o precio faltantes, costo estimado) y del primero (costo estimado), que es
 * la base de las variaciones. Un artículo sin costo o sin precio no se valora en cero: queda fuera del valor.
 */
export function avisosDeCobertura(
  puntos: readonly PuntoDeValoracion[],
  granularidad: Granularidad,
  zonaHoraria: string,
): AvisoDeCobertura[] {
  if (puntos.length === 0) return []
  const primero = puntos[0]
  const ultimo = puntos[puntos.length - 1]
  const avisos: AvisoDeCobertura[] = []

  if (ultimo.articulosSinCosto > 0) {
    const n = ultimo.articulosSinCosto
    avisos.push({
      clave: 'sin-costo',
      texto: `${n} ${plural(n, 'artículo con stock no tiene', 'artículos con stock no tienen')} costo cargado: no se ${plural(n, 'incluye', 'incluyen')} en la mercadería a costo.`,
    })
  }
  if (ultimo.articulosSinPrecio > 0) {
    const n = ultimo.articulosSinPrecio
    avisos.push({
      clave: 'sin-precio',
      texto: `${n} ${plural(n, 'artículo con stock no tiene', 'artículos con stock no tienen')} precio de venta en la lista usada: no se ${plural(n, 'incluye', 'incluyen')} en la mercadería a precio de venta.`,
    })
  }
  if (ultimo.articulosConCostoEstimado > 0) {
    const n = ultimo.articulosConCostoEstimado
    avisos.push({
      clave: 'estimado-ultimo',
      texto: `${n} ${plural(n, 'artículo está valorado', 'artículos están valorados')} con el costo actual por no tener una compra anterior a esa fecha. Valor estimado:`,
      importe: ultimo.valorCostoEstimado,
    })
  }
  if (puntos.length > 1 && primero.articulosConCostoEstimado > 0) {
    const n = primero.articulosConCostoEstimado
    avisos.push({
      clave: 'estimado-primero',
      texto: `En el primer punto (${descripcionDelCierreInicial(puntos, granularidad, zonaHoraria)}), ${n} ${plural(n, 'artículo está valorado', 'artículos están valorados')} con el costo actual por no tener una compra anterior a esa fecha, así que la variación del costo es aproximada. Valor estimado:`,
      importe: primero.valorCostoEstimado,
    })
  }
  return avisos
}

/** Mensaje en español para el error de la consulta; los códigos son los que responde `ServicioDeReportesDeValoracion`. */
export function mensajeDeErrorDeValoracion(error: unknown): string {
  if (error instanceof ErrorApi) {
    switch (error.codigo) {
      case 'rango_en_el_futuro':
        return 'El rango empieza después de hoy. Elegí una fecha inicial de hoy o anterior.'
      case 'demasiados_puntos':
        return 'El rango y la granularidad generan demasiados puntos (máximo 60). Elegí una granularidad más amplia o un rango más corto.'
      case 'rango_demasiado_amplio':
        return 'El rango no puede superar los 366 días.'
      default:
        return error.message
    }
  }
  return 'No se pudo cargar el reporte de valoración.'
}
