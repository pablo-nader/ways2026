/**
 * Mappers puros del POS (stage-5-pos-ventas, Slice 6/7): traducen entre la forma HTTP
 * (`ArticuloEscaneado`, `ResultadoDeResolucion`) y la forma del carrito (`LineaCarrito`), sin
 * que `carrito.ts` ni `Pos.tsx` necesiten conocer el shape crudo de cada respuesta. `clienteDeVentas`
 * wirea `POST /api/ventas` (checkout real, Slice 4, mergeado a main).
 */
import { api } from './cliente'
import { totalesDeAjusteManual } from './ajusteManual'
import { calcularTotalesDeLineaConCombo, comboVigenteDeLinea } from './combos'
import type { ArticuloParaCarrito, LineaCarrito } from './carrito'
import { redondearAPeso } from '../formato/importes'
import type {
  ArticuloEscaneado,
  BloqueDeNumeracionReservado,
  ComprobanteEmitido,
  EstadoDeVencimiento,
  LineaDeResolucion,
  LineaDeVenta,
  LoteListado,
  PagoDeVenta,
  ResultadoDeResolucion,
  SolicitudDeCambioDeMedios,
  SolicitudDeReservaDeNumeracion,
  SolicitudDeVenta,
  VentaDeTurnoListado,
} from './tipos'

/** Desde dónde se corrige: la caja (solo con el turno abierto) o la administración (cualquier turno). */
export type ModoDeCambioDeMedios = 'pos' | 'administracion'

export const clienteDeVentas = {
  /** `POST /api/ventas` (design: API Surface): checkout — 201 + body = comprobante emitido, sin
   * ningún campo de dinero re-derivable en el cliente (el servidor vuelve a resolver todo).
   * `senal` (opcional) corta la request — la usa el envío en segundo plano del outbox. */
  emitir: (solicitud: SolicitudDeVenta, senal?: AbortSignal) =>
    senal ? api.post<ComprobanteEmitido>('/ventas', solicitud, undefined, senal) : api.post<ComprobanteEmitido>('/ventas', solicitud),
  /** `GET /api/ventas/{id}` (stage-17-presupuestos-y-remitos, Slice 8, OD10): reimpresión/lectura
   * de un comprobante ya emitido — `Remito.tsx` lo usa para mostrar el link a la factura de un
   * remito `facturado` (un `TXR`, cuyo detalle sale de `items_remito` del lado del servidor). */
  obtener: (id: number) => api.get<ComprobanteEmitido>(`/ventas/${id}`),
  /** `GET /api/ventas/por-turno/{idTurno}` (stage-desktop-pos, "Ventas del turno"): listado
   * dedicado con cliente y medios de pago, incluye anuladas. */
  listarPorTurno: (idTurno: number) => api.get<VentaDeTurnoListado[]>(`/ventas/por-turno/${idTurno}`),
  /** `POST /api/ventas/{id}/anulacion` (stage-desktop-pos): sin cuerpo — el servidor no acepta
   * `motivo`, revierte stock y cuenta corriente en la misma transacción. Nunca hay "restaurar". */
  anular: (id: number) => api.post<ComprobanteEmitido>(`/ventas/${id}/anulacion`),
  /** `PUT /api/ventas/{id}/medios` (caja) o `PUT /api/ventas/administracion/{id}/medios`: reemplaza los cobros de la venta
   * por `solicitud.pagos`, con el mismo total cobrado. Responde el comprobante ya corregido. */
  cambiarMedios: (id: number, solicitud: SolicitudDeCambioDeMedios, modo: ModoDeCambioDeMedios = 'pos') =>
    api.put<ComprobanteEmitido>(modo === 'pos' ? `/ventas/${id}/medios` : `/ventas/administracion/${id}/medios`, solicitud),
  /** `POST /api/ventas/reservas-numeracion` (stage-pos-reserva-de-numeracion): un dispositivo pide
   * un bloque de números para repartir localmente mientras vende sin red. */
  reservarNumeracion: (solicitud: SolicitudDeReservaDeNumeracion, senal?: AbortSignal) =>
    senal
      ? api.post<BloqueDeNumeracionReservado>('/ventas/reservas-numeracion', solicitud, undefined, senal)
      : api.post<BloqueDeNumeracionReservado>('/ventas/reservas-numeracion', solicitud),
}

/** Respuesta de `GET /api/articulos/escaneo` → acción `escanear` de `carrito.ts` (spec:
 * codigos-barra / Scan Resolution Rule) — separa la cantidad parseada por el servidor
 * (`N*codigo`) de los datos del artículo, tal como espera `AccionCarrito`. Solo un
 * `acumulaEnVenta: false` explícito abre líneas separadas: un valor ausente (servidor anterior al
 * campo) conserva el comportamiento de siempre, acumular. Un artículo por importe con el monto tipeado abre su propia
 * línea (nunca acumula) con `importe` y cantidad 1; el `importe` de cualquier otro artículo no se lleva. */
