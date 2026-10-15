/**
 * Instantánea offline del POS (stage-pos-venta-offline-web, Parte A/B): persistencia local de
 * `GET /api/pos/instantanea?version=2` + la resolución pura de escaneo/precio/cliente contra esa
 * instantánea cuando no hay red. Espeja la semántica del servidor (decisión del backend: "el
 * dispositivo nunca vuelve a evaluar ofertas offline"): el precio es el ya congelado que trajo la
 * instantánea para la lista del cliente y las ofertas nunca se reevalúan. La única regla que el
 * dispositivo SÍ evalúa offline son los combos (`motorDeCombos.ts`): dependen de la composición del
 * carrito, que solo existe en el dispositivo, así que no se pueden congelar de antemano en un precio.
 */
import type { AlmacenClaveValor, AlmacenDeClavesMultiples } from './almacenPos'
import type {
  ArticuloDeInstantanea,
  AtajoDeImporte,
  ArticuloEscaneado,
  ClienteDeInstantanea,
  ClienteListado,
  ComboAplicadoEnPrevia,
  ComboDeInstantanea,
  ComboDeLinea,
  EscalonDeCantidad,
  EscalonDeFamilia,
  InstantaneaDePos,
  ListaDeInstantanea,
  MedioPagoDeInstantanea,
  MedioPagoListado,
  OfertaAplicada,
  PrecioDeListaDeInstantanea,
  TeclaAtajo,
} from '../api/tipos'
import type { LineaCarrito } from '../api/carrito'
import type { PreciosPorLinea } from '../api/ventas'
import { DECIMALES_DE_PESO } from '../api/cantidadPorUnidad'
import { leerImporteTipeado } from '../api/importeDeVenta'
import { productoRedondeado, redondearImporte } from '../formato/importes'
import { parsearNumeroDecimal } from '../formato/numeros'
import { normalizarParaBuscar } from '../formato/texto'
import { resolverCombos, type ComboCandidato, type LineaParaCombos } from './motorDeCombos'

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
    instantanea: sanearCombosDeInstantanea(guardada.instantanea),
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
    (valor.escalones == null || Array.isArray(valor.escalones)) &&
    (valor.escalonesDeFamilia == null || Array.isArray(valor.escalonesDeFamilia))
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
    valor.preciosPorLista.every(esPrecioDeLista) &&
    // Una instantánea guardada antes de `acumulaEnVenta` no lo trae y sigue siendo válida.
    (valor.acumulaEnVenta == null || typeof valor.acumulaEnVenta === 'boolean') &&
    // La tecla falta en una instantánea guardada antes del campo y en todo artículo que no es por importe.
    (valor.teclaAtajo == null || typeof valor.teclaAtajo === 'string') &&
    (valor.idFamilia == null || typeof valor.idFamilia === 'number')
  )
}

function esNumeroOAusente(valor: unknown): boolean {
  return valor == null || typeof valor === 'number'
}

/** Solo lo que el motor y la vista previa leen: `codigo` es un rótulo y se tolera ausente. */
function esCombo(valor: unknown): valor is ComboDeInstantanea {
  return (
    esObjeto(valor) &&
    typeof valor.idCombo === 'number' &&
    typeof valor.nombre === 'string' &&
    typeof valor.precio === 'number' &&
    Array.isArray(valor.componentes) &&
    valor.componentes.every((c) => esObjeto(c) && typeof c.cantidad === 'number' && esNumeroOAusente(c.idArticulo) && esNumeroOAusente(c.idFamilia))
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
    // `listas` tampoco: su ausencia (copia guardada antes del campo) solo manda a cotizar online las líneas por importe.
    // `combos` NO se valida acá: una malformada no invalida la instantánea (un dispositivo sin copia no
    // podría vender offline y uno con copia se quedaría con una vieja para siempre); se sanea al adoptarla
    // o leerla (`sanearCombosDeInstantanea`).
  )
}

