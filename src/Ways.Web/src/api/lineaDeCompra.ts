/**
 * Línea del formulario de compras tal como la ve la grilla (Código, Detalle, UM, Cantidad, Importe,
 * Descuento %, IVA) y su traducción a lo que la API guarda (`unidades`/`bultos`/`unidadesPorBulto`/
 * `costoUnitario`/`descuento` en monto). Todo puro y sin DOM: `valoresParaApi` es la única fuente
 * que comparten lo que se envía (`aLineaSolicitada`) y lo que se calcula en pantalla
 * (`lineaFormularioACalculo`), así ninguno puede divergir del otro. La cuenta usa aritmética
 * decimal exacta (`productoRedondeado`), como el servidor.
 */
import { productoRedondeado, redondearImporte } from '../formato/importes'
import { respetaGranularidad } from './cantidadPorUnidad'
import type {
  AlicuotaIvaListado,
  ArticuloListado,
  CoberturaDeArticulo,
  ItemDeCompra,
  ItemDeOrden,
  LineaDeCompraSolicitada,
  UnidadVenta,
} from './tipos'
import { parsearNumeroDecimal } from '../formato/numeros'

export type UnidadDeMedidaDeLinea = 'Unidad' | 'Bulto'

/** Alícuota que se ofrece cuando un artículo no trae una válida. */
export const PORCENTAJE_DE_IVA_POR_DEFECTO = 21

type VistaDeLinea = {
  cantidad: string
  unidadDeMedida: UnidadDeMedidaDeLinea
  importe: number | null
  descuentoPorcentaje: number | null
}

/**
 * Lo que la API devolvió para una línea ya persistida, junto con lo que la grilla mostró de ella al
 * cargarla. Mientras el operador no toque ninguno de esos cuatro valores visibles, la línea vuelve
 * a viajar con los números guardados: el porcentaje y el precio por bulto que se muestran son una
 * derivación redondeada y reconstruir el monto desde ellos podría correr centavos.
 */
export type LineaGuardada = {
  unidades: number
  bultos: number | null
  unidadesPorBulto: number | null
  costoUnitario: number
  descuento: number
  vista: VistaDeLinea
}

export type LineaDeCompraFormulario = {
  /** Clave solo de React — el `PUT` es un replace-set completo (design decisión 2), ningún id de
   * línea persistido viaja en el request ni sobrevive entre saves. */
  clave: number
  /** `''` = sin artículo: la línea es un concepto (importe libre con descripción, sin stock, costo
   * ni lote). */
  idArticulo: number | ''
  /** Detalle: nombre del artículo elegido o descripción libre del concepto. */
  descripcion: string
  /** Código que el proveedor imprimió para la línea; `''` = sin código. Viaja en la línea aunque no
   * se haya podido asociar: el servidor lo asocia al confirmar. */
  codigoProveedor: string
  /** El artículo salió de buscar `codigoProveedor` entre los códigos del proveedor: el Detalle queda
   * de solo lectura hasta que se edite el código. */
  resueltaPorCodigo: boolean
  /** Último código que se consultó (o que ya venía guardado): evita volver a consultar al pasar por
   * la celda sin cambiarlo. */
  codigoConsultado: string
  unidadDeMedida: UnidadDeMedidaDeLinea
  /** Con UM `Unidad` son unidades; con `Bulto`, la cantidad de bultos. */
  cantidad: string
  /** Precio por UM: por unidad o por bulto. `null` = vacío. */
  importe: number | null
  /** Porcentaje del bruto de la línea (0–100); `null` = sin descuento. */
  descuentoPorcentaje: number | null
  idAlicuotaIva: number | ''
  /** Capturado del artículo elegido: decide si `fechaVencimiento` entra a la validación. Al reabrir
   * un borrador se infiere de si la línea YA trae dato de lote (`itemAFormulario`): una línea
   * lote-efectiva todavía no tocada no se detecta hasta que el operador la edite — el servidor
   * sigue siendo la autoridad final al confirmar. */
  controlaLote: boolean
  codigoLote: string
  fechaVencimiento: string
  /** Unidad de venta del artículo elegido (`cantidadPorUnidad.ts`): decide el paso de `cantidad`
   * con UM `Unidad`. Ausente se trata como `Peso` (permisivo). */
  unidadVenta?: UnidadVenta
  /** Unidades por bulto del artículo (o de la línea guardada): habilita la UM `Bulto`. */
  unidadesPorBulto: number | null
  /** El `actualizaCosto` que tenía la línea guardada: se reenvía mientras la fila conserve el mismo
   * artículo. Un artículo elegido, creado o resuelto por código en la grilla lo deja en `true`. Un
   * concepto no lo usa: el servidor exige `false`. */
  actualizaCosto: boolean
  guardada: LineaGuardada | null
}

