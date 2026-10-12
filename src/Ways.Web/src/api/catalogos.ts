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
import type { SujetoDeBaja } from './bajas'
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
  ListaPrecioAlta,
  ListaPrecioListado,
  MarcaAlta,
  MarcaListado,
  MedioPagoAdministrado,
  MedioPagoAdministradoAlta,
  ModoLista,
  TipoComprobanteListado,
} from './tipos'
import { COMPORTAMIENTOS_MEDIO_PAGO } from './tipos'
import { clienteDeCuentasTesoreria, opcionesDeCuentas, type CuentaTesoreriaListado } from './cuentasTesoreria'

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
  /** stage-3-articulos-y-precios (Slice 6): campo condicional a otro campo del mismo
   * formulario — se oculta (no solo se deshabilita) cuando el predicado da `false`, así un
   * campo `requerido` oculto no bloquea el envío. Único uso hoy: `idListaBase`/`porcentaje`
   * solo aplican cuando `modo = Derivada`. */
  visibleSi?: (valores: Record<string, ValorDeCampo>) => boolean
  /** stage-3-articulos-y-precios (Slice 6): alternativa a `opciones` estáticas — arma las
   * opciones del select a partir del listado que la pantalla ya tiene cargado (el selector de
   * lista base ofrece las demás listas de la misma pantalla, no un catálogo aparte). El
   * descriptor concreto hace el cast desde `unknown[]` a su propio `TListado`. */
  opcionesDesdeListado?: (items: unknown[], idActual: number | null) => { valor: string; etiqueta: string }[]
  /** Opciones de un select que salen de una lectura aparte (p. ej. las cuentas de tesorería): `cargar` corre cada vez
   * que se abre el formulario y `armar` recibe lo leído (`null` mientras llega o si la lectura falló) junto con los
   * valores del formulario, así puede conservar la opción actual aunque la lectura no la traiga. `errorDeCarga` es el
   * aviso si falla; el formulario sigue funcionando. */
  opcionesRemotas?: {
    cargar: () => Promise<unknown>
    armar: (leido: unknown, valores: Record<string, ValorDeCampo>) => { valor: string; etiqueta: string }[]
    /** Aviso debajo del select según lo leído (`null` si la lectura rechazó) y las opciones resultantes; `error` lo pinta
     * como falla. Devuelve null cuando no hay nada que avisar. */
    aviso: (
      leido: unknown,
      opciones: { valor: string; etiqueta: string }[],
    ) => { texto: string; error: boolean } | null
  }
  /** Texto de la opción vacía de un select opcional: si está, la opción vacía se ofrece siempre. */
  opcionVacia?: string
  /** Texto de ayuda debajo del campo. */
  ayuda?: string
  /** `step` del input numérico, cuando el valor por defecto (0.01) no alcanza. */
  paso?: string
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
  /** Sujeto de la baja para `copiaDeFalloDeBaja`/`ConfirmacionDeBaja` (`fix/web-bajas-catalogos`):
   * mismo texto que el `SujetoDeBaja` del servicio del catálogo del lado del servidor, para que
   * la puerta de confirmación y la copia del 409 nombren la entidad igual. Campo del descriptor
   * y no un `switch` en `PaginaCatalogo` sobre `recurso` — un catálogo nuevo que olvide declararlo
   * no compila. */
  sujetoDeBaja: SujetoDeBaja
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
  sujetoDeBaja: 'el área',
}

export const descriptorMarcas: DescriptorDeCatalogo<MarcaListado, MarcaAlta> = {
  recurso: 'marcas',
  titulo: 'Marcas',
  tituloSingular: 'marca',
  campos: [],
  valoresPorDefecto: {},
  aValores: () => ({}),
  aAlta: (nombre, activo) => ({ nombre, idEmpresa: null, activo }),
  sujetoDeBaja: 'la marca',
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
  sujetoDeBaja: 'el grupo',
}

