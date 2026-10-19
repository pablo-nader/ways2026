/**
 * Cliente HTTP + reglas puras de compras (stage-8-compras-transferencias-inventario, Slice 5):
 * CRUD de borrador, confirmar/anular/aplicar precio sugerido, y un mirror **no autoritativo** de
 * `CalculadorDeCompra` (Ways.Domain.Compras) para feedback instantáneo en pantalla — el servidor
 * vuelve a derivar todo (`dto-contract-honesty`): ningún endpoint acepta `cantidad`, un total de
 * línea ni un total de header, solo los inputs (`unidades`/`bultos`/`unidadesPorBulto`/
 * `costoUnitario`/`descuento`/`idAlicuotaIva`).
 */
import { api } from './cliente'
import { brutoDeLinea, cantidadDeLinea, lineaCompletaParaEnvio, aLineaSolicitada, valoresParaApi, type LineaDeCompraFormulario } from './lineaDeCompra'
import { redondearImporte } from '../formato/importes'
import type {
  CompraDetalle,
  EstadoCompra,
  OrigenCompra,
  EstadoPago,
  PaginaDeCompras,
  PercepcionSolicitada,
  ResultadoAnulacion,
  ResultadoAplicarPrecio,
  ResultadoDePagoDeCompra,
  SaldoDeProveedor,
  SolicitudDeAplicarPrecios,
  SolicitudDeCompra,
  SolicitudDePagoDeCompra,
  TipoComprobanteListado,
  TipoDePercepcion,
} from './tipos'

function redondear(valor: number, decimales: number): number {
  const factor = 10 ** decimales
  return Math.round((valor + Number.EPSILON) * factor) / factor
}

// ---- Offset local para el filtro desde/hasta (mismo criterio que cuentaCorriente.ts: el
// servidor corre en UTC, `fecha_recepcion` es `timestamptz` y un `<input type="date">` sin
// offset se interpretaría como UTC, perdiendo actividad nocturna en ART). Duplicado a propósito
// (sibling helper, mismo criterio que los statements crudos de `ServicioDeCompras`): no hay un
// módulo compartido de utilidades de fecha en esta web todavía. -----------------------------
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

export type FiltrosDeCompras = {
  idProveedor: number | null
  estado: EstadoCompra | null
  origen: OrigenCompra | null
  desde: string
  hasta: string
  /** stage-tesoreria-por-empresa (PR5): filtro opcional por la empresa del punto de venta de la
   * compra — usado por el picker "Vincular a compra" de Gastos para no ofrecer compras de otra
   * empresa que la del gasto. */
  idEmpresa: number | null
  pagina: number
  tamanio: number
}

export function filtrosDeComprasVacios(): FiltrosDeCompras {
  return { idProveedor: null, estado: null, origen: null, desde: '', hasta: '', idEmpresa: null, pagina: 1, tamanio: 25 }
}

/** Arma el query string de `GET /api/compras` — `desde`/`hasta` filtran por `fecha_recepcion`
 * (`ServicioDeCompras.ListarAsync`), nunca por `fecha_comprobante`, así que llevan el mismo
 * offset horario explícito que el resto de la web. */
export function construirQueryDeCompras(filtros: FiltrosDeCompras): string {
  const parametros = new URLSearchParams()
  if (filtros.idProveedor !== null) parametros.set('idProveedor', String(filtros.idProveedor))
  if (filtros.estado !== null) parametros.set('estado', filtros.estado)
  if (filtros.origen !== null) parametros.set('origen', filtros.origen)
  if (filtros.desde) parametros.set('desde', fechaIsoConOffset(filtros.desde, '00:00:00'))
  if (filtros.hasta) parametros.set('hasta', fechaIsoConOffset(filtros.hasta, '23:59:59.999'))
  if (filtros.idEmpresa !== null) parametros.set('idEmpresa', String(filtros.idEmpresa))
  parametros.set('pagina', String(filtros.pagina))
  parametros.set('tamanio', String(filtros.tamanio))
  return `?${parametros.toString()}`
}