export function lineaDeCompraVacia(clave: number): LineaDeCompraFormulario {
  return {
    clave,
    idArticulo: '',
    descripcion: '',
    codigoProveedor: '',
    resueltaPorCodigo: false,
    codigoConsultado: '',
    unidadDeMedida: 'Unidad',
    cantidad: '',
    importe: null,
    descuentoPorcentaje: null,
    idAlicuotaIva: '',
    controlaLote: false,
    codigoLote: '',
    fechaVencimiento: '',
    unidadesPorBulto: null,
    actualizaCosto: true,
    guardada: null,
  }
}

export function tipoDeLinea(l: LineaDeCompraFormulario): 'articulo' | 'concepto' {
  return l.idArticulo === '' ? 'concepto' : 'articulo'
}

/** Una fila sin ningún dato cargado se ignora al guardar y es la que cierra la grilla. */
export function lineaTieneDatos(l: LineaDeCompraFormulario): boolean {
  return (
    l.codigoProveedor.trim() !== '' ||
    l.descripcion.trim() !== '' ||
    l.idArticulo !== '' ||
    l.cantidad.trim() !== '' ||
    l.importe !== null ||
    l.descuentoPorcentaje !== null ||
    l.idAlicuotaIva !== ''
  )
}

/** Mantiene exactamente una fila vacía al final: conserva las filas hasta la última con datos y
 * deja de las vacías finales solo la primera (así no se desmonta la que tiene el foco), o suma una
 * nueva con `claveNueva` si no había ninguna. */
export function normalizarFilas(lineas: LineaDeCompraFormulario[], claveNueva: number): LineaDeCompraFormulario[] {
  let fin = lineas.length
  while (fin > 0 && !lineaTieneDatos(lineas[fin - 1])) fin -= 1
  return [...lineas.slice(0, fin), lineas[fin] ?? lineaDeCompraVacia(claveNueva)]
}

function numero(valor: string): number {
  return parsearNumeroDecimal(valor, { permitirNegativo: true }) ?? 0
}

/** Decimales que admite la columna `bultos` (`numeric(10,2)`): con más, la base redondea en silencio
 * y la cantidad registrada deja de coincidir con bultos × unidades por bulto. */
const DECIMALES_DE_BULTOS = 2

export const MENSAJE_DE_BULTOS_CON_DECIMALES_DE_MAS = 'Los bultos admiten hasta 2 decimales.'

export type CampoDeLinea = 'detalle' | 'cantidad' | 'importe' | 'descuento' | 'iva' | 'vencimiento'

function tieneBulto(l: LineaDeCompraFormulario): boolean {
  return l.unidadesPorBulto !== null && l.unidadesPorBulto > 0
}

/** Qué tiene mal la cantidad de bultos de una fila con UM `Bulto` (`null` = nada): más decimales de
 * los que guarda la base. */
export function mensajeDeCantidadDeBultos(l: LineaDeCompraFormulario): string | null {
  if (tipoDeLinea(l) !== 'articulo' || l.unidadDeMedida !== 'Bulto' || !tieneBulto(l)) return null
  const bultos = parsearNumeroDecimal(l.cantidad)
  if (bultos === null || !(bultos > 0)) return null

  const escalada = bultos * 10 ** DECIMALES_DE_BULTOS
  return Math.abs(escalada - Math.round(escalada)) >= 1e-6 ? MENSAJE_DE_BULTOS_CON_DECIMALES_DE_MAS : null
}

/** Qué le falta o tiene mal una fila (vacía si está completa). Una fila sin datos tampoco devuelve
 * nada: no se guarda y no es un error. Espejo de las validaciones del servidor, que sigue siendo la
 * autoridad (`lote_requerido` exige el vencimiento; el código del lote se deriva si se omite). */
