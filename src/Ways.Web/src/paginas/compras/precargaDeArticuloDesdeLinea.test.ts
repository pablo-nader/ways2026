import { describe, expect, it } from 'vitest'
import { precargaDeArticuloDesdeLinea, type DatosDeLineaParaElAlta } from './precargaDeArticuloDesdeLinea'

function linea(sobrescribir: Partial<DatosDeLineaParaElAlta> = {}): DatosDeLineaParaElAlta {
  return {
    descripcion: '  Yerba nueva ',
    codigoProveedor: ' AB-1 ',
    idAlicuotaIva: 4,
    importe: 12.3456,
    idProveedor: 9,
    discriminaIva: false,
    preciosFinales: false,
    ...sobrescribir,
  }
}

describe('precargaDeArticuloDesdeLinea', () => {
  it('lleva al formulario lo que la fila ya tiene: nombre, proveedor habitual, alícuota, costo y código', () => {
    expect(precargaDeArticuloDesdeLinea(linea())).toEqual({
      campos: {
        nombre: 'Yerba nueva',
        idProveedorHabitual: 9,
        idAlicuotaIva: 4,
        costoLista: '12.35',
        costoListaPor: 'Unidad',
        costoListaIncluyeIva: true,
      },
      codigoProveedor: 'AB-1',
    })
  })

  it('redondea el costo a 2 decimales, que es lo que admite el servidor, y deja vacío el que no se tipeó', () => {
    expect(precargaDeArticuloDesdeLinea(linea({ importe: 12.344 })).campos?.costoLista).toBe('12.34')
    expect(precargaDeArticuloDesdeLinea(linea({ importe: 100.5 })).campos?.costoLista).toBe('100.5')
    expect(precargaDeArticuloDesdeLinea(linea({ importe: null })).campos?.costoLista).toBe('')
  })

  it('declara el costo sin IVA solo cuando el comprobante discrimina IVA y los precios son netos', () => {
    expect(precargaDeArticuloDesdeLinea(linea({ discriminaIva: true, preciosFinales: false })).campos?.costoListaIncluyeIva).toBe(false)
    expect(precargaDeArticuloDesdeLinea(linea({ discriminaIva: true, preciosFinales: true })).campos?.costoListaIncluyeIva).toBe(true)
    expect(precargaDeArticuloDesdeLinea(linea({ discriminaIva: false, preciosFinales: false })).campos?.costoListaIncluyeIva).toBe(true)
  })

  it('no pisa lo que el formulario resuelve solo: sin proveedor ni alícuota en la fila, esos campos no viajan', () => {
    const { campos } = precargaDeArticuloDesdeLinea(linea({ idProveedor: null, idAlicuotaIva: '' }))
    expect(campos).not.toHaveProperty('idProveedorHabitual')
    expect(campos).not.toHaveProperty('idAlicuotaIva')
  })

  it('recorta nombre y código al largo que admite el formulario', () => {
    const precarga = precargaDeArticuloDesdeLinea(linea({ descripcion: 'N'.repeat(200), codigoProveedor: 'C'.repeat(80) }))
    expect(precarga.campos?.nombre).toHaveLength(150)
    expect(precarga.codigoProveedor).toHaveLength(50)
  })
})
