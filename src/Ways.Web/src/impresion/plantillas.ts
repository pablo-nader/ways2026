/**
 * Plantillas de ticket ESC/POS (stage-desktop-pos) sobre `ConstructorDeTicket` — puras, sin I/O:
 * reciben los datos ya resueltos por la pantalla y devuelven los bytes listos para `impresora.imprimir`.
 */
import { COLUMNAS_FUENTE_A, ConstructorDeTicket, dosColumnas } from './escpos'
import type { LineaDeTicket } from './escpos'
import { formatearPorcentajeDeAjuste, rotuloDeAjusteManual } from '../api/ajusteManual'
import { tieneDesgloseDeCierre } from '../api/caja'
import { calcularImporteAplicado } from '../api/cuentaCorriente'
import type { ComprobanteEmitido, DatosDeFacturaFiscal, DetalleDeTurno, EmisorDeFacturaFiscal, MedioPagoListado, ReceptorDeFacturaFiscal, TotalesDeFacturaFiscal, ResumenDeCierrePorRetiro, TurnoConArqueos } from '../api/tipos'
import { formatearImporte, formatearImporteConSigno } from '../formato/importes'
import { fechaHoraATexto } from '../formato/fechas'

/** Mismos datos que ya conoce el shell del POS de escritorio al momento de imprimir — nunca se
 * vuelven a pedir acá. */
export type ContextoDeImpresion = { empresa: string; puntoVenta: string; cajero: string }

/** Formateador compartido (`formato/importes.ts`): signo antes del símbolo, nunca `$-`. */
function formatearMoneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

/** Mismo formato que el resto de las pantallas — sin forzar timezone, la máquina del escritorio
 * ya corre en la zona horaria del comercio. */
function formatearFechaHora(iso: string): string {
  return fechaHoraATexto(iso)
}

/** Solo hora:minuto — usado en el detalle de retiros del ticket de cierre, donde la fecha ya
 * está dada por la propia jornada del turno y repetirla en cada fila sería ruido. */
