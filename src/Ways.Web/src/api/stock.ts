/**
 * Cliente HTTP + reglas puras de stock (stage-8-compras-transferencias-inventario, Slice 6):
 * transferencias entre puntos de venta y conteo de inventario — espejo de
 * `Ways.Application.Stock.Contratos`/`ServicioDeStock`. Ningún request lleva un delta
 * (`dto-contract-honesty`): la transferencia manda una cantidad siempre POSITIVA por línea (el
 * signo por punto de venta lo decide el servidor), el conteo manda el TOTAL contado — el servidor
 * deriva el ajuste bajo el lock de la fila de `stock`, nunca el cliente.
 */
import { api } from './cliente'
import type {
  ConteoDeLote,
  LineaDeTransferencia,
  LoteListado,
  ResultadoConteo,
  ResultadoTransferencia,
  SolicitudDeConteo,
  SolicitudDeTransferencia,
  StockActual,
} from './tipos'

export const clienteDeStock = {
  /** `GET /api/stock` — balance actual de un artículo en un punto de venta. Usado por el conteo
   * para mostrar el "antes" honesto en pantalla, antes de que el operador tipee lo contado. */
  obtenerActual: (idPuntoVenta: number, idArticulo: number) =>
    api.get<StockActual>(`/stock?idPuntoVenta=${idPuntoVenta}&idArticulo=${idArticulo}`),
  transferir: (solicitud: SolicitudDeTransferencia) =>
    api.post<ResultadoTransferencia>('/stock/transferencias', solicitud),
  /** `POST /api/stock/conteos` — la respuesta (`ResultadoConteo`) es la ÚNICA fuente de verdad
   * de lo que se escribió; el `GET /api/stock` previo es solo un dato de referencia en pantalla,
   * nunca lo que decide qué se renderiza después de un submit. */
  contar: (solicitud: SolicitudDeConteo) => api.post<ResultadoConteo>('/stock/conteos', solicitud),
  /** stage-12-lotes-vencimientos (Slice 15, espejo de `GET /api/stock/lotes`): feed del picker de
   * lote, con `sugerido` (FEFO server-computed) — el picker lo pre-selecciona, nunca lo
   * recalcula del lado del cliente (design decisión 19). Sin `idComprobanteAsociado` acá: esa
   * variante (sugerencia desde el snapshot de una devolución) es del picker del POS, Slice 14. */
  lotes: (idPuntoVenta: number, idArticulo: number) =>
    api.get<LoteListado[]>(`/stock/lotes?idPuntoVenta=${idPuntoVenta}&idArticulo=${idArticulo}`),
}

// ---- Formulario de transferencia: una línea editable por fila (mismo patrón que compras.ts) --

export type LineaDeTransferenciaFormulario = {
  /** Clave solo de React — cada submit arma el arreglo de líneas desde cero. */
  clave: number
  idArticulo: number | ''
  descripcion: string
  cantidad: string
  /** stage-12-lotes-vencimientos (Slice 15): opcional incluso para un artículo lote-efectivo —
   * `''` deja que el servidor resuelva el lote vía FEFO (design decisión 19: "el server manda"),
   * mismo criterio que el picker del POS. */
  idLote: number | ''
  /** Código del lote elegido, solo para mostrarlo en el `<select>` — nunca viaja al backend
   * (el servidor ya conoce el código a partir de `idLote`). */
  codigoLote: string
  /** `ArticuloListado.controlaLote` del artículo elegido — campo solo de UI (decide si esta fila
   * muestra el picker de lote), nunca viaja al backend: no es parte de `LineaDeTransferencia`. */
  controlaLote: boolean
}

export function lineaDeTransferenciaVacia(clave: number): LineaDeTransferenciaFormulario {
  return { clave, idArticulo: '', descripcion: '', cantidad: '', idLote: '', codigoLote: '', controlaLote: false }
}

export function lineaTransferenciaCompleta(l: LineaDeTransferenciaFormulario): boolean {
  const cantidad = Number(l.cantidad)
  return l.idArticulo !== '' && l.cantidad.trim() !== '' && Number.isFinite(cantidad) && cantidad > 0
}

/** Espejo de `ExigirLineasDeTransferenciaValidas` (design decisión 9: "rechaza un artículo
 * repetido en un mismo request") — feedback instantáneo, nunca autoritativo: el servidor vuelve a
 * validarlo. Solo mira las líneas completas — una fila a medio llenar nunca cuenta como repetida.
 *
 * stage-12-lotes-vencimientos (Slice 15): la clave de detección sigue siendo SOLO `idArticulo` —
 * la restricción real de unicidad del backend es por `(idArticulo, idLote)` (decisión 11), pero
 * dos líneas del mismo artículo con lotes DISTINTOS explícitos son válidas del lado del servidor;
 * acá se preserva el comportamiento previo (un artículo repetido siempre se avisa) porque dos
 * líneas del mismo artículo sin lote explícito (ambas dejando que el servidor FEFO-resuelva)
 * competirían por el mismo lote sugerido en la práctica. */