const esElectronico = (valores: Record<string, ValorDeCampo>) => valores.comportamiento === 'Electronico'

type LecturaDeCuentas = { cuentas: CuentaTesoreriaListado[] | null; empresas: { id: number }[] | null }

const GUARDAR_CON_LA_ACTUAL = 'Podés guardar el medio con la cuenta actual.'

/** Qué falló o por qué no hay cuentas para elegir: cada causa tiene su texto, no una sola frase para todo. */
export function avisoDeLasCuentas(
  leido: unknown,
  opciones: { valor: string; etiqueta: string }[],
): { texto: string; error: boolean } | null {
  const lectura = leido as LecturaDeCuentas | null
  if (lectura === null || lectura.cuentas === null) {
    return { texto: `No se pudieron cargar las cuentas de tesorería. ${GUARDAR_CON_LA_ACTUAL}`, error: true }
  }
  if (lectura.empresas === null) {
    return {
      texto: `No se pudo cargar la lista de empresas, así que no se ofrecen cuentas para elegir. ${GUARDAR_CON_LA_ACTUAL}`,
      error: true,
    }
  }
  if (opciones.length > 0) return null
  if (lectura.cuentas.length === 0) {
    return { texto: 'Todavía no hay cuentas de tesorería: creá una en Cuentas de tesorería.', error: false }
  }

  return {
    texto:
      'No hay cuentas para este medio: un medio de todo el tenant solo puede tener cuenta si el tenant tiene una sola ' +
      'empresa, y uno de una empresa solo toma cuentas de esa empresa.',
    error: false,
  }
}

const AYUDA_DE_LA_CUENTA =
  'Lo cobrado con este medio entra a la cuenta neto de la comisión y las retenciones. Sin cuenta, no entra a ninguna. ' +
  'Un medio de todo el tenant solo puede tener cuenta si hay una sola empresa.'

const AYUDA_DE_LA_COMISION = 'La comisión y las retenciones se descuentan de lo cobrado; sumadas no pueden superar el 100%.'

function campoDeCuenta(clave: string, etiqueta: string, ayuda?: string): CampoDescriptor {
  return { clave, etiqueta, tipo: 'numeroDecimal', paso: '0.001', visibleSi: esElectronico, ayuda }
}

/**
 * La cuenta, la comisión y las retenciones que viajan al servidor. Con el medio no electrónico los campos quedan
 * ocultos y se mandan limpios (sin cuenta, todo en 0): el servidor los rechaza con valores y el formulario no puede
 * mandar lo que quedó cargado antes de cambiar el comportamiento.
 */
export function cuentaDelFormulario(
  valores: Record<string, ValorDeCampo>,
): Pick<
  MedioPagoAdministradoAlta,
  'idCuentaTesoreria' | 'comisionPorcentaje' | 'retencionIibbPorcentaje' | 'retencionIvaPorcentaje' | 'retencionGananciasPorcentaje'
> {
  if (!esElectronico(valores)) {
    return {
      idCuentaTesoreria: null,
      comisionPorcentaje: 0,
      retencionIibbPorcentaje: 0,
      retencionIvaPorcentaje: 0,
      retencionGananciasPorcentaje: 0,
    }
  }

  return {
    idCuentaTesoreria: numeroOVacio(valores.idCuentaTesoreria),
    comisionPorcentaje: numeroOVacio(valores.comisionPorcentaje) ?? 0,
    retencionIibbPorcentaje: numeroOVacio(valores.retencionIibbPorcentaje) ?? 0,
    retencionIvaPorcentaje: numeroOVacio(valores.retencionIvaPorcentaje) ?? 0,
    retencionGananciasPorcentaje: numeroOVacio(valores.retencionGananciasPorcentaje) ?? 0,
  }
}

