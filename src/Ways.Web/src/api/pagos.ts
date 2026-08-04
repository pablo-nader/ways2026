/**
 * Matemática pura de pagos del POS (stage-5-pos-ventas, Slice 7, design decisión 12): espeja
 * `ValidadorDePagos` (Ways.Domain.Ventas) para dar feedback instantáneo en pantalla — nunca es
 * autoritativo, el servidor vuelve a validar todo en el checkout real. El orden de rechazo acá
 * es el MISMO que el del dominio (spec: comprobantes-venta / Payment Validation Rejection Order)
 * para que el mensaje que ve el cajero en pantalla sea consistente con el que devolvería el
 * servidor si de todos modos se envía una mezcla inválida.
 */
import type { ComportamientoMedioPago, MedioPagoListado, PagoDeVenta } from './tipos'

/** Un pago ya resuelto contra su medio de pago — espejo de `PagoAValidar` (Ways.Domain.Ventas). */
export type PagoParaCalculo = {
  idMedioPago: number
  comportamiento: ComportamientoMedioPago
  admiteVuelto: boolean
  requiereReferencia: boolean
  importe: number
  referencia: string | null
}

/** Fila del panel de pagos: estado controlado (texto), igual criterio que el resto de los
 * formularios de la pantalla. `vueltoManual` es la sobreescritura del cajero sobre el vuelto
 * sugerido (`''` ⇒ todavía no la tocó, se usa el sugerido) — solo aplica en medios con
 * `AdmiteVuelto`, un medio sin vuelto nunca tiene uno propio (`vueltoDeFila`). */
export type FilaPago = {
  id: number
  idMedioPago: number | ''
  importe: string
  referencia: string
  vueltoManual: string
}

export function filaPagoVacia(id: number): FilaPago {
  return { id, idMedioPago: '', importe: '', referencia: '', vueltoManual: '' }
}

/** `CuentaCorriente` nunca es una opción para el Consumidor Final (spec: comprobantes-venta /
 * Cuenta Corriente Payment Gating, "Consumidor Final cannot pay by cuenta corriente") — la UI
 * ya lo esconde/deshabilita, el servidor lo vuelve a rechazar igual. */
export function medioDisponibleParaCliente(medio: MedioPagoListado, esConsumidorFinal: boolean): boolean {
  return medio.comportamiento !== 'CuentaCorriente' || !esConsumidorFinal
}

function redondear(valor: number): number {
  return Math.round((valor + Number.EPSILON) * 100) / 100
}

export function sumarImportes(pagos: { importe: number }[]): number {
  return redondear(pagos.reduce((acumulado, p) => acumulado + p.importe, 0))
}

export function sumarVueltos(pagos: { vuelto: number }[]): number {
  return redondear(pagos.reduce((acumulado, p) => acumulado + p.vuelto, 0))
}

/** Lo que todavía falta cubrir del total — `0` si ya está cubierto o superado. */
export function calcularFaltante(total: number, pagos: { importe: number }[]): number {
  return Math.max(0, redondear(total - sumarImportes(pagos)))
}

/** Lo que sobra del pago sobre el total — el vuelto máximo coherente (design: Checkout
 * Contract, regla nueva "Σ vuelto > max(0, Σ importe − total)"). */
export function calcularExcedente(total: number, pagos: { importe: number }[]): number {
  return Math.max(0, redondear(sumarImportes(pagos) - total))
}

/** `Σ importe` de los pagos cuyo medio es `CuentaCorriente` (design: Checkout Contract). */
export function consumoCuentaCorriente(pagos: { comportamiento: ComportamientoMedioPago; importe: number }[]): number {
  return redondear(
    pagos.filter((p) => p.comportamiento === 'CuentaCorriente').reduce((acumulado, p) => acumulado + p.importe, 0),
  )
}

/**
 * Convierte las filas del panel en pagos de cálculo, descartando las que el cajero todavía no
 * terminó de completar (sin medio elegido o sin un importe positivo) — una fila a medio tipear
 * no debe ensuciar el cálculo de faltante/vuelto ni la validación local.
 */
export function filasAPagosParaCalculo(filas: FilaPago[], medioPorId: Record<number, MedioPagoListado>): PagoParaCalculo[] {
  const pagos: PagoParaCalculo[] = []
  for (const fila of filas) {
    if (fila.idMedioPago === '') continue
    const medio = medioPorId[fila.idMedioPago]
    if (!medio) continue
    const importe = Number(fila.importe)
    if (fila.importe.trim() === '' || !Number.isFinite(importe) || importe <= 0) continue
    pagos.push({
      idMedioPago: medio.id,
      comportamiento: medio.comportamiento,
      admiteVuelto: medio.admiteVuelto,
      requiereReferencia: medio.requiereReferencia,
      importe,
      referencia: fila.referencia.trim() === '' ? null : fila.referencia.trim(),
    })
  }
  return pagos
}

/**
 * Asigna el excedente completo como vuelto al PRIMER pago cuyo medio admite vuelto — nunca se
 * reparte entre varios (evita ambigüedad de a qué medio "le sobra" el pago); el resto queda en
 * `0`. Si ningún medio de la mezcla admite vuelto, el excedente completo queda sin asignar (el
 * cajero tiene que ajustar el importe, la regla 2/8 del validador lo va a rechazar si corresponde).
 */