/**
 * Deja en la instantánea solo los combos bien formados: cada combo malformado se descarta y un `combos`
 * que no es una lista se trata como ausente. El resto de la instantánea no se toca. Se aplica donde la
 * instantánea se adopta (respuesta de la red) o se lee del almacén, antes de que nada la cotice.
 */
export function sanearCombosDeInstantanea(instantanea: InstantaneaDePos): InstantaneaDePos {
  const { combos, ...resto } = instantanea
  if (combos === undefined) return instantanea
  return Array.isArray(combos) ? { ...resto, combos: combos.filter(esCombo) } : resto
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
  const separada = separarPrefijoYCodigo(entradaCruda)
  if (!separada) return null
  const { prefijo, codigo } = separada

  const cantidadParseada = prefijo === null ? null : parsearNumeroDecimal(prefijo)
  const cantidad = cantidadParseada !== null && cantidadParseada > 0 ? cantidadParseada : 1

  const objetivo: ObjetivoDeEscaneo = codigo.length < LONGITUD_MINIMA_CODIGO_BARRA ? 'CodigoInterno' : 'CodigoBarra'
  return { cantidad, codigo, objetivo }
}

/** Texto antes del `*` (`null` si la entrada no trae `*`; vacío si estaba solo) y código, ambos recortados. `null` cuando la entrada
 * o el código quedan vacíos. */
function separarPrefijoYCodigo(entradaCruda: string): { prefijo: string | null; codigo: string } | null {
  const texto = entradaCruda.trim()
  if (texto === '') return null

  const indice = texto.indexOf('*')
  const [prefijo, codigoCrudo] = indice < 0 ? [null, texto] : [texto.slice(0, indice).trim(), texto.slice(indice + 1).trim()]

  const codigo = codigoCrudo.trim()
  return codigo === '' ? null : { prefijo, codigo }
}

/** Qué encontró el escaneo local. `importeInvalido` es el equivalente del 400 `importe_invalido` del servidor: un artículo por
 * importe cuyo prefijo no es un importe válido no cae a cantidad 1. */
export type ResultadoDeEscaneoOffline =
  | { tipo: 'noEncontrado' }
  | { tipo: 'importeInvalido' }
  | { tipo: 'articulo'; articulo: ArticuloEscaneado }

/**
 * Busca un artículo en la instantánea local por código escaneado/tipeado — mismo camino de
 * resolución que `ServicioDeEscaneo.ResolverAsync`, contra el catálogo ya congelado. Devuelve la
 * forma `ArticuloEscaneado` (identidad + cantidad, nunca precio — design decisión 7 preservada
 * offline) para que `aLineaDeCarritoDesdeEscaneo` la consuma exactamente igual que la respuesta
 * online. El precio se busca después, en la lista del cliente (`resolverPreciosOffline`).
 * `null` cuando la entrada está vacía o el código no existe en la instantánea (nunca lanza).
 */
export function buscarArticuloOffline(instantanea: InstantaneaDePos, entradaCruda: string): ArticuloEscaneado | null {
  const resultado = resolverEscaneoOffline(instantanea, entradaCruda)
  return resultado.tipo === 'articulo' ? resultado.articulo : null
}

/**
 * `buscarArticuloOffline` con el desenlace completo, espejo de `ServicioDeEscaneo.ResolverAsync` para un artículo por importe: el
 * prefijo es el monto (cantidad 1, `importe` en el resultado) y un prefijo que no es un importe válido se rechaza; sin `*` el
 * artículo vuelve sin importe y la pantalla lo pide. Para cualquier otro artículo el prefijo sigue siendo la cantidad.
 */
