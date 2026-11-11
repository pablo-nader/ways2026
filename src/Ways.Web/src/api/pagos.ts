/**
 * Matemática pura de pagos del POS (stage-5-pos-ventas, Slice 7, design decisión 12): espeja
 * `ValidadorDePagos` (Ways.Domain.Ventas) para dar feedback instantáneo en pantalla — nunca es
 * autoritativo, el servidor vuelve a validar todo en el checkout real. El orden de rechazo acá
 * es el MISMO que el del dominio (spec: comprobantes-venta / Payment Validation Rejection Order)
 * para que el mensaje que ve el cajero en pantalla sea consistente con el que devolvería el
 * servidor si de todos modos se envía una mezcla inválida.
 */
import { redondearImporte } from '../formato/importes'
import type { ComportamientoMedioPago, MedioPagoListado, PagoDeVenta } from './tipos'

/** Un pago ya resuelto contra su medio de pago — espejo de `PagoAValidar` (Ways.Domain.Ventas).
 * `idFila` identifica la fila de origen del panel (`FilaPago.id`): dos filas pueden compartir
 * el mismo `idMedioPago` (split de pago con el mismo medio), así que solo `idFila` es una clave
 * unívoca para reasociar cada pago con su fila. */
export type PagoParaCalculo = {
  idFila: number
  idMedioPago: number
  comportamiento: ComportamientoMedioPago
  admiteVuelto: boolean
  requiereReferencia: boolean
  importe: number
  referencia: string | null
  /** Nombre del medio, solo para los mensajes de rechazo. */
  nombre?: string
  /** Quién cobra por el medio (`mercadopago_qr`): un medio con proveedor no pide referencia, la fija el servidor desde el cobro. */
  proveedor?: string | null
}

/** Fila del panel de pagos: estado controlado, igual criterio que el resto de los formularios de
 * la pantalla. `importe`/`vueltoManual` son `number | null` (`null` = "vacío") — el mismo shape
 * que emite `CampoImporte`, así ningún call-site tiene que parsear texto a mano. `vueltoManual` es
 * la sobreescritura del cajero sobre el vuelto sugerido (`null` ⇒ todavía no la tocó, se usa el
 * sugerido) — solo aplica en medios con `AdmiteVuelto`, un medio sin vuelto nunca tiene uno propio
 * (`vueltoDeFila`). */
export type FilaPago = {
  id: number
  idMedioPago: number | ''
  importe: number | null
  referencia: string
  vueltoManual: number | null
  /** `true` cuando el cajero tocó el importe de la fila (aunque lo haya dejado vacío): deja de seguir a la propuesta de
   * `aplicarImportesSugeridos`. Ausente o `false` + `importe === null` = fila que toma el importe propuesto. */
  importeEditado?: boolean
}

export function filaPagoVacia(id: number): FilaPago {
  return { id, idMedioPago: '', importe: null, referencia: '', vueltoManual: null }
}

/** Id del medio de pago con `comportamiento === 'Efectivo'` — mismo criterio que
 * `algunPagoEnEfectivo` (impresion/plantillas.ts) usa para el pulso del cajón. `''` si `medios`
 * todavía no cargó o el tenant no tiene ningún medio en efectivo configurado. */
export function idMedioEfectivo(medios: MedioPagoListado[] | null): number | '' {
  return medios?.find((m) => m.comportamiento === 'Efectivo')?.id ?? ''
}

/** Fila de pago nueva con Efectivo preseleccionado cuando existe en `medios` (spec: el medio de
 * pago por defecto de una fila nueva es Efectivo) — sin un medio Efectivo configurado se comporta
 * exactamente como `filaPagoVacia` (ninguna preselección, el cajero elige a mano como siempre). */
export function filaPagoInicial(id: number, medios: MedioPagoListado[] | null): FilaPago {
  return { ...filaPagoVacia(id), idMedioPago: idMedioEfectivo(medios) }
}

/** `CuentaCorriente` nunca es una opción para el Consumidor Final (spec: comprobantes-venta /
 * Cuenta Corriente Payment Gating, "Consumidor Final cannot pay by cuenta corriente") — la UI
 * ya lo esconde/deshabilita, el servidor lo vuelve a rechazar igual. */
export function medioDisponibleParaCliente(medio: MedioPagoListado, esConsumidorFinal: boolean): boolean {
  return medio.comportamiento !== 'CuentaCorriente' || !esConsumidorFinal
}

// Sumas de importes TIPEADOS por el cajero (pagos, vueltos, saldos): conservan hasta 2 decimales,
// no son importes de venta calculados. Se redondean con el helper central para limpiar el ruido
// de punto flotante; el servidor valida contra las sumas exactas (ver el doc-comment del módulo).
const redondear = (valor: number): number => redondearImporte(valor)

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

/** Regla 6 de `ValidadorDePagos`: `saldo + consumo > límite`, salvo crédito ilimitado. Única
 * fuente de la comparación en el front — la usan `validarPagosLocal`, la consulta al servidor
 * antes de encolar una venta local y el aviso con los datos de la instantánea. */
