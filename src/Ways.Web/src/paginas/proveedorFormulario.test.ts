import { describe, expect, it } from 'vitest'
import type { ProveedorListado } from '../api/tipos'
import { aAlta, aFormulario, formularioVacio } from './proveedorFormulario'

function proveedor(sobrescribir: Partial<ProveedorListado> = {}): ProveedorListado {
  return {
    id: 4,
    razonSocial: 'Distribuidora SA',
    nombreFantasia: null,
    cuit: null,
    idCondicionFiscal: 1,
    domicilio: null,
    telefono: null,
    email: null,
    vendedor: null,
    celularVendedor: null,
    supervisor: null,
    celularSupervisor: null,
    margen: null,
    observaciones: null,
    activo: true,
    idEmpresa: null,
    percibeIibb: false,
    percibeIva: false,
    preciosIncluyenIva: false,
    ...sobrescribir,
  }
}

describe('formularioVacio', () => {
  it('un proveedor nuevo arranca sin percepciones ni precios con IVA incluido', () => {
    expect(formularioVacio()).toMatchObject({ percibeIibb: false, percibeIva: false, preciosIncluyenIva: false })
  })
})

describe('aFormulario', () => {
  it('copia los tres flags del proveedor', () => {
    expect(aFormulario(proveedor({ percibeIibb: true, percibeIva: false, preciosIncluyenIva: true }))).toMatchObject({
      percibeIibb: true,
      percibeIva: false,
      preciosIncluyenIva: true,
    })
  })
})

describe('aAlta', () => {
  it('manda los tres flags tal cual salen del formulario', () => {
    const alta = aAlta({ ...aFormulario(proveedor()), percibeIibb: true, percibeIva: true, preciosIncluyenIva: true })

    expect(alta).toMatchObject({ percibeIibb: true, percibeIva: true, preciosIncluyenIva: true })
  })

  it('un flag apagado viaja como false, nunca ausente', () => {
    const alta = aAlta(aFormulario(proveedor({ percibeIibb: true })))
    const apagada = aAlta({ ...aFormulario(proveedor({ percibeIibb: true })), percibeIibb: false })

    expect(alta.percibeIibb).toBe(true)
    expect(apagada.percibeIibb).toBe(false)
    expect(Object.keys(apagada)).toEqual(expect.arrayContaining(['percibeIibb', 'percibeIva', 'preciosIncluyenIva']))
  })

  it('conserva el resto del mapeo: vacío a null, condición fiscal requerida, margen numérico', () => {
    const alta = aAlta({ ...aFormulario(proveedor()), nombreFantasia: '  ', margen: '12.5', idCondicionFiscal: '' })

    expect(alta.nombreFantasia).toBeNull()
    expect(alta.margen).toBe(12.5)
    expect(alta.idCondicionFiscal).toBe(0)
  })
})