export function camposInvalidosDeLinea(l: LineaDeCompraFormulario): CampoDeLinea[] {
  if (!lineaTieneDatos(l)) return []

  const invalidos: CampoDeLinea[] = []
  const esConcepto = tipoDeLinea(l) === 'concepto'
  const esBulto = !esConcepto && l.unidadDeMedida === 'Bulto'

  if (esConcepto && l.descripcion.trim() === '') invalidos.push('detalle')

  const cantidad = parsearNumeroDecimal(l.cantidad) ?? Number.NaN
  const granularidad = esBulto
    ? mensajeDeCantidadDeBultos(l) === null
    : respetaGranularidad(esConcepto ? undefined : l.unidadVenta, cantidad)
  if (!(cantidad > 0) || !granularidad || (esBulto && !tieneBulto(l))) invalidos.push('cantidad')

  if (l.importe === null) invalidos.push('importe')
  if (l.descuentoPorcentaje !== null && (l.descuentoPorcentaje < 0 || l.descuentoPorcentaje > 100)) invalidos.push('descuento')
  if (l.idAlicuotaIva === '') invalidos.push('iva')
  if (!esConcepto && l.controlaLote && l.fechaVencimiento.trim() === '') invalidos.push('vencimiento')

  return invalidos
}

export function lineaCompletaParaEnvio(l: LineaDeCompraFormulario): boolean {
  return lineaTieneDatos(l) && camposInvalidosDeLinea(l).length === 0
}

// ---- Cuenta exacta de la línea -----------------------------------------------------------------

/** `unidades + bultos × unidadesPorBulto`, a 3 decimales (espejo de `CalculadorDeCompra`). */
export function cantidadDeLinea(unidades: number, bultos: number | null, unidadesPorBulto: number | null): number {
  return redondearImporte(unidades + productoRedondeado([bultos ?? 0, unidadesPorBulto ?? 0], 3), 3)
}

/** Bruto de la línea (`cantidad × costoUnitario`, a 2 decimales): la base del descuento. */
export function brutoDeLinea(
  unidades: number,
  bultos: number | null,
  unidadesPorBulto: number | null,
  costoUnitario: number,
): number {
  return productoRedondeado([cantidadDeLinea(unidades, bultos, unidadesPorBulto), costoUnitario], 2)
}

/** Monto del descuento: `bruto × porcentaje / 100` redondeado half-away-from-zero a 2 decimales. */
export function descuentoDesdePorcentaje(bruto: number, porcentaje: number | null): number {
  return porcentaje === null ? 0 : productoRedondeado([bruto, porcentaje, 0.01], 2)
}

/** Porcentaje que representa un descuento guardado sobre su bruto, a 2 decimales para mostrarlo;
 * `null` si no hay descuento o no hay bruto sobre el cual expresarlo. */
export function porcentajeDeDescuento(descuento: number | null, bruto: number): number | null {
  if (descuento === null || descuento === 0 || bruto <= 0) return null
  return redondearImporte((descuento * 100) / bruto, 2)
}

export type ValoresDeApi = {
  unidades: number
  bultos: number | null
  unidadesPorBulto: number | null
  costoUnitario: number
  descuento: number
}

function vistaDe(l: LineaDeCompraFormulario): VistaDeLinea {
  return {
    cantidad: l.cantidad,
    unidadDeMedida: l.unidadDeMedida,
    importe: l.importe,
    descuentoPorcentaje: l.descuentoPorcentaje,
  }
}

function mismaVista(a: VistaDeLinea, b: VistaDeLinea): boolean {
  return (
    a.cantidad === b.cantidad &&
    a.unidadDeMedida === b.unidadDeMedida &&
    a.importe === b.importe &&
    a.descuentoPorcentaje === b.descuentoPorcentaje
  )
}

/**
 * Lo que la API recibe de la fila. Con UM `Bulto` la cantidad son bultos y el importe es el precio
 * por bulto: viaja `bultos = cantidad`, `unidadesPorBulto` del artículo, `unidades = 0` y
 * `costoUnitario = importe / unidadesPorBulto` a 4 decimales (la precisión de la columna). El
 * descuento se arma sobre el MISMO bruto que va a calcular el servidor, así nunca lo supera.
 * Una línea guardada que no se tocó devuelve exactamente lo guardado.
 */
export function valoresParaApi(l: LineaDeCompraFormulario): ValoresDeApi {
  if (l.guardada !== null && mismaVista(l.guardada.vista, vistaDe(l))) {
    const { unidades, bultos, unidadesPorBulto, costoUnitario, descuento } = l.guardada
    return { unidades, bultos, unidadesPorBulto, costoUnitario, descuento }
  }

  const cantidad = numero(l.cantidad)
  const importe = l.importe ?? 0
  const porBulto = tipoDeLinea(l) === 'articulo' && l.unidadDeMedida === 'Bulto' && tieneBulto(l)
  const unidadesPorBulto = porBulto ? l.unidadesPorBulto : null
  const unidades = porBulto ? 0 : cantidad
  const bultos = porBulto ? cantidad : null
  const costoUnitario = porBulto ? redondearImporte(importe / (unidadesPorBulto as number), 4) : importe
  const bruto = brutoDeLinea(unidades, bultos, unidadesPorBulto, costoUnitario)

  return {
    unidades,
    bultos,
    unidadesPorBulto,
    costoUnitario,
    descuento: descuentoDesdePorcentaje(bruto, l.descuentoPorcentaje),
  }
}