export function superaLimiteDeCredito(
  cliente: { saldo: number; limiteCredito: number; creditoIlimitado: boolean },
  consumoCc: number,
): boolean {
  return consumoCc > 0 && !cliente.creditoIlimitado && redondear(cliente.saldo + consumoCc) > cliente.limiteCredito
}

/**
 * Filas con el importe propuesto en las que el cajero no tocó: cada una toma lo que falta del total después de las anteriores
 * (`max(0, total − Σ importes anteriores)`), así la primera propone el total completo. Se aplica en cada render sobre el
 * estado crudo, de modo que la propuesta sigue al total (el carrito crece) y es el importe que de verdad se valida y se cobra.
 * Una fila con importe cargado, aunque no tenga la marca (borradores anteriores a `importeEditado`), no se pisa. Con `total`
 * en `null` (total no confiable) no propone nada. Devuelve la misma referencia si no hay nada para proponer.
 */
export function aplicarImportesSugeridos(filas: FilaPago[], total: number | null): FilaPago[] {
  if (total === null) return filas
  let cambio = false
  let acumulado = 0
  const resultado = filas.map((fila) => {
    let importe = fila.importe
    if (importe === null && fila.importeEditado !== true) {
      const sugerido = Math.max(0, redondear(total - acumulado))
      if (sugerido > 0) {
        importe = sugerido
        cambio = true
      }
    }
    acumulado = redondear(acumulado + (importe ?? 0))
    return importe === fila.importe ? fila : { ...fila, importe }
  })
  return cambio ? resultado : filas
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
    if (fila.importe === null || fila.importe <= 0) continue
    pagos.push({
      idFila: fila.id,
      idMedioPago: medio.id,
      nombre: medio.nombre,
      comportamiento: medio.comportamiento,
      admiteVuelto: medio.admiteVuelto,
      requiereReferencia: medio.requiereReferencia && !medio.proveedor,
      importe: fila.importe,
      referencia: medio.proveedor || fila.referencia.trim() === '' ? null : fila.referencia.trim(),
      ...(medio.proveedor ? { proveedor: medio.proveedor } : {}),
    })
  }
  return pagos
}

/**
 * stage-pos-borrador-persistente: filas con el medio reseteado a "sin elegir" cuando referencian
 * un id que no existe en `medioPorId` — el caso real es una fila restaurada de un borrador
 * persistido cuyo medio se dio de baja mientras la app estaba cerrada. `filasAPagosParaCalculo`
 * (arriba) YA excluye estas filas de cualquier cálculo (`if (!medio) continue`), así que ninguna
 * venta se cobra jamás contra un medio inválido con o sin esta función — esto solo repara el
 * ESTADO visible en el `<select>` del panel de pagos, para que el cajero no vea una fila
 * "elegida" que en los hechos no cuenta para nada (y cuyo importe, sin este reset, parecería
 * ignorado sin explicación). Devuelve la MISMA referencia de `filas` sin cambios (para no
 * disparar un re-render de más) cuando ninguna fila necesita resetearse.
 */
export function filasConMedioInvalidoReseteado(filas: FilaPago[], medioPorId: Record<number, MedioPagoListado>): FilaPago[] {
  let cambio = false
  const siguiente = filas.map((fila) => {
    if (fila.idMedioPago === '' || medioPorId[fila.idMedioPago]) return fila
    cambio = true
    return { ...fila, idMedioPago: '' as const }
  })
  return cambio ? siguiente : filas
}

/**
 * Asigna el excedente completo como vuelto al PRIMER pago cuyo medio admite vuelto — nunca se
 * reparte entre varios (evita ambigüedad de a qué medio "le sobra" el pago); el resto queda en
 * `0`. Si ningún medio de la mezcla admite vuelto, el excedente completo queda sin asignar (el
 * cajero tiene que ajustar el importe: la regla 9 del validador rechaza lo cobrado de más).
 */
export function calcularPagosConVuelto(pagos: PagoParaCalculo[], total: number): (PagoParaCalculo & { vuelto: number })[] {
  const excedente = calcularExcedente(total, pagos)
  const indiceDestino = pagos.findIndex((p) => p.admiteVuelto)
  return pagos.map((p, indice) => ({ ...p, vuelto: indice === indiceDestino ? excedente : 0 }))
}

/** Vuelto final de una fila: lo que tipeó el cajero si tocó el campo (`vueltoManual`), si no el
 * sugerido — un medio sin `AdmiteVuelto` nunca tiene vuelto propio, sin importar lo que diga
 * `vueltoManual` (el input queda deshabilitado en pantalla para ese caso, esto es la defensa
 * equivalente del lado del cálculo). `vueltoManual` ya viene validado/redondeado por
 * `CampoImporte` (nunca `NaN`) — `null` es la única forma de "no tocado". */
export function vueltoDeFila(fila: FilaPago, admiteVuelto: boolean, sugerido: number): number {
  if (!admiteVuelto) return 0
  return fila.vueltoManual === null ? sugerido : fila.vueltoManual
}