export function resolverEscaneoOffline(instantanea: InstantaneaDePos, entradaCruda: string): ResultadoDeEscaneoOffline {
  const entrada = parsearEntradaDeEscaneoOffline(entradaCruda)
  const separada = separarPrefijoYCodigo(entradaCruda)
  if (!entrada || !separada) return { tipo: 'noEncontrado' }

  const articulo =
    entrada.objetivo === 'CodigoInterno'
      ? instantanea.articulos.find((a) => a.codigoInterno === entrada.codigo)
      : instantanea.articulos.find((a) => a.codigosBarra.includes(entrada.codigo))

  if (!articulo) return { tipo: 'noEncontrado' }

  const codigoBarra = entrada.objetivo === 'CodigoBarra' ? entrada.codigo : null

  if (articulo.unidadVenta !== 'Importe') {
    return {
      tipo: 'articulo',
      articulo: {
        idArticulo: articulo.idArticulo,
        codigoInterno: articulo.codigoInterno,
        nombre: articulo.nombre,
        codigoBarra,
        cantidad: entrada.cantidad,
        acumulaEnVenta: articulo.acumulaEnVenta ?? true,
        unidadVenta: articulo.unidadVenta,
      },
    }
  }

  if (separada.prefijo === null) return { tipo: 'articulo', articulo: escaneadoPorImporte(articulo, codigoBarra) }

  const importe = leerImporteTipeado(separada.prefijo)
  return importe === null ? { tipo: 'importeInvalido' } : { tipo: 'articulo', articulo: escaneadoPorImporte(articulo, codigoBarra, importe) }
}

/** El artículo por importe como lo responde el escaneo del servidor: cantidad 1, sin acumular, con `importe` solo si se tipeó. */
function escaneadoPorImporte(
  articulo: Pick<ArticuloDeInstantanea, 'idArticulo' | 'codigoInterno' | 'nombre' | 'teclaAtajo'>,
  codigoBarra: string | null, importe?: number): ArticuloEscaneado {
  return {
    idArticulo: articulo.idArticulo,
    codigoInterno: articulo.codigoInterno,
    nombre: articulo.nombre,
    codigoBarra,
    cantidad: 1,
    acumulaEnVenta: false,
    unidadVenta: 'Importe',
    teclaAtajo: articulo.teclaAtajo ?? null,
    ...(importe !== undefined ? { importe } : {}),
  }
}

const ORDEN_DE_TECLAS: readonly string[] = ['F5', 'F6', 'F7', 'F8']

/** Los artículos por importe de la instantánea con tecla asignada, ordenados por tecla. Una tecla en un artículo que no es por
 * importe no cuenta: el servidor no la admite, y una instantánea inconsistente no la convierte en atajo. */
export function atajosDeImporteOffline(instantanea: InstantaneaDePos): AtajoDeImporte[] {
  return instantanea.articulos
    .filter((a): a is ArticuloDeInstantanea & { teclaAtajo: TeclaAtajo } => a.unidadVenta === 'Importe' && a.teclaAtajo != null)
    .map((a) => ({ teclaAtajo: a.teclaAtajo, idArticulo: a.idArticulo, codigoInterno: a.codigoInterno, nombre: a.nombre }))
    .sort((a, b) => ORDEN_DE_TECLAS.indexOf(a.teclaAtajo) - ORDEN_DE_TECLAS.indexOf(b.teclaAtajo))
}

/** Artículo por importe asignado a `tecla`, o `null` si ninguno la tiene. */
export function articuloDeAtajoOffline(instantanea: InstantaneaDePos, tecla: TeclaAtajo): AtajoDeImporte | null {
  return atajosDeImporteOffline(instantanea).find((a) => a.teclaAtajo === tecla) ?? null
}

export type ResultadoDeAtajoOffline = { tipo: 'sinArticulo' } | { tipo: 'importeInvalido' } | { tipo: 'articulo'; articulo: ArticuloEscaneado }

/** Lo que agrega apretar `tecla` con `textoImporte` en el campo de código: el mismo artículo que devolvería escribir
 * `importe*código`, pero tomado del registro de la tecla (no del parser de códigos, que decide por la longitud del código). */
