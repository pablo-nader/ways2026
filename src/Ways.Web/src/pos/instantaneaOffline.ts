/**
 * Instantánea offline del POS (stage-pos-venta-offline-web, Parte A/B): persistencia local de
 * `GET /api/pos/instantanea?version=2` + la resolución pura de escaneo/precio/cliente contra esa
 * instantánea cuando no hay red. Espeja la semántica del servidor (decisión del backend: "el
 * dispositivo nunca vuelve a evaluar ofertas offline") — nunca reimplementa el motor de reglas,
 * solo lee el precio ya congelado que trajo la instantánea para la lista del cliente.
 */
import type { AlmacenClaveValor, AlmacenDeClavesMultiples } from './almacenPos'
import type {
  ArticuloDeInstantanea,
  ArticuloEscaneado,
  ClienteDeInstantanea,
  ClienteListado,
  EscalonDeCantidad,
  InstantaneaDePos,
  MedioPagoDeInstantanea,
  MedioPagoListado,
  OfertaAplicada,
  PrecioDeListaDeInstantanea,
  ResultadoDeResolucion,
} from '../api/tipos'
import type { LineaCarrito } from '../api/carrito'

/** Clave versionada: la instantánea anterior a los precios por lista quedó guardada bajo
 * `'instantanea'` con otra forma y nunca se lee — se vuelve a descargar entera. */
const CLAVE_INSTANTANEA = 'instantanea.v2'
const CLAVE_INSTANTANEA_ANTERIOR = 'instantanea'

/**
 * Lo que se persiste: la instantánea, la etiqueta con la que pedir el refresco condicional y la
 * hora local de la última verificación exitosa contra el servidor (un `200` o un `304`). La vejez
 * que ve el cajero sale de `verificadaEn`, no de `instantanea.momento`: un `304` confirma que los
 * precios siguen vigentes aunque el contenido no haya cambiado desde hace horas.
 */
export type InstantaneaLocal = {
  version: 2
  instantanea: InstantaneaDePos
  etag: string | null
  verificadaEn: string
}

export async function guardarInstantaneaLocal(almacen: AlmacenClaveValor, local: Omit<InstantaneaLocal, 'version'>): Promise<void> {
  await almacen.escribir<InstantaneaLocal>(CLAVE_INSTANTANEA, { version: 2, ...local })
}

/** `null` si no hay nada guardado o si lo guardado no tiene la forma esperada: nunca se cotiza con
 * una instantánea de forma desconocida. */
export async function leerInstantaneaLocal(almacen: AlmacenClaveValor): Promise<InstantaneaLocal | null> {
  const guardada = await almacen.leer<unknown>(CLAVE_INSTANTANEA)
  if (!esObjeto(guardada) || guardada.version !== 2 || !esInstantaneaValida(guardada.instantanea)) return null
  if (typeof guardada.verificadaEn !== 'string') return null
  return {
    version: 2,
    instantanea: guardada.instantanea,
    etag: typeof guardada.etag === 'string' ? guardada.etag : null,
    verificadaEn: guardada.verificadaEn,
  }
}

function esObjeto(valor: unknown): valor is Record<string, unknown> {
  return typeof valor === 'object' && valor !== null
}

function esPrecioDeLista(valor: unknown): boolean {
  return (
    esObjeto(valor) &&
    typeof valor.idListaPrecio === 'number' &&
    typeof valor.precioOriginal === 'number' &&
    typeof valor.precioFinal === 'number' &&
    typeof valor.descuentoUnitario === 'number' &&
    Array.isArray(valor.aplicadas) &&
    (valor.escalones == null || Array.isArray(valor.escalones))
  )
}

function esArticulo(valor: unknown): boolean {
  return (
    esObjeto(valor) &&
    typeof valor.idArticulo === 'number' &&
    typeof valor.codigoInterno === 'string' &&
    Array.isArray(valor.codigosBarra) &&
    typeof valor.porcentajeIva === 'number' &&
    Array.isArray(valor.preciosPorLista) &&
    valor.preciosPorLista.every(esPrecioDeLista)
  )
}

