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
  const n = Number(valor)
  return valor.trim() === '' || !Number.isFinite(n) ? 0 : n
}

export type CampoDeLinea = 'detalle' | 'cantidad' | 'importe' | 'descuento' | 'iva' | 'vencimiento'

function tieneBulto(l: LineaDeCompraFormulario): boolean {
  return l.unidadesPorBulto !== null && l.unidadesPorBulto > 0
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

  const cantidad = l.cantidad.trim() === '' ? Number.NaN : Number(l.cantidad)
  const granularidad = respetaGranularidad(esConcepto || esBulto ? undefined : l.unidadVenta, cantidad)
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

function codigoProveedorONulo(l: LineaDeCompraFormulario): string | null {
  const codigo = l.codigoProveedor.trim()
  return codigo === '' ? null : codigo
}

/** Fila de formulario → `LineaDeCompraSolicitada`. Un artículo siempre actualiza el costo; un
 * concepto nunca lo hace y no lleva bultos ni lote. */
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
    actualizaCosto: true,
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

/** Costo con el que se precarga la fila: el de lista con el descuento habitual del proveedor (un
 * PORCENTAJE, `descuento_proveedor numeric(5,2)`), que es lo que el comprobante del proveedor
 * muestra; si el artículo no tiene lista, el costo nominal (el de reposición, que ya es neto de
 * descuento). `null` en ambos = el rol no ve costos o el artículo no tiene ninguno. */
export function costoDeArticuloParaCompra(
  articulo: Pick<ArticuloListado, 'costoLista' | 'descuentoProveedor' | 'costoNominal'>,
): { importe: number; descuentoPorcentaje: number | null } | null {
  if (articulo.costoLista !== null) {
    return { importe: articulo.costoLista, descuentoPorcentaje: articulo.descuentoProveedor }
  }
  if (articulo.costoNominal !== null) return { importe: articulo.costoNominal, descuentoPorcentaje: null }
  return null
}

/** La alícuota del artículo si el catálogo la conoce; si no, la del 21 %. */
export function alicuotaParaArticulo(articulo: Pick<ArticuloListado, 'idAlicuotaIva'>, alicuotas: AlicuotaIvaListado[]): number | '' {
  if (alicuotas.some((a) => a.id === articulo.idAlicuotaIva)) return articulo.idAlicuotaIva
  return alicuotas.find((a) => a.porcentaje === PORCENTAJE_DE_IVA_POR_DEFECTO)?.id ?? ''
}

/**
 * Cambios que deja elegir (o crear) un artículo en una fila: Detalle con su nombre, UM `Unidad`,
 * importe y descuento del artículo, su alícuota y los datos que habilitan bultos y lote. La
 * cantidad no se toca. Cambiar de artículo invalida el lote ya cargado (era del anterior) y la
 * línea guardada. Un artículo recién creado desde la fila (`alta`) conserva lo que ya se tipeó en
 * ella. Sin costo visible (rol sin permiso) el importe tipeado se respeta.
 */
export function cambiosAlElegirArticulo(
  l: LineaDeCompraFormulario,
  articulo: ArticuloListado,
  alicuotas: AlicuotaIvaListado[],
  origen: OrigenDeArticulo,
): Partial<LineaDeCompraFormulario> {
  const costo = costoDeArticuloParaCompra(articulo)
  const conservarLoTipeado = origen === 'alta'
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
    idAlicuotaIva: conservarLoTipeado && l.idAlicuotaIva !== '' ? l.idAlicuotaIva : alicuotaParaArticulo(articulo, alicuotas),
    guardada: null,
    ...(l.idArticulo !== articulo.id ? { codigoLote: '', fechaVencimiento: '' } : {}),
  }
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

/** Editar el código de una fila resuelta por código la deja sin resolver: el Detalle vuelve a ser
 * editable y se vuelve a consultar al salir de la celda. El artículo ya elegido queda hasta que la
 * nueva consulta lo reemplace. */
export function cambiosAlEscribirCodigo(l: LineaDeCompraFormulario, codigo: string): Partial<LineaDeCompraFormulario> {
  return l.resueltaPorCodigo ? { codigoProveedor: codigo, resueltaPorCodigo: false } : { codigoProveedor: codigo }
}

/** Datos del artículo que una fila reabierta (borrador o recepción de una orden) todavía no trae:
 * las unidades por bulto que habilitan la UM `Bulto`. Una línea guardada con bultos conserva las
 * suyas, que son las que se pactaron. */
export function completarConArticulo(
  l: LineaDeCompraFormulario,
  articulo: Pick<ArticuloListado, 'unidadesPorBulto'>,
): Partial<LineaDeCompraFormulario> {
  const delArticulo = articulo.unidadesPorBulto !== null && articulo.unidadesPorBulto > 0 ? articulo.unidadesPorBulto : null
  return { unidadesPorBulto: l.unidadesPorBulto ?? delArticulo }
}