export function resolverAtajoOffline(instantanea: InstantaneaDePos, tecla: TeclaAtajo, textoImporte: string): ResultadoDeAtajoOffline {
  const atajo = articuloDeAtajoOffline(instantanea, tecla)
  const articulo = atajo ? instantanea.articulos.find((a) => a.idArticulo === atajo.idArticulo) : undefined
  if (!articulo) return { tipo: 'sinArticulo' }

  const importe = leerImporteTipeado(textoImporte)
  return importe === null ? { tipo: 'importeInvalido' } : { tipo: 'articulo', articulo: escaneadoPorImporte(articulo, null, importe) }
}

/** Lo mismo con los atajos que sirve el servidor: el artículo sale del registro de la tecla, nunca de reinterpretar su código
 * como `importe*código` (un código de 7 o más caracteres se leería como código de barras). */
export function resolverAtajoEnLinea(atajo: AtajoDeImporte, textoImporte: string): ResultadoDeAtajoOffline {
  const importe = leerImporteTipeado(textoImporte)
  return importe === null ? { tipo: 'importeInvalido' } : { tipo: 'articulo', articulo: escaneadoPorImporte(atajo, null, importe) }
}

/** Precio del artículo en la lista pedida; `null` si el artículo no tiene precio en esa lista. */
export function precioEnLista(articulo: ArticuloDeInstantanea, idListaPrecio: number): PrecioDeListaDeInstantanea | null {
  return articulo.preciosPorLista.find((p) => p.idListaPrecio === idListaPrecio) ?? null
}

/**
 * Tramo de cantidad vigente para `cantidad`: el que tiene el `cantidadDesde` MÁS ALTO entre los
 * que cumplen `cantidadDesde <= cantidad`. `null` cuando el precio no trae tramos o cuando ninguno
 * califica todavía; en ambos casos rige el precio plano. Es la curva de UN artículo: describe cómo
 * cambia el precio cuando la cantidad de la familia crece junto con la del artículo.
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

/**
 * Celda de la grilla de familia vigente: entre las celdas con `cantidadDeFamiliaDesde <=
 * cantidadDeFamilia` toma las del umbral de familia MÁS ALTO y, dentro de ellas, la de
 * `cantidadDesde` más alto que entra en `cantidad`. Si `cantidad` no alcanza ni la menor (una
 * fracción por debajo de la base de la instantánea) toma la de menor `cantidadDesde`: es la
 * resolución base de ese umbral de familia, igual que el precio plano lo es para `elegirEscalon`.
 * `null` si el precio no trae grilla o la familia no llega a ningún umbral: ahí rige `elegirEscalon`,
 * que es exacto porque sin umbral de familia alcanzado ningún tramo de la curva viene de una oferta
 * de familia.
 */
export function elegirCeldaDeFamilia(
  precio: PrecioDeListaDeInstantanea,
  cantidad: number,
  cantidadDeFamilia: number,
): EscalonDeFamilia | null {
  const alcanzadas = (precio.escalonesDeFamilia ?? []).filter((c) => c.cantidadDeFamiliaDesde <= cantidadDeFamilia)
  if (alcanzadas.length === 0) return null

  const umbralDeFamilia = Math.max(...alcanzadas.map((c) => c.cantidadDeFamiliaDesde))
  const delUmbral = alcanzadas.filter((c) => c.cantidadDeFamiliaDesde === umbralDeFamilia)
  const quePueden = delUmbral.filter((c) => c.cantidadDesde <= cantidad)

  const propias = (quePueden.length > 0 ? quePueden : delUmbral).map((c) => c.cantidadDesde)
  const cantidadDesde = quePueden.length > 0 ? Math.max(...propias) : Math.min(...propias)
  return delUmbral.find((c) => c.cantidadDesde === cantidadDesde) ?? null
}

/** Los cuatro campos de precio que rigen para una línea — el escalón o la celda vigente pisa
 * `precioFinal`/`descuentoUnitario`/`aplicadas`, y `precioOriginal` queda SIEMPRE el de la lista
 * (ni el escalón ni la celda lo traen: es constante entre cantidades). */
export type PreciosVigentesOffline = Pick<PrecioDeListaDeInstantanea, 'precioOriginal' | 'precioFinal' | 'descuentoUnitario' | 'aplicadas'>

