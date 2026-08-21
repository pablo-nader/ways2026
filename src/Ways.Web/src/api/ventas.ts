/**
 * Mappers puros del POS (stage-5-pos-ventas, Slice 6/7): traducen entre la forma HTTP
 * (`ArticuloEscaneado`, `ResultadoDeResolucion`) y la forma del carrito (`LineaCarrito`), sin
 * que `carrito.ts` ni `Pos.tsx` necesiten conocer el shape crudo de cada respuesta. `clienteDeVentas`
 * wirea `POST /api/ventas` (checkout real, Slice 4, mergeado a main).
 */
import { api } from './cliente'
import type { LineaCarrito } from './carrito'
import type {
  ArticuloEscaneado,
  ComprobanteEmitido,
  EstadoDeVencimiento,
  LineaDeResolucion,
  LineaDeVenta,
  LoteListado,
  PagoDeVenta,
  ResultadoDeResolucion,
  SolicitudDeVenta,
} from './tipos'

export const clienteDeVentas = {
  /** `POST /api/ventas` (design: API Surface): checkout — 201 + body = comprobante emitido, sin
   * ningún campo de dinero re-derivable en el cliente (el servidor vuelve a resolver todo). */
  emitir: (solicitud: SolicitudDeVenta) => api.post<ComprobanteEmitido>('/ventas', solicitud),
}

/** Respuesta de `GET /api/articulos/escaneo` → acción `escanear` de `carrito.ts` (spec:
 * codigos-barra / Scan Resolution Rule) — separa la cantidad parseada por el servidor
 * (`N*codigo`) de los datos de identidad de la línea, tal como espera `AccionCarrito`. */
export function aLineaDeCarritoDesdeEscaneo(
  articulo: ArticuloEscaneado,
): { linea: Omit<LineaCarrito, 'cantidad'>; cantidad: number } {
  return {
    linea: {
      idArticulo: articulo.idArticulo,
      codigoInterno: articulo.codigoInterno,
      nombre: articulo.nombre,
      codigoBarra: articulo.codigoBarra,
    },
    cantidad: articulo.cantidad,
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
  return lineas.map((l) => ({ idArticulo: l.idArticulo, idEmpresa, idListaPrecio, cantidad: l.cantidad }))
}

/** Indexa el resultado del lote por `idArticulo` para lookup O(1) por línea al renderizar la
 * tabla del carrito — el servidor no garantiza el mismo orden que el request. */
export function indexarResolucionPorArticulo(resultados: ResultadoDeResolucion[]): Record<number, ResultadoDeResolucion> {
  const indice: Record<number, ResultadoDeResolucion> = {}
  for (const r of resultados) indice[r.idArticulo] = r
  return indice
}

/** Previsualización de una línea: `precioFinal` ya viene neto de descuento por unidad
 * (`ServicioDeOfertas.ResolverAsync`), así que el total de línea es `cantidad × precioFinal` —
 * nunca autoritativo (design decisión 3: el servidor vuelve a resolver en el checkout). */
export function previaDeLinea(
  linea: LineaCarrito,
  resultado: ResultadoDeResolucion | undefined,
): { precioUnitario: number | null; descuentoUnitario: number; total: number | null } {
  if (!resultado || resultado.precioFinal === null) {
    return { precioUnitario: null, descuentoUnitario: 0, total: null }
  }
  return {
    precioUnitario: resultado.precioFinal,
    descuentoUnitario: resultado.descuentoUnitario,
    total: linea.cantidad * resultado.precioFinal,
  }
}

/** Subtotal previsualizado del carrito completo — `null` mientras no haya ningún precio
 * resuelto todavía (primera carga, o el lote de `/resolver` falló); una línea sin precio propio
 * dentro de un carrito parcialmente resuelto contribuye 0 y no rompe la suma. */
export function calcularSubtotalPrevia(lineas: LineaCarrito[], precios: Record<number, ResultadoDeResolucion>): number | null {
  if (Object.keys(precios).length === 0) return null
  return lineas.reduce((acumulado, l) => {
    const previa = previaDeLinea(l, precios[l.idArticulo])
    return acumulado + (previa.total ?? 0)
  }, 0)
}

/** Selección explícita de lote por línea del carrito, indexada por `idArticulo`
 * (stage-12-lotes-vencimientos, Slice 14) — una línea AUSENTE acá viaja con `idLote: null`, el
 * camino feliz de cero tecleo (design decisión 19): mostrar el `sugerido` resaltado en el picker
 * no cuenta como elección, solo tocar el select la registra. */
export type LotesSeleccionados = Record<number, number>

/**
 * Carrito confirmado → `SolicitudDeVenta` (design: Checkout Contract), invocado por `Pos.tsx`
 * al cobrar. Sin precios en `LineaDeVenta` a propósito (design decisión 3: "no precioUnitario,
 * no descuento, no total en el request").
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
}): SolicitudDeVenta & { idCliente: number; lineas: LineaDeVenta[] } {
  const lineasDeVenta: LineaDeVenta[] = params.lineas.map((l) => ({
    idArticulo: l.idArticulo,
    cantidad: l.cantidad,
    codigoBarra: l.codigoBarra,
    idLote: params.lotesSeleccionados[l.idArticulo] ?? null,
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
