/**
 * Máquina de catálogos (ADR-11): un solo componente genérico (`PaginaCatalogo`) parametrizado
 * por un descriptor de campos, en vez de 8 pantallas copiadas. Cada catálogo concreto declara
 * acá sus campos propios (todo lo que no sea `nombre`/`activo`, comunes a los 5) y cómo
 * traducirlos hacia/desde el contrato de alta de la API.
 *
 * `idEmpresa` no aparece en ningún descriptor a propósito: el filtro `DeLaEmpresa` (ADR-10)
 * queda diferido, así que todo catálogo se crea compartido (`idEmpresa: null`) — de una sola
 * empresa por tenant en esta etapa, es una UX correcta sin ese selector.
 */
import { api } from './cliente'
import type {
  AlicuotaIvaListado,
  AreaAlta,
  AreaListado,
  CatalogoListado,
  CategoriaAlta,
  CategoriaListado,
  CondicionFiscalListado,
  GrupoAlta,
  GrupoListado,
  MarcaAlta,
  MarcaListado,
  MedioPagoAlta,
  MedioPagoListado,
  TipoComprobanteListado,
} from './tipos'
import { COMPORTAMIENTOS_MEDIO_PAGO } from './tipos'

/** Todo campo propio se maneja como texto en el formulario (incl. números y el select de
 * comportamiento) salvo los booleanos — simplifica el estado controlado; cada descriptor
 * convierte hacia/desde el tipo real de la API en `aAlta`/`aValores`. */
export type ValorDeCampo = string | boolean

export type CampoDescriptor = {
  clave: string
  etiqueta: string
  tipo: 'texto' | 'numeroEntero' | 'numeroDecimal' | 'booleano' | 'select'
  opciones?: { valor: string; etiqueta: string }[]
  requerido?: boolean
  /** Se muestra también como columna extra en la tabla de listado, no solo en el formulario. */
  columnaEnListado?: boolean
  formatearListado?: (item: unknown) => string
}

export type DescriptorDeCatalogo<TListado extends CatalogoListado, TAlta> = {
  recurso: string
  titulo: string
  tituloSingular: string
  campos: CampoDescriptor[]
  valoresPorDefecto: Record<string, ValorDeCampo>
  /** Extrae los valores editables de un item de listado, para precargar el formulario. */
  aValores: (item: TListado) => Record<string, ValorDeCampo>
  /** Arma el contrato de alta/edición a partir de los campos comunes + los propios. */
  aAlta: (nombre: string, activo: boolean, valores: Record<string, ValorDeCampo>) => TAlta
}

export function clienteDeCatalogo<TListado, TAlta>(recurso: string) {
  return {
    listar: (incluirInactivos: boolean) =>
      api.get<TListado[]>(`/catalogos/${recurso}${incluirInactivos ? '?incluirInactivos=true' : ''}`),
    crear: (datos: TAlta) => api.post<TListado>(`/catalogos/${recurso}`, datos),
    actualizar: (id: number, datos: TAlta) => api.put<TListado>(`/catalogos/${recurso}/${id}`, datos),
    eliminar: (id: number) => api.delete(`/catalogos/${recurso}/${id}`),
  }
}

function numeroOVacio(valor: ValorDeCampo): number | null {
  return valor === '' || valor === null || valor === undefined ? null : Number(valor)
}

export const descriptorAreas: DescriptorDeCatalogo<AreaListado, AreaAlta> = {
  recurso: 'areas',
  titulo: 'Áreas',
  tituloSingular: 'área',
  campos: [
    {
      clave: 'orden',
      etiqueta: 'Orden',
      tipo: 'numeroEntero',
      requerido: true,
      columnaEnListado: true,
    },
  ],
  valoresPorDefecto: { orden: '1' },
  aValores: (item) => ({ orden: String(item.orden) }),
  aAlta: (nombre, activo, valores) => ({
    nombre,
    idEmpresa: null,
    activo,
    orden: numeroOVacio(valores.orden) ?? 1,
  }),
}

export const descriptorMarcas: DescriptorDeCatalogo<MarcaListado, MarcaAlta> = {
  recurso: 'marcas',
  titulo: 'Marcas',
  tituloSingular: 'marca',
  campos: [],
  valoresPorDefecto: {},
  aValores: () => ({}),
  aAlta: (nombre, activo) => ({ nombre, idEmpresa: null, activo }),
}

