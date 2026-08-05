/**
 * Cliente HTTP + reglas puras de cuenta corriente (stage-7-cuenta-corriente, Slice 5): estado de
 * cuenta (header + ledger) y pago a cuenta (RC). `validarPagoACuentaLocal` espeja
 * `ValidadorDePagoACuenta` (Ways.Domain.CuentaCorriente) para dar feedback instantáneo en
 * pantalla — nunca es autoritativo, el servidor vuelve a validar todo (mismo criterio que
 * `pagos.ts`, stage-5). `disponibilidadPrevia` espeja
 * `CalculadorDeEstadoDeCuenta.CalcularDisponibilidad` — mismo criterio no-autoritativo que
 * `arqueo.ts`.
 */
import { api } from './cliente'
import type {
  ComportamientoMedioPago,
  ComprobanteEmitido,
  DetalleDeConsumo,
  DetalleDeLinea,
  EstadoDeCuenta,
  MedioPagoListado,
  MovimientoDeCuentaCorriente,
  PagoDeCuenta,
  ResultadoDeReliquidacion,
  SolicitudDeAjuste,
  SolicitudDePagoACuenta,
  SolicitudDeReliquidacion,
} from './tipos'

function redondear(valor: number): number {
  return Math.round((valor + Number.EPSILON) * 100) / 100
}

/** Offset UTC del navegador (`±HH:MM`) para la fecha local dada — el servidor corre en UTC
 * (Docker) mientras el local opera en ART (UTC-3); sin este offset explícito, un `desde`/`hasta`
 * sin zona se interpreta como UTC del lado del servidor y un movimiento nocturno queda fuera del
 * día que el cajero ve en pantalla. Se calcula por fecha (no una única vez) para no romper en un
 * cambio de DST dentro del rango. */
function desplazamientoUtcLocal(anio: number, mes: number, dia: number): string {
  const minutos = new Date(anio, mes - 1, dia).getTimezoneOffset()
  const signo = minutos > 0 ? '-' : '+'
  const minutosAbsolutos = Math.abs(minutos)
  const horas = String(Math.floor(minutosAbsolutos / 60)).padStart(2, '0')
  const restoMinutos = String(minutosAbsolutos % 60).padStart(2, '0')
  return `${signo}${horas}:${restoMinutos}`
}

function fechaIsoConOffset(fechaIso: string, horaLimite: string): string {
  const [anio, mes, dia] = fechaIso.split('-').map(Number)
  return `${fechaIso}T${horaLimite}${desplazamientoUtcLocal(anio, mes, dia)}`
}

/** Arma el query string de `GET …/cuenta-corriente` — `historico` gana sobre `desde`/`hasta`
 * (mismo criterio que `ObtenerEstadoDeCuentaAsync`); un `desde`/`hasta` vacío se omite (el
 * servidor aplica su propio default de último mes). Un `desde`/`hasta` de un `<input type="date">`
 * (`YYYY-MM-DD`) se expande a los bordes inclusivos del día, con el offset horario local del
 * navegador (nunca un `Z`/sin-offset que el servidor interpretaría como UTC). */
export function construirQueryEstadoDeCuenta(desde: string, hasta: string, historico: boolean): string {
  const parametros = new URLSearchParams()
  if (historico) {
    parametros.set('historico', 'true')
  } else {
    if (desde) parametros.set('desde', fechaIsoConOffset(desde, '00:00:00'))
    if (hasta) parametros.set('hasta', fechaIsoConOffset(hasta, '23:59:59.999'))
  }
  const cadena = parametros.toString()
  return cadena ? `?${cadena}` : ''
}

function fechaLocalAIso(fecha: Date): string {
  const anio = fecha.getFullYear()
  const mes = String(fecha.getMonth() + 1).padStart(2, '0')
  const dia = String(fecha.getDate()).padStart(2, '0')
  return `${anio}-${mes}-${dia}`
}

/** Ventana por defecto de la pantalla (design.md, decisión 9: "the screen sends last-month by
 * default") — replica en el cliente el default del servidor (`hoy − 1 mes` → `hoy`) para que los
 * inputs de filtro nunca queden vacíos mostrando una ventana invisible. */
