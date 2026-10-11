import { describe, expect, it } from 'vitest'
import { formularioVacio } from './FormularioArticulo'
import { codigoDeProveedorDeAlta } from './useFormularioDeArticulo'

describe('codigoDeProveedorDeAlta', () => {
  const conProveedor = { ...formularioVacio(), idProveedorHabitual: 7 as const }

  it('no envía nada si el formulario no ofrece el código', () => {
    expect(codigoDeProveedorDeAlta(conProveedor, null)).toBeNull()
  })

  it('no envía el código sin proveedor habitual: el servidor lo exige', () => {
    expect(codigoDeProveedorDeAlta(formularioVacio(), 'AB-1')).toBeNull()
  })

  it('recorta el código y trata el vacío como ausente', () => {
    expect(codigoDeProveedorDeAlta(conProveedor, '  AB-1 ')).toBe('AB-1')
    expect(codigoDeProveedorDeAlta(conProveedor, '   ')).toBeNull()
  })
})