export const clienteDeCompras = {
  listar: (filtros: FiltrosDeCompras) => api.get<PaginaDeCompras>(`/compras${construirQueryDeCompras(filtros)}`),
  obtener: (id: number) => api.get<CompraDetalle>(`/compras/${id}`),
  crear: (solicitud: SolicitudDeCompra) => api.post<CompraDetalle>('/compras', solicitud),
  actualizar: (id: number, solicitud: SolicitudDeCompra) => api.put<CompraDetalle>(`/compras/${id}`, solicitud),
  /** `DELETE /api/compras/{id}` — borra de verdad un borrador (409 `compra_no_descartable` si ya no lo es). */
  descartar: (id: number) => api.delete<void>(`/compras/${id}`),
  confirmar: (id: number) => api.post<CompraDetalle>(`/compras/${id}/confirmar`),
  anular: (id: number) => api.post<ResultadoAnulacion>(`/compras/${id}/anular`),
  aplicarPrecios: (id: number, solicitud: SolicitudDeAplicarPrecios) =>
    api.post<ResultadoAplicarPrecio[]>(`/compras/${id}/precios`, solicitud),
  /** `POST /api/compras/{id}/pagos` — `GestionDeCatalogo` del lado del servidor: crea el gasto de
   * tesorería y el pago de cuenta corriente imputado a la compra en una transacción. */
  pagar: (id: number, solicitud: SolicitudDePagoDeCompra) =>
    api.post<ResultadoDePagoDeCompra>(`/compras/${id}/pagos`, solicitud),
  /** `GET /api/proveedores/{id}/saldo` — top-level, no dentro de `/api/proveedores` (design: API
   * Surface). Usado acá solo para la columna de estado de pago cuando el listado está filtrado
   * por un proveedor puntual; el panel completo lo construye `Proveedores.tsx` en la Slice 6. */
  obtenerSaldoDeProveedor: (idProveedor: number) => api.get<SaldoDeProveedor>(`/proveedores/${idProveedor}/saldo`),
}

/** Aviso del editor: un borrador que cargó el asistente se confirma recién después de revisarlo contra la factura. */
export function esBorradorCargadoPorElAsistente(compra: Pick<CompraDetalle, 'estado' | 'origen'> | null): boolean {
  return compra !== null && compra.estado === 'Borrador' && compra.origen === 'Asistente'
}

export function etiquetaDeEstadoCompra(estado: EstadoCompra): string {
  switch (estado) {
    case 'Borrador':
      return 'Borrador'
    case 'Confirmada':
      return 'Confirmada'
    case 'Anulada':
      return 'Anulada'
  }
}

/** Espejo de `EstadoPago` (`ServicioDeSaldoDeProveedor`) — compartido por `Compras.tsx` (columna
 * de estado de pago) y el panel de saldo de `Proveedores.tsx` (Slice 6). */
export function etiquetaDeEstadoPago(estado: EstadoPago): string {
  switch (estado) {
    case 'Pagada':
      return 'Pagada'
    case 'Parcial':
      return 'Parcial'
    case 'Impaga':
      return 'Impaga'
  }
}

export function claseDeBadgeDeEstadoPago(estado: EstadoPago): string {
  switch (estado) {
    case 'Pagada':
      return 'text-bg-success'
    case 'Parcial':
      return 'text-bg-warning'
    case 'Impaga':
      return 'text-bg-secondary'
  }
}

export * from './lineaDeCompra'

/** Una percepción del formulario. Mientras `automatica` es `true` su base (el neto gravado del
 * comprobante) y su importe (`base × alícuota / 100`) se derivan en cada render y el estado los deja
 * en `null`; el primer cambio manual la congela con los valores que el operador estaba viendo. El
 * importe que se envía es siempre el de la factura, nunca uno recalculado por el servidor. */
export type PercepcionFormulario = {
  tipo: TipoDePercepcion
  baseImponible: number | null
  alicuota: number | null
  importe: number | null
  automatica: boolean
}