export function rangoUltimoMes(ahora: Date = new Date()): { desde: string; hasta: string } {
  const hasta = fechaLocalAIso(ahora)
  const anio = ahora.getFullYear()
  const mes = ahora.getMonth()
  // `new Date(anio, mes, 0)` cae en el último día del mes anterior — semántica AddMonths-clamp
  // (.NET): un 31 de marzo no puede convertirse en un inexistente "31 de febrero", se recorta al
  // último día real del mes anterior (28/29 de febrero, 30 de junio, etc.).
  const diasDelMesAnterior = new Date(anio, mes, 0).getDate()
  const dia = Math.min(ahora.getDate(), diasDelMesAnterior)
  const desde = fechaLocalAIso(new Date(anio, mes - 1, dia))
  return { desde, hasta }
}

export const clienteDeCuentaCorriente = {
  /** `GET /api/clientes/{id}/cuenta-corriente?desde=&hasta=&historico=` — header + página en un
   * único payload (design decisión 9), 200 siempre (nunca 404, incluso para un cliente sin
   * ningún movimiento). */
  obtenerEstado: (idCliente: number, desde: string, hasta: string, historico: boolean) =>
    api.get<EstadoDeCuenta>(
      `/clientes/${idCliente}/cuenta-corriente${construirQueryEstadoDeCuenta(desde, hasta, historico)}`,
    ),
  /** `POST /api/clientes/{id}/cuenta-corriente/pagos` — 201 con el comprobante RC, o
   * `409 turno_no_abierto` si el punto de venta no tiene turno abierto (spec: RC Requires An
   * Open Turno — rechazado antes que cualquier otro procesamiento). */
  registrarPago: (idCliente: number, solicitud: SolicitudDePagoACuenta) =>
    api.post<ComprobanteEmitido>(`/clientes/${idCliente}/cuenta-corriente/pagos`, solicitud),
  /** `POST /api/clientes/{id}/cuenta-corriente/ajustes` — 201 con el movimiento `Ajuste`. Sin
   * turno (design: Open Questions — "provenance, not authority"), a diferencia de un pago. */
  registrarAjuste: (idCliente: number, solicitud: SolicitudDeAjuste) =>
    api.post<MovimientoDeCuentaCorriente>(`/clientes/${idCliente}/cuenta-corriente/ajustes`, solicitud),
  /** `GET /api/clientes/{id}/cuenta-corriente/reliquidacion` — preview, sin lock, NUNCA
   * autoritativo (design: API Surface): un consumo marcado entre este preview y el commit
   * siguiente simplemente deja de aparecer, sin ninguna "reserva" del resultado. */
  previsualizarReliquidacion: (idCliente: number) =>
    api.get<ResultadoDeReliquidacion>(`/clientes/${idCliente}/cuenta-corriente/reliquidacion`),
  /** `POST /api/clientes/{id}/cuenta-corriente/reliquidacion` — commit, irreversible (spec:
   * Reliquidación Is Irreversible). Misma forma de respuesta que el preview (design: "never two
   * formulas") — `idsMovimientosCubiertos` vacío + `delta === 0` es un no-op limpio. */
  ejecutarReliquidacion: (idCliente: number, solicitud: SolicitudDeReliquidacion) =>
    api.post<ResultadoDeReliquidacion>(`/clientes/${idCliente}/cuenta-corriente/reliquidacion`, solicitud),
}

/** Etiqueta de pantalla por tipo de movimiento — un `Ajuste` se distingue por `etiqueta` (manual
 * vs. contramovimiento de anulación, espejo de `CalculadorDeEstadoDeCuenta.EtiquetarAjuste`). */
export function etiquetaDeMovimiento(m: Pick<MovimientoDeCuentaCorriente, 'tipo' | 'etiqueta'>): string {
  switch (m.tipo) {
    case 'Consumo':
      return 'Consumo'
    case 'Pago':
      return 'Pago'
    case 'ActualizacionPrecios':
      return 'Actualización de precios'
    case 'Ajuste':
      return m.etiqueta === 'AnulacionContramovimiento' ? 'Contramov. de anulación' : 'Ajuste manual'
  }
}

