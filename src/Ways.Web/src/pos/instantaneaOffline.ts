/**
 * Instantánea offline del POS (stage-pos-venta-offline-web, Parte A/B): persistencia local de
 * `GET /api/pos/instantanea` + la resolución pura de escaneo/precio contra esa instantánea cuando
 * no hay red. Espeja EXACTAMENTE la semántica del servidor (design decisión del backend: "el
 * dispositivo nunca vuelve a evaluar ofertas offline") — nunca reimplementa el motor de reglas,
 * solo lee el precio ya congelado que trajo la instantánea.
 */
import type { AlmacenClaveValor } from './almacenPos'
import type { ArticuloDeInstantanea, ArticuloEscaneado, InstantaneaDePos, OfertaAplicada, ResultadoDeResolucion } from '../api/tipos'
import type { LineaCarrito } from '../api/carrito'

const CLAVE_INSTANTANEA = 'instantanea'

export async function guardarInstantaneaLocal(almacen: AlmacenClaveValor, instantanea: InstantaneaDePos): Promise<void> {
  await almacen.escribir(CLAVE_INSTANTANEA, instantanea)
}

export function leerInstantaneaLocal(almacen: AlmacenClaveValor): Promise<InstantaneaDePos | null> {
  return almacen.leer<InstantaneaDePos>(CLAVE_INSTANTANEA)
}

// --- Parseo de escaneo, espejo EXACTO de `Ways.Domain.Ventas.ParserDeEscaneo` -------------------
// Duplicado a propósito (no compartido con el backend, no hay forma de importar C# desde TS): un
// cambio en la regla del servidor tiene que replicarse acá a mano. Mismo criterio documentado que
// `ServicioDeReservasDeNumeracion.ResolverTipoAsync` sobre `ServicioDeVentas.ResolverTipoComprobanteAsync`.

/** Regla I.2 del servidor: menos de 7 dígitos ⇒ código interno, 7 o más ⇒ código de barra. */
const LONGITUD_MINIMA_CODIGO_BARRA = 7

export type ObjetivoDeEscaneo = 'CodigoInterno' | 'CodigoBarra'

export type EntradaDeEscaneo = { cantidad: number; codigo: string; objetivo: ObjetivoDeEscaneo }

/** Sintaxis `<cantidad>*<codigo>` — un prefijo ausente, vacío, "0", negativo o no numérico nunca
 * invalida el escaneo, cae a cantidad 1 (mismo criterio que `ParserDeEscaneo.SepararCantidadYCodigo`).
 * `null` solo cuando la entrada (recortada) queda vacía. */
export function parsearEntradaDeEscaneoOffline(entradaCruda: string): EntradaDeEscaneo | null {
  const texto = entradaCruda.trim()
  if (texto === '') return null

  const indice = texto.indexOf('*')
  const [prefijo, codigoCrudo] = indice < 0 ? [null, texto] : [texto.slice(0, indice).trim(), texto.slice(indice + 1).trim()]

  const codigo = codigoCrudo.trim()
  if (codigo === '') return null

  const cantidadParseada = prefijo === null ? Number.NaN : Number(prefijo)
  const cantidad = prefijo !== null && Number.isFinite(cantidadParseada) && cantidadParseada > 0 ? cantidadParseada : 1

  const objetivo: ObjetivoDeEscaneo = codigo.length < LONGITUD_MINIMA_CODIGO_BARRA ? 'CodigoInterno' : 'CodigoBarra'
  return { cantidad, codigo, objetivo }
}

/**
 * Busca un artículo en la instantánea local por código escaneado/tipeado — mismo camino de
 * resolución que `ServicioDeEscaneo.ResolverAsync`, contra el catálogo ya congelado. Devuelve la
 * forma `ArticuloEscaneado` (identidad + cantidad, nunca precio — design decisión 7 preservada
 * offline) para que `aLineaDeCarritoDesdeEscaneo` la consuma exactamente igual que la respuesta
 * online: el resto del carrito no necesita saber si el escaneo vino de la red o de la instantánea.
 * `null` cuando la entrada está vacía o el código no existe en la instantánea (nunca lanza).
 */
export function buscarArticuloOffline(instantanea: InstantaneaDePos, entradaCruda: string): ArticuloEscaneado | null {
  const entrada = parsearEntradaDeEscaneoOffline(entradaCruda)
  if (!entrada) return null

  const articulo =
    entrada.objetivo === 'CodigoInterno'
      ? instantanea.articulos.find((a) => a.codigoInterno === entrada.codigo)
      : instantanea.articulos.find((a) => a.codigosBarra.includes(entrada.codigo))

  if (!articulo) return null

  return {
    idArticulo: articulo.idArticulo,
    codigoInterno: articulo.codigoInterno,
    nombre: articulo.nombre,
    codigoBarra: entrada.objetivo === 'CodigoBarra' ? entrada.codigo : null,
    cantidad: entrada.cantidad,
  }
}