export type EncabezadoDeCompraFormulario = {
  idProveedor: number | ''
  idTipoComprobante: number | ''
  idPuntoVenta: number | ''
  numeroExterno: string
  fechaComprobante: string
  observaciones: string
  /** stage-16-ordenes-de-compra, Slice 6: la OC que esta compra liga (pre-carga desde
   * `?idOrdenCompra=` en `CompraEditor`, design decisión — "Registrar recepción" de
   * `OrdenDeCompra.tsx`). `null` = compra sin OC, el 100% del tráfico previo a esta etapa. */
  idOrdenCompra: number | null
  /** Elección de quien carga el comprobante: solo cuenta en un tipo que no registra libro IVA
   * (remito, comprobante no fiscal). `null` = el valor del tipo. En una factura el editor la deja
   * en `null`: lo fija el tipo y mandar lo contrario es 400. */
  discriminaIva: boolean | null
  /** IVA impreso por el proveedor, por id de alícuota, cuando difiere del calculado por
   * redondeo. Solo se editan las alícuotas que el comprobante tiene; `null` = sin override. */
  ivaImpreso: Record<number, number | null>
  /** El costo unitario tipeado ya trae el IVA. Se pre-carga del proveedor y solo se envía si el
   * comprobante discrimina IVA. */
  preciosIncluyenIva: boolean
  percepciones: PercepcionFormulario[]
  /** Tipos que el operador quitó a mano: la sugerencia automática no los vuelve a agregar hasta que
   * cambie el proveedor. */
  percepcionesDescartadas: TipoDePercepcion[]
}

/** Si el comprobante discrimina IVA: lo fija el tipo en una factura y lo elige quien carga un
 * remito o comprobante no fiscal (espejo de `ReglaDeDiscriminacionDeIva`). */
export function discriminaIvaEfectivo(tipo: TipoComprobanteListado | null, eleccion: boolean | null): boolean {
  if (tipo === null) return false
  return tipo.registraLibroIva ? tipo.discriminaIva : (eleccion ?? tipo.discriminaIva)
}

/** Un comprobante persistido → overrides de IVA impreso: solo las alícuotas cuyo IVA guardado difiere
 * del que el servidor calcularía en la modalidad del comprobante (las demás se recalculan al guardar
 * de nuevo, sin override). Con precios finales el neto guardado es `final − iva`, así que el IVA
 * calculado sale de `neto + iva` (el final de la alícuota), no del neto solo. */
export function ivaImpresoDesdeDetalle(compra: CompraDetalle): Record<number, number | null> {
  const overrides: Record<number, number | null> = {}
  for (const a of compra.alicuotas) {
    if (a.neto === null || a.iva === null) continue
    const calculado = compra.preciosIncluyenIva
      ? redondear(((a.neto + a.iva) * a.porcentaje) / (100 + a.porcentaje), 2)
      : redondear((a.neto * a.porcentaje) / 100, 2)
    if (calculado !== a.iva) overrides[a.idAlicuotaIva] = a.iva
  }
  return overrides
}

/** `fechaComprobante` es `date` (`DateOnly`), no `timestamptz` — viaja tal cual el
 * `<input type="date">` la entrega (`YYYY-MM-DD`), sin ningún offset horario (a diferencia de
 * `desde`/`hasta` del listado, que sí filtran contra un `timestamptz`). */
export function aSolicitudDeCompra(
  encabezado: EncabezadoDeCompraFormulario,
  lineas: LineaDeCompraFormulario[],
  discriminaEfectivo = false,
  registraLibroIva = false,
): SolicitudDeCompra {
  const completas = lineas.filter(lineaCompletaParaEnvio)
  const idsPresentes = new Set(completas.map((l) => Number(l.idAlicuotaIva)))
  const ivaImpreso = discriminaEfectivo
    ? Object.entries(encabezado.ivaImpreso)
        .filter(([id, iva]) => iva !== null && idsPresentes.has(Number(id)))
        .map(([id, iva]) => ({ idAlicuotaIva: Number(id), iva: iva as number }))
    : []

  return {
    idProveedor: encabezado.idProveedor === '' ? 0 : encabezado.idProveedor,
    idTipoComprobante: encabezado.idTipoComprobante === '' ? 0 : encabezado.idTipoComprobante,
    idPuntoVenta: encabezado.idPuntoVenta === '' ? 0 : encabezado.idPuntoVenta,
    numeroExterno: encabezado.numeroExterno.trim() === '' ? null : encabezado.numeroExterno.trim(),
    fechaComprobante: encabezado.fechaComprobante === '' ? null : encabezado.fechaComprobante,
    observaciones: encabezado.observaciones.trim() === '' ? null : encabezado.observaciones.trim(),
    items: completas.map(aLineaSolicitada),
    idOrdenCompra: encabezado.idOrdenCompra,
    discriminaIva: encabezado.discriminaIva,
    ivaImpreso: ivaImpreso.length === 0 ? null : ivaImpreso,
    preciosIncluyenIva: discriminaEfectivo && encabezado.preciosIncluyenIva,
    percepciones: aPercepcionesSolicitadas(encabezado.percepciones, registraLibroIva, discriminaEfectivo),
  }
}