function formatearHora(iso: string): string {
  return new Date(iso).toLocaleTimeString('es-AR', { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' })
}

function encabezado(ticket: ConstructorDeTicket, contexto: ContextoDeImpresion): void {
  ticket
    .inicializar()
    .codificarPagina()
    .alinear('centro')
    .negrita(true)
    .linea(contexto.empresa)
    .negrita(false)
    .linea(contexto.puntoVenta)
    .lineaDeGuiones()
}

/** Cliente identificado de un ticket, con su saldo antes y después de la operación. */
export type ClienteConSaldoEnTicket = {
  numero: number
  nombre: string
  saldoAntes: number
  saldoDespues: number
  /** `true` si el saldo no está confirmado por el servidor: se rotula "(estimado)". */
  estimado?: boolean
}

/** `cliente` solo se pasa para un cliente identificado (nunca el Consumidor Final): agrega su nombre
 * y su saldo antes y después de la venta. Una reimpresión los ignora, porque no conoce los saldos de
 * aquel momento. */
export type OpcionesDeTicketDeVenta = { reimpresion?: boolean; cliente?: ClienteConSaldoEnTicket }

/** Recortada al ancho del ticket con el mismo helper de columnas, para que un nombre largo no parta la línea. */
function lineaDeCliente(cliente: { numero: number; nombre: string }): string {
  return dosColumnas(`Cliente: #${cliente.numero} - ${cliente.nombre}`, '').trimEnd()
}

const ABREVIATURAS_DE_SALDO: Record<string, string> = {
  'Saldo anterior': 'Saldo ant.',
  'Saldo actual': 'Saldo act.',
  'Saldo nuevo': 'Saldo nvo.',
}

/** Una línea de saldo: un saldo negativo es plata a favor del cliente y se imprime con su valor
 * absoluto; uno no confirmado se rotula "(estimado)". Si con un importe grande el rótulo no entra
 * en el ancho, se abrevia la base ("Saldo ant.", "a fav.") antes de perder la marca: el helper de
 * columnas recorta lo de la izquierda, y "(estimado)" va al final. */
function lineaDeSaldo(ticket: ConstructorDeTicket, rotulo: string, valor: number, estimado: boolean): void {
  const importe = formatearMoneda(Math.abs(valor))
  const armar = (base: string, aFavor: string) => `${base}${valor < 0 ? aFavor : ''}${estimado ? ' (estimado)' : ''}`
  let etiqueta = armar(rotulo, ' a favor')
  if (etiqueta.length + 1 + importe.length > COLUMNAS_FUENTE_A) etiqueta = armar(ABREVIATURAS_DE_SALDO[rotulo] ?? rotulo, ' a fav.')
  ticket.lineaDeColumnas(etiqueta, importe)
}

/**
 * Ticket de venta (`POST /api/ventas`) — NO es un comprobante fiscal: el flujo actual solo emite
 * tipo `TX`, así que el ticket lo dice explícitamente en vez de parecer una factura. `medios`
 * resuelve el `comportamiento` de cada pago (para el rótulo, no para el cajón — eso lo decide
 * `algunPagoEnEfectivo` aparte).
 *
 * Ajuste manual de precio: un comprobante con descuento o recargo manual lo dice en el encabezado
 * (`*** CON DESCUENTO MANUAL ***` / `*** CON RECARGO MANUAL ***`, en mayúsculas y sin tilde como el
 * resto de los avisos) para que un precio cambiado a mano nunca pase inadvertido en un ticket
 * impreso; cada línea ajustada lleva su porcentaje y monto con signo, y el bloque de totales suma
 * las filas "Desc. manual" y "Recargo". Un comprobante sin ajustes imprime exactamente lo de antes.
 * Los campos son nuevos: un servidor anterior no los manda, y ahí no se imprime nada de esto.
 *
 * `opciones.reimpresion` (stage-desktop-pos, acción "Imprimir" de "Ventas del turno"): imprime
 * una línea "REIMPRESION" bien visible para que una copia nunca se confunda con el original, y
 * NUNCA pulsa el cajón de dinero aunque el comprobante tenga un pago en efectivo (decisión del
 * dueño: reimprimir no es una venta nueva, abrir el cajón sin eso es un agujero de control de
 * caja) — el resto del contenido queda igual.
 */
export function ticketDeVenta(
  comprobante: ComprobanteEmitido,
  contexto: ContextoDeImpresion,
  medios: MedioPagoListado[],
  opciones: OpcionesDeTicketDeVenta = {},
): Uint8Array {
  return armarComprobanteDeVenta(comprobante, null, contexto, medios, opciones)
}

/** `yyyy-MM-dd` (un `DateOnly` del servidor) → `dd/mm/yyyy`, sin pasar por `Date` para no correr el día por zona horaria. */
function fechaDeFactura(fecha: string): string {
  const [anio, mes, dia] = fecha.split('-')
  return `${dia}/${mes}/${anio}`
}

/** Código ARCA del tipo de comprobante según la letra de la factura (A 001, B 006, C 011); el contrato no trae el número. */
const CODIGO_ARCA_POR_LETRA: Record<string, string> = { A: '001', B: '006', C: '011' }

/**
 * Factura de una venta aprobada por ARCA, en el modelo de tique-factura de la RG 4291 art. 15: emisor, letra y
 * código, número, fecha, condición de venta, receptor, el cuerpo del ticket (ítems, totales, pagos, cajón), el IVA
 * discriminado en la letra A, la leyenda de transparencia fiscal, CAE con su vencimiento y el QR de ARCA.
 * `emisor`, `receptor` y `totales` son opcionales (una factura guardada antes de T7b no los trae): lo que falta se
 * omite, nunca se imprime "undefined". Sin tildes en los avisos fijos, igual que el ticket.
 */
export function facturaDeVenta(
  comprobante: ComprobanteEmitido,
  factura: DatosDeFacturaFiscal,
  contexto: ContextoDeImpresion,
  medios: MedioPagoListado[],
  opciones: OpcionesDeTicketDeVenta = {},
): Uint8Array {
  return armarComprobanteDeVenta(comprobante, factura, contexto, medios, opciones)
}

function armarComprobanteDeVenta(
  comprobante: ComprobanteEmitido,
  factura: DatosDeFacturaFiscal | null,
  contexto: ContextoDeImpresion,
  medios: MedioPagoListado[],
  opciones: OpcionesDeTicketDeVenta,
): Uint8Array {
  const medioPorId = new Map(medios.map((m) => [m.id, m]))
  const ticket = new ConstructorDeTicket()

  if (factura?.emisor) encabezadoLegal(ticket, factura.emisor)
  else encabezado(ticket, contexto)

  if (opciones.reimpresion) {
    // Sin tilde a propósito: mismo criterio que "COMPROBANTE NO VALIDO COMO FACTURA" de abajo —
    // el ticket entero evita acentos en las líneas de aviso para no depender de que la tabla
    // CP858 los tenga mapeados en el hardware real.
    ticket.alinear('centro').negrita(true).linea('*** REIMPRESION ***').negrita(false)
  }

  const descuentoManual = comprobante.descuentoManualTotal ?? 0
  const recargoManual = comprobante.recargoManualTotal ?? 0
  const clienteConSaldo = opciones.reimpresion ? undefined : opciones.cliente

  ticket.alinear('centro').negrita(true).linea(factura ? `FACTURA ${factura.letra}` : 'COMPROBANTE NO VALIDO COMO FACTURA')
  const codigoArca = factura ? CODIGO_ARCA_POR_LETRA[factura.letra] : undefined
  if (codigoArca) ticket.linea(`Codigo N° ${codigoArca}`)
  if (descuentoManual !== 0) ticket.linea('*** CON DESCUENTO MANUAL ***')
  if (recargoManual !== 0) ticket.linea('*** CON RECARGO MANUAL ***')

  ticket.negrita(false).alinear('izquierda')
  if (factura) {
    ticket
      .linea(`Nro ${String(factura.puntoDeVentaFiscal).padStart(5, '0')}-${String(factura.numero).padStart(8, '0')}`)
      .linea(`Fecha: ${fechaDeFactura(factura.fecha)}`)
    ticket.linea(`Condicion de venta: ${condicionDeVenta(comprobante, medioPorId)}`)
  } else {
    ticket.linea(`Comprobante: ${comprobante.numeroVisible}`).linea(`Fecha: ${formatearFechaHora(comprobante.fecha)}`)
  }
  ticket.linea(`Cajero: ${contexto.cajero}`)
  if (clienteConSaldo) ticket.linea(lineaDeCliente(clienteConSaldo))
  ticket.lineaDeGuiones()
  if (factura?.receptor) bloqueDeReceptor(ticket, factura.receptor)

  for (const item of comprobante.items) {
    ticket.lineaDeColumnas(`${item.cantidad} x ${item.descripcion}`, formatearMoneda(item.total))
    if (item.descuento > 0) {
      ticket.lineaDeColumnas('  Descuento', `-${formatearMoneda(item.descuento)}`)
    }
    if (item.ajusteManualPorcentaje != null) {
      ticket.lineaDeColumnas(
        `  ${rotuloDeAjusteManual(item.ajusteManualPorcentaje)} ${formatearPorcentajeDeAjuste(item.ajusteManualPorcentaje)}%`,
        formatearImporteConSigno(item.ajusteManual, { simbolo: true }),
      )
    }
  }

  ticket.lineaDeGuiones()
  ticket.lineaDeColumnas('Subtotal', formatearMoneda(comprobante.subtotal))
  if (comprobante.descuentoTotal > 0) {
    ticket.lineaDeColumnas('Descuento', `-${formatearMoneda(comprobante.descuentoTotal)}`)
  }
  if (descuentoManual !== 0) {
    ticket.lineaDeColumnas('Desc. manual', formatearImporteConSigno(-descuentoManual, { simbolo: true }))
  }
  if (recargoManual !== 0) {
    ticket.lineaDeColumnas('Recargo', formatearImporteConSigno(recargoManual, { simbolo: true }))
  }
  ticket.tamanioDoble(true).negrita(true)
  ticket.lineaDeColumnas('TOTAL', formatearMoneda(comprobante.total))
  ticket.tamanioDoble(false).negrita(false)

  if (factura?.letra === 'A' && factura.totales) bloqueDeIvaDiscriminado(ticket, factura.totales)

  ticket.lineaDeGuiones()
  ticket.linea('Pagos:')
  for (const pago of comprobante.pagos) {
    const nombreMedio = medioPorId.get(pago.idMedioPago)?.nombre ?? `Medio #${pago.idMedioPago}`
    ticket.lineaDeColumnas(nombreMedio, formatearMoneda(pago.importe))
    if (pago.vuelto > 0) {
      ticket.lineaDeColumnas('  Vuelto', formatearMoneda(pago.vuelto))
    }
  }

  if (clienteConSaldo) {
    ticket.lineaDeGuiones()
    lineaDeSaldo(ticket, 'Saldo anterior', clienteConSaldo.saldoAntes, clienteConSaldo.estimado ?? false)
    lineaDeSaldo(ticket, 'Saldo actual', clienteConSaldo.saldoDespues, clienteConSaldo.estimado ?? false)
  }

  if (factura) pieLegal(ticket, factura)

  ticket.alinear('centro').avanzar(1).linea('¡Gracias por su compra!')

  // Pulso del cajón en el MISMO trabajo de impresión (nunca un segundo `imprimir` aparte): si
  // algún pago es en efectivo se abre antes del corte, para que el cajero lo encuentre abierto
  // apenas termina de imprimirse el ticket. NUNCA en una reimpresión (decisión del dueño): abrir
  // el cajón sin una venta nueva de por medio es un agujero de control de caja — una copia del
  // ticket original no vuelve a mover dinero, así que no vuelve a pulsar el cajón aunque el pago
  // original haya sido en efectivo.
  if (!opciones.reimpresion && algunPagoEnEfectivo(comprobante, medios)) {
    ticket.abrirCajon()
  }

  ticket.avanzar(2).cortar()

  return ticket.bytes()
}

/** `Cuenta corriente` si algún pago mueve el saldo del cliente (comportamiento del medio), si no `Contado`. */
function condicionDeVenta(comprobante: ComprobanteEmitido, medioPorId: Map<number, MedioPagoListado>): string {
  const aCuentaCorriente = comprobante.pagos.some((pago) => medioPorId.get(pago.idMedioPago)?.comportamiento === 'CuentaCorriente')
  return aCuentaCorriente ? 'Cuenta corriente' : 'Contado'
}

/** Bloque del emisor (RG 1415 Anexo II A I a): los datos que faltan se omiten. */
function encabezadoLegal(ticket: ConstructorDeTicket, emisor: EmisorDeFacturaFiscal): void {
  ticket.inicializar().codificarPagina().alinear('centro').negrita(true).linea(emisor.razonSocial).negrita(false)
  if (emisor.nombreFantasia) ticket.linea(emisor.nombreFantasia)
  if (emisor.domicilio) ticket.linea(emisor.domicilio)
  if (emisor.cuit) ticket.linea(`CUIT: ${emisor.cuit}`)
  if (emisor.ingresosBrutos) ticket.linea(`Ing. Brutos: ${emisor.ingresosBrutos}`)
  if (emisor.condicionFiscal) ticket.linea(emisor.condicionFiscal)
  if (emisor.inicioActividades) ticket.linea(`Inicio de actividades: ${fechaDeFactura(emisor.inicioActividades)}`)
  ticket.lineaDeGuiones()
}

const DOCUMENTO_SIN_IDENTIFICAR = 99

/** Receptor (RG 1415 Anexo II A II): el consumidor final sin documento se imprime solo como tal. */
function bloqueDeReceptor(ticket: ConstructorDeTicket, receptor: ReceptorDeFacturaFiscal): void {
  const sinDocumento = receptor.codigoTipoDocumento === DOCUMENTO_SIN_IDENTIFICAR
  const consumidorFinal = sinDocumento || /consumidor final/i.test(receptor.condicionFiscal ?? '')
  ticket.alinear('izquierda')
  if (consumidorFinal) {
    ticket.linea('A CONSUMIDOR FINAL')
    if (!sinDocumento) ticket.linea(`${receptor.tipoDocumento}: ${receptor.numeroDocumento}`)
  } else {
    ticket.linea(receptor.nombre).linea(`${receptor.tipoDocumento}: ${receptor.numeroDocumento}`)
    if (receptor.condicionFiscal) ticket.linea(receptor.condicionFiscal)
    if (receptor.domicilio) ticket.linea(receptor.domicilio)
  }
  ticket.lineaDeGuiones()
}

/** IVA discriminado de la letra A: neto gravado, un renglón por alícuota y los conceptos exento / no gravado. */
function bloqueDeIvaDiscriminado(ticket: ConstructorDeTicket, totales: TotalesDeFacturaFiscal): void {
  ticket.lineaDeGuiones()
  ticket.lineaDeColumnas('Neto gravado', formatearMoneda(totales.impNeto))
  for (const alicuota of totales.alicuotas) {
    ticket.lineaDeColumnas(`IVA ${String(alicuota.porcentaje).replace('.', ',')}%`, formatearMoneda(alicuota.iva))
  }
  if (totales.impOpEx > 0) ticket.lineaDeColumnas('Exento', formatearMoneda(totales.impOpEx))
  if (totales.impTotConc > 0) ticket.lineaDeColumnas('No gravado', formatearMoneda(totales.impTotConc))
}

/**
 * Pie legal: leyenda de transparencia fiscal (RG 5614; en todas las letras), CAE con su vencimiento y QR de ARCA.
 * Sin `totales` la leyenda se omite en A y B, porque no hay IVA que informar; la C imprime 0,00 igual.
 */
function pieLegal(ticket: ConstructorDeTicket, factura: DatosDeFacturaFiscal): void {
  const ivaContenido = factura.totales ? factura.totales.impIva : factura.letra === 'C' ? 0 : null
  ticket.lineaDeGuiones()
  if (ivaContenido !== null) {
    ticket.alinear('centro').negrita(true).linea('OTRAS LEYENDAS').linea('TRANSPARENCIA FISCAL').negrita(false).alinear('izquierda')
    ticket.lineaDeColumnas('IVA Contenido', formatearMoneda(ivaContenido))
    ticket.lineaDeColumnas('Otros Impuestos Nacionales Indirectos', formatearMoneda(0))
    ticket.lineaDeGuiones()
  }
  ticket.alinear('izquierda')
  if (factura.cae) ticket.linea(`C.A.E. N°: ${factura.cae}`)
  if (factura.caeVencimiento) ticket.linea(`Fecha de Vto.: ${fechaDeFactura(factura.caeVencimiento)}`)
  if (factura.payloadQr) {
    try {
      ticket.alinear('centro').qr(factura.payloadQr)
    } catch {
      // Un QR no imprimible (qr() solo lanza RangeError) no puede tirar abajo la factura: se omite.
    }
  }
}

/** Datos del recibo de un pago a cuenta. `saldoAnterior - total pagado = saldoNuevo`: quien lo arma
 * los deriva juntos (`saldosDeUnPagoACuenta`) para que los tres números nunca se contradigan. */
export type DatosDeReciboDePago = {
  comprobante: ComprobanteEmitido
  cliente: { numero: number; nombre: string }
  saldoAnterior: number
  saldoNuevo: number
  /** `true` si los saldos no están confirmados por el servidor: se rotulan "(estimado)". */
  estimado?: boolean
}

/**
 * Recibo de un pago a cuenta (comprobante RC, `POST /api/clientes/{id}/cuenta-corriente/pagos`): cada
 * medio con su importe y vuelto, el total pagado (importes menos vueltos) y el saldo del cliente antes
 * y después. Con algún pago en efectivo pulsa el cajón en el mismo trabajo de impresión, antes del
 * corte, igual que `ticketDeVenta`.
 */
export function ticketDePagoACuenta(datos: DatosDeReciboDePago, contexto: ContextoDeImpresion, medios: MedioPagoListado[]): Uint8Array {
  const { comprobante } = datos
  const medioPorId = new Map(medios.map((m) => [m.id, m]))
  const ticket = new ConstructorDeTicket()

  encabezado(ticket, contexto)

  ticket
    .alinear('centro')
    .negrita(true)
    .linea('RECIBO DE PAGO A CUENTA')
    .negrita(false)
    .alinear('izquierda')
    .linea(`Comprobante: RC ${comprobante.numeroVisible}`)
    .linea(`Fecha: ${formatearFechaHora(comprobante.fecha)}`)
    .linea(`Cajero: ${contexto.cajero}`)
    .linea(lineaDeCliente(datos.cliente))
    .lineaDeGuiones()

  ticket.linea('Pagos:')
  for (const pago of comprobante.pagos) {
    ticket.lineaDeColumnas(medioPorId.get(pago.idMedioPago)?.nombre ?? `Medio #${pago.idMedioPago}`, formatearMoneda(pago.importe))
    if (pago.vuelto > 0) {
      ticket.lineaDeColumnas('  Vuelto', formatearMoneda(pago.vuelto))
    }
  }
  ticket.negrita(true).lineaDeColumnas('Total pagado', formatearMoneda(calcularImporteAplicado(comprobante.pagos))).negrita(false)

  ticket.lineaDeGuiones()
  lineaDeSaldo(ticket, 'Saldo anterior', datos.saldoAnterior, datos.estimado ?? false)
  ticket.negrita(true)
  lineaDeSaldo(ticket, 'Saldo nuevo', datos.saldoNuevo, datos.estimado ?? false)
  ticket.negrita(false)

  if (algunPagoEnEfectivo(comprobante, medios)) {
    ticket.abrirCajon()
  }

  ticket.avanzar(2).cortar()

  return ticket.bytes()
}

/** `true` si algún pago del comprobante usa un medio `Efectivo` (`comportamiento`) — dispara el
 * pulso del cajón. Sin el medio en la lista (debería ser imposible: el comprobante solo puede
 * referenciar medios del propio tenant) se trata como "no es efectivo", nunca se asume lo
 * contrario — abrir el cajón de más es peor que no abrirlo. */
export function algunPagoEnEfectivo(comprobante: ComprobanteEmitido, medios: MedioPagoListado[]): boolean {
  const medioPorId = new Map(medios.map((m) => [m.id, m]))
  return comprobante.pagos.some((pago) => medioPorId.get(pago.idMedioPago)?.comportamiento === 'Efectivo')
}

/**
 * Reporte Z — acepta tanto la respuesta directa del cierre (`TurnoConArqueos`, con el arqueo
 * declarado/esperado/diferencia por medio) como el detalle leído después (`DetalleDeTurno`, solo
 * con el esperado del resumen) — ambos vienen de la MISMA derivación server-side, la plantilla
 * solo elige qué columnas tiene disponibles.
 */
export function reporteZ(datos: TurnoConArqueos | DetalleDeTurno, contexto: ContextoDeImpresion): Uint8Array {
  const turno = 'resumen' in datos ? null : datos
  const resumen = 'resumen' in datos ? datos.resumen : null

  const ticket = new ConstructorDeTicket()
  encabezado(ticket, contexto)

  ticket.alinear('centro').negrita(true).linea('REPORTE Z').negrita(false).alinear('izquierda')

  if (turno) {
    ticket
      .linea(`Turno #${turno.id}`)
      .linea(`Apertura: ${formatearFechaHora(turno.fechaApertura)}`)
      .linea(`Cierre: ${turno.fechaCierre ? formatearFechaHora(turno.fechaCierre) : '—'}`)
      .linea(`Fondo inicial: ${formatearMoneda(turno.fondoInicial)}`)
      .lineaDeGuiones()

    for (const arqueo of turno.arqueos) {
      ticket.lineaDeColumnas(`Medio #${arqueo.idMedioPago} esperado`, formatearMoneda(arqueo.importeEsperado))
      ticket.lineaDeColumnas(`Medio #${arqueo.idMedioPago} declarado`, formatearMoneda(arqueo.importeDeclarado))
      ticket.lineaDeColumnas(`Medio #${arqueo.idMedioPago} diferencia`, formatearMoneda(arqueo.diferencia))
    }
    if (turno.arqueos.length === 0) {
      ticket.linea('Sin actividad en el turno.')
    }
  }

  if (resumen) {
    ticket.linea(`Turno #${resumen.idTurnoCaja}`).linea(`Tickets: ${resumen.cantidadTickets}`).lineaDeGuiones()

    for (const medio of resumen.medios) {
      ticket.lineaDeColumnas(`Medio #${medio.idMedioPago} esperado`, formatearMoneda(medio.importeEsperado))
    }
    if (resumen.medios.length === 0) {
      ticket.linea('Sin actividad en el turno.')
    }
  }

  if ('fechaRecalculo' in datos && datos.fechaRecalculo) {
    ticket.linea(`Arqueo recalculado el ${formatearFechaHora(datos.fechaRecalculo)}`)
  }

  ticket.lineaDeGuiones().linea(`Cajero: ${contexto.cajero}`)
  ticket.avanzar(2).cortar()

  return ticket.bytes()
}

/**
 * "Ticket en blanco" (stage-pos-retiros-y-cierre-por-retiro, etapa 5): el único propósito es
 * pulsar el cajón — el dueño lo llama así porque no imprime nada legible. Únicamente `ESC @`
 * (para dejar el firmware en un estado conocido) seguido de `ESC p` (`abrirCajon`): sin
 * `codificarPagina`, sin texto, sin avance de papel ni corte — abrir el cajón para contar/retirar
 * NUNCA debe gastar papel. Se emite en su propio trabajo de impresión, ANTES de que el cajero
 * cuente nada — nunca en el mismo trabajo que un ticket con contenido.
 */
export function pulsoDeCajon(): Uint8Array {
  return new ConstructorDeTicket().inicializar().abrirCajon().bytes()
}

/** Datos que ya resolvió la pantalla para el ticket "RETIRO DE EFECTIVO" — `vendedor` es siempre
 * `contexto.cajero` (quien opera la caja en el momento del retiro), nunca se vuelve a pedir acá. */
export type DatosTicketRetiro = { fecha: string; importe: number }

/**
 * Ticket "RETIRO DE EFECTIVO" (stage-pos-retiros-y-cierre-por-retiro, etapa 5): comprobante interno
 * del retiro de efectivo a mitad de turno — encabezado + fecha/hora + importe + vendedor + una
 * línea de firma. NUNCA pulsa el cajón (`abrirCajon`, ver judgment-day del ticket de venta): el
 * cajón para este retiro ya se abrió en su propio trabajo previo (`pulsoDeCajon`), volver a
 * pulsarlo acá sería un segundo pulso sin motivo.
 */
export function ticketRetiroDeEfectivo(datos: DatosTicketRetiro, contexto: ContextoDeImpresion): Uint8Array {
  const ticket = new ConstructorDeTicket()
  encabezado(ticket, contexto)

  ticket
    .alinear('centro')
    .negrita(true)
    .linea('RETIRO DE EFECTIVO')
    .negrita(false)
    .alinear('izquierda')
    .linea(`Fecha: ${formatearFechaHora(datos.fecha)}`)
    .lineaDeGuiones()

  ticket.tamanioDoble(true).negrita(true)
  ticket.lineaDeColumnas('Importe', formatearMoneda(datos.importe))
  ticket.tamanioDoble(false).negrita(false)

  ticket.lineaDeGuiones().linea(`Vendedor: ${contexto.cajero}`)

  ticket.avanzar(3).linea('_'.repeat(COLUMNAS_FUENTE_A)).alinear('centro').linea('Firma')

  ticket.avanzar(2).cortar()

  return ticket.bytes()
}

/**
 * Ticket "CIERRE DE TURNO" (stage-pos-retiros-y-cierre-por-retiro, etapa 5, cierre por retiro): el
 * comprobante que el cajero se lleva al cerrar el turno retirando el efectivo contado — todo el
 * contenido sale de `ResumenDeCierrePorRetiro`, la MISMA derivación que ya persistió el servidor
 * (nunca un recálculo local). NUNCA pulsa el cajón: para este cierre el cajón ya se abrió en su
 * propio trabajo previo (`pulsoDeCajon`, antes de que el cajero cuente el efectivo a retirar).
 *
 * `gastosEnEfectivo`/`refuerzos` solo se imprimen si son `!== 0` (spec del dueño: una línea en
 * cero es ruido en un ticket que ya es largo). `diferencia` se rotula "Sobrante"/"Faltante"/"Sin
 * diferencia" según el signo — se imprime el VALOR ABSOLUTO junto al rótulo: un "Faltante:
 * -$100,00" sería un signo negativo redundante con la propia palabra "Faltante" (el rótulo ya dice
 * la dirección, mostrar el importe crudo con signo duplicaría la información y podría leerse como
 * una resta más).
 */
export function cierreDeTurno(resumen: ResumenDeCierrePorRetiro, contexto: ContextoDeImpresion): Uint8Array {
  return armarCierreDeTurno(resumen, contexto).bytes()
}

/** Las líneas del MISMO ticket de `cierreDeTurno`, para mostrarlo e imprimirlo desde el navegador
 * (back office): comparten el armado, así que el contenido no puede divergir del de la térmica. */
export function lineasDeCierreDeTurno(resumen: ResumenDeCierrePorRetiro, contexto: ContextoDeImpresion): LineaDeTicket[] {
  return armarCierreDeTurno(resumen, contexto).lineas()
}

/** Rótulos cortos de los motivos fijos: el POS escribe "Retiro de efectivo" y el servidor "Retiro de
 * cierre de turno". Cualquier otro motivo (texto libre de un movimiento manual) se imprime tal cual;
 * `lineaDeColumnas` lo trunca si no entra, sin desplazar el importe. */
const ROTULOS_DE_RETIRO: Record<string, string> = {
  'Retiro de efectivo': 'Efectivo',
  'Retiro de cierre de turno': 'Cierre',
}

function rotuloDeRetiro(motivo: string): string {
  return ROTULOS_DE_RETIRO[motivo] ?? motivo
}

function armarCierreDeTurno(resumen: ResumenDeCierrePorRetiro, contexto: ContextoDeImpresion): ConstructorDeTicket {
  const ticket = new ConstructorDeTicket()
  encabezado(ticket, contexto)

  ticket.alinear('centro').negrita(true).linea('CIERRE DE TURNO').negrita(false).alinear('izquierda')

  ticket
    .linea(`Turno #${resumen.idTurnoCaja}`)
    .linea(`Apertura: ${formatearFechaHora(resumen.fechaApertura)}`)
    .linea(`Cierre: ${formatearFechaHora(resumen.fechaCierre)}`)
    .linea(`Vendedor: ${resumen.vendedor}`)

  // Solo si quien cerró es distinto de quien abrió — repetir el mismo nombre dos veces es ruido.
  if (resumen.empleadoCierre !== resumen.vendedor) {
    ticket.linea(`Cerrado por: ${resumen.empleadoCierre}`)
  }

  ticket.lineaDeGuiones()

  ticket.linea('Ventas por medio de pago:')
  for (const venta of resumen.ventasPorMedio) {
    ticket.lineaDeColumnas(venta.nombre, formatearMoneda(venta.importe))
  }
  if (resumen.ventasPorMedio.length === 0) {
    ticket.linea('Sin ventas en el turno.')
  }
  // Un resumen anterior al desglose, o parcial, no trae los subtotales: se omiten antes que adivinarlos.
  const desglose = tieneDesgloseDeCierre(resumen)
  if (desglose) {
    ticket.lineaDeGuiones()
    ticket.lineaDeColumnas('Total digitales', formatearMoneda(resumen.totalDigitales))
    ticket.lineaDeColumnas('Total efectivo', formatearMoneda(resumen.totalEfectivo))
  }
  ticket.negrita(true).lineaDeColumnas('Total general', formatearMoneda(resumen.totalVentas)).negrita(false)
  if (desglose && resumen.ventasACuentaCorriente !== 0) {
    ticket.linea('')
    ticket.lineaDeColumnas('Fiado en cuenta corriente', formatearMoneda(resumen.ventasACuentaCorriente))
  }

  ticket.lineaDeGuiones()

  ticket.linea('Retiros:')
  for (const retiro of resumen.retiros) {
    ticket.lineaDeColumnas(`${formatearHora(retiro.fecha)} ${rotuloDeRetiro(retiro.motivo)}`, formatearMoneda(retiro.importe))
  }
  if (resumen.retiros.length === 0) {
    ticket.linea('Sin retiros en el turno.')
  }
  ticket.negrita(true).lineaDeColumnas('Total retiros', formatearMoneda(resumen.totalRetiros)).negrita(false)

  ticket.lineaDeGuiones()
  ticket.lineaDeColumnas('Ventas en efectivo', formatearMoneda(resumen.ventasEnEfectivoNetas))
  if (resumen.gastosEnEfectivo !== 0) {
    ticket.lineaDeColumnas('Gastos en efectivo', formatearMoneda(resumen.gastosEnEfectivo))
  }
  if (resumen.refuerzos !== 0) {
    ticket.lineaDeColumnas('Refuerzos', formatearMoneda(resumen.refuerzos))
  }

  ticket.lineaDeGuiones()
  const rotuloDiferencia = resumen.diferencia > 0 ? 'Sobrante' : resumen.diferencia < 0 ? 'Faltante' : 'Sin diferencia'
  ticket.tamanioDoble(true).negrita(true)
  ticket.lineaDeColumnas(rotuloDiferencia, formatearMoneda(Math.abs(resumen.diferencia)))
  ticket.tamanioDoble(false).negrita(false)

  ticket.lineaDeGuiones()
  ticket.lineaDeColumnas('Fondo inicial (queda en caja)', formatearMoneda(resumen.fondoInicial))

  ticket.avanzar(2).cortar()

  return ticket
}
