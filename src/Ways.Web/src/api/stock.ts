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
  LineaDeTransferencia,
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
}

// ---- Formulario de transferencia: una línea editable por fila (mismo patrón que compras.ts) --

export type LineaDeTransferenciaFormulario = {
  /** Clave solo de React — cada submit arma el arreglo de líneas desde cero. */
  clave: number
  idArticulo: number | ''
  descripcion: string
  cantidad: string
}

export function lineaDeTransferenciaVacia(clave: number): LineaDeTransferenciaFormulario {
  return { clave, idArticulo: '', descripcion: '', cantidad: '' }
}

export function lineaTransferenciaCompleta(l: LineaDeTransferenciaFormulario): boolean {
  const cantidad = Number(l.cantidad)
  return l.idArticulo !== '' && l.cantidad.trim() !== '' && Number.isFinite(cantidad) && cantidad > 0
}

/** Espejo de `ExigirLineasDeTransferenciaValidas` (design decisión 9: "rechaza un artículo
 * repetido en un mismo request") — feedback instantáneo, nunca autoritativo: el servidor vuelve a
 * validarlo. Solo mira las líneas completas — una fila a medio llenar nunca cuenta como repetida. */
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
  return lineas.filter(lineaTransferenciaCompleta).map((l) => ({ idArticulo: Number(l.idArticulo), cantidad: Number(l.cantidad) }))
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