export function aLineaDeCarritoDesdeEscaneo(articulo: ArticuloEscaneado): { linea: ArticuloParaCarrito; cantidad: number } {
  const importe = articulo.unidadVenta === 'Importe' && articulo.importe != null ? articulo.importe : undefined
  return {
    linea: {
      idArticulo: articulo.idArticulo,
      codigoInterno: articulo.codigoInterno,
      nombre: articulo.nombre,
      codigoBarra: articulo.codigoBarra,
      acumulaEnVenta: importe === undefined && articulo.acumulaEnVenta !== false,
      unidadVenta: articulo.unidadVenta,
      ...(importe !== undefined ? { importe } : {}),
    },
    cantidad: importe !== undefined ? 1 : articulo.cantidad,
  }
}

/** Carrito → lote de `POST /api/ofertas/resolver` (spec: operacion-de-pos / "Cart Pricing Has
 * Exactly One Path"): un `idEmpresa`/`idListaPrecio` fijo para todo el lote — la resolución de
 * precio no varía línea a línea por esos dos campos, solo por `idArticulo`/`cantidad`. */
export function aLineasDeResolucion(
  lineas: LineaCarrito[],
  idListaPrecio: number,
  idEmpresa: number | null,
): LineaDeResolucion[] {
  return lineas.map((l) => ({
    idArticulo: l.idArticulo,
    idEmpresa,
    idListaPrecio,
    cantidad: l.cantidad,
    ...(l.importe !== undefined ? { importe: l.importe } : {}),
  }))
}

/** Precio resuelto de cada línea del carrito, indexado por `idLinea`. */
export type PreciosPorLinea = Record<string, ResultadoDeResolucion>

/** Indexa el resultado del lote por `idLinea` de las MISMAS `lineas` con las que se armó el
 * request: `ServicioDeOfertas.ResolverAsync` devuelve un resultado por línea pedida, en el mismo
 * orden. La resolución depende de la cantidad de cada línea (tramos, ofertas por cantidad), así
 * que dos líneas del mismo artículo pueden resolver distinto y no se indexan por artículo. Una
 * posición cuyo `idArticulo` no coincide con el de la línea queda sin precio, igual que un
 * artículo sin precio en la lista. */
export function indexarResolucionPorLinea(lineas: readonly LineaCarrito[], resultados: readonly ResultadoDeResolucion[]): PreciosPorLinea {
  const indice: PreciosPorLinea = {}
  lineas.forEach((l, posicion) => {
    const resultado = resultados[posicion]
    if (resultado !== undefined && resultado.idArticulo === l.idArticulo) indice[l.idLinea] = resultado
  })
  return indice
}

/** Previsualización de una línea con la fórmula del servidor (`calcularTotalesDeLinea`): el bruto se
 * redondea al peso entero y el neto es la suma de dos términos al peso (las unidades fuera del combo con su
 * oferta, y las que consumió el combo con su ahorro); el descuento es `bruto − neto`. El ajuste manual de la
 * línea (si lo tiene) se aplica sobre ese neto posterior a ofertas y combo. `total` ya incluye el ajuste y
 * `ajusteManual` es su monto con signo (0 sin ajuste). `ahorroDeCombo` es el importe de línea que el combo
 * descuenta (0 sin combo). Nunca autoritativo (design decisión 3: el servidor vuelve a resolver en el
 * checkout). Una línea por importe se cotiza con el precio que el servidor le resolvió (su importe con el porcentaje de la lista
 * derivada del cliente, sin oferta ni combo): hasta que llega ese precio no tiene total, como cualquier otra línea. */
export function previaDeLinea(
  linea: LineaCarrito,
  resultado: ResultadoDeResolucion | undefined,
): { precioUnitario: number | null; descuentoUnitario: number; total: number | null; ajusteManual: number; ahorroDeCombo: number } {
  if (!resultado || resultado.precioFinal === null) {
    return { precioUnitario: null, descuentoUnitario: 0, total: null, ajusteManual: 0, ahorroDeCombo: 0 }
  }
  const totales = calcularTotalesDeLineaConCombo({
    cantidad: linea.cantidad,
    // `precioFinal` ya es el precio de lista menos el descuento por unidad; sin lista explícita se
    // reconstruye con esa misma identidad.
    precioOriginal: resultado.precioOriginal ?? resultado.precioFinal + resultado.descuentoUnitario,
    descuentoUnitario: resultado.descuentoUnitario,
    descuentoDeLinea: resultado.descuentoDeLinea,
    porcentaje: linea.ajusteManualPorcentaje,
    combo: resultado.combo,
  })
  return {
    precioUnitario: resultado.precioFinal,
    descuentoUnitario: resultado.descuentoUnitario,
    total: totales.total,
    ajusteManual: totales.ajuste,
    ahorroDeCombo: comboVigenteDeLinea(linea.cantidad, resultado.combo)?.ahorro ?? 0,
  }
}

