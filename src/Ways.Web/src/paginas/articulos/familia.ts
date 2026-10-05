/**
 * Helpers puros de familias de artículos en el formulario de artículo (doc 10 §3), separados de
 * `Articulos.tsx` y de `FormularioArticulo.tsx` para poder probarlos sin montar la pantalla
 * (web-descriptor-tests). Una familia agrupa artículos idénticos en sus trece campos compartidos y
 * en el estado de precios de cada lista fija; lo demás (nombre, descripción, códigos, marca,
 * activo y disponibilidad) es propio de cada artículo.
 */
import { formatearImporte } from '../../formato/importes'
import type {
  AlcanceDeFamilia,
  EstadoDePrecios,
  FamiliaDetalle,
  FamiliaListado,
  ValoresCompartidosDeLaFamilia,
} from '../../api/tipos'
import type { Formulario } from './FormularioArticulo'

/** Las trece claves compartidas son las de `ValoresCompartidosDeLaFamilia`: un campo nuevo del
 * contrato obliga a decidir acá si se comparte, y `Pick<Formulario, ClaveCompartida>` obliga a que el
 * formulario lo tenga. */
export type ClaveCompartida = keyof ValoresCompartidosDeLaFamilia

type TipoDeCampo = 'id' | 'decimal' | 'otro'

export type CampoCompartido = {
  clave: ClaveCompartida
  /** Nombre de la columna de `articulos`, con el que el servidor nombra los campos que difieren. */
  columna: string
  etiqueta: string
  tipo: TipoDeCampo
}

/** En el orden de declaración de `ValoresCompartidosDeFamilia` del servidor. */
export const CAMPOS_COMPARTIDOS: readonly CampoCompartido[] = [
  { clave: 'idArea', columna: 'id_area', etiqueta: 'Área', tipo: 'id' },
  { clave: 'idCategoria', columna: 'id_categoria', etiqueta: 'Categoría', tipo: 'id' },
  { clave: 'idGrupo', columna: 'id_grupo', etiqueta: 'Grupo', tipo: 'id' },
  { clave: 'idProveedorHabitual', columna: 'id_proveedor_habitual', etiqueta: 'Proveedor habitual', tipo: 'id' },
  { clave: 'idAlicuotaIva', columna: 'id_alicuota_iva', etiqueta: 'Alícuota de IVA', tipo: 'id' },
  { clave: 'unidadVenta', columna: 'unidad_venta', etiqueta: 'Unidad de venta', tipo: 'otro' },
  { clave: 'unidadesPorBulto', columna: 'unidades_por_bulto', etiqueta: 'Unidades por bulto', tipo: 'decimal' },
  { clave: 'esProducto', columna: 'es_producto', etiqueta: 'Es producto', tipo: 'otro' },
  { clave: 'controlaLote', columna: 'controla_lote', etiqueta: 'Controla lote', tipo: 'otro' },
  { clave: 'acumulaEnVenta', columna: 'acumula_en_venta', etiqueta: 'Acumula en una sola línea al vender', tipo: 'otro' },
  { clave: 'costoLista', columna: 'costo_lista', etiqueta: 'Costo de lista', tipo: 'decimal' },
  { clave: 'descuentoProveedor', columna: 'descuento_proveedor', etiqueta: 'Descuento de proveedor', tipo: 'decimal' },
  { clave: 'costoNominal', columna: 'costo_nominal', etiqueta: 'Costo nominal', tipo: 'decimal' },
]

/** Lo que el artículo tiene propio y nunca comparte con su familia. */
export const CAMPOS_PROPIOS = 'nombre, descripción, códigos, marca, activo y disponibilidad por empresa'

function textoDeDecimal(valor: number | null): string {
  return valor === null ? '' : String(valor)
}

/**
 * Los trece campos compartidos de una familia, con la forma del formulario (`''` por "sin valor", los
 * decimales como texto): lo que prellena el alta de un artículo dentro de la familia. Un id de
 * catálogo dado de baja que el servidor presenta como `null` queda en `''`.
 */
export function valoresDeFamiliaAFormulario(valores: ValoresCompartidosDeLaFamilia): Pick<Formulario, ClaveCompartida> {
  return {
    idArea: valores.idArea ?? '',
    idCategoria: valores.idCategoria ?? '',
    idGrupo: valores.idGrupo ?? '',
    idProveedorHabitual: valores.idProveedorHabitual ?? '',
    idAlicuotaIva: valores.idAlicuotaIva,
    unidadVenta: valores.unidadVenta,
    unidadesPorBulto: textoDeDecimal(valores.unidadesPorBulto),
    esProducto: valores.esProducto,
    controlaLote: valores.controlaLote,
    acumulaEnVenta: valores.acumulaEnVenta,
    costoLista: textoDeDecimal(valores.costoLista),
    descuentoProveedor: textoDeDecimal(valores.descuentoProveedor),
    costoNominal: textoDeDecimal(valores.costoNominal),
  }
}

