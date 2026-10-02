import { afterEach, describe, expect, it } from 'vitest'
import { guardarTurnoConfirmadoLocal, leerTurnoConfirmadoLocal, olvidarTurnoConfirmadoLocal } from './turnoConfirmadoLocal'
import type { TurnoResumen } from '../api/tipos'

function turnoFixture(sobrescribir: Partial<TurnoResumen> = {}): TurnoResumen {
  return {
    id: 900,
    idPuntoVenta: 7,
    idEmpleadoApertura: 3,
    idEmpleadoCierre: null,
    fechaApertura: '2026-10-01T12:00:00Z',
    fechaCierre: null,
    fondoInicial: 500,
    estado: 'Abierto',
    observaciones: null,
    ...sobrescribir,
  }
}

afterEach(() => {
  localStorage.clear()
})

describe('turnoConfirmadoLocal', () => {
  it('guarda y vuelve a leer el turno abierto de ese punto de venta', () => {
    guardarTurnoConfirmadoLocal(7, turnoFixture())

    expect(leerTurnoConfirmadoLocal(7)).toEqual(turnoFixture())
    expect(leerTurnoConfirmadoLocal(8)).toBeNull()
  })

  it('con null borra el guardado', () => {
    guardarTurnoConfirmadoLocal(7, turnoFixture())
    guardarTurnoConfirmadoLocal(7, null)

    expect(leerTurnoConfirmadoLocal(7)).toBeNull()
  })

  it('nunca guarda un turno cerrado ni uno de otro punto de venta: borra lo que había', () => {
    guardarTurnoConfirmadoLocal(7, turnoFixture())
    guardarTurnoConfirmadoLocal(7, turnoFixture({ estado: 'Cerrado' }))
    expect(leerTurnoConfirmadoLocal(7)).toBeNull()

    guardarTurnoConfirmadoLocal(7, turnoFixture({ idPuntoVenta: 8 }))
    expect(leerTurnoConfirmadoLocal(7)).toBeNull()
  })

  it.each([
    ['JSON roto', '{no es json'],
    ['de otro punto de venta', JSON.stringify(turnoFixture({ idPuntoVenta: 8 }))],
    ['cerrado', JSON.stringify(turnoFixture({ estado: 'Cerrado' }))],
    ['sin id', JSON.stringify({ ...turnoFixture(), id: undefined })],
  ])('lo guardado %s se lee como ninguno', (_titulo, crudo) => {
    localStorage.setItem('ways.pos.turnoConfirmado.7', crudo)
    expect(leerTurnoConfirmadoLocal(7)).toBeNull()
  })
})

describe('olvidarTurnoConfirmadoLocal', () => {
  it('borra el guardado solo si es el turno que se cerró', () => {
    guardarTurnoConfirmadoLocal(7, turnoFixture({ id: 900 }))

    olvidarTurnoConfirmadoLocal(7, 901)
    expect(leerTurnoConfirmadoLocal(7)?.id).toBe(900)

    olvidarTurnoConfirmadoLocal(7, 900)
    expect(leerTurnoConfirmadoLocal(7)).toBeNull()
  })
})