export const descriptorMediosPago: DescriptorDeCatalogo<MedioPagoAdministrado, MedioPagoAdministradoAlta> = {
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
    { clave: 'porcentajeFacturacion', etiqueta: 'Porcentaje a facturar (%)', tipo: 'numeroDecimal' },
    {
      clave: 'idCuentaTesoreria',
      etiqueta: 'Cuenta de tesorería',
      tipo: 'select',
      visibleSi: esElectronico,
      opcionVacia: '— Sin cuenta —',
      ayuda: AYUDA_DE_LA_CUENTA,
      opcionesRemotas: {
        cargar: async () => {
          const [cuentas, empresas] = await Promise.allSettled([
            clienteDeCuentasTesoreria.listar(),
            api.get<{ id: number }[]>('/empresas'),
          ])
          return {
            cuentas: cuentas.status === 'fulfilled' ? cuentas.value : null,
            empresas: empresas.status === 'fulfilled' ? empresas.value : null,
          }
        },
        armar: (leido, valores) => {
          const lectura = leido as LecturaDeCuentas | null
          return opcionesDeCuentas(
            lectura?.cuentas ?? null,
            lectura?.empresas ?? null,
            String(valores.idEmpresa ?? ''),
            String(valores.idCuentaTesoreria ?? ''),
            String(valores.nombreCuentaTesoreria ?? ''),
            Boolean(valores.cuentaTesoreriaInactiva),
          )
        },
        aviso: avisoDeLasCuentas,
      },
    },
    campoDeCuenta('comisionPorcentaje', 'Comisión (%)', AYUDA_DE_LA_COMISION),
    campoDeCuenta('retencionIibbPorcentaje', 'Retención IIBB (%)'),
    campoDeCuenta('retencionIvaPorcentaje', 'Retención IVA (%)'),
    campoDeCuenta('retencionGananciasPorcentaje', 'Retención Ganancias (%)'),
  ],
  valoresPorDefecto: {
    orden: '1',
    comportamiento: 'Efectivo',
    admiteVuelto: true,
    requiereReferencia: false,
    recargoPorcentaje: '',
    porcentajeFacturacion: '0',
    idEmpresa: '',
    idCuentaTesoreria: '',
    nombreCuentaTesoreria: '',
    cuentaTesoreriaInactiva: false,
    comisionPorcentaje: '0',
    retencionIibbPorcentaje: '0',
    retencionIvaPorcentaje: '0',
    retencionGananciasPorcentaje: '0',
  },
  aValores: (item) => ({
    orden: String(item.orden),
    comportamiento: item.comportamiento,
    admiteVuelto: item.admiteVuelto,
    requiereReferencia: item.requiereReferencia,
    recargoPorcentaje: item.recargoPorcentaje === null ? '' : String(item.recargoPorcentaje),
    porcentajeFacturacion: String(item.porcentajeFacturacion),
    idEmpresa: item.idEmpresa === null ? '' : String(item.idEmpresa),
    idCuentaTesoreria: item.idCuentaTesoreria === null ? '' : String(item.idCuentaTesoreria),
    nombreCuentaTesoreria: item.nombreCuentaTesoreria ?? '',
    cuentaTesoreriaInactiva: item.cuentaTesoreriaInactiva,
    comisionPorcentaje: String(item.comisionPorcentaje),
    retencionIibbPorcentaje: String(item.retencionIibbPorcentaje),
    retencionIvaPorcentaje: String(item.retencionIvaPorcentaje),
    retencionGananciasPorcentaje: String(item.retencionGananciasPorcentaje),
  }),
  aAlta: (nombre, activo, valores) => ({
    nombre,
    // La edición devuelve la empresa de la fila: mandar null convertiría un medio de una empresa en uno de todo el tenant.
    idEmpresa: numeroOVacio(valores.idEmpresa),
    activo,
    orden: numeroOVacio(valores.orden) ?? 1,
    comportamiento: (valores.comportamiento as string as MedioPagoAdministradoAlta['comportamiento']) ?? 'Efectivo',
    admiteVuelto: Boolean(valores.admiteVuelto),
    requiereReferencia: Boolean(valores.requiereReferencia),
    recargoPorcentaje: numeroOVacio(valores.recargoPorcentaje),
    porcentajeFacturacion: numeroOVacio(valores.porcentajeFacturacion) ?? 0,
    ...cuentaDelFormulario(valores),
  }),
  sujetoDeBaja: 'el medio de pago',
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
  sujetoDeBaja: 'la categoría',
}

