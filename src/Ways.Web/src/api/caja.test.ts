import { describe, expect, it, vi } from 'vitest'

const apiGetMock = vi.fn(() => Promise.resolve(undefined))

vi.mock('./cliente', () => ({
  api: { get: (...args: unknown[]) => apiGetMock(...(args as [])) },
}))

const { aSolicitudDeMovimiento, clienteDeCaja, importeValidoParaTipo, motivoValido, rutasDeExportacionDeCaja } = await import('./caja')

describe('motivoValido', () => {
  it('rechaza un motivo vacío', () => {
    expect(motivoValido('')).toBe(false)
  })

  it('rechaza un motivo de menos de 5 caracteres tras recortar espacios', () => {
    expect(motivoValido('  abc  ')).toBe(false)
  })

  it('acepta un motivo de exactamente 5 caracteres (el límite)', () => {
    expect(motivoValido('abcde')).toBe(true)
  })

  it('acepta un motivo largo', () => {
    expect(motivoValido('cambio de caja fuerte')).toBe(true)
  })

  it('recorta espacios antes de contar la longitud', () => {
    expect(motivoValido('   ab   ')).toBe(false)
    expect(motivoValido('   abcde   ')).toBe(true)
  })
})

describe('importeValidoParaTipo', () => {
  it('apertura_cajon solo acepta exactamente 0', () => {
    expect(importeValidoParaTipo('AperturaCajon', 0)).toBe(true)
    expect(importeValidoParaTipo('AperturaCajon', 50)).toBe(false)
    expect(importeValidoParaTipo('AperturaCajon', -1)).toBe(false)
  })

  it('retiro exige un importe positivo', () => {
    expect(importeValidoParaTipo('Retiro', 200)).toBe(true)
    expect(importeValidoParaTipo('Retiro', 0)).toBe(false)
    expect(importeValidoParaTipo('Retiro', -10)).toBe(false)
  })

  it('refuerzo exige un importe positivo', () => {
    expect(importeValidoParaTipo('Refuerzo', 100)).toBe(true)
    expect(importeValidoParaTipo('Refuerzo', 0)).toBe(false)
  })

  it('un importe no finito nunca es válido para retiro/refuerzo', () => {
    expect(importeValidoParaTipo('Retiro', Number.NaN)).toBe(false)
  })
})

describe('aSolicitudDeMovimiento', () => {
  it('retiro conserva el importe tipeado y recorta el motivo', () => {
    const resultado = aSolicitudDeMovimiento('Retiro', '200', '  cambio de caja fuerte  ')

    expect(resultado).toEqual({ tipo: 'Retiro', importe: 200, motivo: 'cambio de caja fuerte' })
  })

  it('apertura_cajon fuerza importe 0 sin importar lo que traiga el campo', () => {
    const resultado = aSolicitudDeMovimiento('AperturaCajon', '999', 'conteo inicial de turno')

    expect(resultado).toEqual({ tipo: 'AperturaCajon', importe: 0, motivo: 'conteo inicial de turno' })
  })

  it('un importe con texto no numérico se convierte en NaN — la validación previa (importeValidoParaTipo) debe rechazarlo antes de llegar acá', () => {
    const resultado = aSolicitudDeMovimiento('Refuerzo', 'abc', 'motivo válido')

    expect(Number.isNaN(resultado.importe)).toBe(true)
  })
})

// ---- stage-11-exportacion-reportes, Slice 6b: clienteDeCaja.obtenerDetalle + la ruta de export
// del Z-report -----------------------------------------------------------------------------

describe('clienteDeCaja.obtenerDetalle', () => {
  it('pega contra GET /caja/turnos/{id}/detalle', async () => {
    apiGetMock.mockClear()
    await clienteDeCaja.obtenerDetalle(412)

    expect(apiGetMock).toHaveBeenCalledWith('/caja/turnos/412/detalle')
  })
})

describe('rutasDeExportacionDeCaja.detalleDeTurno', () => {
  it('arma la ruta del export sibling con formato=xlsx', () => {
    expect(rutasDeExportacionDeCaja.detalleDeTurno(412)).toBe('/caja/turnos/412/detalle/export?formato=xlsx')
  })
})
