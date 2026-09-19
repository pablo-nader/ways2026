/**
 * Plantillas de ticket ESC/POS (stage-desktop-pos) sobre `ConstructorDeTicket` — puras, sin I/O:
 * reciben los datos ya resueltos por la pantalla y devuelven los bytes listos para `impresora.imprimir`.
 */
import { COLUMNAS_FUENTE_A, ConstructorDeTicket } from './escpos'
import type { ComprobanteEmitido, DetalleDeTurno, MedioPagoListado, ResumenDeCierrePorRetiro, TurnoConArqueos } from '../api/tipos'
import { formatearImporte } from '../formato/importes'

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
  return new Date(iso).toLocaleString('es-AR')
}

/** Solo hora:minuto — usado en el detalle de retiros del ticket de cierre, donde la fecha ya
 * está dada por la propia jornada del turno y repetirla en cada fila sería ruido. */
function formatearHora(iso: string): string {
  return new Date(iso).toLocaleTimeString('es-AR', { hour: '2-digit', minute: '2-digit', hour12: false })
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

/** Opciones de `ticketDeVenta` — hoy solo `reimpresion` (stage-desktop-pos, "Ventas del turno"). */
export type OpcionesDeTicketDeVenta = { reimpresion?: boolean }

/**
 * Ticket de venta (`POST /api/ventas`) — NO es un comprobante fiscal: el flujo actual solo emite
 * tipo `TX`, así que el ticket lo dice explícitamente en vez de parecer una factura. `medios`
 * resuelve el `comportamiento` de cada pago (para el rótulo, no para el cajón — eso lo decide
 * `algunPagoEnEfectivo` aparte).
 *
 * `opciones.reimpresion` (stage-desktop-pos, acción "Reimprimir" de "Ventas del turno"): imprime
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
  const medioPorId = new Map(medios.map((m) => [m.id, m]))
  const ticket = new ConstructorDeTicket()

  encabezado(ticket, contexto)

  if (opciones.reimpresion) {
    // Sin tilde a propósito: mismo criterio que "COMPROBANTE NO VALIDO COMO FACTURA" de abajo —
    // el ticket entero evita acentos en las líneas de aviso para no depender de que la tabla
    // CP858 los tenga mapeados en el hardware real.
    ticket.alinear('centro').negrita(true).linea('*** REIMPRESION ***').negrita(false)
  }

  ticket
    .alinear('centro')
    .negrita(true)
    .linea('COMPROBANTE NO VALIDO COMO FACTURA')
    .negrita(false)
    .alinear('izquierda')
    .linea(`Comprobante: ${comprobante.numeroVisible}`)
    .linea(`Fecha: ${formatearFechaHora(comprobante.fecha)}`)
    .linea(`Cajero: ${contexto.cajero}`)
    .lineaDeGuiones()

  for (const item of comprobante.items) {
    ticket.lineaDeColumnas(`${item.cantidad} x ${item.descripcion}`, formatearMoneda(item.total))
    if (item.descuento > 0) {
      ticket.lineaDeColumnas('  Descuento', `-${formatearMoneda(item.descuento)}`)
    }
  }

  ticket.lineaDeGuiones()
  ticket.lineaDeColumnas('Subtotal', formatearMoneda(comprobante.subtotal))
  if (comprobante.descuentoTotal > 0) {
    ticket.lineaDeColumnas('Descuento', `-${formatearMoneda(comprobante.descuentoTotal)}`)
  }
  ticket.tamanioDoble(true).negrita(true)
  ticket.lineaDeColumnas('TOTAL', formatearMoneda(comprobante.total))
  ticket.tamanioDoble(false).negrita(false)

  ticket.lineaDeGuiones()
  ticket.linea('Pagos:')
  for (const pago of comprobante.pagos) {
    const nombreMedio = medioPorId.get(pago.idMedioPago)?.nombre ?? `Medio #${pago.idMedioPago}`
    ticket.lineaDeColumnas(nombreMedio, formatearMoneda(pago.importe))
    if (pago.vuelto > 0) {
      ticket.lineaDeColumnas('  Vuelto', formatearMoneda(pago.vuelto))
    }
  }

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
  ticket.negrita(true).lineaDeColumnas('Total ventas', formatearMoneda(resumen.totalVentas)).negrita(false)

  ticket.lineaDeGuiones()

  ticket.linea('Retiros:')
  for (const retiro of resumen.retiros) {
    ticket.lineaDeColumnas(formatearHora(retiro.fecha), formatearMoneda(retiro.importe))
    ticket.linea(`  ${retiro.motivo} (${retiro.empleado})`)
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

  return ticket.bytes()
}