/**
 * Precio vigente de UNA línea de cantidad `cantidad` cuya familia suma `cantidadDeFamilia` en el
 * carrito (por omisión, la propia: sin otros artículos de la familia). Primero la grilla de
 * familia, si la familia llegó a un umbral; si no, la curva de siempre.
 */
export function preciosVigentesOffline(
  precio: PrecioDeListaDeInstantanea,
  cantidad: number,
  cantidadDeFamilia: number = cantidad,
): PreciosVigentesOffline {
  const vigente = elegirCeldaDeFamilia(precio, cantidad, cantidadDeFamilia) ?? elegirEscalon(precio, cantidad)
  if (!vigente) {
    const { precioOriginal, precioFinal, descuentoUnitario, aplicadas } = precio
    return { precioOriginal, precioFinal, descuentoUnitario, aplicadas }
  }
  return {
    precioOriginal: precio.precioOriginal,
    precioFinal: vigente.precioFinal,
    descuentoUnitario: vigente.descuentoUnitario,
    aplicadas: vigente.aplicadas,
  }
}

/** La lista `idListaPrecio` tal como la trae la instantánea, o `null` si falta (copia anterior al campo, lista dada de baja)
 * o viene malformada. Una derivada sin porcentaje numérico tampoco sirve para cotizar. */
function listaDeInstantanea(instantanea: InstantaneaDePos, idListaPrecio: number): ListaDeInstantanea | null {
  if (!Array.isArray(instantanea.listas)) return null
  const lista = instantanea.listas.find((l) => esObjeto(l) && l.idListaPrecio === idListaPrecio)
  if (!lista) return null
  if (lista.modo === 'Fija') return lista
  return lista.modo === 'Derivada' && typeof lista.porcentaje === 'number' && Number.isFinite(lista.porcentaje) ? lista : null
}

/**
 * Precio de una línea por importe en `idListaPrecio`, espejo de `ServicioDeOfertas.ResolverPorImporte`: con una lista derivada el
 * importe lleva su porcentaje (tipeado 1500 a -10 % es 1350, a 2 decimales y empate lejos de cero), con una fija queda igual.
 * `null` cuando la instantánea no conoce la lista: no se cotiza a ciegas el importe sin descuento. El factor `1 + porcentaje/100`
 * se arma como cociente de enteros según los decimales del porcentaje, así su representación más corta es el decimal exacto
 * y `productoRedondeado` multiplica sin ruido de punto flotante (`importe × 1,116666`, no `importe × 1,1166659999…`).
 */
function precioDeLineaPorImporte(importe: number, instantanea: InstantaneaDePos, idListaPrecio: number): number | null {
  const lista = listaDeInstantanea(instantanea, idListaPrecio)
  if (!lista) return null
  if (lista.modo === 'Fija' || lista.porcentaje === null) return importe
  const decimales = Math.min(String(lista.porcentaje).split('.')[1]?.length ?? 0, 8)
  const escala = 10 ** decimales
  return productoRedondeado([importe, (100 * escala + Math.round(lista.porcentaje * escala)) / (100 * escala)], 2)
}

function indicePorArticulo(instantanea: InstantaneaDePos): Map<number, ArticuloDeInstantanea> {
  return new Map(instantanea.articulos.map((a) => [a.idArticulo, a]))
}

/**
 * Precio vigente de cada línea del carrito en la lista pedida, en el orden de las líneas; `null`
 * donde el artículo no está en la instantánea o no tiene precio en esa lista.
 *
 * ÚNICO lugar donde se aplica la regla de precio offline — la consumen la vista previa del carrito
 * (`resolverPreciosOffline`), el payload que el servidor cobra literal
 * (`enriquecerLineasConPrecioOffline`) y el ticket sintético (`construirComprobanteOfflineSintetico`):
 * si divergieran, el cajero vería un importe y se cobraría otro. La cantidad de familia de cada
 * línea es la suma de las cantidades de TODAS las líneas cuyo artículo comparte su `idFamilia`
 * (una línea sin familia, o de un artículo ausente de la instantánea, solo cuenta la suya). Una línea por importe (`importe`) se
 * cotiza a ese importe con el porcentaje de la lista si es derivada (`precioDeLineaPorImporte`), sin oferta, y no suma a la cantidad de ninguna familia: el servidor tampoco la cuenta.
 */
