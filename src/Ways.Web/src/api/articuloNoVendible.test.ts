import { describe, expect, it } from 'vitest'
import { CODIGO_ARTICULO_NO_VENDIBLE, copiaDeArticuloNoVendible } from './articuloNoVendible'
import { ErrorApi } from './cliente'

describe('copiaDeArticuloNoVendible', () => {
  it('el código del contrato es articulo_no_vendible', () => {
    expect(CODIGO_ARTICULO_NO_VENDIBLE).toBe('articulo_no_vendible')
  })

  it('usa el mensaje del servidor, que nombra el artículo, y le suma qué hacer', () => {
    const copia = copiaDeArticuloNoVendible(
      new ErrorApi(409, 'articulo_no_vendible', 'El artículo A0007 no está disponible a la venta.'),
    )

    expect(copia).toBe('El artículo A0007 no está disponible a la venta. Sacalo para poder continuar.')
  })

  it('sin mensaje del servidor cae en una frase genérica', () => {
    expect(copiaDeArticuloNoVendible(new ErrorApi(409, 'articulo_no_vendible', '   '))).toBe(
      'Hay un artículo que no está disponible a la venta. Sacalo para poder continuar.',
    )
  })

  it('cualquier otro código de la API devuelve null y no se reescribe', () => {
    expect(copiaDeArticuloNoVendible(new ErrorApi(409, 'stock_insuficiente', 'Sin stock.'))).toBeNull()
    expect(copiaDeArticuloNoVendible(new ErrorApi(400, 'solicitud_invalida', 'Mal.'))).toBeNull()
  })

  it('un error que no es de la API devuelve null', () => {
    expect(copiaDeArticuloNoVendible(new Error('articulo_no_vendible'))).toBeNull()
    expect(copiaDeArticuloNoVendible(undefined)).toBeNull()
  })
})
