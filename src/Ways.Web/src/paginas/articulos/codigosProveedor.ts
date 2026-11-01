import { ErrorApi } from '../../api/cliente'
import type { ProveedorListado } from '../../api/tipos'
import { etiquetaDeProveedor } from './helpers'

export const LARGO_MAXIMO_CODIGO_PROVEEDOR = 50

const MENSAJES_POR_CODIGO: Record<string, string> = {
  codigo_proveedor_duplicado: 'Ese código ya lo tiene otro artículo de este proveedor.',
  codigo_requerido: 'El código es obligatorio.',
  codigo_muy_largo: `El código no puede superar los ${LARGO_MAXIMO_CODIGO_PROVEEDOR} caracteres.`,
}

/** Mensaje legible para un fallo de escritura de códigos de proveedor; `porDefecto` cubre lo que no es del servidor. */
export function mensajeDeErrorDeCodigoProveedor(e: unknown, porDefecto: string): string {
  if (!(e instanceof ErrorApi)) return porDefecto
  if (e.estado === 403) return 'Solo un administrador puede modificar los códigos de proveedor.'
  return MENSAJES_POR_CODIGO[e.codigo] ?? (e.message || porDefecto)
}

/** Nombre del proveedor para la tabla; uno dado de baja o fuera de la lista cargada no se inventa. */
export function nombreDeProveedorDeCodigo(
  idProveedor: number,
  proveedores: Pick<ProveedorListado, 'id' | 'razonSocial' | 'nombreFantasia'>[],
): string {
  const proveedor = proveedores.find((p) => p.id === idProveedor)
  return proveedor ? etiquetaDeProveedor(proveedor) : '(no disponible)'
}