export type DiferenciaDePrecioPorBulto = {
  /** Bruto que se va a registrar: el que calcula el servidor con el costo por unidad enviado. */
  registrado: number
  /** `cantidad × importe` menos lo registrado: positivo = se registra de menos. */
  diferencia: number
  unidadesPorBulto: number
}

/**
 * Con UM `Bulto` el precio por bulto se divide entre las unidades del bulto y se redondea a los 4
 * decimales de la columna, así que el servidor puede registrar un bruto distinto de
 * `bultos × precio por bulto` (1000 bultos de 3 unidades a $ 100,00 dan $ 99.999,90). Devuelve esa
 * diferencia, comparada con aritmética decimal exacta, o `null` si no hay ninguna (o la fila no
 * viaja por bultos).
 */
export function diferenciaDePrecioPorBulto(l: LineaDeCompraFormulario): DiferenciaDePrecioPorBulto | null {
  if (tipoDeLinea(l) !== 'articulo' || l.unidadDeMedida !== 'Bulto' || !tieneBulto(l)) return null
  const v = valoresParaApi(l)
  if (v.bultos === null || v.unidadesPorBulto === null) return null

  const esperado = productoRedondeado([numero(l.cantidad), l.importe ?? 0], 2)
  const registrado = brutoDeLinea(v.unidades, v.bultos, v.unidadesPorBulto, v.costoUnitario)
  const diferencia = redondearImporte(esperado - registrado, 2)
  return diferencia === 0 ? null : { registrado, diferencia, unidadesPorBulto: v.unidadesPorBulto }
}

function codigoProveedorONulo(l: LineaDeCompraFormulario): string | null {
  const codigo = l.codigoProveedor.trim()
  return codigo === '' ? null : codigo
}

/** Fila de formulario → `LineaDeCompraSolicitada`. Un artículo reenvía su `actualizaCosto` (`true`
 * si lo eligió la grilla); un concepto nunca lo actualiza —el servidor lo exige— y no lleva bultos
 * ni lote. */
export function aLineaSolicitada(l: LineaDeCompraFormulario): LineaDeCompraSolicitada {
  const v = valoresParaApi(l)

  if (tipoDeLinea(l) === 'concepto') {
    return {
      idArticulo: null,
      descripcion: l.descripcion.trim(),
      unidades: v.unidades,
      bultos: null,
      unidadesPorBulto: null,
      costoUnitario: v.costoUnitario,
      descuento: v.descuento,
      idAlicuotaIva: Number(l.idAlicuotaIva),
      actualizaCosto: false,
      codigoLote: null,
      fechaVencimiento: null,
      codigoProveedor: codigoProveedorONulo(l),
    }
  }

  return {
    idArticulo: Number(l.idArticulo),
    descripcion: l.descripcion.trim(),
    unidades: v.unidades,
    bultos: v.bultos,
    unidadesPorBulto: v.unidadesPorBulto,
    costoUnitario: v.costoUnitario,
    descuento: v.descuento,
    idAlicuotaIva: Number(l.idAlicuotaIva),
    actualizaCosto: l.actualizaCosto,
    codigoLote: l.codigoLote.trim() === '' ? null : l.codigoLote.trim(),
    fechaVencimiento: l.fechaVencimiento === '' ? null : l.fechaVencimiento,
    codigoProveedor: codigoProveedorONulo(l),
  }
}

// ---- Líneas persistidas y precargas -------------------------------------------------------------

/** Un item ya persistido → fila de la grilla. Con bultos y sin unidades sueltas se muestra como UM
 * `Bulto` con el precio por bulto; una línea que mezcla unidades sueltas y bultos (posible desde el
 * editor anterior) se muestra como UM `Unidad` con la cantidad total y, mientras no se toque, se
 * reenvía tal cual está guardada. */