/** Total previsualizado del carrito completo, ya con los ajustes manuales — `null` mientras no haya
 * ningún precio resuelto todavía (primera carga, o el lote de `/resolver` falló); una línea sin
 * precio propio dentro de un carrito parcialmente resuelto contribuye 0 y no rompe la suma. */
export function calcularSubtotalPrevia(lineas: LineaCarrito[], precios: PreciosPorLinea): number | null {
  if (Object.keys(precios).length === 0) return null
  return redondearAPeso(
    lineas.reduce((acumulado, l) => {
      const previa = previaDeLinea(l, precios[l.idLinea])
      return acumulado + (previa.total ?? 0)
    }, 0),
  )
}

/** Descuento y recargo manual del carrito previsualizado, separados por el signo del porcentaje de
 * cada línea (`totalesDeAjusteManual`). Una línea sin precio resuelto no aporta ajuste. */
export function calcularAjustesManualesPrevia(
  lineas: LineaCarrito[],
  precios: PreciosPorLinea,
): { descuentoManualTotal: number; recargoManualTotal: number } {
  return totalesDeAjusteManual(
    lineas.map((l) => ({ porcentaje: l.ajusteManualPorcentaje, ajuste: previaDeLinea(l, precios[l.idLinea]).ajusteManual })),
  )
}

/** Selección explícita de lote por línea del carrito, indexada por `idLinea`
 * (stage-12-lotes-vencimientos, Slice 14) — una línea AUSENTE acá viaja con `idLote: null`, el
 * camino feliz de cero tecleo (design decisión 19): mostrar el `sugerido` resaltado en el picker
 * no cuenta como elección, solo tocar el select la registra. */
export type LotesSeleccionados = Record<string, number>

/**
 * Carrito confirmado → `SolicitudDeVenta` (design: Checkout Contract), invocado por `Pos.tsx`
 * al cobrar. Sin precios en `LineaDeVenta` a propósito (design decisión 3: "no precioUnitario,
 * no descuento, no total en el request"): el ajuste manual viaja solo como porcentaje, y solo en la
 * línea que lo tiene. La línea por importe es la excepción: lleva el monto que tipeó el cajero (`importe`, nunca un precio
 * de lista), y solo ella.
 */
export function aSolicitudDeVenta(params: {
  idPuntoVenta: number
  idCliente: number
  codigoTipoComprobante: 'TX' | 'NCX'
  idComprobanteAsociado: number | null
  lineas: LineaCarrito[]
  lotesSeleccionados: LotesSeleccionados
  pagos: PagoDeVenta[]
  direccionEntrega: string | null
  observaciones: string | null
  /** Solo `true` viaja: apagado se omite, como en cualquier venta que no lo pidió. */
  forzarFactura?: boolean
  /** Solo `true` viaja: el servidor agrega la línea Envío con su propio importe. */
  conEnvio?: boolean
}): SolicitudDeVenta & { idCliente: number; lineas: LineaDeVenta[] } {
  const lineasDeVenta: LineaDeVenta[] = params.lineas.map((l) => ({
    idArticulo: l.idArticulo,
    cantidad: l.cantidad,
    codigoBarra: l.codigoBarra,
    idLote: params.lotesSeleccionados[l.idLinea] ?? null,
    ...(l.ajusteManualPorcentaje != null ? { ajusteManualPorcentaje: l.ajusteManualPorcentaje } : {}),
    ...(l.importe !== undefined ? { importe: l.importe } : {}),
  }))

  return {
    idPuntoVenta: params.idPuntoVenta,
    idCliente: params.idCliente,
    codigoTipoComprobante: params.codigoTipoComprobante,
    idComprobanteAsociado: params.idComprobanteAsociado,
    lineas: lineasDeVenta,
    pagos: params.pagos,
    direccionEntrega: params.direccionEntrega,
    observaciones: params.observaciones,
    ...(params.forzarFactura ? { forzarFactura: true } : {}),
    ...(params.conEnvio ? { conEnvio: true } : {}),
  }
}

// ---- Picker de lote del carrito (stage-12-lotes-vencimientos, Slice 14) -----------------------

function etiquetaDeEstadoDeVencimiento(estado: EstadoDeVencimiento): string {
  switch (estado) {
    case 'Vencido':
      return 'vencido'
    case 'PorVencer':
      return 'por vencer'
    case 'Vigente':
      return 'vigente'
    case 'SinFecha':
      return 'sin fecha'
  }
}

/** `LoteListado` → opción del `<select>` del picker — pura y testeable sin DOM (design decisión
 * 19: `sugerido` es server-authored, acá solo se traduce a texto, nunca se recalcula FEFO). */
export function opcionDeLote(l: LoteListado): { valor: string; etiqueta: string } {
  const marca = l.sugerido ? ' — sugerido' : ''
  const codigo = l.esSinIdentificar ? 'Sin identificar' : l.codigo
  return {
    valor: String(l.idLote),
    etiqueta: `${codigo} — ${etiquetaDeEstadoDeVencimiento(l.estado)} — saldo ${l.cantidad}${marca}`,
  }
}