function esCliente(valor: unknown): boolean {
  return (
    esObjeto(valor) &&
    typeof valor.idCliente === 'number' &&
    typeof valor.numero === 'number' &&
    typeof valor.nombre === 'string' &&
    (valor.idListaPrecio === null || typeof valor.idListaPrecio === 'number') &&
    typeof valor.esConsumidorFinal === 'boolean' &&
    typeof valor.saldo === 'number'
  )
}

/** Valida la forma de una instantánea, venga del almacén o de la red: un servidor viejo (sin
 * `?version=2`) responde la forma anterior, que no se puede usar para cotizar. */
export function esInstantaneaValida(valor: unknown): valor is InstantaneaDePos {
  return (
    esObjeto(valor) &&
    typeof valor.momento === 'string' &&
    typeof valor.idPuntoVenta === 'number' &&
    typeof valor.toleranciaPago === 'number' &&
    Array.isArray(valor.mediosDePago) &&
    Array.isArray(valor.articulos) &&
    valor.articulos.every(esArticulo) &&
    Array.isArray(valor.clientes) &&
    valor.clientes.every(esCliente)
  )
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
 * online. El precio se busca después, en la lista del cliente (`resolverPreciosOffline`).
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

/** Precio del artículo en la lista pedida; `null` si el artículo no tiene precio en esa lista. */
export function precioEnLista(articulo: ArticuloDeInstantanea, idListaPrecio: number): PrecioDeListaDeInstantanea | null {
  return articulo.preciosPorLista.find((p) => p.idListaPrecio === idListaPrecio) ?? null
}

/**
 * Tramo de cantidad vigente para `cantidad`: el que tiene el `cantidadDesde` MÁS ALTO entre los
 * que cumplen `cantidadDesde <= cantidad`. `null` cuando el precio no trae tramos o cuando ninguno
 * califica todavía; en ambos casos rige el precio plano.
 *
 * ÚNICO lugar donde vive esta regla — la consumen la vista previa del carrito
 * (`resolverPreciosOffline`), el payload que el servidor cobra literal
 * (`enriquecerLineasConPrecioOffline`) y el ticket sintético
 * (`construirComprobanteOfflineSintetico`): si divergieran, el cajero vería un importe y se
 * cobraría otro. Elige por umbral, no por posición en el array.
 */
export function elegirEscalon(precio: PrecioDeListaDeInstantanea, cantidad: number): EscalonDeCantidad | null {
  let elegido: EscalonDeCantidad | null = null
  for (const escalon of precio.escalones ?? []) {
    if (escalon.cantidadDesde <= cantidad && (elegido === null || escalon.cantidadDesde > elegido.cantidadDesde)) {
      elegido = escalon
    }
  }
  return elegido
}

/** Los cuatro campos de precio que rigen para `cantidad` — el escalón vigente pisa
 * `precioFinal`/`descuentoUnitario`/`aplicadas`, y `precioOriginal` queda SIEMPRE el de la lista
 * (el escalón no lo trae: es constante entre cantidades, ver `EscalonDeCantidad`). */
export type PreciosVigentesOffline = Pick<PrecioDeListaDeInstantanea, 'precioOriginal' | 'precioFinal' | 'descuentoUnitario' | 'aplicadas'>

/** Proyección de `elegirEscalon` a los campos de precio efectivos — segundo tramo de la MISMA
 * regla, exportado aparte para que los tres consumidores no repitan cada uno el "escalón, si no
 * el plano". */
export function preciosVigentesOffline(precio: PrecioDeListaDeInstantanea, cantidad: number): PreciosVigentesOffline {
  const escalon = elegirEscalon(precio, cantidad)
  if (!escalon) {
    const { precioOriginal, precioFinal, descuentoUnitario, aplicadas } = precio
    return { precioOriginal, precioFinal, descuentoUnitario, aplicadas }
  }
  return {
    precioOriginal: precio.precioOriginal,
    precioFinal: escalon.precioFinal,
    descuentoUnitario: escalon.descuentoUnitario,
    aplicadas: escalon.aplicadas,
  }
}

function indicePorArticulo(instantanea: InstantaneaDePos): Map<number, ArticuloDeInstantanea> {
  return new Map(instantanea.articulos.map((a) => [a.idArticulo, a]))
}

/**
 * Resuelve el precio de cada línea del carrito contra la instantánea local, en la lista del
 * cliente de la venta — mismo shape de salida que `indexarResolucionPorArticulo` (indexado por
 * `idArticulo`), así que `previaDeLinea`/`calcularSubtotalPrevia` siguen funcionando sin cambios
 * vengan los precios de la red o de la instantánea. Una línea cuyo artículo no está en la
 * instantánea o no tiene precio en esa lista queda ausente del índice — el mismo tratamiento que
 * un artículo sin precio en la resolución online.
 *
 * La `cantidad` de cada línea elige el tramo de precio vigente (`preciosVigentesOffline`).
 */
export function resolverPreciosOffline(
  lineas: LineaCarrito[],
  instantanea: InstantaneaDePos,
  idListaPrecio: number,
): Record<number, ResultadoDeResolucion> {
  const porId = indicePorArticulo(instantanea)
  const indice: Record<number, ResultadoDeResolucion> = {}
  for (const linea of lineas) {
    const articulo = porId.get(linea.idArticulo)
    const precio = articulo ? precioEnLista(articulo, idListaPrecio) : null
    if (precio) indice[linea.idArticulo] = { idArticulo: linea.idArticulo, idListaPrecio, ...preciosVigentesOffline(precio, linea.cantidad) }
  }
  return indice
}

/** `true` si TODAS las líneas tienen precio en la lista pedida dentro de la instantánea — la
 * precondición de la vista previa local y del checkout offline (todas con precio, o directamente
 * ir por la red en vez de mostrar o vender media venta a precio inventado). ÚNICO gate para esa
 * precondición. Tipo de línea mínimo (`{ idArticulo }`) para validar tanto el carrito en pantalla
 * como un `LineaDeVenta[]` ya armado. */
export function todasLasLineasTienenPrecioOffline(
  lineas: readonly { idArticulo: number }[],
  instantanea: InstantaneaDePos,
  idListaPrecio: number,
): boolean {
  const porId = indicePorArticulo(instantanea)
  return (
    lineas.length > 0 &&
    lineas.every((l) => {
      const articulo = porId.get(l.idArticulo)
      return articulo !== undefined && precioEnLista(articulo, idListaPrecio) !== null
    })
  )
}

// --- Clientes y medios de pago de la instantánea -------------------------------------------------

/** Mismo tope que la primera página de `GET /api/clientes` (`ServicioDeClientes.ListarAsync`). */
export const TOPE_DE_RESULTADOS_DE_CLIENTES = 25

function normalizar(texto: string): string {
  return texto
    .normalize('NFD')
    .replace(/[̀-ͯ]/g, '')
    .toLowerCase()
    .trim()
}

function soloDigitos(texto: string): string {
  return texto.replace(/\D/g, '')
}

/**
 * Busca clientes en la instantánea por nombre, apellido, razón social, número de cliente o
 * documento, sin distinguir mayúsculas ni acentos. El documento también se compara solo por sus
 * dígitos, así "20304050607" encuentra "20-30405060-7". Término vacío ⇒ los primeros clientes.
 * Devuelve a lo sumo `TOPE_DE_RESULTADOS_DE_CLIENTES`, en el orden de la instantánea (por número).
 */
export function buscarClientesOffline(instantanea: InstantaneaDePos, termino: string): ClienteDeInstantanea[] {
  const buscado = normalizar(termino)
  const digitos = soloDigitos(termino)
  const coincide = (c: ClienteDeInstantanea) => {
    if (buscado === '') return true
    const textos = [c.nombre, c.apellido, c.razonSocial, [c.nombre, c.apellido].filter(Boolean).join(' '), c.numeroDocumento]
    if (textos.some((t) => t != null && normalizar(t).includes(buscado))) return true
    if (String(c.numero) === buscado) return true
    return digitos !== '' && digitos === buscado.replace(/[\s.-]/g, '') && c.numeroDocumento != null && soloDigitos(c.numeroDocumento).includes(digitos)
  }
  return instantanea.clientes.filter(coincide).slice(0, TOPE_DE_RESULTADOS_DE_CLIENTES)
}

/**
 * Lleva un cliente de la instantánea a la forma `ClienteListado` que usa la pantalla de venta.
 * Los datos de contacto no viajan en la instantánea y quedan en `null`; la pantalla de venta no
 * los lee. Una lista dada de baja (`idListaPrecio: null`) se lleva a `0`, un id que ninguna lista
 * tiene: la vista previa local no encuentra precios y la online rechaza igual que con la FK real.
 */
export function aClienteListado(c: ClienteDeInstantanea): ClienteListado {
  return {
    id: c.idCliente,
    numero: c.numero,
    nombre: c.nombre,
    apellido: c.apellido,
    razonSocial: c.razonSocial,
    tipoDocumento: c.tipoDocumento,
    numeroDocumento: c.numeroDocumento,
    idCondicionFiscal: c.idCondicionFiscal,
    nacimiento: null,
    domicilio: null,
    telefono: null,
    celular: null,
    email: null,
    observaciones: null,
    idListaPrecio: c.idListaPrecio ?? 0,
    limiteCredito: c.limiteCredito,
    creditoIlimitado: c.creditoIlimitado,
    saldo: c.saldo,
    activo: true,
    idEmpresa: c.idEmpresa,
    esConsumidorFinal: c.esConsumidorFinal,
  }
}

/** Lleva un medio de pago de la instantánea a `MedioPagoListado`. `orden` sale de la posición (la
 * instantánea ya viene ordenada); `idEmpresa`/`recargoPorcentaje` no viajan y la pantalla de venta
 * no los lee. */
export function aMedioPagoListado(m: MedioPagoDeInstantanea, indice: number): MedioPagoListado {
  return {
    id: m.idMedioPago,
    nombre: m.nombre,
    activo: true,
    idEmpresa: null,
    orden: indice + 1,
    comportamiento: m.comportamiento,
    admiteVuelto: m.admiteVuelto,
    requiereReferencia: m.requiereReferencia,
    recargoPorcentaje: null,
  }
}

// --- Vejez --------------------------------------------------------------------------------------

const MINUTO_EN_MS = 60_000
const HORA_EN_MS = 60 * MINUTO_EN_MS
const DIA_EN_MS = 24 * HORA_EN_MS

/** Vejez en minutos completos — nunca negativa (un reloj local desincronizado hacia el pasado se
 * trata como "recién ahora", no como un absurdo "en el futuro"). */
export function edadDeInstantaneaEnMinutos(momento: string, ahora: Date): number {
  const diferenciaMs = ahora.getTime() - new Date(momento).getTime()
  return Math.max(0, Math.floor(diferenciaMs / MINUTO_EN_MS))
}

/** Texto legible de la vejez de un dato local. Se le pasa la hora de la última verificación
 * exitosa (`InstantaneaLocal.verificadaEn`), no la del último cambio de contenido. */
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
export type { ArticuloDeInstantanea, EscalonDeCantidad, OfertaAplicada, PrecioDeListaDeInstantanea }

/** Borra la instantánea guardada (y la de la forma anterior, que nadie más lee). Nunca rechaza. */
export async function purgarInstantaneaLocal(almacen: Pick<AlmacenDeClavesMultiples, 'eliminar'>): Promise<void> {
  await Promise.all([almacen.eliminar(CLAVE_INSTANTANEA), almacen.eliminar(CLAVE_INSTANTANEA_ANTERIOR)])
}
