/**
 * Cliente del personal (back office): ABM dedicado, mismo shape que `proveedores.ts`. Los dos
 * listados de referencia (`usuariosParaVincular`, `clientesParaVincular`) alimentan los selectores
 * de vínculo del formulario y son lecturas de solo lectura de los catálogos de usuarios y clientes.
 */
import { api } from './cliente'
import { construirQueryDePadron, type ConsultaDePadron } from './padron'
import type { ClienteListado, DatosDePersonal, PaginaDe, PersonalListado, UsuarioListado } from './tipos'

/** Tope de filas por página que acepta el servidor (`Math.Clamp(tamanio, 1, 200)`). */
export const TAMANIO_MAXIMO_DE_PAGINA = 200

/** Claves de `orden` que acepta `GET /api/personal`: una por cada columna de datos de la grilla. */
export const COLUMNAS_ORDENABLES_DE_PERSONAL = ['nombre', 'valorHora', 'saldo', 'usuario', 'cliente'] as const

export type ColumnaOrdenableDePersonal = (typeof COLUMNAS_ORDENABLES_DE_PERSONAL)[number]

export function esColumnaOrdenableDePersonal(columna: string): columna is ColumnaOrdenableDePersonal {
  return (COLUMNAS_ORDENABLES_DE_PERSONAL as readonly string[]).includes(columna)
}

/** Lo que muestran los controles de filtro; cada clave es un parámetro de `GET /api/personal`. `busqueda` es la caja
 * general; el valor hora y el saldo son rangos inclusivos. */
export type FiltrosDePersonal = {
  busqueda: string
  nombre: string
  valorHoraDesde: number | null
  valorHoraHasta: number | null
  saldoDesde: number | null
  saldoHasta: number | null
  usuario: string
  cliente: string
}

export const FILTROS_DE_PERSONAL_VACIOS: FiltrosDePersonal = {
  busqueda: '',
  nombre: '',
  valorHoraDesde: null,
  valorHoraHasta: null,
  saldoDesde: null,
  saldoHasta: null,
  usuario: '',
  cliente: '',
}

export type ConsultaDePersonal = ConsultaDePadron<FiltrosDePersonal, ColumnaOrdenableDePersonal>

export const clienteDePersonal = {
  /** El padrón: filtra, ordena y pagina en el servidor. */
  listarGrilla: (consulta: ConsultaDePersonal) =>
    api.get<PaginaDe<PersonalListado>>(`/personal${construirQueryDePadron(consulta)}`),
  crear: (datos: DatosDePersonal) => api.post<PersonalListado>('/personal', datos),
  actualizar: (id: number, datos: DatosDePersonal) => api.put<PersonalListado>(`/personal/${id}`, datos),
  eliminar: (id: number) => api.delete(`/personal/${id}`),
  /** `GET /api/usuarios` (solo Admin y Root): los usuarios que se pueden vincular a un personal. */
  usuariosParaVincular: (busqueda = '') =>
    api.get<PaginaDe<UsuarioListado>>(`/usuarios?${parametrosDeReferencia(busqueda)}`),
  /** `GET /api/clientes`: los clientes que se pueden vincular a un personal. */
  clientesParaVincular: (busqueda = '') =>
    api.get<PaginaDe<ClienteListado>>(`/clientes?${parametrosDeReferencia(busqueda)}`),
}

function parametrosDeReferencia(busqueda: string): string {
  const parametros = new URLSearchParams()
  if (busqueda) parametros.set('busqueda', busqueda)
  parametros.set('tamanio', String(TAMANIO_MAXIMO_DE_PAGINA))
  return parametros.toString()
}
