/**
 * Cliente de ofertas (stage-4-ofertas, Slice 4): ABM dedicado, no la máquina genérica de
 * catálogos (design decision 9) — `AltaOferta`/`EdicionOferta` no tienen ninguna columna
 * inmutable en edición, así que un solo mapper de escritura (`aAltaOferta`) alcanza para
 * crear y actualizar. Los mappers viven acá, fuera de `Ofertas.tsx`, para que
 * `web-descriptor-tests` siga aplicando (el criterio de la skill es el helper, no el
 * descriptor literal).
 */
import { api, ErrorApi } from './cliente'
import type {
  AltaOferta,
  EdicionOferta,
  FamiliaListado,
  LineaDeResolucion,
  ListaPrecioListado,
  OfertaListado,
  ResultadoDeResolucion,
  ResolucionConCombos,
  AgregacionDeResolucion,
} from './tipos'
import { formatearImporte } from '../formato/importes'
import { parsearEnteroTipeado, parsearNumeroDecimal } from '../formato/numeros'

export const clienteDeOfertas = {
  listar: (incluirEliminados: boolean) =>
    api.get<OfertaListado[]>(`/ofertas${incluirEliminados ? '?incluirEliminados=true' : ''}`),
  /** El listado no completa `idsListas` (evita el N+1) — antes de editar hay que pedir el
   * detalle puntual para no perder el subconjunto real de listas objetivo. */
  obtener: (id: number) => api.get<OfertaListado>(`/ofertas/${id}`),
  crear: (datos: AltaOferta) => api.post<OfertaListado>('/ofertas', datos),
  actualizar: (id: number, datos: EdicionOferta) => api.put<OfertaListado>(`/ofertas/${id}`, datos),
  eliminar: (id: number) => api.delete(`/ofertas/${id}`),
  /** `POST /api/ofertas/resolver` (stage-4, re-gateado a `OperacionDePos` en stage-5): único
   * camino de precio del carrito del POS (spec: operacion-de-pos / "Cart Pricing Has Exactly
   * One Path") — no muta nada, solo reporta precio final y ofertas aplicadas por línea.
   * `agregacion` dice si el lote es un documento (el carrito, por omisión) o consultas sueltas. */
  resolver: (lineas: LineaDeResolucion[], agregacion: AgregacionDeResolucion = 'Documento') =>
    api.post<ResultadoDeResolucion[]>('/ofertas/resolver', { lineas, agregacion }),
  /** `POST /api/ofertas/resolver-con-combos`: el carrito del POS (documento completo). Mismo cuerpo que
   * `resolver`, pero responde `{ lineas, combos }`: cada línea que un combo consumió trae `combo`. Toda
   * línea tiene que mandar el `idEmpresa` del punto de venta y la lista del cliente (valores mezclados →
   * 400). Las búsquedas y consultas de precio siguen en `resolver`: muestran un precio unitario por línea.
   * Un `combos` ausente en la respuesta es una lista vacía. Un servidor que todavía no tiene la ruta (404 o
   * 405, la ventana de un despliegue) cae al `resolver` de siempre como documento y sin combos: el carrito
   * conserva su vista previa. Cualquier otro error (400, 500, red) se propaga tal cual. */
  resolverConCombos: async (lineas: LineaDeResolucion[]): Promise<ResolucionConCombos> => {
    try {
      const respuesta = await api.post<ResolucionConCombos>('/ofertas/resolver-con-combos', { lineas, agregacion: 'Documento' })
      return { ...respuesta, combos: respuesta.combos ?? [] }
    } catch (e) {
      if (e instanceof ErrorApi && (e.estado === 404 || e.estado === 405)) {
        return { lineas: await clienteDeOfertas.resolver(lineas, 'Documento'), combos: [] }
      }
      throw e
    }
  },
}

export type AlcanceOferta = 'Articulo' | 'Grupo' | 'Categoria' | 'Familia'
export type BeneficioOferta = 'PrecioUnitario' | 'Porcentaje' | 'ImporteFijo'

/** Estado controlado del formulario: todo campo numérico/temporal se maneja como texto (mismo
 * criterio que `Formulario` en `Articulos.tsx`); `alcance`/`beneficio` son la representación en
 * UI de la exclusividad de cada grupo — `aAltaOferta` los proyecta de vuelta a las columnas
 * nullable (cuatro de alcance, tres de beneficio), forzando a `null` las no elegidas. */