/** Percepciones con importe que corresponden al comprobante: solo un tipo que registra libro IVA las
 * admite y la de IVA además exige que discrimine IVA (espejo de `ReglaDePercepciones`). Las filas
 * ocultas por el tipo no se envían, pero tampoco suman al total que muestra el editor. */
export function percepcionesAplicables(
  percepciones: PercepcionFormulario[],
  registraLibroIva: boolean,
  discriminaIva: boolean,
): PercepcionFormulario[] {
  if (!registraLibroIva) return []
  return percepciones.filter((p) => p.tipo !== 'iva' || discriminaIva)
}

function aPercepcionesSolicitadas(
  percepciones: PercepcionFormulario[],
  registraLibroIva: boolean,
  discriminaIva: boolean,
): PercepcionSolicitada[] | null {
  // Una automática en cero (base o alícuota cero) no es una percepción de la factura: no se
  // persiste. Una fila que el operador tocó o agregó viaja aunque sea cero.
  const enviables = percepcionesAplicables(percepciones, registraLibroIva, discriminaIva)
    .filter((p) => p.importe !== null && !(p.automatica && p.importe === 0))
    .map((p) => ({
      tipo: p.tipo,
      baseImponible: p.baseImponible ?? 0,
      alicuota: p.alicuota ?? 0,
      importe: p.importe as number,
    }))
  return enviables.length === 0 ? null : enviables
}

// ---- Mirror no autoritativo de CalculadorDeCompra (design: "Compra Arithmetic") --------------

export type LineaDeCalculo = {
  idAlicuotaIva: number
  unidades: number
  bultos: number
  unidadesPorBulto: number
  costoUnitario: number
  descuento: number
  porcentajeIva: number
}

export type ItemCalculado = { cantidad: number; bruto: number; total: number; costoEfectivo: number | null }

/** Una alícuota del desglose: `ivaCalculado` es lo que sale del neto; `iva` lo que se guarda (el
 * impreso, si se informó). `fueraDeTolerancia` anticipa el 400 del servidor. */
export type AlicuotaDelDesglose = {
  idAlicuotaIva: number
  porcentaje: number
  neto: number
  ivaCalculado: number
  iva: number
  fueraDeTolerancia: boolean
}

export type TotalesDeCompra = {
  items: ItemCalculado[]
  subtotal: number
  descuentoTotal: number
  ivaTotal: number | null
  total: number
  alicuotas: AlicuotaDelDesglose[]
  /** Suma de los importes de percepción, que ya están dentro de `total`. */
  percepcionesTotal: number
}

/** Diferencia máxima, en pesos, entre el IVA calculado y el impreso (espejo de
 * `CalculadorDeCompra.ToleranciaDeIvaImpreso`). */
export const TOLERANCIA_DE_IVA_IMPRESO = 1

/** Fila de formulario → `LineaDeCalculo`, resolviendo `porcentajeIva` desde el catálogo de
 * alícuotas (el formulario solo guarda el id, nunca el porcentaje). */