export function preciosVigentesDeLasLineas(
  lineas: readonly { idArticulo: number; cantidad: number; importe?: number }[],
  instantanea: InstantaneaDePos,
  idListaPrecio: number,
): (PreciosVigentesOffline | null)[] {
  const porId = indicePorArticulo(instantanea)

  const cantidadPorFamilia = new Map<number, number>()
  for (const linea of lineas) {
    if (linea.importe !== undefined) continue
    const idFamilia = porId.get(linea.idArticulo)?.idFamilia
    if (idFamilia != null) cantidadPorFamilia.set(idFamilia, (cantidadPorFamilia.get(idFamilia) ?? 0) + linea.cantidad)
  }
  // El servidor suma en decimal: 0,1 + 0,7 llega a 0,8, y en punto flotante da 0.7999999999999999.
  // Redondear la suma a la precisión de las cantidades evita quedar apenas debajo de un umbral.
  for (const [idFamilia, cantidad] of cantidadPorFamilia) cantidadPorFamilia.set(idFamilia, redondearImporte(cantidad, DECIMALES_DE_PESO))

  return lineas.map((linea) => {
    if (linea.importe !== undefined) {
      const precio = precioDeLineaPorImporte(linea.importe, instantanea, idListaPrecio)
      return precio === null ? null : { precioOriginal: precio, precioFinal: precio, descuentoUnitario: 0, aplicadas: [] }
    }
    const articulo = porId.get(linea.idArticulo)
    const precio = articulo ? precioEnLista(articulo, idListaPrecio) : null
    if (!articulo || !precio) return null
    const cantidadDeFamilia = articulo.idFamilia != null ? (cantidadPorFamilia.get(articulo.idFamilia) ?? linea.cantidad) : linea.cantidad
    return preciosVigentesOffline(precio, linea.cantidad, cantidadDeFamilia)
  })
}

/** Precios de una línea con el combo que la consumió, si lo hay. */
export type PreciosDeLineaConCombo = PreciosVigentesOffline & { combo?: ComboDeLinea }

/** Los combos de la instantánea como candidatos del motor. */
function candidatosDeCombos(combos: readonly ComboDeInstantanea[]): ComboCandidato[] {
  return combos.map((c) => ({ id: c.idCombo, nombre: c.nombre, precio: c.precio, componentes: c.componentes, codigo: c.codigo }))
}

/**
 * Precio de cada línea del documento con los combos de la instantánea aplicados, en el orden de las líneas
 * (`null` donde `preciosVigentesDeLasLineas` no tiene precio), y los combos que se aplicaron.
 *
 * ÚNICO lugar donde se aplica la regla de combos offline — la consumen la vista previa del carrito, el
 * payload encolado y el ticket sintético, como `preciosVigentesDeLasLineas` con la regla de precio. Mismas
 * exclusiones que el servidor (`CombosEnVenta.Detectar`): quedan fuera de la detección las líneas sin precio
 * y las de descuento de oferta negativo. Tampoco entra una línea que trae `importe` (la venta por importe no
 * consume combos), sin mirar la unidad del artículo en la instantánea. La unidad de venta de cada artículo viaja
 * al motor, que decide con ella si la línea entra (`Importe` nunca) y qué cantidades valen (una unidad ausente,
 * la de una instantánea anterior al dato, cuenta como `Peso`).
 * Una instantánea sin `combos` devuelve los precios vigentes tal cual.
 *
 * El ahorro de una línea nunca lleva más de dos decimales (el servidor rechazaría la venta encolada con
 * `combo_offline_invalido`): el motor redondea a dos el importe de lista de lo que consume antes de repartir.
 */