export function calcularPagosConVuelto(pagos: PagoParaCalculo[], total: number): (PagoParaCalculo & { vuelto: number })[] {
  const excedente = calcularExcedente(total, pagos)
  const indiceDestino = pagos.findIndex((p) => p.admiteVuelto)
  return pagos.map((p, indice) => ({ ...p, vuelto: indice === indiceDestino ? excedente : 0 }))
}

/** Vuelto final de una fila: lo que tipeó el cajero si tocó el campo (`vueltoManual`), si no el
 * sugerido — un medio sin `AdmiteVuelto` nunca tiene vuelto propio, sin importar lo que diga
 * `vueltoManual` (el input queda deshabilitado en pantalla para ese caso, esto es la defensa
 * equivalente del lado del cálculo). */
export function vueltoDeFila(fila: FilaPago, admiteVuelto: boolean, sugerido: number): number {
  if (!admiteVuelto) return 0
  if (fila.vueltoManual.trim() === '') return sugerido
  const manual = Number(fila.vueltoManual)
  return Number.isFinite(manual) ? manual : sugerido
}

/**
 * Filas del panel → pagos de cálculo con vuelto final ya resuelto (design decisión 12): combina
 * `filasAPagosParaCalculo` + `calcularPagosConVuelto` (la sugerencia) y aplica la sobreescritura
 * manual de cada fila (`vueltoDeFila`) — es la función que arma el número que después ve
 * `validarPagosLocal` y `aPagosDeVenta`.
 */
export function filasAPagosConVuelto(
  filas: FilaPago[],
  medioPorId: Record<number, MedioPagoListado>,
  total: number,
): (PagoParaCalculo & { vuelto: number })[] {
  const pagos = filasAPagosParaCalculo(filas, medioPorId)
  const sugeridos = calcularPagosConVuelto(pagos, total)
  const filaPorMedio = new Map(filas.filter((f) => f.idMedioPago !== '').map((f) => [f.idMedioPago, f]))

  return sugeridos.map((p) => {
    const fila = filaPorMedio.get(p.idMedioPago)
    return { ...p, vuelto: fila ? vueltoDeFila(fila, p.admiteVuelto, p.vuelto) : p.vuelto }
  })
}

export type RechazoDePago = { codigo: string; mensaje: string }

/**
 * Espejo pixel-a-pixel del orden de `ValidadorDePagos.Validar` (Ways.Domain.Ventas) — corta en
 * el primer rechazo, nunca acumula errores, mismos códigos de dominio (spec: comprobantes-venta
 * / Payment Validation Rejection Order; consumo-cuenta-corriente / Credit-Limit Evaluation).
 * Nunca autoritativo: solo guía al cajero antes de intentar el checkout real.
 */
export function validarPagosLocal(params: {
  total: number
  pagos: (PagoParaCalculo & { vuelto: number })[]
  toleranciaPago: number
  vueltoMaximo: number
  esConsumidorFinal: boolean
  saldoCliente: number
  limiteCredito: number
  creditoIlimitado: boolean
}): RechazoDePago | null {
  const { total, pagos, toleranciaPago, vueltoMaximo, esConsumidorFinal, saldoCliente, limiteCredito, creditoIlimitado } = params

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

  const sumaImportes = sumarImportes(pagos)
  const sumaVueltos = sumarVueltos(pagos)

  if (sumaImportes === 0 && total > 0) {
    return { codigo: 'pago_no_ingresado', mensaje: 'Tenés que ingresar al menos un pago.' }
  }

  if (sumaImportes + toleranciaPago < total) {
    return {
      codigo: 'tolerancia_de_pago_superada',
      mensaje: 'El pago ingresado no cubre el total, ni siquiera con la tolerancia.',
    }
  }

  if (sumaVueltos > vueltoMaximo) {
    return { codigo: 'vuelto_excedido', mensaje: 'El vuelto supera el máximo permitido.' }
  }

  for (const pago of pagos) {
    if (pago.vuelto > 0 && !pago.admiteVuelto) {
      return { codigo: 'medio_no_admite_vuelto', mensaje: 'El medio de pago elegido no admite vuelto.' }
    }
  }

  const consumoCc = consumoCuentaCorriente(pagos)

  if (consumoCc > 0 && esConsumidorFinal) {
    return { codigo: 'cuenta_corriente_no_permitida', mensaje: 'El Consumidor Final no puede pagar con cuenta corriente.' }
  }

  if (consumoCc > 0 && !creditoIlimitado && saldoCliente + consumoCc > limiteCredito) {
    return { codigo: 'limite_credito_excedido', mensaje: 'El pago supera el límite de crédito del cliente.' }
  }

  for (const pago of pagos) {
    if (pago.requiereReferencia && (pago.referencia ?? '').trim() === '') {
      return { codigo: 'referencia_de_pago_requerida', mensaje: 'Este medio de pago requiere una referencia.' }
    }
  }

  const vueltoMaximoCoherente = Math.max(0, redondear(sumaImportes - total))
  if (sumaVueltos > vueltoMaximoCoherente) {
    return { codigo: 'vuelto_invalido', mensaje: 'El vuelto no coincide con lo que sobra del pago.' }
  }

  return null
}

/** Pagos de cálculo (ya con vuelto derivado) → `PagoDeVenta[]` del request de checkout. */
export function aPagosDeVenta(pagos: (PagoParaCalculo & { vuelto: number })[]): PagoDeVenta[] {
  return pagos.map((p) => ({
    idMedioPago: p.idMedioPago,
    importe: p.importe,
    referencia: p.referencia,
    vuelto: p.vuelto,
  }))
}