/**
 * stage-3-articulos-y-precios (Slice 6, design: ABM Composition): `listas_precio` reusa la
 * máquina genérica igual que `descriptorCategorias`, con ruta propia (`/listas-precio`, no
 * `/catalogos/:recurso`) — mismo criterio que `categorias`, no se agrega a
 * `DESCRIPTORES_DE_CATALOGO`. `idListaBase`/`porcentaje` solo aplican en modo `Derivada`
 * (`visibleSi`); el swap de `esDefault` y las guardas de modo/desactivación las valida el
 * servidor — esta pantalla solo muestra el mensaje que devuelve (`ErrorApi.message`).
 */
export const descriptorListasPrecio: DescriptorDeCatalogo<ListaPrecioListado, ListaPrecioAlta> = {
  recurso: 'listas-precio',
  titulo: 'Listas de precio',
  tituloSingular: 'lista de precio',
  campos: [
    { clave: 'esDefault', etiqueta: 'Lista default', tipo: 'booleano', columnaEnListado: true },
    {
      clave: 'modo',
      etiqueta: 'Modo',
      tipo: 'select',
      requerido: true,
      columnaEnListado: true,
      opciones: [
        { valor: 'Fija', etiqueta: 'Fija' },
        { valor: 'Derivada', etiqueta: 'Derivada (calculada sobre otra lista)' },
      ],
    },
    {
      clave: 'idListaBase',
      etiqueta: 'Lista base',
      tipo: 'select',
      requerido: true,
      visibleSi: (valores) => valores.modo === 'Derivada',
      opcionesDesdeListado: (items, idActual) =>
        (items as ListaPrecioListado[])
          .filter((item) => item.activo && item.modo === 'Fija' && item.id !== idActual)
          .map((item) => ({ valor: String(item.id), etiqueta: item.nombre })),
    },
    {
      clave: 'porcentaje',
      etiqueta: 'Porcentaje sobre la base (%)',
      tipo: 'numeroDecimal',
      requerido: true,
      visibleSi: (valores) => valores.modo === 'Derivada',
    },
  ],
  valoresPorDefecto: { esDefault: false, modo: 'Fija', idListaBase: '', porcentaje: '' },
  aValores: (item) => ({
    esDefault: item.esDefault,
    modo: item.modo,
    idListaBase: item.idListaBase === null ? '' : String(item.idListaBase),
    porcentaje: item.porcentaje === null ? '' : String(item.porcentaje),
  }),
  aAlta: (nombre, activo, valores) => {
    const esDerivada = valores.modo === 'Derivada'
    return {
      nombre,
      idEmpresa: null,
      activo,
      esDefault: Boolean(valores.esDefault),
      modo: (valores.modo as ModoLista) ?? 'Fija',
      idListaBase: esDerivada ? numeroOVacio(valores.idListaBase) : null,
      porcentaje: esDerivada ? numeroOVacio(valores.porcentaje) : null,
    }
  },
  sujetoDeBaja: 'la lista de precios',
}

// --- Catálogos fiscales (globales, solo lectura — ADR-11, gate #4) ---

export const clienteDeCatalogosFiscales = {
  condicionesFiscales: () => api.get<CondicionFiscalListado[]>('/catalogos-fiscales/condiciones-fiscales'),
  alicuotasIva: () => api.get<AlicuotaIvaListado[]>('/catalogos-fiscales/alicuotas-iva'),
  tiposComprobante: () => api.get<TipoComprobanteListado[]>('/catalogos-fiscales/tipos-comprobante'),
}