export function resolverDocumentoOffline(
  lineas: readonly { idArticulo: number; cantidad: number; importe?: number }[],
  instantanea: InstantaneaDePos,
  idListaPrecio: number,
): { lineas: (PreciosDeLineaConCombo | null)[]; combos: ComboAplicadoEnPrevia[] } {
  const vigentes = preciosVigentesDeLasLineas(lineas, instantanea, idListaPrecio)
  const candidatos = candidatosDeCombos(instantanea.combos ?? [])
  if (candidatos.length === 0) return { lineas: vigentes, combos: [] }

  const porId = indicePorArticulo(instantanea)
  const entrada: LineaParaCombos[] = []
  lineas.forEach((linea, i) => {
    const precios = vigentes[i]
    const articulo = porId.get(linea.idArticulo)
    if (!precios || !articulo || linea.importe !== undefined || precios.descuentoUnitario < 0) return
    entrada.push({
      indice: i,
      idArticulo: linea.idArticulo,
      idFamilia: articulo.idFamilia ?? null,
      cantidad: linea.cantidad,
      precioLista: precios.precioOriginal,
      descuentoUnitarioDeOferta: precios.descuentoUnitario,
      unidadVenta: articulo.unidadVenta,
    })
  })

  const resolucion = resolverCombos(entrada, candidatos)
  const conCombo: (PreciosDeLineaConCombo | null)[] = [...vigentes]
  for (const consumo of resolucion.lineas) {
    const precios = vigentes[consumo.indice]
    if (precios) {
      conCombo[consumo.indice] = {
        ...precios,
        combo: { unidadesConsumidas: consumo.unidadesConsumidas, ahorro: consumo.ahorroDeCombo, idCombo: Math.min(...consumo.idsDeCombo) },
      }
    }
  }
  return {
    lineas: conCombo,
    combos: resolucion.combos.map((c) => ({ idCombo: c.idCombo, codigo: c.codigo ?? 0, nombre: c.nombre, veces: c.veces, ahorro: c.ahorro })),
  }
}

/**
 * Resuelve el precio de cada línea del carrito contra la instantánea local, en la lista del
 * cliente de la venta — mismo shape de salida que `indexarResolucionPorLinea` (indexado por
 * `idLinea`), así que `previaDeLinea`/`calcularSubtotalPrevia` siguen funcionando sin cambios
 * vengan los precios de la red o de la instantánea. Una línea cuyo artículo no está en la
 * instantánea o no tiene precio en esa lista queda ausente del índice — el mismo tratamiento que
 * un artículo sin precio en la resolución online. Las líneas que un combo consumió traen `combo`
 * y `combos` lista los aplicados, como `POST /api/ofertas/resolver-con-combos`.
 *
 * La cantidad de cada línea y la de su familia en el carrito eligen el precio vigente
 * (`preciosVigentesDeLasLineas`); los combos se aplican después (`resolverDocumentoOffline`).
 */
export function resolverPreviaOffline(
  lineas: LineaCarrito[],
  instantanea: InstantaneaDePos,
  idListaPrecio: number,
): { precios: PreciosPorLinea; combos: ComboAplicadoEnPrevia[] } {
  const resolucion = resolverDocumentoOffline(lineas, instantanea, idListaPrecio)
  const precios: PreciosPorLinea = {}
  lineas.forEach((linea, i) => {
    const resuelto = resolucion.lineas[i]
    if (resuelto) precios[linea.idLinea] = { idArticulo: linea.idArticulo, idListaPrecio, ...resuelto }
  })
  return { precios, combos: resolucion.combos }
}

/** Solo los precios de `resolverPreviaOffline`, para quien no muestra los combos aplicados. */
export function resolverPreciosOffline(lineas: LineaCarrito[], instantanea: InstantaneaDePos, idListaPrecio: number): PreciosPorLinea {
  return resolverPreviaOffline(lineas, instantanea, idListaPrecio).precios
}