/** Espejo de `CalculadorDeEstadoDeCuenta.CalcularDisponibilidad` — `null` cuando
 * `creditoIlimitado` (nunca un número fabricado). Se usa para previsualizar la disponibilidad
 * tras un pago a cuenta antes de enviarlo (mismo criterio no-autoritativo que `arqueo.ts`). */
export function disponibilidadPrevia(saldo: number, limiteCredito: number, creditoIlimitado: boolean): number | null {
  return creditoIlimitado ? null : limiteCredito - saldo
}

// ---- Pago a cuenta: filas del panel + validación local --------------------------------------

/** `CuentaCorriente` nunca es un medio válido para pagar una RC — a diferencia de
 * `medioDisponibleParaCliente` (pagos.ts), esto no depende del cliente: una deuda no puede pagar
 * otra deuda (design decisión 6, spec: RC Forbids Cuenta Corriente Medios). */
export function medioFisicoParaPagoACuenta(medio: MedioPagoListado): boolean {
  return medio.comportamiento !== 'CuentaCorriente'
}

export type FilaPagoACuenta = { id: number; idMedioPago: number | ''; importe: string; referencia: string; vuelto: string }

export function filaPagoACuentaVacia(id: number): FilaPagoACuenta {
  return { id, idMedioPago: '', importe: '', referencia: '', vuelto: '' }
}

export type PagoACuentaParaCalculo = {
  idFila: number
  idMedioPago: number
  comportamiento: ComportamientoMedioPago
  admiteVuelto: boolean
  requiereReferencia: boolean
  importe: number
  vuelto: number
  referencia: string | null
}

/** Filas del panel → pagos de cálculo, descartando las que el cajero todavía no terminó de
 * completar (sin medio elegido o sin un importe positivo) — mismo criterio que
 * `filasAPagosParaCalculo` (pagos.ts). */
export function filasAPagosACuentaParaCalculo(
  filas: FilaPagoACuenta[],
  medioPorId: Record<number, MedioPagoListado>,
): PagoACuentaParaCalculo[] {
  const pagos: PagoACuentaParaCalculo[] = []
  for (const fila of filas) {
    if (fila.idMedioPago === '') continue
    const medio = medioPorId[fila.idMedioPago]
    if (!medio) continue
    const importe = Number(fila.importe)
    if (fila.importe.trim() === '' || !Number.isFinite(importe) || importe <= 0) continue
    const vueltoCandidato = fila.vuelto.trim() === '' ? 0 : Number(fila.vuelto)
    pagos.push({
      idFila: fila.id,
      idMedioPago: medio.id,
      comportamiento: medio.comportamiento,
      admiteVuelto: medio.admiteVuelto,
      requiereReferencia: medio.requiereReferencia,
      importe,
      vuelto: Number.isFinite(vueltoCandidato) ? vueltoCandidato : 0,
      referencia: fila.referencia.trim() === '' ? null : fila.referencia.trim(),
    })
  }
  return pagos
}

/** `importeAplicado = Σ importe − Σ vuelto` (legacy parity, espejo de
 * `ValidadorDePagoACuenta.Validar` — la RC no tiene ningún campo de importe propio, este es el
 * único lugar donde ese número existe). */
export function calcularImporteAplicado(pagos: { importe: number; vuelto: number }[]): number {
  const sumaImportes = pagos.reduce((acumulado, p) => acumulado + p.importe, 0)
  const sumaVueltos = pagos.reduce((acumulado, p) => acumulado + p.vuelto, 0)
  return redondear(sumaImportes - sumaVueltos)
}

export type RechazoDePagoACuenta = { codigo: string; mensaje: string }

/**
 * Espejo pixel-a-pixel del orden de `ValidadorDePagoACuenta.Validar` (Ways.Domain.CuentaCorriente)
 * — corta en el primer rechazo, nunca acumula errores, mismos códigos de dominio. Nunca
 * autoritativo: solo guía al cajero antes de intentar el pago real.
 */