export function itemAFormulario(clave: number, item: ItemDeCompra): LineaDeCompraFormulario {
  const { bultos, unidadesPorBulto, costoUnitario, descuento } = item
  const hayBultos = bultos !== null && bultos > 0 && unidadesPorBulto !== null && unidadesPorBulto > 0
  const unidadesSueltas = hayBultos
    ? redondearImporte(item.cantidad - productoRedondeado([bultos as number, unidadesPorBulto as number], 3), 3)
    : item.cantidad
  const soloBultos = hayBultos && unidadesSueltas === 0

  const vista: VistaDeLinea = {
    cantidad: String(soloBultos ? bultos : item.cantidad),
    unidadDeMedida: soloBultos ? 'Bulto' : 'Unidad',
    importe:
      costoUnitario === null
        ? null
        : soloBultos
          ? productoRedondeado([costoUnitario, unidadesPorBulto as number], 4)
          : costoUnitario,
    descuentoPorcentaje:
      costoUnitario === null
        ? null
        : porcentajeDeDescuento(descuento, brutoDeLinea(unidadesSueltas, bultos, unidadesPorBulto, costoUnitario)),
  }

  return {
    ...lineaDeCompraVacia(clave),
    idArticulo: item.idArticulo ?? '',
    descripcion: item.descripcion,
    codigoProveedor: item.codigoProveedor ?? '',
    codigoConsultado: item.codigoProveedor ?? '',
    ...vista,
    idAlicuotaIva: item.idAlicuotaIva,
    // Heurística documentada arriba (`LineaDeCompraFormulario.controlaLote`): un dato de lote ya
    // persistido prueba que el artículo controla lote; su ausencia no prueba lo contrario.
    controlaLote: item.idLote !== null || item.codigoLote !== null || item.fechaVencimiento !== null,
    codigoLote: item.codigoLote ?? '',
    fechaVencimiento: item.fechaVencimiento ?? '',
    unidadesPorBulto: unidadesPorBulto !== null && unidadesPorBulto > 0 ? unidadesPorBulto : null,
    actualizaCosto: item.actualizaCosto,
    guardada:
      costoUnitario === null || descuento === null
        ? null
        : { unidades: unidadesSueltas, bultos, unidadesPorBulto, costoUnitario, descuento, vista },
  }
}

/** stage-16-ordenes-de-compra, Slice 6: una fila de `CoberturaDeArticulo` (`Pendiente > 0`) de una
 * orden de compra → una fila de la grilla de recepción. La cantidad arranca en el pendiente; el
 * importe, en el `costoEstimado` de la OC (vacío — nunca 0 — cuando nunca se cotizó, para que el
 * operador lo confirme). La descripción sale de la línea original de la orden, nunca inventada. */
export function lineaDesdeCoberturaDeOrden(
  clave: number,
  cobertura: CoberturaDeArticulo,
  itemsDeOrden: ItemDeOrden[],
): LineaDeCompraFormulario {
  const itemOriginal = itemsDeOrden.find((i) => i.idArticulo === cobertura.idArticulo)
  return {
    ...lineaDeCompraVacia(clave),
    idArticulo: cobertura.idArticulo,
    descripcion: itemOriginal?.descripcion ?? `Artículo #${cobertura.idArticulo}`,
    cantidad: String(cobertura.pendiente),
    importe: cobertura.costoEstimado,
  }
}

// ---- Precarga desde un artículo -----------------------------------------------------------------

export type OrigenDeArticulo = 'codigo' | 'busqueda' | 'alta'

/** La alícuota del artículo si el catálogo la conoce; si no, la del 21 %. */
export function alicuotaParaArticulo(articulo: Pick<ArticuloListado, 'idAlicuotaIva'>, alicuotas: AlicuotaIvaListado[]): number | '' {
  if (alicuotas.some((a) => a.id === articulo.idAlicuotaIva)) return articulo.idAlicuotaIva
  return alicuotas.find((a) => a.porcentaje === PORCENTAJE_DE_IVA_POR_DEFECTO)?.id ?? ''
}

/** Cómo se cargan los precios del comprobante que se está armando. */
export type ModoDePreciosDeCompra = { discriminaIva: boolean; preciosIncluyenIva: boolean }

/**
 * Recibe un costo por unidad y con IVA incluido (`costoDeArticuloParaCompra` lleva a esa base el costo
 * de lista, que se guarda tal como se cargó, y el nominal ya viene así; docs/10, compras). Un
 * comprobante que discrimina IVA con precios netos pide el Importe sin IVA: se lo extrae con la
 * alícuota de la fila, a los 4 decimales de la columna. En los demás modos
 * (precios con IVA incluido, o un comprobante que no discrimina) el importe es el costo tal cual.
 * Solo se aplica al precargar: cambiar el modo después no reescribe lo que ya está en las filas.
 */