export function lineaFormularioACalculo(
  l: LineaDeCompraFormulario,
  porcentajePorAlicuota: Record<number, number>,
): LineaDeCalculo {
  const v = valoresParaApi(l)
  return {
    idAlicuotaIva: l.idAlicuotaIva === '' ? 0 : l.idAlicuotaIva,
    unidades: v.unidades,
    bultos: v.bultos ?? 0,
    unidadesPorBulto: v.unidadesPorBulto ?? 0,
    costoUnitario: v.costoUnitario,
    descuento: v.descuento,
    porcentajeIva: l.idAlicuotaIva === '' ? 0 : (porcentajePorAlicuota[l.idAlicuotaIva] ?? 0),
  }
}

/** `true` si el costo unitario tipeado en una línea ya trae el IVA. Solo es neto de IVA cuando el comprobante discrimina
 * IVA y los precios no son finales (`preciosFinales`: discrimina y sus precios incluyen IVA). */
export function costoDeLineaIncluyeIva(discriminaIva: boolean, preciosFinales: boolean): boolean {
  return !(discriminaIva && !preciosFinales)
}

function calcularItem(l: LineaDeCalculo, discriminaIva: boolean, preciosIncluyenIva: boolean): ItemCalculado {
  const cantidad = cantidadDeLinea(l.unidades, l.bultos, l.unidadesPorBulto)
  const bruto = brutoDeLinea(l.unidades, l.bultos, l.unidadesPorBulto, l.costoUnitario)
  const total = redondearImporte(bruto - l.descuento, 2)
  const costoEfectivo =
    cantidad <= 0
      ? null
      : !costoDeLineaIncluyeIva(discriminaIva, preciosIncluyenIva)
        ? redondear((total * (1 + l.porcentajeIva / 100)) / cantidad, 2)
        : redondear(total / cantidad, 2)
  return { cantidad, bruto, total, costoEfectivo }
}

/** Espejo de `CalculadorDeCompra.Calcular` (design: "Compra Arithmetic") — puramente informativo,
 * el servidor recalcula todo desde cero al guardar (`dto-contract-honesty`). `ivaImpreso` es el
 * override por alícuota del IVA que imprimió el proveedor: `null` o ausente = el calculado.
 * Con `preciosIncluyenIva` (solo si discrimina) el costo tipeado ya trae el IVA: el total de cada
 * línea es el precio final, el IVA se extrae del final de cada alícuota y el neto es el resto.
 * `percepciones` (ya filtradas con `percepcionesAplicables`) suman a `total` con el importe de la
 * factura. */
export function calcularTotalesDeCompra(
  lineas: LineaDeCalculo[],
  discriminaIva: boolean,
  ivaImpreso: Record<number, number | null> = {},
  preciosIncluyenIva = false,
  percepciones: { importe: number | null }[] = [],
): TotalesDeCompra {
  const precioFinal = discriminaIva && preciosIncluyenIva
  const items = lineas.map((l) => calcularItem(l, discriminaIva, precioFinal))
  const subtotal = redondear(
    items.reduce((acumulado, i) => acumulado + i.bruto, 0),
    2,
  )
  const descuentoTotal = redondear(
    lineas.reduce((acumulado, l) => acumulado + l.descuento, 0),
    2,
  )

  const percepcionesTotal = redondear(
    percepciones.reduce((acumulado, p) => acumulado + (p.importe ?? 0), 0),
    2,
  )

  if (!discriminaIva) {
    return {
      items,
      subtotal,
      descuentoTotal,
      ivaTotal: null,
      total: redondear(subtotal - descuentoTotal + percepcionesTotal, 2),
      alicuotas: [],
      percepcionesTotal,
    }
  }

  const alicuotas = desglosarIva(lineas, items, ivaImpreso, precioFinal)
  const ivaTotal = redondear(
    alicuotas.reduce((acumulado, a) => acumulado + a.iva, 0),
    2,
  )
  // Con precios finales el IVA ya está dentro de subtotal − descuento: sumarlo lo contaría dos veces.
  const sinPercepciones = precioFinal ? subtotal - descuentoTotal : subtotal - descuentoTotal + ivaTotal
  return {
    items,
    subtotal,
    descuentoTotal,
    ivaTotal,
    total: redondear(sinPercepciones + percepcionesTotal, 2),
    alicuotas,
    percepcionesTotal,
  }
}

