/**
 * Listado de usuarios del back office: filtra, ordena y pagina en el servidor (`GET /api/usuarios`). Las altas, las
 * ediciones y las bajas las hace la pantalla directamente contra el recurso.
 */
import { api } from './cliente'
import { construirQueryDePadron, type ConsultaDePadron } from './padron'
import type { PaginaDe, UsuarioListado } from './tipos'

/** Claves de `orden` que acepta `GET /api/usuarios`: una por cada columna de datos de la grilla. `tenant` es solo del
 * actor de plataforma: para cualquier otro el servidor responde 400 `orden_no_permitido`. */
export const COLUMNAS_ORDENABLES_DE_USUARIOS = [
  'id',
  'usuario',
  'mail',
  'tenant',
  'rol',
  'estado',
  'ultimaConexion',
] as const

export type ColumnaOrdenableDeUsuarios = (typeof COLUMNAS_ORDENABLES_DE_USUARIOS)[number]

export function esColumnaOrdenableDeUsuarios(columna: string): columna is ColumnaOrdenableDeUsuarios {
  return (COLUMNAS_ORDENABLES_DE_USUARIOS as readonly string[]).includes(columna)
}

/** Lo que muestran los controles de filtro; cada clave es un parámetro de `GET /api/usuarios`. `busqueda` es la caja
 * general; `id` y `rolId` son exactos; `estado` es `Activo`, `Inactivo` o `Bloqueado`; `conectado` es `'true'` (alguna vez
 * inició sesión), `'false'` o vacío; `tenant` es un id o `sin-tenant` y solo lo manda el actor de plataforma. */
export type FiltrosDeUsuarios = {
  busqueda: string
  id: string
  usuario: string
  mail: string
  tenant: string
  rolId: string
  estado: string
  conectado: string
}

export const FILTROS_DE_USUARIOS_VACIOS: FiltrosDeUsuarios = {
  busqueda: '',
  id: '',
  usuario: '',
  mail: '',
  tenant: '',
  rolId: '',
  estado: '',
  conectado: '',
}

export type ConsultaDeUsuarios = ConsultaDePadron<FiltrosDeUsuarios, ColumnaOrdenableDeUsuarios>

export const clienteDeUsuarios = {
  listarGrilla: (consulta: ConsultaDeUsuarios) =>
    api.get<PaginaDe<UsuarioListado>>(`/usuarios${construirQueryDePadron(consulta)}`),
}
