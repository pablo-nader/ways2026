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
  MinimosDeStock,
  ResultadoConteo,
  ResultadoTransferencia,
  SolicitudDeConteo,
  SolicitudDeMinimos,
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
  /** `GET /api/stock/lotes` — feed del picker (stage-12-lotes-vencimientos, Slices 14/15, design
   * decisión 19): `sugerido` es FEFO server-computed y el picker lo pre-selecciona, nunca lo
   * recalcula del lado del cliente. Se pide bajo demanda, nunca de arranque para cada línea —
   * el camino feliz (omitir `idLote`) no necesita esta llamada. La variante con
   * `idComprobanteAsociado` (sugerencia desde el snapshot de una devolución) vive en el POS. */
  listarLotes: (idPuntoVenta: number, idArticulo: number) =>
    api.get<LoteListado[]>(`/stock/lotes?idPuntoVenta=${idPuntoVenta}&idArticulo=${idArticulo}`),
  /** `PUT /api/stock/minimos` (stage-13-stock-inteligente, Slice 1/3; design decisión 11/16): la
   * respuesta es la fila PERSISTIDA leída del mismo `RETURNING` que escribió — `Existencias.tsx`
   * la aplica con un updater funcional desde `prev`, sin volver a pedir el reporte. */
  escribirMinimos: (solicitud: SolicitudDeMinimos) => api.put<MinimosDeStock>('/stock/minimos', solicitud),
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
 * stage-12-lotes-vencimientos (Slice 15, judgment-day fix): la clave real de unicidad del backend
 * es `(idArticulo, idLote)` (decisión 11) — dedupear por `idArticulo` a secas bloqueaba una
 * transferencia legal (dos líneas del mismo artículo con lotes explícitos DISTINTOS, la operación
 * real que el picker de lote existe para habilitar). Acá se espeja esa clave con lo único que el
 * cliente PUEDE saber sin adivinar el FEFO del servidor (decisión 19: "el server manda"):
 * - Dos líneas completas con el MISMO `(idArticulo, idLote explícito)` → repetido (choca contra la
 *   restricción real del backend).
 * - Dos o más líneas del mismo artículo, TODAS con lote en "Auto (FEFO)" → repetido: el servidor
 *   lee los saldos UNA sola vez antes de resolver todas las líneas (`LeerSaldosAsync`, snapshot
 *   único pre-transacción) y `ElegirFefo` es una función pura sobre ese mismo snapshot — dos
 *   líneas Auto del mismo artículo SIEMPRE resuelven al mismo lote, nunca "probablemente"; el
 *   servidor las rechaza con `400 articulo_repetido` de forma determinística.
 * - Mismo artículo con lotes explícitos DISTINTOS → PERMITIDO (válido en el backend).
 * - Mismo artículo, una línea con lote explícito y otra en Auto → PERMITIDO client-side (el
 *   cliente no puede computar el pick FEFO de la línea Auto para compararlo); si el servidor
 *   resuelve al mismo lote igual, arbitra con un `400 articulo_repetido` que el funnel de error
 *   existente (`ErrorApi.message` en el catch de `transferir`) muestra tal cual, sin tragarlo.
 *
 * Devuelve las `clave` (no los `idArticulo`) de las líneas efectivamente en conflicto — a
 * diferencia del `idArticulo` a secas, dos líneas del mismo artículo pueden coexistir sin
 * conflicto (lotes distintos), así que marcar por artículo ya no alcanza para decidir qué fila
 * mostrar en rojo. */
export function articulosRepetidosEnTransferencia(lineas: LineaDeTransferenciaFormulario[]): Set<number> {
  const porArticulo = new Map<number, LineaDeTransferenciaFormulario[]>()
  for (const l of lineas.filter(lineaTransferenciaCompleta)) {
    const id = Number(l.idArticulo)
    const grupo = porArticulo.get(id) ?? []
    grupo.push(l)
    porArticulo.set(id, grupo)
  }

  const repetidas = new Set<number>()
  for (const grupo of porArticulo.values()) {
    if (grupo.length < 2) continue

    const autoFefo = grupo.filter((l) => l.idLote === '')
    if (autoFefo.length > 1) {
      for (const l of autoFefo) repetidas.add(l.clave)
    }

    const porLoteExplicito = new Map<number, LineaDeTransferenciaFormulario[]>()
    for (const l of grupo) {
      if (l.idLote === '') continue
      const idLote = Number(l.idLote)
      const g = porLoteExplicito.get(idLote) ?? []
      g.push(l)
      porLoteExplicito.set(idLote, g)
    }
    for (const g of porLoteExplicito.values()) {
      if (g.length > 1) {
        for (const l of g) repetidas.add(l.clave)
      }
    }
  }
  return repetidas
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

// ---- Mínimos y reposición (stage-13-stock-inteligente, Slice 3) --------------------------------
// Editor inline de fila única de `Existencias.tsx` (design: Web Composition, decisión 15/16): un
// umbral vacío es "no gestionado" (unmanage), nunca 0 — el mismo criterio que ya usa
// `SolicitudDeMinimos` del lado del servidor.

function aUmbral(texto: string): number | null {
  const limpio = texto.trim()
  return limpio === '' ? null : Number(limpio)
}

/** Un campo vacío siempre es válido (unmanage); uno tipeado tiene que parsear a un número finito.
 * `Number('1,5')` (coma decimal) es `NaN`, así que ese texto queda INVÁLIDO en vez de viajar como
 * `null` en silencio — que es lo que `JSON.stringify` haría con un `NaN` sin este guard, la
 * misma clase de "campo aceptado y descartado" que `dto-contract-honesty` prohíbe. */
export function umbralTextoValido(texto: string): boolean {
  const limpio = texto.trim()
  return limpio === '' || Number.isFinite(Number(limpio))
}

/** Espejo cliente de `reposicion_menor_que_minimo` (design decisión 11) — feedback instantáneo
 * que deshabilita el guardado; el servidor lo vuelve a validar igual (`react-async-state` regla
 * 7: la copia nunca promete un bloqueo que la UI no aplica). Solo compara cuando AMBOS campos son
 * números finitos — un campo vacío o con formato inválido no dispara este aviso en particular,
 * eso ya lo bloquea `umbralTextoValido` por su propia razón. */
export function reposicionMenorQueMinimo(minimoTexto: string, reposicionTexto: string): boolean {
  if (!umbralTextoValido(minimoTexto) || !umbralTextoValido(reposicionTexto)) return false
  const minimo = aUmbral(minimoTexto)
  const reposicion = aUmbral(reposicionTexto)
  return minimo !== null && reposicion !== null && reposicion < minimo
}

/** `SolicitudDeMinimos` completa desde el formulario de edición de una fila — REEMPLAZO completo
 * (decisión 11): ambos campos vacíos limpia el par (unmanage). */
export function aSolicitudDeMinimos(
  idPuntoVenta: number,
  idArticulo: number,
  minimoTexto: string,
  reposicionTexto: string,
): SolicitudDeMinimos {
  return {
    idPuntoVenta,
    idArticulo,
    minimo: aUmbral(minimoTexto),
    reposicion: aUmbral(reposicionTexto),
  }
}