export function validarPagoACuentaLocal(params: {
  pagos: PagoACuentaParaCalculo[]
  vueltoMaximo: number
}): RechazoDePagoACuenta | null {
  const { pagos, vueltoMaximo } = params

  for (const pago of pagos) {
    if (pago.importe < 0) {
      return { codigo: 'pago_importe_negativo', mensaje: 'El importe de un pago no puede ser negativo.' }
    }
  }

  for (const pago of pagos) {
    if (pago.vuelto < 0) {
      return { codigo: 'vuelto_negativo', mensaje: 'El vuelto de un pago no puede ser negativo.' }
    }
  }

  for (const pago of pagos) {
    if (pago.comportamiento === 'CuentaCorriente') {
      return {
        codigo: 'pago_a_cuenta_sin_medios_fisicos',
        mensaje: 'Un pago a cuenta no admite cuenta corriente como medio de pago.',
      }
    }
  }

  for (const pago of pagos) {
    if (pago.vuelto > 0 && !pago.admiteVuelto) {
      return { codigo: 'medio_no_admite_vuelto', mensaje: 'El medio de pago elegido no admite vuelto.' }
    }
  }

  const sumaVueltos = pagos.reduce((acumulado, p) => acumulado + p.vuelto, 0)
  if (sumaVueltos > vueltoMaximo) {
    return { codigo: 'vuelto_excedido', mensaje: 'El vuelto supera el máximo permitido.' }
  }

  for (const pago of pagos) {
    if (pago.requiereReferencia && (pago.referencia ?? '').trim() === '') {
      return { codigo: 'referencia_de_pago_requerida', mensaje: 'Este medio de pago requiere una referencia.' }
    }
  }

  const importeAplicado = calcularImporteAplicado(pagos)
  if (importeAplicado <= 0) {
    return { codigo: 'pago_a_cuenta_sin_importe', mensaje: 'Tenés que ingresar al menos un pago a cuenta.' }
  }

  return null
}

/** Pagos de cálculo → `SolicitudDePagoACuenta` — recorta observaciones vacías a `null` (mismo
 * criterio que el resto de la web). */
export function aSolicitudDePagoACuenta(
  idPuntoVenta: number,
  pagos: PagoACuentaParaCalculo[],
  observaciones: string,
): SolicitudDePagoACuenta {
  const cuerpo: PagoDeCuenta[] = pagos.map((p) => ({
    idMedioPago: p.idMedioPago,
    importe: p.importe,
    referencia: p.referencia,
    vuelto: p.vuelto,
  }))
  return { idPuntoVenta, pagos: cuerpo, observaciones: observaciones.trim() === '' ? null : observaciones.trim() }
}

// ---- Ajuste manual: validación local + mapper ------------------------------------------------

export const LONGITUD_MINIMA_DETALLE_AJUSTE = 5

export type RechazoDeAjuste = { codigo: string; mensaje: string }

/**
 * Espejo pixel-a-pixel de `ReglaDeAjusteDeCuenta.Validar` (Ways.Domain.CuentaCorriente): `importe`
 * no puede ser cero, `detalle` recortado tiene que tener al menos 5 caracteres. Nunca autoritativo
 * — solo guía al supervisor antes de intentar el ajuste real.
 */
export function validarAjusteLocal(params: { importe: number; detalle: string }): RechazoDeAjuste | null {
  const { importe, detalle } = params

  if (!Number.isFinite(importe) || importe === 0) {
    return { codigo: 'ajuste_importe_invalido', mensaje: 'El importe del ajuste no puede ser cero.' }
  }

  const detalleNormalizado = detalle.trim()
  if (detalleNormalizado.length < LONGITUD_MINIMA_DETALLE_AJUSTE) {
    return {
      codigo: 'ajuste_detalle_requerido',
      mensaje: `El detalle del ajuste es obligatorio y tiene que tener al menos ${LONGITUD_MINIMA_DETALLE_AJUSTE} caracteres.`,
    }
  }

  return null
}

/** `importe` positivo aumenta la deuda, negativo la reduce (spec: Ajuste Requires A Detalle —
 * "importe MAY be positive or negative") — el preview de saldo resultante es la misma suma que
 * hace el servidor (`UPDATE clientes SET saldo = saldo + importe`), nunca autoritativo. */
export function saldoResultanteDeAjuste(saldoActual: number, importe: number): number {
  return redondear(saldoActual + importe)
}

/** Importe/detalle → `SolicitudDeAjuste` — recorta el detalle (mismo criterio que el resto de la
 * web; el servidor también lo recorta, esto es solo para que el `POST` viaje ya prolijo). */