function aResultadoDeResolucion(articulo: ArticuloDeInstantanea, idListaPrecio: number): ResultadoDeResolucion {
  return {
    idArticulo: articulo.idArticulo,
    idListaPrecio,
    precioOriginal: articulo.precioOriginal,
    precioFinal: articulo.precioFinal,
    descuentoUnitario: articulo.descuentoUnitario,
    aplicadas: articulo.aplicadas,
  }
}

/**
 * Resuelve el precio de cada línea del carrito contra la instantánea local — mismo shape de
 * salida que `indexarResolucionPorArticulo` (indexado por `idArticulo`), así que `previaDeLinea`/
 * `calcularSubtotalPrevia` (ambos en `ventas.ts`) siguen funcionando sin cambios sobre el
 * resultado, vengan los precios de la red o de la instantánea. Una línea cuyo artículo no está en
 * la instantánea (dado de baja, o nunca tuvo precio vigente) queda ausente del índice — el mismo
 * tratamiento que ya tiene un artículo sin precio en la resolución online. `idListaPrecio` es el
 * del cliente de la venta (offline solo admite Consumidor Final, ver `reglasOffline.ts`) — la
 * instantánea ya congeló el precio contra esa lista server-side, este valor es solo para
 * completar el shape de `ResultadoDeResolucion`, ningún renderizado de la pantalla lo lee.
 */
export function resolverPreciosOffline(
  lineas: LineaCarrito[],
  instantanea: InstantaneaDePos,
  idListaPrecio: number,
): Record<number, ResultadoDeResolucion> {
  const porId = new Map(instantanea.articulos.map((a) => [a.idArticulo, a]))
  const indice: Record<number, ResultadoDeResolucion> = {}
  for (const linea of lineas) {
    const articulo = porId.get(linea.idArticulo)
    if (articulo) indice[linea.idArticulo] = aResultadoDeResolucion(articulo, idListaPrecio)
  }
  return indice
}

/** `true` si TODAS las líneas del carrito tienen precio resuelto en la instantánea — la
 * precondición que el checkout offline exige (todas con precio, o directamente rechazar en vez de
 * vender media venta a precio inventado). */
export function todasLasLineasTienenPrecioOffline(lineas: LineaCarrito[], instantanea: InstantaneaDePos): boolean {
  const porId = new Set(instantanea.articulos.map((a) => a.idArticulo))
  return lineas.length > 0 && lineas.every((l) => porId.has(l.idArticulo))
}

const MINUTO_EN_MS = 60_000
const HORA_EN_MS = 60 * MINUTO_EN_MS
const DIA_EN_MS = 24 * HORA_EN_MS

/** Vejez de la instantánea en minutos completos — nunca negativa (un reloj local desincronizado
 * hacia el pasado del servidor se trata como "recién ahora", no como un absurdo "en el futuro"). */
export function edadDeInstantaneaEnMinutos(momento: string, ahora: Date): number {
  const diferenciaMs = ahora.getTime() - new Date(momento).getTime()
  return Math.max(0, Math.floor(diferenciaMs / MINUTO_EN_MS))
}

/** Texto legible de la vejez de la instantánea, para mostrarle al cajero cuánto puede confiar en
 * el precio congelado — `momento` es la única marca real (doc-comment de `InstantaneaDePos`). */
export function formatearVejezDeInstantanea(momento: string, ahora: Date): string {
  const diferenciaMs = Math.max(0, ahora.getTime() - new Date(momento).getTime())

  if (diferenciaMs < MINUTO_EN_MS) return 'hace instantes'
  if (diferenciaMs < HORA_EN_MS) {
    const minutos = Math.floor(diferenciaMs / MINUTO_EN_MS)
    return `hace ${minutos} minuto${minutos === 1 ? '' : 's'}`
  }
  if (diferenciaMs < DIA_EN_MS) {
    const horas = Math.floor(diferenciaMs / HORA_EN_MS)
    return `hace ${horas} hora${horas === 1 ? '' : 's'}`
  }
  const dias = Math.floor(diferenciaMs / DIA_EN_MS)
  return `hace ${dias} día${dias === 1 ? '' : 's'}`
}

// Reexportado para que otros módulos (`outboxOffline.ts`) tipen sin importar desde `tipos.ts`
// directamente donde no haga falta.
export type { ArticuloDeInstantanea, OfertaAplicada }