/** Neto por alícuota (suma de totales de línea) e IVA redondeado una sola vez sobre ese neto — la
 * convención de una factura, igual que el servidor —, ordenado por id de alícuota. Con precio final
 * la suma de líneas es el final de la alícuota: el IVA sale de ahí (`final × p / (100 + p)`) y el
 * neto es el resto, así que neto + IVA suma exactamente lo tipeado aun con un IVA impreso. */
function desglosarIva(
  lineas: LineaDeCalculo[],
  items: ItemCalculado[],
  ivaImpreso: Record<number, number | null>,
  precioFinal: boolean,
): AlicuotaDelDesglose[] {
  const porAlicuota = new Map<number, { porcentaje: number; neto: number }>()
  lineas.forEach((l, indice) => {
    const acumulado = porAlicuota.get(l.idAlicuotaIva)
    porAlicuota.set(l.idAlicuotaIva, {
      porcentaje: l.porcentajeIva,
      neto: redondear((acumulado?.neto ?? 0) + items[indice].total, 2),
    })
  })

  return [...porAlicuota.entries()]
    .sort(([a], [b]) => a - b)
    .map(([idAlicuotaIva, { porcentaje, neto: monto }]) => {
      const ivaCalculado = precioFinal
        ? redondear((monto * porcentaje) / (100 + porcentaje), 2)
        : redondear((monto * porcentaje) / 100, 2)
      const impreso = ivaImpreso[idAlicuotaIva] ?? null
      const iva = impreso ?? ivaCalculado
      const neto = precioFinal ? redondear(monto - iva, 2) : monto
      const fueraDeTolerancia =
        impreso !== null &&
        (impreso < 0 || neto < 0 || redondear(Math.abs(impreso - ivaCalculado), 2) > TOLERANCIA_DE_IVA_IMPRESO)
      return { idAlicuotaIva, porcentaje, neto, ivaCalculado, iva, fueraDeTolerancia }
    })
}

// ---- Pago de una compra confirmada -----------------------------------------------------------

/** Hoy como `YYYY-MM-DD` en la zona del navegador, la misma con la que el resto de la web decide la
 * fecha de negocio por defecto (`Gastos.tsx`). */
export function fechaDeHoyParaPago(ahora: Date = new Date()): string {
  const mes = String(ahora.getMonth() + 1).padStart(2, '0')
  const dia = String(ahora.getDate()).padStart(2, '0')
  return `${ahora.getFullYear()}-${mes}-${dia}`
}

/** Una compra se puede pagar solo si está confirmada y todavía le falta pagar algo. */
export function sePuedePagarLaCompra(compra: Pick<CompraDetalle, 'estado' | 'saldoPendiente'>): boolean {
  return compra.estado === 'Confirmada' && compra.saldoPendiente > 0
}

export type PagoDeCompraFormulario = {
  importe: number | null
  fecha: string
  idMedioPago: number | null
  concepto: string
}

/** Valida el formulario con lo mismo que el servidor rechaza antes de escribir (importe positivo y
 * hasta el saldo pendiente, fecha no futura, medio de pago elegido) y, si pasa, arma la
 * `SolicitudDePagoDeCompra`: feedback instantáneo, nunca autoritativo. Un concepto en blanco viaja
 * como `null` y el servidor arma `Pago <tipo> <número>`. */
export function prepararPagoDeCompra(
  formulario: PagoDeCompraFormulario,
  saldoPendiente: number,
  hoy: string,
): { solicitud: SolicitudDePagoDeCompra } | { error: string } {
  const { importe, idMedioPago } = formulario
  if (importe === null || !(importe > 0)) return { error: 'Ingresá un importe mayor a 0.' }
  if (importe > saldoPendiente) return { error: 'El importe no puede superar el saldo pendiente de la compra.' }
  if (!formulario.fecha) return { error: 'Elegí la fecha del pago.' }
  if (formulario.fecha > hoy) return { error: 'La fecha del pago no puede ser futura.' }
  if (idMedioPago === null) return { error: 'Elegí el medio de pago.' }

  const concepto = formulario.concepto.trim()
  return {
    solicitud: { fecha: formulario.fecha, importe, idMedioPago, concepto: concepto === '' ? null : concepto },
  }
}
