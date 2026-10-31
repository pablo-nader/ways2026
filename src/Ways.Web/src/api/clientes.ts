/**
 * Cliente de clientes (stage-2-clientes-proveedores): ABM dedicado, no la máquina genérica de
 * catálogos (design decision 1). `listasDePrecioAsignables` es una referencia de solo lectura
 * para el selector del formulario — no un ABM de `listas_precio` (sin ABM propio esta etapa).
 */
import { api } from './cliente'
import { construirQueryDePadron, type ConsultaDePadron } from './padron'
import type { AltaCliente, ClienteListado, EdicionCliente, ListaPrecioAsignable, PaginaDe } from './tipos'

/** Texto de la celda "Cliente" de un listado de documentos: lo que resolvió el servidor para esa fila, nunca
 * una lista de referencia acotada. Un cliente dado de baja llega sin número ni nombre y se rotula por su id. */
export function etiquetaDeClienteDeFila(fila: {
  idCliente: number
  numeroCliente?: number | null
  nombreCliente?: string | null
}): string {
  if (fila.numeroCliente == null || fila.nombreCliente == null) return `Cliente #${fila.idCliente}`
  return `#${fila.numeroCliente} — ${fila.nombreCliente}`
}

/** Claves de `orden` que acepta `GET /api/clientes`: una por cada columna de datos de la grilla. */
export const COLUMNAS_ORDENABLES_DE_CLIENTES = [
  'numero',
  'nombre',
  'documento',
  'telefono',
  'email',
  'estado',
] as const

export type ColumnaOrdenableDeClientes = (typeof COLUMNAS_ORDENABLES_DE_CLIENTES)[number]

export function esColumnaOrdenableDeClientes(columna: string): columna is ColumnaOrdenableDeClientes {
  return (COLUMNAS_ORDENABLES_DE_CLIENTES as readonly string[]).includes(columna)
}

/** Lo que muestran los controles de filtro; cada clave es un parámetro de `GET /api/clientes`. `busqueda` es la caja
 * general; `activo` es `'true'`, `'false'` o vacío. */
export type FiltrosDeClientes = {
  busqueda: string
  numero: string
  nombre: string
  documento: string
  telefono: string
  email: string
  activo: string
}

export const FILTROS_DE_CLIENTES_VACIOS: FiltrosDeClientes = {
  busqueda: '',
  numero: '',
  nombre: '',
  documento: '',
  telefono: '',
  email: '',
  activo: '',
}

export type ConsultaDeClientes = ConsultaDePadron<FiltrosDeClientes, ColumnaOrdenableDeClientes>

export const clienteDeClientes = {
  /** El padrón: filtra, ordena y pagina en el servidor. */
  listarGrilla: (consulta: ConsultaDeClientes) =>
    api.get<PaginaDe<ClienteListado>>(`/clientes${construirQueryDePadron(consulta)}`),
  /** Buscador de clientes de las demás pantallas (POS, cuenta corriente): sin filtros por columna. */
  listar: (busqueda: string, incluirEliminados: boolean) => {
    const parametros = new URLSearchParams()
    if (busqueda) parametros.set('busqueda', busqueda)
    if (incluirEliminados) parametros.set('incluirEliminados', 'true')
    const cadena = parametros.toString()
    return api.get<PaginaDe<ClienteListado>>(`/clientes${cadena ? `?${cadena}` : ''}`)
  },
  obtener: (id: number) => api.get<ClienteListado>(`/clientes/${id}`),
  crear: (datos: AltaCliente) => api.post<ClienteListado>('/clientes', datos),
  actualizar: (id: number, datos: EdicionCliente) => api.put<ClienteListado>(`/clientes/${id}`, datos),
  eliminar: (id: number) => api.delete(`/clientes/${id}`),
  listasDePrecioAsignables: () => api.get<ListaPrecioAsignable[]>('/listas-precio'),
}