/**
 * Filas del panel → pagos de cálculo con vuelto final ya resuelto (design decisión 12): combina
 * `filasAPagosParaCalculo` + `calcularPagosConVuelto` (la sugerencia) y aplica la sobreescritura
 * manual de cada fila (`vueltoDeFila`) — es la función que arma el número que después ve
 * `validarPagosLocal` y `aPagosDeVenta`. La reasociación con la fila de origen se hace por
 * `idFila`, nunca por `idMedioPago` — dos filas con el mismo medio (split de pago) tienen que
 * conservar cada una su propio `vueltoManual`.
 */
export function filasAPagosConVuelto(
  filas: FilaPago[],
  medioPorId: Record<number, MedioPagoListado>,
  total: number,
): (PagoParaCalculo & { vuelto: number })[] {
  const pagos = filasAPagosParaCalculo(filas, medioPorId)
  const sugeridos = calcularPagosConVuelto(pagos, total)
  const filaPorId = new Map(filas.map((f) => [f.id, f]))

  return sugeridos.map((p) => {
    const fila = filaPorId.get(p.idFila)
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
  esConsumidorFinal: boolean
  saldoCliente: number
  limiteCredito: number
  creditoIlimitado: boolean
  /** `false` en una venta local: el límite lo juzga la consulta al servidor antes de encolar, y los
   * datos del cliente que tiene la pantalla pueden ser viejos. Default `true`. */
  exigirLimiteDeCredito?: boolean
  /** `false` saltea solo la regla 9, como el servidor con `excedenteComoPagoACuenta`: lo que sobra pasa a un pago a cuenta. */
  exigirCobroSinExcedente?: boolean
}): RechazoDePago | null {
  const {
    total,
    pagos,
    toleranciaPago,
    esConsumidorFinal,
    saldoCliente,
    limiteCredito,
    creditoIlimitado,
    exigirLimiteDeCredito = true,
    exigirCobroSinExcedente = true,
  } = params

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

  if (total < 0 && pagos.some((pago) => pago.importe > 0 || pago.vuelto > 0)) {
    return { codigo: 'pago_en_comprobante_negativo', mensaje: 'Una nota de crédito no lleva pagos: la devolución no cobra.' }
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

  for (const pago of pagos) {
    if (pago.vuelto > pago.importe) {
      return { codigo: 'vuelto_supera_el_pago', mensaje: 'El vuelto no puede superar el importe del pago que lo da.' }
    }
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

  if (exigirLimiteDeCredito && superaLimiteDeCredito({ saldo: saldoCliente, limiteCredito, creditoIlimitado }, consumoCc)) {
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

  if (!exigirCobroSinExcedente) return null

  // Una nota de crédito no cobra (regla 0c): el tope parte de cero, no de su total negativo.
  const topeDelCobro = redondear(Math.max(0, total) + toleranciaPago)
  for (const pago of pagos) {
    if (!pago.admiteVuelto && pago.importe > topeDelCobro) {
      return {
        codigo: 'importe_supera_el_total',
        mensaje: `El importe de ${pago.nombre ?? 'un pago sin vuelto'} supera el total de la venta.`,
      }
    }
  }

  if (redondear(sumaImportes - sumaVueltos) > topeDelCobro) {
    return {
      codigo: 'cobro_supera_el_total',
      mensaje: 'Lo cobrado supera el total de la venta: lo que sobra del efectivo tiene que volver como vuelto.',
    }
  }

  return null
}

/**
 * Excedente que se puede dejar como pago a cuenta (espejo de `RepartoDeExcedente`): con todo el vuelto en cero, lo cobrado por
 * encima del total. `null` cuando no hay excedente, cuando no alcanza con los pagos que el servidor puede ceder (cuenta
 * corriente no cede) o cuando algún pago es de un medio con proveedor: su cobro viaja con el número reservado para él, y el
 * servidor no admite el pago a cuenta en una venta con número pre-asignado. `sugerido` cuando el excedente no puede volver como
 * vuelto: ningún pago admite vuelto, o un medio sin vuelto supera el total más la tolerancia.
 */
export function excedenteParaPagoACuenta(params: {
  total: number
  pagos: PagoParaCalculo[]
  toleranciaPago: number
}): { importe: number; sugerido: boolean } | null {
  const { total, pagos, toleranciaPago } = params
  if (pagos.some((p) => p.proveedor)) return null
  const importe = redondear(sumarImportes(pagos) - total)
  if (importe <= 0) return null
  const cedible = sumarImportes(pagos.filter((p) => p.comportamiento !== 'CuentaCorriente'))
  if (cedible < importe) return null
  const tope = redondear(Math.max(0, total) + toleranciaPago)
  const sugerido = !pagos.some((p) => p.admiteVuelto) || pagos.some((p) => !p.admiteVuelto && p.importe > tope)
  return { importe, sugerido }
}

/** Los pagos tal como viajan cuando el excedente queda a cuenta: ninguno da vuelto, lo que sobra lo cede el servidor al recibo. */
export function sinVuelto<T extends { vuelto: number }>(pagos: T[]): T[] {
  return pagos.map((p) => (p.vuelto === 0 ? p : { ...p, vuelto: 0 }))
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