export function articulosRepetidosEnTransferencia(lineas: LineaDeTransferenciaFormulario[]): Set<number> {
  const conteoPorArticulo = new Map<number, number>()
  for (const l of lineas.filter(lineaTransferenciaCompleta)) {
    const id = Number(l.idArticulo)
    conteoPorArticulo.set(id, (conteoPorArticulo.get(id) ?? 0) + 1)
  }
  const repetidos = new Set<number>()
  for (const [id, cantidad] of conteoPorArticulo) {
    if (cantidad > 1) repetidos.add(id)
  }
  return repetidos
}

export function aLineasDeTransferencia(lineas: LineaDeTransferenciaFormulario[]): LineaDeTransferencia[] {
  return lineas
    .filter(lineaTransferenciaCompleta)
    .map((l) => ({
      idArticulo: Number(l.idArticulo),
      cantidad: Number(l.cantidad),
      idLote: l.idLote === '' ? null : Number(l.idLote),
    }))
}

export function aSolicitudDeTransferencia(
  idPuntoVentaOrigen: number | '',
  idPuntoVentaDestino: number | '',
  observaciones: string,
  lineas: LineaDeTransferenciaFormulario[],
): SolicitudDeTransferencia {
  return {
    idPuntoVentaOrigen: idPuntoVentaOrigen === '' ? 0 : idPuntoVentaOrigen,
    idPuntoVentaDestino: idPuntoVentaDestino === '' ? 0 : idPuntoVentaDestino,
    observaciones: observaciones.trim(),
    lineas: aLineasDeTransferencia(lineas),
  }
}

// ---- Conteo de inventario ---------------------------------------------------------------------

export function aSolicitudDeConteo(
  idPuntoVenta: number | '',
  idArticulo: number | '',
  contada: string,
  observaciones: string,
): SolicitudDeConteo {
  return {
    idPuntoVenta: idPuntoVenta === '' ? 0 : idPuntoVenta,
    idArticulo: idArticulo === '' ? 0 : idArticulo,
    contada: contada.trim() === '' ? 0 : Number(contada),
    observaciones: observaciones.trim(),
  }
}

export function contadaValida(contada: string): boolean {
  if (contada.trim() === '') return false
  const n = Number(contada)
  return Number.isFinite(n) && n >= 0
}

// ---- Conteo por lote (stage-12-lotes-vencimientos, Slice 15, design decisión 12/18) ------------
// Un artículo lote-efectivo cuenta por lote — nunca el total agregado (`400
// conteo_no_aplica_lotes` del lado del servidor si se manda `contada`) — exactly-one-of espejado
// client-side: la pantalla decide QUÉ forma armar según `ArticuloListado.controlaLote`, nunca
// arma ambas ni ninguna.

export type LineaDeConteoDeLoteFormulario = { idLote: number; codigo: string; contada: string }

export function lineaDeConteoDeLoteVacia(lote: LoteListado): LineaDeConteoDeLoteFormulario {
  return { idLote: lote.idLote, codigo: lote.codigo, contada: '' }
}

/** Solo cuentan como "completas" las líneas con una `contada` tipeada — un lote listado pero sin
 * tocar por el operador se omite del request (mismo criterio que `ContarAsync`: no hace falta
 * recontar cada lote existente para actualizar uno solo). */
export function lineasDeConteoDeLoteCompletas(lineas: LineaDeConteoDeLoteFormulario[]): LineaDeConteoDeLoteFormulario[] {
  return lineas.filter((l) => contadaValida(l.contada))
}

export function aConteoDeLotes(lineas: LineaDeConteoDeLoteFormulario[]): ConteoDeLote[] {
  return lineasDeConteoDeLoteCompletas(lineas).map((l) => ({ idLote: l.idLote, contada: Number(l.contada) }))
}

/** Rama por-lote de `SolicitudDeConteo` — `contada: null`, `lotes` con al menos una línea
 * completa (dto-contract-honesty: nunca se manda `lotes: []`, el backend lo rechazaría igual que
 * "ninguna de las dos formas"). */
export function aSolicitudDeConteoPorLote(
  idPuntoVenta: number | '',
  idArticulo: number | '',
  lineas: LineaDeConteoDeLoteFormulario[],
  observaciones: string,
): SolicitudDeConteo {
  return {
    idPuntoVenta: idPuntoVenta === '' ? 0 : idPuntoVenta,
    idArticulo: idArticulo === '' ? 0 : idArticulo,
    contada: null,
    observaciones: observaciones.trim(),
    lotes: aConteoDeLotes(lineas),
  }
}
