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
  EstadoDeCuenta,
  MedioPagoListado,
  MovimientoDeCuentaCorriente,
  PagoDeCuenta,
  SolicitudDePagoACuenta,
} from './tipos'

function redondear(valor: number): number {
  return Math.round((valor + Number.EPSILON) * 100) / 100
}

/** Arma el query string de `GET …/cuenta-corriente` — `historico` gana sobre `desde`/`hasta`
 * (mismo criterio que `ObtenerEstadoDeCuentaAsync`); un `desde`/`hasta` vacío se omite (el
 * servidor aplica su propio default de último mes). Un `desde`/`hasta` de un `<input type="date">`
 * (`YYYY-MM-DD`) se expande a los bordes inclusivos del día. */
export function construirQueryEstadoDeCuenta(desde: string, hasta: string, historico: boolean): string {
  const parametros = new URLSearchParams()
  if (historico) {
    parametros.set('historico', 'true')
  } else {
    if (desde) parametros.set('desde', `${desde}T00:00:00`)
    if (hasta) parametros.set('hasta', `${hasta}T23:59:59.999`)
  }
  const cadena = parametros.toString()
  return cadena ? `?${cadena}` : ''
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