export const descriptorGrupos: DescriptorDeCatalogo<GrupoListado, GrupoAlta> = {
  recurso: 'grupos',
  titulo: 'Grupos',
  tituloSingular: 'grupo',
  campos: [
    {
      clave: 'margen',
      etiqueta: 'Margen sugerido (%)',
      tipo: 'numeroDecimal',
      columnaEnListado: true,
    },
  ],
  valoresPorDefecto: { margen: '' },
  aValores: (item) => ({ margen: item.margen === null ? '' : String(item.margen) }),
  aAlta: (nombre, activo, valores) => ({
    nombre,
    idEmpresa: null,
    activo,
    margen: numeroOVacio(valores.margen),
  }),
}

export const descriptorMediosPago: DescriptorDeCatalogo<MedioPagoListado, MedioPagoAlta> = {
  recurso: 'medios-pago',
  titulo: 'Medios de pago',
  tituloSingular: 'medio de pago',
  campos: [
    { clave: 'orden', etiqueta: 'Orden', tipo: 'numeroEntero', requerido: true },
    {
      clave: 'comportamiento',
      etiqueta: 'Comportamiento',
      tipo: 'select',
      requerido: true,
      columnaEnListado: true,
      opciones: COMPORTAMIENTOS_MEDIO_PAGO.map((c) => ({ valor: c.valor, etiqueta: c.etiqueta })),
    },
    { clave: 'admiteVuelto', etiqueta: 'Admite vuelto', tipo: 'booleano' },
    { clave: 'requiereReferencia', etiqueta: 'Requiere referencia', tipo: 'booleano' },
    { clave: 'recargoPorcentaje', etiqueta: 'Recargo (%)', tipo: 'numeroDecimal' },
  ],
  valoresPorDefecto: {
    orden: '1',
    comportamiento: 'Efectivo',
    admiteVuelto: true,
    requiereReferencia: false,
    recargoPorcentaje: '',
  },
  aValores: (item) => ({
    orden: String(item.orden),
    comportamiento: item.comportamiento,
    admiteVuelto: item.admiteVuelto,
    requiereReferencia: item.requiereReferencia,
    recargoPorcentaje: item.recargoPorcentaje === null ? '' : String(item.recargoPorcentaje),
  }),
  aAlta: (nombre, activo, valores) => ({
    nombre,
    idEmpresa: null,
    activo,
    orden: numeroOVacio(valores.orden) ?? 1,
    comportamiento: (valores.comportamiento as string as MedioPagoAlta['comportamiento']) ?? 'Efectivo',
    admiteVuelto: Boolean(valores.admiteVuelto),
    requiereReferencia: Boolean(valores.requiereReferencia),
    recargoPorcentaje: numeroOVacio(valores.recargoPorcentaje),
  }),
}

/** Registro de los 4 catálogos que pasan por la máquina genérica — `categorias` es el escape
 * hatch de ADR-11 (árbol con regla de profundidad) y tiene su propia página. Cada entrada
 * mantiene su propio par `TListado`/`TAlta`; el consumidor (`RutaCatalogo`) borra ese tipo a
 * propósito para poder indexar por el `recurso` de la URL — `PaginaCatalogo` en sí sigue
 * chequeando tipos en cada uso concreto. */
export const DESCRIPTORES_DE_CATALOGO = {
  areas: descriptorAreas,
  marcas: descriptorMarcas,
  grupos: descriptorGrupos,
  'medios-pago': descriptorMediosPago,
}

export type RecursoDeCatalogo = keyof typeof DESCRIPTORES_DE_CATALOGO

export const descriptorCategorias: DescriptorDeCatalogo<CategoriaListado, CategoriaAlta> = {
  recurso: 'categorias',
  titulo: 'Categorías',
  tituloSingular: 'categoría',
  campos: [],
  valoresPorDefecto: {},
  aValores: () => ({}),
  aAlta: (nombre, activo) => ({ nombre, idEmpresa: null, activo, orden: 1, idCategoriaPadre: null }),
}

// --- Catálogos fiscales (globales, solo lectura — ADR-11, gate #4) ---

export const clienteDeCatalogosFiscales = {
  condicionesFiscales: () => api.get<CondicionFiscalListado[]>('/catalogos-fiscales/condiciones-fiscales'),
  alicuotasIva: () => api.get<AlicuotaIvaListado[]>('/catalogos-fiscales/alicuotas-iva'),
  tiposComprobante: () => api.get<TipoComprobanteListado[]>('/catalogos-fiscales/tipos-comprobante'),
}