export function importeSegunModoDePrecios(
  costoConIva: number,
  porcentajeIva: number | null,
  modo: ModoDePreciosDeCompra | undefined,
): number {
  if (!modo || !modo.discriminaIva || modo.preciosIncluyenIva || porcentajeIva === null) return costoConIva
  return redondearImporte(productoRedondeado([costoConIva, 100], 6) / (100 + porcentajeIva), 4)
}

type ArticuloConCosto = Pick<ArticuloListado, 'costoLista' | 'descuentoProveedor' | 'costoNominal'> &
  Partial<Pick<ArticuloListado, 'costoListaPor' | 'costoListaIncluyeIva' | 'unidadesPorBulto'>>

/** Alícuotas con las que se normaliza el costo: la del artículo (la que usa el servidor para llevar un
 * costo sin IVA a su nominal) y la que le queda a la fila (la que decide el neteo del comprobante). */
export type AlicuotasDePrecarga = { delArticulo: number | null; deLaFila: number | null }

/**
 * Costo con el que se precarga la fila: el de lista llevado a POR UNIDAD y CON IVA —espejo de
 * `CalculadorDeCostoNominal.Calcular` hasta antes del descuento, que la grilla muestra en su propia
 * columna— con el descuento habitual del proveedor (un PORCENTAJE, `descuento_proveedor numeric(5,2)`);
 * luego se aplica el modo de precios del comprobante. El costo de lista se guarda tal como se cargó:
 * por bulto se divide entre `unidadesPorBulto`; sin IVA se le suma el de la alícuota del artículo. Sin
 * indicadores (payload viejo) se asume por unidad y con IVA, los defaults del servidor.
 *
 * Si el comprobante pide el importe neto y el costo se guardó sin IVA con la misma alícuota, se
 * precarga el costo guardado tal cual, sin sumar y volver a quitar el IVA (evita el redondeo de ida y
 * vuelta). Si la lista no se puede usar (bulto sin unidades por bulto, o sin IVA y sin alícuota
 * conocida) o el artículo no tiene lista, se usa el costo nominal (el de reposición, que ya es neto de
 * descuento y viene por unidad con IVA). `null` en ambos = el rol no ve costos o no hay ninguno.
 */
export function costoDeArticuloParaCompra(
  articulo: ArticuloConCosto,
  alicuotas: AlicuotasDePrecarga = { delArticulo: null, deLaFila: null },
  modo?: ModoDePreciosDeCompra,
): { importe: number; descuentoPorcentaje: number | null } | null {
  const lista = articulo.costoLista
  const porBulto = articulo.costoListaPor === 'Bulto'
  const incluyeIva = articulo.costoListaIncluyeIva ?? true
  const { delArticulo, deLaFila } = alicuotas
  const unidades = articulo.unidadesPorBulto ?? null
  const utilizable = lista !== null && (!porBulto || (unidades !== null && unidades > 0)) && (incluyeIva || delArticulo !== null)

  if (utilizable) {
    const porUnidad = porBulto ? redondearImporte(lista / (unidades as number), 6) : lista
    const descuentoPorcentaje = articulo.descuentoProveedor
    const netea = modo !== undefined && modo.discriminaIva && !modo.preciosIncluyenIva && deLaFila !== null

    if (incluyeIva) return { importe: redondearImporte(importeSegunModoDePrecios(porUnidad, deLaFila, modo), 4), descuentoPorcentaje }
    if (!netea || deLaFila === delArticulo) {
      const importe = netea ? porUnidad : productoRedondeado([porUnidad, 100 + (delArticulo as number), 0.01], 6)
      return { importe: redondearImporte(importe, 4), descuentoPorcentaje }
    }
    const importe = redondearImporte(productoRedondeado([porUnidad, 100 + (delArticulo as number)], 6) / (100 + deLaFila), 4)
    return { importe, descuentoPorcentaje }
  }

  if (articulo.costoNominal !== null) {
    return { importe: redondearImporte(importeSegunModoDePrecios(articulo.costoNominal, deLaFila, modo), 4), descuentoPorcentaje: null }
  }
  return null
}