export type FormularioOferta = {
  id: number | null
  nombre: string
  idEmpresa: number | ''
  alcance: AlcanceOferta
  idArticulo: number | ''
  nombreArticulo: string
  idGrupo: number | ''
  idCategoria: number | ''
  idFamilia: number | ''
  fechaDesde: string
  fechaHasta: string
  horaDesde: string
  horaHasta: string
  diasSemana: number[]
  cantidadMinima: string
  soloMultiplos: boolean
  beneficio: BeneficioOferta
  precioUnitario: string
  porcentaje: string
  importeFijo: string
  prioridad: string
  acumulable: boolean
  idsListas: number[]
  activo: boolean
  disponibleOnline: boolean
}

export function formularioOfertaVacio(): FormularioOferta {
  return {
    id: null,
    nombre: '',
    idEmpresa: '',
    alcance: 'Articulo',
    idArticulo: '',
    nombreArticulo: '',
    idGrupo: '',
    idCategoria: '',
    idFamilia: '',
    fechaDesde: '',
    fechaHasta: '',
    horaDesde: '',
    horaHasta: '',
    diasSemana: [],
    cantidadMinima: '',
    soloMultiplos: false,
    beneficio: 'Porcentaje',
    precioUnitario: '',
    porcentaje: '',
    importeFijo: '',
    prioridad: '0',
    acumulable: false,
    idsListas: [],
    activo: true,
    disponibleOnline: true,
  }
}

function numeroOpcional(valor: string): number | null {
  const limpio = valor.trim()
  return limpio === '' ? null : parsearNumeroDecimal(limpio)
}

function fechaOpcional(valor: string): string | null {
  return valor === '' ? null : valor
}

/** El `<input type="time">` entrega/espera `HH:mm`; el servidor entrega `TimeOnly` como
 * `HH:mm:ss` — se completan los segundos al enviar y se recortan al leer (`aValoresOferta`). */
function horaOpcional(valor: string): string | null {
  if (valor === '') return null
  return valor.length === 5 ? `${valor}:00` : valor
}

/**
 * `Formulario → AltaOferta`/`EdicionOferta` (mismo shape, ver el comentario del tipo en
 * `tipos.ts`). El campo no elegido de cada grupo exclusivo se fuerza a `null` sin importar lo
 * que haya quedado tipeado en el formulario — evita enviar un alcance/beneficio ambiguo tras
 * cambiar el radio.
 */
export function aAltaOferta(f: FormularioOferta): AltaOferta {
  const cantidadMinima = numeroOpcional(f.cantidadMinima)
  return {
    nombre: f.nombre.trim(),
    idEmpresa: f.idEmpresa === '' ? null : f.idEmpresa,
    idArticulo: f.alcance === 'Articulo' ? (f.idArticulo === '' ? null : f.idArticulo) : null,
    idGrupo: f.alcance === 'Grupo' ? (f.idGrupo === '' ? null : f.idGrupo) : null,
    idCategoria: f.alcance === 'Categoria' ? (f.idCategoria === '' ? null : f.idCategoria) : null,
    idFamilia: f.alcance === 'Familia' ? (f.idFamilia === '' ? null : f.idFamilia) : null,
    fechaDesde: fechaOpcional(f.fechaDesde),
    fechaHasta: fechaOpcional(f.fechaHasta),
    horaDesde: horaOpcional(f.horaDesde),
    horaHasta: horaOpcional(f.horaHasta),
    diasSemana: f.diasSemana.length === 0 ? null : [...f.diasSemana].sort((a, b) => a - b),
    cantidadMinima,
    soloMultiplos: cantidadMinima !== null && f.soloMultiplos,
    precioUnitario: f.beneficio === 'PrecioUnitario' ? numeroOpcional(f.precioUnitario) : null,
    porcentaje: f.beneficio === 'Porcentaje' ? numeroOpcional(f.porcentaje) : null,
    importeFijo: f.beneficio === 'ImporteFijo' ? numeroOpcional(f.importeFijo) : null,
    prioridad: parsearEnteroTipeado(f.prioridad) ?? 0,
    acumulable: f.acumulable,
    idsListas: f.idsListas,
    activo: f.activo,
    disponibleOnline: f.disponibleOnline,
  }
}

/**
 * `OfertaListado → Formulario`: deriva `alcance`/`beneficio` de cuál de las columnas
 * nullable de cada grupo viene completa (invariante ya garantizado por el servidor — nunca hay
 * cero ni más de una). `nombreArticulo` queda vacío acá a propósito: el detalle de oferta no
 * trae el nombre del artículo referenciado, así que el picker lo resuelve por separado cuando
 * el alcance es `Articulo` (ver `Ofertas.tsx`).
 */