/** El valor de un campo compartido comparable entre dos formularios: `''` es "sin valor" (igual que
 * `null`) y los decimales se comparan como número, así que `"10"` y `"10.0"` son el mismo valor. */
function valorComparable(formulario: Formulario, campo: CampoCompartido): number | string | boolean | null {
  const valor = formulario[campo.clave]

  if (campo.tipo === 'id') return valor === '' ? null : Number(valor)

  if (campo.tipo === 'decimal') {
    const texto = String(valor).trim()
    return texto === '' ? null : Number(texto)
  }

  return valor as string | boolean
}

function sonIguales(a: number | string | boolean | null, b: number | string | boolean | null): boolean {
  return a === b || (typeof a === 'number' && typeof b === 'number' && Number.isNaN(a) && Number.isNaN(b))
}

/**
 * Los campos compartidos cuyo valor difiere entre `original` (el artículo como se cargó o se guardó por
 * última vez) y `actual`, en el orden de `CAMPOS_COMPARTIDOS`. Es lo que decide si guardar la edición de
 * un miembro de una familia obliga a preguntar el alcance: sin ninguno, solo cambian campos propios y no
 * hay nada que replicar a los demás miembros.
 */
export function camposCompartidosModificados(original: Formulario, actual: Formulario): CampoCompartido[] {
  return CAMPOS_COMPARTIDOS.filter((campo) => !sonIguales(valorComparable(original, campo), valorComparable(actual, campo)))
}

/**
 * Los campos compartidos que un texto del servidor nombra por su columna (`difieren id_area, costo_lista`),
 * en el orden de `CAMPOS_COMPARTIDOS` y sin repetir. El servidor no manda datos estructurados con el
 * conflicto, así que la web busca las columnas conocidas en el texto en vez de depender de su redacción.
 */
export function camposNombradosEnMensaje(mensaje: string): CampoCompartido[] {
  return CAMPOS_COMPARTIDOS.filter((campo) => new RegExp(`(^|[^a-z_])${campo.columna}([^a-z_]|$)`).test(mensaje))
}

/** `{ valor, etiqueta }` de las familias que admiten un artículo nuevo: activas y con al menos un
 * miembro vivo (sin él no hay referencia de la que copiar valores y precios). Conserva el orden del
 * servidor, que ya viene por nombre. */
export function opcionesDeFamilia(familias: FamiliaListado[]): { valor: number; etiqueta: string }[] {
  return familias
    .filter((familia) => familia.activo && familia.cantidadArticulos > 0)
    .map((familia) => ({ valor: familia.id, etiqueta: `${familia.nombre} (${cantidadDeArticulos(familia.cantidadArticulos)})` }))
}

export function cantidadDeArticulos(cantidad: number): string {
  return cantidad === 1 ? '1 artículo' : `${cantidad} artículos`
}

/** La familia de un artículo miembro. `nombre` y `cantidad` son `null` cuando no se pudo cargar su
 * detalle: el artículo sigue siendo miembro, solo que no se sabe cómo se llama ni cuántos artículos tiene. */
export type FamiliaDelArticulo = { id: number; nombre: string | null; cantidad: number | null }

/** La familia de `idFamilia` con lo que se sabe de ella: su nombre y su cantidad de miembros solo si el
 * detalle cargado es el de esa misma familia. `null` cuando el formulario no tiene familia. */
export function familiaDelArticulo(idFamilia: number, detalle: FamiliaDetalle | null): FamiliaDelArticulo
export function familiaDelArticulo(idFamilia: number | '', detalle: FamiliaDetalle | null): FamiliaDelArticulo | null
export function familiaDelArticulo(idFamilia: number | '', detalle: FamiliaDetalle | null): FamiliaDelArticulo | null {
  if (idFamilia === '') return null

  return detalle !== null && detalle.id === idFamilia
    ? { id: idFamilia, nombre: detalle.nombre, cantidad: detalle.articulos.length }
    : { id: idFamilia, nombre: null, cantidad: null }
}

function entreParentesisLaCantidad(familia: FamiliaDelArticulo): string {
  return familia.cantidad === null ? '' : ` (${cantidadDeArticulos(familia.cantidad)})`
}

/** `de la familia "Sabores" (4 artículos)`; sin nombre conocido, `de una familia`. */
export function descripcionDeFamilia(familia: FamiliaDelArticulo): string {
  return familia.nombre === null ? 'de una familia' : `de la familia "${familia.nombre}"${entreParentesisLaCantidad(familia)}`
}

/** El rótulo del miembro en el formulario: `Familia "Sabores" (4 artículos)`. */
export function etiquetaDeFamilia(familia: FamiliaDelArticulo): string {
  return familia.nombre === null ? 'Pertenece a una familia' : `Familia "${familia.nombre}"${entreParentesisLaCantidad(familia)}`
}