/**
 * Cambios que deja elegir (o crear) un artículo en una fila: Detalle con su nombre, UM `Unidad`,
 * importe y descuento del artículo, su alícuota y los datos que habilitan bultos y lote. La
 * cantidad no se toca. Cambiar de artículo invalida el lote ya cargado (era del anterior) y la
 * línea guardada, y el `actualizaCosto` vuelve a ser `true` (elegir el mismo artículo que la fila ya
 * tiene lo conserva). Elegir buscando deja el código sin consultar (queda por asociar al confirmar, sin importar si antes no se encontró). Un artículo recién creado desde la
 * fila (`alta`) conserva el importe y el descuento tipeados, pero toma la alícuota con la que se creó. Sin costo visible (rol sin permiso) el importe
 * tipeado se respeta. El importe se precarga según el modo de precios (`importeSegunModoDePrecios`).
 */
export function cambiosAlElegirArticulo(
  l: LineaDeCompraFormulario,
  articulo: ArticuloListado,
  alicuotas: AlicuotaIvaListado[],
  origen: OrigenDeArticulo,
  modo?: ModoDePreciosDeCompra,
  conservarIva = false,
): Partial<LineaDeCompraFormulario> {
  const conservarLoTipeado = origen === 'alta'
  const idAlicuotaIva =
    conservarIva ? l.idAlicuotaIva : alicuotaParaArticulo(articulo, alicuotas)
  const porcentajeIva = alicuotas.find((a) => a.id === idAlicuotaIva)?.porcentaje ?? null
  const porcentajeDelArticulo = alicuotas.find((a) => a.id === articulo.idAlicuotaIva)?.porcentaje ?? null
  const costo = costoDeArticuloParaCompra(articulo, { delArticulo: porcentajeDelArticulo, deLaFila: porcentajeIva }, modo)
  const importe = conservarLoTipeado ? (l.importe ?? costo?.importe ?? null) : (costo?.importe ?? l.importe)
  const descuentoPorcentaje = conservarLoTipeado || costo === null ? l.descuentoPorcentaje : costo.descuentoPorcentaje

  return {
    idArticulo: articulo.id,
    descripcion: articulo.nombre,
    resueltaPorCodigo: origen === 'codigo',
    controlaLote: articulo.controlaLote,
    unidadVenta: articulo.unidadVenta,
    unidadesPorBulto: articulo.unidadesPorBulto !== null && articulo.unidadesPorBulto > 0 ? articulo.unidadesPorBulto : null,
    unidadDeMedida: 'Unidad',
    importe,
    descuentoPorcentaje,
    idAlicuotaIva,
    actualizaCosto: l.idArticulo === articulo.id ? l.actualizaCosto : true,
    guardada: null,
    ...(origen === 'busqueda' ? { codigoConsultado: '' } : {}),
    ...(l.idArticulo !== articulo.id ? { codigoLote: '', fechaVencimiento: '' } : {}),
  }
}

/**
 * Lo que aplica una consulta de código que encontró un artículo, sobre la fila tal como está AHORA
 * (`actual`) y tal como estaba cuando se lanzó (`alLanzar`): lo que el operador tocó mientras la
 * consulta estaba en vuelo se respeta. Importe y Descuento son un par: si tocó uno, no se precarga
 * ninguno. Un IVA elegido en el vuelo se conserva y el importe precargado se neta con él.
 */
export function cambiosAlResolverPorCodigo(
  alLanzar: LineaDeCompraFormulario,
  actual: LineaDeCompraFormulario,
  articulo: ArticuloListado,
  alicuotas: AlicuotaIvaListado[],
  modo?: ModoDePreciosDeCompra,
): Partial<LineaDeCompraFormulario> {
  const parTocado =
    !Object.is(actual.importe, alLanzar.importe) || !Object.is(actual.descuentoPorcentaje, alLanzar.descuentoPorcentaje)
  const ivaTocado = !Object.is(actual.idAlicuotaIva, alLanzar.idAlicuotaIva)

  const cambios = cambiosAlElegirArticulo(actual, articulo, alicuotas, 'codigo', modo, ivaTocado)
  if (!Object.is(actual.unidadDeMedida, alLanzar.unidadDeMedida)) delete cambios.unidadDeMedida
  if (parTocado) {
    delete cambios.importe
    delete cambios.descuentoPorcentaje
  }
  if (ivaTocado) delete cambios.idAlicuotaIva
  return cambios
}

/**
 * Cambiar la UM de una fila con Importe no cambia el costo por unidad: el Importe se convierte (por
 * unidad ↔ por bulto, a los 4 decimales de la celda). La Cantidad no se toca: el operador cambia cómo
 * cuenta y la vuelve a tipear. La fila deja de reenviar los valores guardados.
 */
