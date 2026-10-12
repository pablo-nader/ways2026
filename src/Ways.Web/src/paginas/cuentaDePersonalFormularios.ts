import { LONGITUD_MINIMA_DETALLE_AJUSTE } from '../api/cuentaCorriente'
import type {
  LineaDeDesgloseDePago,
  MedioPagoListado,
  OrigenFondosGasto,
  SolicitudDeAjusteDePersonal,
  SolicitudDeCargaDeHoras,
  SolicitudDePagoDePersonal,
  TipoMovimientoPersonal,
} from '../api/tipos'
import { isoATexto } from '../formato/fechas'
import { formatearImporte, formatearImporteConSigno, productoRedondeado, redondearImporte } from '../formato/importes'
import { etiquetaDeOrigenFondos } from './utilidadesGastosDelTurno'

/** Campo de un formulario de la cuenta donde se muestra un rechazo; `general` va arriba del formulario. */
export type CampoDeCuenta =
  | 'fecha'
  | 'horas'
  | 'valorHora'
  | 'sentido'
  | 'importe'
  | 'detalle'
  | 'descuento'
  | 'lineas'
  | 'origen'
  | 'empresa'
  | 'general'

export type ErrorDeCuenta = { campo: CampoDeCuenta; mensaje: string }

/** Límites del servidor (`ReglaDeMovimientoDePersonal` y `ReglaDePagoDePersonal`). */
export const MAXIMO_DE_HORAS = 24
export const LONGITUD_MAXIMA_DE_DETALLE = 500
export const LONGITUD_MAXIMA_DE_REFERENCIA = 100
export const MAXIMO_DE_LINEAS = 20

const error = (campo: CampoDeCuenta, mensaje: string): ErrorDeCuenta => ({ campo, mensaje })

function exigir<T>(valor: T | null, que: string): T {
  if (valor === null) throw new Error(`Falta ${que}: el formulario se valida antes de armar la solicitud.`)

  return valor
}

function detalleOpcional(detalle: string): { detalle: string } | Record<string, never> {
  const limpio = detalle.trim()

  return limpio === '' ? {} : { detalle: limpio }
}

/** Saldo del empleado después de aplicar un importe con signo, sin arrastre de punto flotante. */
export function saldoTrasMovimiento(saldo: number, importe: number): number {
  return redondearImporte(saldo + importe)
}

// ---- Carga de horas ----------------------------------------------------------------------

export type FormularioDeHoras = {
  fecha: string
  horas: number | null
  valorHora: number | null
  detalle: string
}

export function formularioDeHorasVacio(hoy: string, valorHora: number): FormularioDeHoras {
  return { fecha: hoy, horas: null, valorHora, detalle: '' }
}

/** Vista previa del importe: la misma cuenta que el servidor (horas por valor hora, al centavo, los
 * empates se alejan de cero) con aritmética decimal exacta. */
export function importeDeHoras(horas: number | null, valorHora: number | null): number | null {
  if (horas === null || valorHora === null) return null

  return productoRedondeado([horas, valorHora])
}

const DECIMALES_DE_HORAS = 2

/** Tipeo del campo de horas. Las horas son una cantidad, no un importe: tanto "," como "." son el
 * separador decimal (en un importe el "." es de miles y se descarta). Se queda con el primer separador,
 * lo normaliza a "," y recorta a dos decimales. */
export function sanearTipeoDeHoras(bruto: string): string {
  const soloValidos = bruto.replace(/[^0-9.,]/g, '')
  const posicion = soloValidos.search(/[.,]/)
  if (posicion === -1) return soloValidos

  const decimales = soloValidos.slice(posicion + 1).replace(/[.,]/g, '').slice(0, DECIMALES_DE_HORAS)

  return `${soloValidos.slice(0, posicion)},${decimales}`
}

/** Texto tipeado → horas, o `null` si todavía no hay un número ("", ","). */
export function parsearHoras(texto: string): number | null {
  const saneado = sanearTipeoDeHoras(texto)
  if (saneado === '' || saneado === ',') return null

  return Number(saneado.replace(',', '.'))
}

export function validarHoras(f: FormularioDeHoras): ErrorDeCuenta | null {
  if (f.fecha === '') return error('fecha', 'Ingresá el día trabajado.')

  if (f.horas === null || f.horas <= 0 || f.horas > MAXIMO_DE_HORAS) {
    return error('horas', `Las horas deben ser mayores que 0 y hasta ${MAXIMO_DE_HORAS}.`)
  }

  if (f.valorHora === null) return error('valorHora', 'Ingresá el valor hora.')
  if (f.valorHora < 0) return error('valorHora', 'El valor hora no puede ser negativo.')

  return validarDetalleLargo(f.detalle)
}