/** Un alta con una familia elegida solo se puede guardar cuando esa familia está cargada y tiene
 * referencia: sin sus valores no hay con qué completar los campos compartidos que el servidor compara. */
export function altaConFamiliaLista(
  idFamilia: number | '',
  familia: Pick<EstadoDeFamiliaDelFormulario, 'detalle' | 'cargando' | 'error'>,
): boolean {
  if (idFamilia === '') return true

  return !familia.cargando && familia.error === '' && familia.detalle?.id === idFamilia && familia.detalle.valores !== null
}

export const PREGUNTA_DE_ALCANCE = '¿Aplicar el cambio a toda la familia?'

/** Con qué se abre la pregunta de alcance cuando se conoce la familia del artículo. */
export function contextoDeAlcance(familia: FamiliaDelArticulo): string {
  return `Este artículo es parte ${descripcionDeFamilia(familia)}.`
}

/** Lo que el servidor rechaza cuando un artículo no puede entrar a la familia elegida en un alta. */
export const CODIGOS_DE_CONFLICTO_DE_ALTA = ['familia_valores_distintos', 'familia_inactiva', 'familia_sin_articulos'] as const

export const AVISO_DE_FAMILIA_CAMBIO =
  'La pertenencia del artículo a su familia cambió desde que se cargó la pantalla. Se recargó el artículo: revisá los datos y volvé a intentar.'

/** El aviso de un alta que el servidor rechazó por `familia_valores_distintos`, con los campos que el texto del
 * servidor nombra. La familia ya se volvió a cargar y sus valores quedaron en el formulario. */
export function avisoDeValoresDistintos(campos: CampoCompartido[]): string {
  const donde = campos.length > 0 ? ` en: ${campos.map((c) => c.etiqueta).join(', ')}` : ''
  return `Los valores del artículo no coinciden con los de la familia${donde}. Se volvieron a cargar los de la familia: revisalos y guardá de nuevo.`
}

export function mensajeDeAlta(nombre: string, codigoInterno: string, familia: FamiliaDelArticulo | null): string {
  const base = `Artículo "${nombre}" creado con código interno ${codigoInterno}.`
  if (familia === null) return base

  const de = familia.nombre === null ? 'una familia' : `la familia "${familia.nombre}"`
  return `${base} Es parte de ${de}: tomó sus valores compartidos y sus precios.`
}

export function mensajeDeEdicion(nombre: string, alcance: AlcanceDeFamilia | undefined, nombreDeFamilia: string | null): string {
  const base = `Artículo "${nombre}" actualizado.`
  const familia = nombreDeFamilia === null ? 'la familia' : `la familia "${nombreDeFamilia}"`

  if (alcance === 'Familia') return `${base} Los cambios en los campos compartidos se aplicaron a toda ${familia}.`
  if (alcance === 'SoloEste') return `${base} Salió de ${familia} y el cambio quedó solo en él.`
  return base
}

/** El estado de precios de una lista, en una línea: `$ 1.200,00`, `$ 1.200,00 · programado $ 1.300,00 desde
 * 12/10/2026 15:00` o `—` sin ningún precio. */
export function describirEstadoDePrecios(estado: EstadoDePrecios): string {
  const vigente = formatearImporte(estado.vigente, { simbolo: true })
  if (estado.pendiente === null) return vigente

  const programado = `programado ${formatearImporte(estado.pendiente.monto, { simbolo: true })} desde ${formatearFechaHora(estado.pendiente.vigenteDesde)}`
  return estado.vigente === null ? programado : `${vigente} · ${programado}`
}

export function formatearFechaHora(iso: string): string {
  return new Date(iso).toLocaleString('es-AR')
}

/** Lo que `Articulos` le pasa al formulario sobre la familia del artículo que se está abriendo. */
export type EstadoDeFamiliaDelFormulario = {
  /** Las familias que se ofrecen en un alta (ver `opcionesDeFamilia`). */
  opciones: FamiliaListado[]
  errorOpciones: string
  /** La familia elegida en un alta, o la del miembro que se edita. */
  detalle: FamiliaDetalle | null
  cargando: boolean
  error: string
}

export type AccionesDeFamiliaDelFormulario = {
  elegir: (idFamilia: number | '') => void
  reintentar: () => void
  /** Saca al artículo editado de su familia. Resuelve `true` cuando la escritura terminó, haya salido bien o
   * no, y `false` si no llegó a empezar porque había otra en curso. */
  sacar: () => Promise<boolean>
  /** El editor de precios sacó al artículo de la familia (`SoloEste`). */
  alSalirDeLaFamilia: (nombre: string | null) => void
  /** El servidor dijo que la pertenencia cambió (`familia_cambio`): hay que recargar el artículo. */
  alCambiarLaFamilia: () => void
}