/** `true` si TODAS las líneas tienen precio en la lista pedida dentro de la instantánea — la
 * precondición de la vista previa local y del checkout offline (todas con precio, o directamente
 * ir por la red en vez de mostrar o vender media venta a precio inventado); una línea por importe necesita que la instantánea conozca la lista del cliente (para
 * aplicarle su porcentaje; una copia sin `listas` no sirve). ÚNICO gate para esa
 * precondición. Tipo de línea mínimo (`{ idArticulo }`) para validar tanto el carrito en pantalla
 * como un `LineaDeVenta[]` ya armado. */
export function todasLasLineasTienenPrecioOffline(
  lineas: readonly { idArticulo: number; importe?: number }[],
  instantanea: InstantaneaDePos,
  idListaPrecio: number,
): boolean {
  const porId = indicePorArticulo(instantanea)
  return (
    lineas.length > 0 &&
    lineas.every((l) => {
      const articulo = porId.get(l.idArticulo)
      if (articulo === undefined) return false
      // La línea por importe se cotiza con la lista del cliente: sin ella no se cobra un importe sin el porcentaje que el servidor aplicaría.
      if (l.importe !== undefined) return precioDeLineaPorImporte(l.importe, instantanea, idListaPrecio) !== null
      return precioEnLista(articulo, idListaPrecio) !== null
    })
  )
}

/** Líneas que llevan un importe pero cuyo artículo la instantánea conoce hoy como NO por importe (un administrador lo cambió
 * después de cargar la línea): el servidor rechazaría la venta con `importe_no_admitido`. Un artículo que la instantánea no
 * conoce no se señala: no hay con qué contradecir la línea. */
export function lineasConImporteDeArticuloOrdinario(lineas: readonly LineaCarrito[], instantanea: InstantaneaDePos): LineaCarrito[] {
  const porId = indicePorArticulo(instantanea)
  return lineas.filter((l) => {
    if (l.importe === undefined) return false
    const articulo = porId.get(l.idArticulo)
    return articulo !== undefined && articulo.unidadVenta !== 'Importe'
  })
}

// --- Clientes y medios de pago de la instantánea -------------------------------------------------

/** Mismo tope que la primera página de `GET /api/clientes` (`ServicioDeClientes.ListarAsync`). */
export const TOPE_DE_RESULTADOS_DE_CLIENTES = 25

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
  const buscado = normalizarParaBuscar(termino).trim()
  const digitos = soloDigitos(termino)
  const coincide = (c: ClienteDeInstantanea) => {
    if (buscado === '') return true
    const textos = [c.nombre, c.apellido, c.razonSocial, [c.nombre, c.apellido].filter(Boolean).join(' '), c.numeroDocumento]
    if (textos.some((t) => t != null && normalizarParaBuscar(t).includes(buscado))) return true
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
    porcentajeFacturacion: m.porcentajeFacturacion ?? 0,
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
export type { ArticuloDeInstantanea, EscalonDeCantidad, EscalonDeFamilia, OfertaAplicada, PrecioDeListaDeInstantanea }

/** Época de la sesión local: cada purga la avanza. Una sincronización captura la época al empezar y
 * no persiste nada si cambió mientras tanto (la sesión terminó con la respuesta en vuelo). */
let epocaDeSesionLocal = 0

export function epocaDeSesionLocalActual(): number {
  return epocaDeSesionLocal
}

/** Borra la instantánea guardada (y la de la forma anterior, que nadie más lee) y avanza la época
 * antes de borrar, para que ninguna respuesta en vuelo la vuelva a escribir. Nunca rechaza. */
export async function purgarInstantaneaLocal(almacen: Pick<AlmacenDeClavesMultiples, 'eliminar'>): Promise<void> {
  epocaDeSesionLocal += 1
  await Promise.all([almacen.eliminar(CLAVE_INSTANTANEA), almacen.eliminar(CLAVE_INSTANTANEA_ANTERIOR)])
}