function validarDetalleLargo(detalle: string): ErrorDeCuenta | null {
  return detalle.trim().length > LONGITUD_MAXIMA_DE_DETALLE
    ? error('detalle', `El detalle no puede superar los ${LONGITUD_MAXIMA_DE_DETALLE} caracteres.`)
    : null
}

export function aSolicitudDeHoras(f: FormularioDeHoras): SolicitudDeCargaDeHoras {
  return {
    fecha: f.fecha,
    horas: exigir(f.horas, 'las horas'),
    valorHora: exigir(f.valorHora, 'el valor hora'),
    ...detalleOpcional(f.detalle),
  }
}

// ---- Ajuste ------------------------------------------------------------------------------

/** El signo se elige, no se escribe: "a favor" aumenta lo que la empresa le debe al empleado. */
export type SentidoDeAjuste = 'aFavor' | 'enContra'

export type FormularioDeAjuste = {
  fecha: string
  /** `null` mientras no se eligió: el signo no tiene un valor por defecto. */
  sentido: SentidoDeAjuste | null
  /** Magnitud, siempre positiva: el sentido le pone el signo. */
  importe: number | null
  detalle: string
}

export function formularioDeAjusteVacio(hoy: string): FormularioDeAjuste {
  return { fecha: hoy, sentido: null, importe: null, detalle: '' }
}

export function importeFirmadoDeAjuste(f: FormularioDeAjuste): number | null {
  if (f.importe === null || f.sentido === null) return null

  return f.sentido === 'aFavor' ? f.importe : -f.importe
}

export function validarAjuste(f: FormularioDeAjuste): ErrorDeCuenta | null {
  if (f.fecha === '') return error('fecha', 'Ingresá la fecha del ajuste.')

  if (f.sentido === null) return error('sentido', 'Elegí si el ajuste es a favor o en contra del empleado.')

  if (f.importe === null || f.importe <= 0) return error('importe', 'Ingresá un importe mayor que cero.')

  if (f.detalle.trim().length < LONGITUD_MINIMA_DETALLE_AJUSTE) {
    return error('detalle', `El detalle es obligatorio y tiene que tener al menos ${LONGITUD_MINIMA_DETALLE_AJUSTE} caracteres.`)
  }

  return validarDetalleLargo(f.detalle)
}

export function aSolicitudDeAjuste(f: FormularioDeAjuste): SolicitudDeAjusteDePersonal {
  return {
    fecha: f.fecha,
    importe: exigir(importeFirmadoDeAjuste(f), 'el importe'),
    detalle: f.detalle.trim(),
  }
}

// ---- Pago --------------------------------------------------------------------------------

export type LineaDeFormulario = {
  /** Identidad estable de la fila en pantalla (clave de React), no viaja al servidor. */
  clave: number
  idMedioPago: number | null
  importe: number | null
  referencia: string
}

export type FormularioDePago = {
  fecha: string
  /** `null` = sin descuento. */
  descuento: number | null
  lineas: LineaDeFormulario[]
  /** `''` mientras no se eligió: con líneas es obligatorio, sin ellas no existe. */
  origen: OrigenFondosGasto | ''
  idEmpresa: number | null
  detalle: string
}

/** Lo que el formulario de pago necesita saber de afuera. */
export type ContextoDePago = {
  /** Lo que el cliente vinculado debe hoy (nunca negativo), o `null` si no se pudo leer. */
  deudaDelCliente: number | null
  esAdmin: boolean
  /** Punto de venta de la sesión, o `null` si no hay uno elegido. */
  idPuntoVenta: number | null
  idEmpresaDelPuntoVenta: number | null
}

export function formularioDePagoVacio(hoy: string): FormularioDePago {
  return { fecha: hoy, descuento: null, lineas: [], origen: '', idEmpresa: null, detalle: '' }
}

export function totalDelPago(f: FormularioDePago): number {
  return redondearImporte(f.lineas.reduce((suma, l) => suma + (l.importe ?? 0), f.descuento ?? 0))
}

export function saldoTrasPago(saldo: number, f: FormularioDePago): number {
  return saldoTrasMovimiento(saldo, -totalDelPago(f))
}

