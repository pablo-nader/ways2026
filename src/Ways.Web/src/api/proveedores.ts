/**
 * Cliente de proveedores (stage-2-clientes-proveedores): ABM dedicado, no la máquina genérica
 * de catálogos (design decision 1) — mismo shape que `clientes.ts`.
 */
import { api } from './cliente'
import { construirQueryDePadron, type ConsultaDePadron } from './padron'
import type { AltaProveedor, EdicionProveedor, OpcionDeProveedor, PaginaDe, ProveedorListado } from './tipos'

/** Claves de `orden` que acepta `GET /api/proveedores`: una por cada columna de datos de la grilla. */
export const COLUMNAS_ORDENABLES_DE_PROVEEDORES = [
  'razonSocial',
  'cuit',
  'telefono',
  'email',
  'margen',
  'estado',
] as const

export type ColumnaOrdenableDeProveedores = (typeof COLUMNAS_ORDENABLES_DE_PROVEEDORES)[number]

export function esColumnaOrdenableDeProveedores(columna: string): columna is ColumnaOrdenableDeProveedores {
  return (COLUMNAS_ORDENABLES_DE_PROVEEDORES as readonly string[]).includes(columna)
}

/** Lo que muestran los controles de filtro; cada clave es un parámetro de `GET /api/proveedores`. `busqueda` es la caja
 * general; `cuitContiene` filtra por el texto de la celda (el `cuit` del servidor es la igualdad exacta por dígitos);
 * `activo` es `'true'`, `'false'` o vacío; el margen es un rango inclusivo. */
export type FiltrosDeProveedores = {
  busqueda: string
  razonSocial: string
  cuitContiene: string
  telefono: string
  email: string
  margenDesde: number | null
  margenHasta: number | null
  activo: string
}

export const FILTROS_DE_PROVEEDORES_VACIOS: FiltrosDeProveedores = {
  busqueda: '',
  razonSocial: '',
  cuitContiene: '',
  telefono: '',
  email: '',
  margenDesde: null,
  margenHasta: null,
  activo: '',
}

export type ConsultaDeProveedores = ConsultaDePadron<FiltrosDeProveedores, ColumnaOrdenableDeProveedores>

export const clienteDeProveedores = {
  /** El padrón: filtra, ordena y pagina en el servidor. */
  listarGrilla: (consulta: ConsultaDeProveedores) =>
    api.get<PaginaDe<ProveedorListado>>(`/proveedores${construirQueryDePadron(consulta)}`),
  listar: (busqueda: string, incluirEliminados: boolean) => {
    const parametros = new URLSearchParams()
    if (busqueda) parametros.set('busqueda', busqueda)
    if (incluirEliminados) parametros.set('incluirEliminados', 'true')
    const cadena = parametros.toString()
    return api.get<PaginaDe<ProveedorListado>>(`/proveedores${cadena ? `?${cadena}` : ''}`)
  },
  /** `GET /api/proveedores/opciones` (JD-A1, judgment-day, `Politicas.OperacionDePos`): proyección
   * mínima para selectores fuera de la gestión de catálogo — mismo criterio de least-privilege
   * que separa `saldo` del ABM completo. Nunca usar `listar` para poblar un selector accesible a
   * un Vendedor: expone margen/cuit/datos de contacto. */
  opciones: () => api.get<OpcionDeProveedor[]>('/proveedores/opciones'),
  /** `GET /api/proveedores/{id}` — resuelve la identidad de un proveedor sin pasar por el
   * listado (stage-15-cc-proveedores-ledger, Slice 6): único camino de `CuentaCorrienteDeProveedor`
   * cuando se llega por URL directa, sin `location.state` (mismo criterio que
   * `clienteDeClientes.obtener`). */
  obtener: (id: number) => api.get<ProveedorListado>(`/proveedores/${id}`),
  crear: (datos: AltaProveedor) => api.post<ProveedorListado>('/proveedores', datos),
  actualizar: (id: number, datos: EdicionProveedor) => api.put<ProveedorListado>(`/proveedores/${id}`, datos),
  eliminar: (id: number) => api.delete(`/proveedores/${id}`),
}
