import { describe, expect, it } from 'vitest'
import type { EmpresaListado } from '../api/tipos'
import {
  aEdicionDeEmpresa,
  aFormularioDeEmpresa,
  alicuotasDePercepcionValidas,
  type FormularioDeEmpresa,
} from './empresaFormulario'

function empresa(sobrescribir: Partial<EmpresaListado> = {}): EmpresaListado {
  return {
    id: 10,
    idTenant: 1,
    razonSocial: 'Sur SRL',
    nombreFantasia: null,
    cuit: null,
    nombreTenant: 'Comercio Sur',
    alicuotaPercepcionIibb: null,
    alicuotaPercepcionIva: null,
    ...sobrescribir,
  }
}

function formulario(sobrescribir: Partial<FormularioDeEmpresa> = {}): FormularioDeEmpresa {
  return {
    id: 10,
    razonSocial: 'Sur SRL',
    nombreFantasia: '',
    cuit: '',
    alicuotaPercepcionIibb: '',
    alicuotaPercepcionIva: '',
    ...sobrescribir,
  }
}

describe('aFormularioDeEmpresa', () => {
  it('las alícuotas sin informar quedan vacías y las informadas como texto', () => {
    expect(aFormularioDeEmpresa(empresa())).toMatchObject({ alicuotaPercepcionIibb: '', alicuotaPercepcionIva: '' })
    expect(
      aFormularioDeEmpresa(empresa({ alicuotaPercepcionIibb: 3.5, alicuotaPercepcionIva: 0 })),
    ).toMatchObject({ alicuotaPercepcionIibb: '3.5', alicuotaPercepcionIva: '0' })
  })
})

describe('aEdicionDeEmpresa', () => {
  it('vacío es null, un número es el número y cero es cero (no ausencia)', () => {
    expect(aEdicionDeEmpresa(formulario()).alicuotaPercepcionIibb).toBeNull()
    expect(aEdicionDeEmpresa(formulario({ alicuotaPercepcionIibb: '3,5' })).alicuotaPercepcionIibb).toBe(3.5)
    expect(aEdicionDeEmpresa(formulario({ alicuotaPercepcionIva: '0' })).alicuotaPercepcionIva).toBe(0)
  })

  it('conserva los campos descriptivos, con vacío a null', () => {
    expect(aEdicionDeEmpresa(formulario({ nombreFantasia: 'Sur', cuit: '20-1' }))).toEqual({
      razonSocial: 'Sur SRL',
      nombreFantasia: 'Sur',
      cuit: '20-1',
      alicuotaPercepcionIibb: null,
      alicuotaPercepcionIva: null,
    })
  })
})

describe('alicuotasDePercepcionValidas', () => {
  it('acepta vacío, los extremos 0 y 100 y hasta tres decimales', () => {
    expect(alicuotasDePercepcionValidas(formulario())).toBe(true)
    expect(alicuotasDePercepcionValidas(formulario({ alicuotaPercepcionIibb: '0', alicuotaPercepcionIva: '100' }))).toBe(true)
    expect(alicuotasDePercepcionValidas(formulario({ alicuotaPercepcionIibb: '2.512' }))).toBe(true)
  })

  it.each(['-0.001', '100.001', '101', '1.2345', 'abc'])('rechaza %s', (texto) => {
    expect(alicuotasDePercepcionValidas(formulario({ alicuotaPercepcionIibb: texto }))).toBe(false)
    expect(alicuotasDePercepcionValidas(formulario({ alicuotaPercepcionIva: texto }))).toBe(false)
  })
})