/** El saldo del cliente es lo que debe; con saldo a favor no hay deuda que descontar. */
export function deudaDeCliente(saldoDelCliente: number): number {
  return Math.max(0, saldoDelCliente)
}

/** La cuenta corriente es el crédito de un cliente en una venta, nunca dinero que sale de una caja. */
export function mediosOfrecidos(medios: MedioPagoListado[]): MedioPagoListado[] {
  return medios.filter((m) => m.comportamiento !== 'CuentaCorriente')
}

const AVISO_SIN_PUNTO_DE_VENTA = 'Elegí un punto de venta desde el menú para poder'

/** Lo que falta de punto de venta, dicho antes de enviar. Tesorería sin descuento no lo necesita. */
export function avisoDeUbicacionDelPago(f: FormularioDePago, ctx: ContextoDePago): string | null {
  if (ctx.idPuntoVenta !== null) return null

  if (f.origen === 'CajaTurno' && f.lineas.length > 0) {
    return `${AVISO_SIN_PUNTO_DE_VENTA} pagar del cajón del turno.`
  }

  if (f.descuento !== null) return `${AVISO_SIN_PUNTO_DE_VENTA} descontar de la cuenta corriente.`

  return null
}

function validarDescuento(descuento: number, ctx: ContextoDePago): ErrorDeCuenta | null {
  if (descuento <= 0) return error('descuento', 'El descuento debe ser mayor que 0.')

  if (ctx.deudaDelCliente !== null && descuento > ctx.deudaDelCliente) {
    return error('descuento', `El descuento no puede superar la deuda del cliente (${formatearImporte(ctx.deudaDelCliente, { simbolo: true })}).`)
  }

  if (ctx.idPuntoVenta === null) {
    return error('descuento', `${AVISO_SIN_PUNTO_DE_VENTA} descontar de la cuenta corriente.`)
  }

  return null
}

function validarLineas(lineas: LineaDeFormulario[]): ErrorDeCuenta | null {
  if (lineas.length > MAXIMO_DE_LINEAS) {
    return error('lineas', `Un pago admite hasta ${MAXIMO_DE_LINEAS} líneas de dinero.`)
  }

  for (const l of lineas) {
    if (l.idMedioPago === null) return error('lineas', 'Elegí el medio de pago de cada línea.')
    if (l.importe === null || l.importe <= 0) return error('lineas', 'El importe de cada línea debe ser mayor que 0.')
    if (l.referencia.trim().length > LONGITUD_MAXIMA_DE_REFERENCIA) {
      return error('lineas', `La referencia no puede superar los ${LONGITUD_MAXIMA_DE_REFERENCIA} caracteres.`)
    }
  }

  return null
}

function validarOrigen(f: FormularioDePago, ctx: ContextoDePago): ErrorDeCuenta | null {
  if (f.origen === '') return error('origen', 'Elegí de dónde sale el dinero.')

  if (f.origen === 'CajaTurno') {
    return ctx.idPuntoVenta === null
      ? error('origen', `${AVISO_SIN_PUNTO_DE_VENTA} pagar del cajón del turno.`)
      : null
  }

  if (!ctx.esAdmin) return error('origen', 'Pagar desde la tesorería requiere ser administrador.')
  if (f.idEmpresa === null) return error('empresa', 'Elegí la empresa que paga.')

  if (f.descuento !== null && ctx.idEmpresaDelPuntoVenta !== f.idEmpresa) {
    return error('empresa', 'Con descuento de cuenta corriente, la empresa tiene que ser la del punto de venta de la sesión.')
  }

  return null
}

/** Primer rechazo del pago, en el orden del formulario; espeja lo que el servidor rechaza antes de escribir. */
export function validarPago(f: FormularioDePago, ctx: ContextoDePago): ErrorDeCuenta | null {
  if (f.fecha === '') return error('fecha', 'Ingresá la fecha del pago.')

  if (f.descuento === null && f.lineas.length === 0) {
    return error('general', 'Cargá un descuento de cuenta corriente o al menos una línea de dinero.')
  }

  const rechazoDelDescuento = f.descuento === null ? null : validarDescuento(f.descuento, ctx)
  if (rechazoDelDescuento) return rechazoDelDescuento

  const rechazoDeLineas = validarLineas(f.lineas)
  if (rechazoDeLineas) return rechazoDeLineas

  const rechazoDelOrigen = f.lineas.length === 0 ? null : validarOrigen(f, ctx)
  if (rechazoDelOrigen) return rechazoDelOrigen

  return validarDetalleLargo(f.detalle)
}