export function aValoresOferta(o: OfertaListado): FormularioOferta {
  const alcance: AlcanceOferta =
    o.idArticulo !== null ? 'Articulo' : o.idGrupo !== null ? 'Grupo' : o.idFamilia != null ? 'Familia' : 'Categoria'
  const beneficio: BeneficioOferta =
    o.precioUnitario !== null ? 'PrecioUnitario' : o.porcentaje !== null ? 'Porcentaje' : 'ImporteFijo'

  return {
    id: o.id,
    nombre: o.nombre,
    idEmpresa: o.idEmpresa ?? '',
    alcance,
    idArticulo: o.idArticulo ?? '',
    nombreArticulo: '',
    idGrupo: o.idGrupo ?? '',
    idCategoria: o.idCategoria ?? '',
    idFamilia: o.idFamilia ?? '',
    fechaDesde: o.fechaDesde ?? '',
    fechaHasta: o.fechaHasta ?? '',
    horaDesde: o.horaDesde ? o.horaDesde.slice(0, 5) : '',
    horaHasta: o.horaHasta ? o.horaHasta.slice(0, 5) : '',
    diasSemana: o.diasSemana,
    cantidadMinima: o.cantidadMinima === null ? '' : String(o.cantidadMinima),
    soloMultiplos: o.soloMultiplos,
    beneficio,
    precioUnitario: o.precioUnitario === null ? '' : String(o.precioUnitario),
    porcentaje: o.porcentaje === null ? '' : String(o.porcentaje),
    importeFijo: o.importeFijo === null ? '' : String(o.importeFijo),
    prioridad: String(o.prioridad),
    acumulable: o.acumulable,
    idsListas: o.idsListas,
    activo: o.activo,
    disponibleOnline: o.disponibleOnline,
  }
}

const COPIA_ONLINE_REQUERIDO = 'Falta indicar si la oferta está disponible online. Recargá la pantalla e intentá de nuevo.'

/** Texto a mostrar ante el fallo de guardar una oferta. El `400 disponible_online_requerido` es defensivo y hoy no se
 * alcanza: el servidor toma un campo ausente como "sin cambios" y el formulario lo manda siempre; la copia queda por si
 * una versión del servidor vuelve a exigirlo. Cualquier otro rechazo de la API rinde el mensaje del servidor. */
export function copiaDeFalloAlGuardarOferta(e: unknown): string {
  if (!(e instanceof ErrorApi)) return 'No se pudo guardar la oferta.'
  if (e.codigo === 'disponible_online_requerido') return COPIA_ONLINE_REQUERIDO

  return e.message
}

/** Opciones del multi-select de listas objetivo: solo listas activas (una lista dada de baja no
 * tiene sentido como target nuevo, mismo criterio que el resto de los selectores de referencia
 * de la pantalla de artículos). */
export function opcionesDeLista(listas: ListaPrecioListado[]): { valor: string; etiqueta: string }[] {
  return listas.filter((l) => l.activo).map((l) => ({ valor: String(l.id), etiqueta: l.nombre }))
}

/** Opciones del selector de familia objetivo: todas las familias vivas del listado, sin filtrar por estado (el servidor no
 * mira `activo` ni si tiene miembros, igual que con los grupos). Una familia inactiva o sin artículos vivos se rotula para que
 * se entienda que una oferta sobre ella no alcanza a ningún artículo por ahora. */
export function opcionesDeFamiliaDeOferta(familias: FamiliaListado[]): { valor: number; etiqueta: string }[] {
  return familias.map((f) => {
    const avisos = [...(f.activo ? [] : ['inactiva']), ...(f.cantidadArticulos > 0 ? [] : ['sin artículos'])]
    return { valor: f.id, etiqueta: avisos.length === 0 ? f.nombre : `${f.nombre} (${avisos.join(', ')})` }
  })
}

/** Resumen de una sola línea del beneficio para la columna de listado — no persiste ni valida
 * nada, solo formatea lo que el servidor ya validó como exclusivo. */
export function resumenDeBeneficio(o: OfertaListado): string {
  const base = textoDeBeneficio(o)
  return o.soloMultiplos && o.cantidadMinima !== null ? `${base} (solo múltiplos de ${o.cantidadMinima})` : base
}

function textoDeBeneficio(o: OfertaListado): string {
  if (o.porcentaje !== null) return `${o.porcentaje}% de descuento`
  if (o.importeFijo !== null) return `${formatearImporte(o.importeFijo, { simbolo: true })} fijo por unidad`
  if (o.precioUnitario !== null) return `Precio unitario ${formatearImporte(o.precioUnitario, { simbolo: true })}`
  return '—'
}
