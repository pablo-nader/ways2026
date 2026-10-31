import { describe, expect, it } from 'vitest'
import { ErrorApi } from '../../api/cliente'
import { mensajeDeErrorDeCodigoProveedor, nombreDeProveedorDeCodigo } from './codigosProveedor'

describe('mensajeDeErrorDeCodigoProveedor', () => {
  it('traduce el 409 de duplicado', () => {
    expect(mensajeDeErrorDeCodigoProveedor(new ErrorApi(409, 'codigo_proveedor_duplicado', 'x'), 'def')).toBe(
      'Ese código ya lo tiene otro artículo de este proveedor.',
    )
  })

  it('traduce los 400 de código requerido y muy largo', () => {
    expect(mensajeDeErrorDeCodigoProveedor(new ErrorApi(400, 'codigo_requerido', 'x'), 'def')).toBe('El código es obligatorio.')
    expect(mensajeDeErrorDeCodigoProveedor(new ErrorApi(400, 'codigo_muy_largo', 'x'), 'def')).toBe(
      'El código no puede superar los 50 caracteres.',
    )
  })

  it('explica el 403', () => {
    expect(mensajeDeErrorDeCodigoProveedor(new ErrorApi(403, 'prohibido', 'x'), 'def')).toContain('administrador')
  })

  it('usa el mensaje del servidor para un código desconocido y el de respaldo si no es un ErrorApi', () => {
    expect(mensajeDeErrorDeCodigoProveedor(new ErrorApi(404, 'no_encontrado', 'No existe.'), 'def')).toBe('No existe.')
    expect(mensajeDeErrorDeCodigoProveedor(new Error('red'), 'def')).toBe('def')
  })
})

describe('nombreDeProveedorDeCodigo', () => {
  const proveedores = [
    { id: 1, razonSocial: 'Acme SA', nombreFantasia: 'Acme' },
    { id: 2, razonSocial: 'Beta SRL', nombreFantasia: null },
  ]

  it('usa el nombre de fantasía si existe y la razón social si no', () => {
    expect(nombreDeProveedorDeCodigo(1, proveedores)).toBe('Acme')
    expect(nombreDeProveedorDeCodigo(2, proveedores)).toBe('Beta SRL')
  })

  it('marca como no disponible al proveedor que no está en la lista', () => {
    expect(nombreDeProveedorDeCodigo(9, proveedores)).toBe('(no disponible)')
  })
})