/** Manda solo lo que la solicitud admite en cada caso: la empresa únicamente con tesorería, el origen
 * únicamente con líneas y el punto de venta únicamente con el cajón o con descuento. */
export function aSolicitudDePago(f: FormularioDePago, ctx: ContextoDePago): SolicitudDePagoDePersonal {
  const conLineas = f.lineas.length > 0
  const origen = conLineas ? exigir(f.origen === '' ? null : f.origen, 'el origen de los fondos') : null
  const pideEmpresa = origen === 'Tesoreria'
  const pidePuntoVenta = origen === 'CajaTurno' || f.descuento !== null

  return {
    fecha: f.fecha,
    ...(f.descuento !== null && { descuentoDeCuentaCorriente: f.descuento }),
    ...(origen !== null && {
      origenFondos: origen,
      lineas: f.lineas.map((l) => {
        const referencia = l.referencia.trim()

        return {
          idMedioPago: exigir(l.idMedioPago, 'el medio de pago'),
          importe: exigir(l.importe, 'el importe de la línea'),
          ...(referencia !== '' && { referencia }),
        }
      }),
    }),
    ...(pideEmpresa && { idEmpresa: exigir(f.idEmpresa, 'la empresa') }),
    ...(pidePuntoVenta && { idPuntoVenta: exigir(ctx.idPuntoVenta, 'el punto de venta') }),
    ...detalleOpcional(f.detalle),
  }
}

// ---- Etiquetas ---------------------------------------------------------------------------

/** Saldo con la leyenda de adelanto cuando es negativo: la misma que el listado de personal. */
export function textoDeSaldo(saldo: number): string {
  const texto = formatearImporte(saldo, { simbolo: true })

  return saldo < 0 ? `${texto} (adelanto ya pagado)` : texto
}

export function etiquetaDeTipo(tipo: TipoMovimientoPersonal): string {
  switch (tipo) {
    case 'Horas':
      return 'Horas trabajadas'
    case 'Pago':
      return 'Pago'
    case 'Ajuste':
      return 'Ajuste'
  }
}

export function textoDeLineaDeDesglose(l: LineaDeDesgloseDePago): string {
  const medio = l.nombreMedioPago ?? 'Medio de pago dado de baja'
  const referencia = l.referencia ? `, ref. ${l.referencia}` : ''

  return `${medio}: ${formatearImporte(l.importe, { simbolo: true })} (${etiquetaDeOrigenFondos(l.origenFondos)})${referencia}`
}

// ---- Resultado incierto ------------------------------------------------------------------

/** Cómo se nombra una escritura cuyo resultado no se pudo confirmar: tipo, día y monto, para que quien
 * lee el aviso la reconozca entre los movimientos de la tabla. */
export function descripcionDeHoras(f: FormularioDeHoras): string {
  const importe = importeDeHoras(f.horas, f.valorHora)

  return `carga de ${f.horas === null ? '—' : formatearImporte(f.horas)} horas del ${isoATexto(f.fecha)} por ${importe === null ? '—' : formatearImporte(importe, { simbolo: true })}`
}

export function descripcionDeAjuste(f: FormularioDeAjuste): string {
  const firmado = importeFirmadoDeAjuste(f)

  return `ajuste del ${isoATexto(f.fecha)} por ${firmado === null ? '—' : formatearImporteConSigno(firmado, { simbolo: true })}`
}

export function descripcionDePago(f: FormularioDePago): string {
  return `pago del ${isoATexto(f.fecha)} por ${formatearImporte(totalDelPago(f), { simbolo: true })}`
}

const CIERRE_DEL_AVISO_INCIERTO = 'Revisá los movimientos que se muestran abajo antes de volver a cargarla.'

/** Aviso persistente de una escritura cuyo resultado no se pudo confirmar. Con `vistaActualizada` en
 * `false` ni siquiera se pudo releer la cuenta, así que lo que sigue es recargar la pantalla. */
export function avisoDeResultadoIncierto(descripcion: string, vistaActualizada: boolean): string {
  const encabezado = `No se pudo confirmar si se registró esta operación: ${descripcion}.`

  return vistaActualizada
    ? `${encabezado} ${CIERRE_DEL_AVISO_INCIERTO}`
    : `${encabezado} Tampoco se pudo actualizar la vista: recargá la pantalla y revisá los movimientos antes de volver a cargarla.`
}