export function aSolicitudDeAjuste(idPuntoVenta: number, importe: number, detalle: string): SolicitudDeAjuste {
  return { idPuntoVenta, importe, detalle: detalle.trim() }
}

// ---- Reliquidación: mapper + lectura del resultado -------------------------------------------

/** `idPuntoVenta` → `SolicitudDeReliquidacion` — sin ningún otro campo (design: API Surface). */
export function aSolicitudDeReliquidacion(idPuntoVenta: number): SolicitudDeReliquidacion {
  return { idPuntoVenta }
}

/** Un resultado de reliquidación (preview o commit) es un no-op limpio cuando no cubrió ningún
 * consumo — distinguible de un error, nunca reportado como fallo (spec: A Run With No Eligible
 * Consumos Is A No-Op). `delta === 0` solo no alcanza: un consumo cubierto con líneas
 * no-precificables también puede aportar delta 0 sin ser un no-op de "nada para hacer". */
export function reliquidacionEsNoOp(resultado: Pick<ResultadoDeReliquidacion, 'idsMovimientosCubiertos'>): boolean {
  return resultado.idsMovimientosCubiertos.length === 0
}

// ---- Reliquidación: parseo defensivo del detalle guardado en el ledger ----------------------

function leerCampo<T>(objeto: Record<string, unknown>, ...claves: string[]): T {
  for (const clave of claves) {
    if (clave in objeto) return objeto[clave] as T
  }
  throw new Error(`campo ausente: ${claves.join('/')}`)
}

function normalizarDetalleDeLinea(crudo: unknown): DetalleDeLinea {
  if (typeof crudo !== 'object' || crudo === null) throw new Error('línea de reliquidación inválida')
  const o = crudo as Record<string, unknown>
  return {
    idArticulo: leerCampo<number | null>(o, 'idArticulo', 'IdArticulo') ?? null,
    cantidad: leerCampo<number>(o, 'cantidad', 'Cantidad'),
    precioHistorico: leerCampo<number>(o, 'precioHistorico', 'PrecioHistorico'),
    precioActual: leerCampo<number | null>(o, 'precioActual', 'PrecioActual') ?? null,
    totalHistorico: leerCampo<number>(o, 'totalHistorico', 'TotalHistorico'),
    totalDelDia: leerCampo<number | null>(o, 'totalDelDia', 'TotalDelDia') ?? null,
    delta: leerCampo<number>(o, 'delta', 'Delta'),
    motivo: leerCampo<string | null>(o, 'motivo', 'Motivo') ?? null,
  }
}

function normalizarDetalleDeConsumo(crudo: unknown): DetalleDeConsumo {
  if (typeof crudo !== 'object' || crudo === null) throw new Error('consumo de reliquidación inválido')
  const o = crudo as Record<string, unknown>
  const lineas = leerCampo<unknown[]>(o, 'lineas', 'Lineas')
  if (!Array.isArray(lineas)) throw new Error('lineas de reliquidación inválidas')
  return {
    idMovimiento: leerCampo<number>(o, 'idMovimiento', 'IdMovimiento'),
    idComprobanteVenta: leerCampo<number>(o, 'idComprobanteVenta', 'IdComprobanteVenta'),
    delta: leerCampo<number>(o, 'delta', 'Delta'),
    lineas: lineas.map(normalizarDetalleDeLinea),
  }
}

/**
 * Parsea el `detalle` crudo de un movimiento `ActualizacionPrecios` — el backend lo guarda con
 * `JsonSerializer.Serialize(resultado.Detalle)` SIN el naming policy camelCase de la API (a
 * diferencia de la respuesta de preview/commit), así que las claves llegan en PascalCase; se
 * aceptan ambos casos por robustez. Nunca lanza: cualquier JSON malformado o con una forma
 * inesperada devuelve `null` para que la pantalla caiga al texto crudo en vez de romper.
 */
export function parsearDetalleDeActualizacionPrecios(detalle: string | null): DetalleDeConsumo[] | null {
  if (!detalle) return null
  try {
    const crudo: unknown = JSON.parse(detalle)
    if (!Array.isArray(crudo)) return null
    return crudo.map(normalizarDetalleDeConsumo)
  } catch {
    return null
  }
}