export function cambiosAlCambiarUnidadDeMedida(
  l: LineaDeCompraFormulario,
  unidadDeMedida: UnidadDeMedidaDeLinea,
): Partial<LineaDeCompraFormulario> {
  if (unidadDeMedida === l.unidadDeMedida) return {}
  const cambios: Partial<LineaDeCompraFormulario> = { unidadDeMedida, guardada: null }
  if (l.importe === null || !tieneBulto(l)) return cambios

  const porBulto = l.unidadesPorBulto as number
  const importe =
    unidadDeMedida === 'Bulto' ? productoRedondeado([l.importe, porBulto], 4) : redondearImporte(l.importe / porBulto, 4)
  return { ...cambios, importe }
}

/** Escribir en el Detalle de una fila con artículo la deja sin artículo: pasa a ser un concepto con
 * el texto tipeado (se conservan importe, descuento e IVA ya cargados). */
export function cambiosAlEscribirDetalle(l: LineaDeCompraFormulario, texto: string): Partial<LineaDeCompraFormulario> {
  if (l.idArticulo === '') return { descripcion: texto }
  return {
    descripcion: texto,
    idArticulo: '',
    resueltaPorCodigo: false,
    controlaLote: false,
    codigoLote: '',
    fechaVencimiento: '',
    unidadVenta: undefined,
    unidadesPorBulto: null,
    unidadDeMedida: 'Unidad',
    guardada: null,
  }
}

/** Deja la fila sin el artículo que le dio su código: vuelve a ser un concepto con el código sin
 * consultar y sin el nombre del artículo. Importe, descuento e IVA ya cargados se conservan como
 * valores tipeados. */
export function cambiosAlDesresolver(): Partial<LineaDeCompraFormulario> {
  return {
    descripcion: '',
    idArticulo: '',
    resueltaPorCodigo: false,
    codigoConsultado: '',
    controlaLote: false,
    codigoLote: '',
    fechaVencimiento: '',
    unidadVenta: undefined,
    unidadesPorBulto: null,
    unidadDeMedida: 'Unidad',
    actualizaCosto: true,
    guardada: null,
  }
}

/** Cambio de proveedor sobre una fila resuelta por código: pierde el artículo y todo lo que su
 * precarga escribió (importe, descuento e IVA), y conserva Código y Cantidad. Si estaba en bultos
 * también pierde la Cantidad: volver a Unidad sin tocarla convertiría bultos en unidades. Queda lista
 * para una primera consulta contra el proveedor nuevo. */
export function cambiosAlCambiarDeProveedor(l: LineaDeCompraFormulario): Partial<LineaDeCompraFormulario> {
  const cambios: Partial<LineaDeCompraFormulario> = { ...cambiosAlDesresolver(), importe: null, descuentoPorcentaje: null, idAlicuotaIva: '' }
  return l.unidadDeMedida === 'Bulto' ? { ...cambios, cantidad: '' } : cambios
}

/** Editar el código de una fila resuelta por código le saca el artículo (era de OTRO código): el
 * Detalle vuelve a ser editable y la nueva consulta, al salir de la celda, la vuelve a resolver. En
 * una fila cuyo artículo se eligió buscando o se creó con "+", el código es el pendiente de asociar
 * y el artículo se conserva. */
export function cambiosAlEscribirCodigo(l: LineaDeCompraFormulario, codigo: string): Partial<LineaDeCompraFormulario> {
  return l.resueltaPorCodigo ? { ...cambiosAlDesresolver(), codigoProveedor: codigo } : { codigoProveedor: codigo }
}

/** Datos del artículo que una fila reabierta (borrador o recepción de una orden) todavía no trae:
 * las unidades por bulto que habilitan la UM `Bulto`, si controla lote y la alícuota de IVA. Una
 * línea guardada con bultos conserva las suyas, que son las que se pactaron; una alícuota ya
 * cargada, guardada o tipeada, nunca se pisa, y un lote ya inferido no se baja. */
export function completarConArticulo(
  l: LineaDeCompraFormulario,
  articulo: Pick<ArticuloListado, 'unidadesPorBulto' | 'controlaLote' | 'idAlicuotaIva'>,
): Partial<LineaDeCompraFormulario> {
  const delArticulo = articulo.unidadesPorBulto !== null && articulo.unidadesPorBulto > 0 ? articulo.unidadesPorBulto : null
  return {
    unidadesPorBulto: l.unidadesPorBulto ?? delArticulo,
    controlaLote: l.controlaLote || articulo.controlaLote,
    idAlicuotaIva: l.idAlicuotaIva === '' ? articulo.idAlicuotaIva : l.idAlicuotaIva,
  }
}
