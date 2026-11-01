import {
  lineaCompletaParaEnvio,
  lineaTieneDatos,
  type EncabezadoDeCompraFormulario,
  type LineaDeCompraFormulario,
} from '../../api/compras'
import { valorMedioLleno } from './numeroDeComprobante'

export const MENSAJE_NUMERO_INCOMPLETO =
  'El número del comprobante está incompleto: complete el punto de venta y el número, o deje ambos vacíos.'

/** Filas con algún dato al que todavía le falta algo: no se descartan en silencio, bloquean guardar. */
export function lineasIncompletas(lineas: LineaDeCompraFormulario[]): LineaDeCompraFormulario[] {
  return lineas.filter((l) => lineaTieneDatos(l) && !lineaCompletaParaEnvio(l))
}

function unirConY(items: string[]): string {
  return items.length <= 1 ? (items[0] ?? '') : `${items.slice(0, -1).join(', ')} y ${items[items.length - 1]}`
}

/**
 * Lo que impide guardar, dicho antes de llamar a la API (`null` = se puede). Guardar un borrador
 * pide líneas sin huecos y un número de comprobante entero o vacío; guardar y confirmar pide además
 * lo que exige la confirmación del servidor: número, fecha y al menos una línea completa.
 */
export function mensajeDeLoQueImpideGuardar(
  encabezado: Pick<EncabezadoDeCompraFormulario, 'numeroExterno' | 'fechaComprobante'>,
  lineas: LineaDeCompraFormulario[],
  confirmar: boolean,
): string | null {
  const frases: string[] = []

  if (valorMedioLleno(encabezado.numeroExterno)) {
    frases.push(MENSAJE_NUMERO_INCOMPLETO)
  } else if (confirmar) {
    const falta: string[] = []
    if (encabezado.numeroExterno.trim() === '') falta.push('el número del comprobante')
    if (encabezado.fechaComprobante.trim() === '') falta.push('la fecha del comprobante')
    if (!lineas.some(lineaCompletaParaEnvio)) falta.push('al menos una línea completa')
    if (falta.length > 0) frases.push(`Para guardar y confirmar falta: ${unirConY(falta)}.`)
  }

  const incompletas = lineasIncompletas(lineas).length
  if (incompletas > 0) {
    frases.push(
      incompletas === 1
        ? 'Hay 1 línea incompleta: complétela o quítela antes de guardar.'
        : `Hay ${incompletas} líneas incompletas: complételas o quítelas antes de guardar.`,
    )
  }

  return frases.length === 0 ? null : frases.join(' ')
}

/** Huella de lo que el operador puede haber cambiado: el encabezado y las filas con datos, sin las
 * claves de React ni los datos que la pantalla completa sola (unidad de venta del artículo). */
export function huellaDeFormulario(encabezado: EncabezadoDeCompraFormulario, lineas: LineaDeCompraFormulario[]): string {
  return JSON.stringify({
    encabezado,
    lineas: lineas.filter(lineaTieneDatos).map((l) => [
      l.idArticulo,
      l.descripcion,
      l.codigoProveedor,
      l.unidadDeMedida,
      l.cantidad,
      l.importe,
      l.descuentoPorcentaje,
      l.idAlicuotaIva,
      l.impuestoInterno,
      l.codigoLote,
      l.fechaVencimiento,
    ]),
  })
}

/**
 * Si hay algo sin guardar. Una compra existente se compara contra la huella de lo último guardado.
 * Una nueva no tiene baseline: cuenta lo que el operador cargó (líneas, número, fecha,
 * observaciones, proveedor o tipo), no lo que la pantalla preselecciona sola (punto de venta único,
 * percepciones sugeridas).
 */
export function hayCambiosSinGuardar(params: {
  esNuevo: boolean
  encabezado: EncabezadoDeCompraFormulario
  lineas: LineaDeCompraFormulario[]
  huellaGuardada: string | null
}): boolean {
  const { esNuevo, encabezado, lineas, huellaGuardada } = params
  if (!esNuevo) return huellaGuardada !== null && huellaDeFormulario(encabezado, lineas) !== huellaGuardada

  return (
    lineas.some(lineaTieneDatos) ||
    encabezado.numeroExterno.trim() !== '' ||
    encabezado.fechaComprobante !== '' ||
    encabezado.observaciones.trim() !== '' ||
    encabezado.idProveedor !== '' ||
    encabezado.idTipoComprobante !== ''
  )
}
