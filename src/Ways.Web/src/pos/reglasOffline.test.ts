import { describe, expect, it } from 'vitest'
import { clienteAdmitidoOffline, medioAdmitidoOffline, pagosAdmitidosOffline } from './reglasOffline'
import type { ComportamientoMedioPago } from '../api/tipos'

describe('medioAdmitidoOffline — regla dura "offline es solo efectivo"', () => {
  it.each([
    ['Efectivo', true],
    ['Electronico', false],
    ['CuentaCorriente', false],
  ] as [ComportamientoMedioPago, boolean][])('%s → %s', (comportamiento, esperado) => {
    expect(medioAdmitidoOffline(comportamiento)).toBe(esperado)
  })
})

describe('pagosAdmitidosOffline', () => {
  it('true con un solo pago en efectivo', () => {
    expect(pagosAdmitidosOffline([{ comportamiento: 'Efectivo' }])).toBe(true)
  })

  it('false con la lista vacía (nunca "todos" vacuamente true)', () => {
    expect(pagosAdmitidosOffline([])).toBe(false)
  })

  it('false si CUALQUIER pago no es efectivo, aunque haya otros que sí lo son', () => {
    expect(pagosAdmitidosOffline([{ comportamiento: 'Efectivo' }, { comportamiento: 'Electronico' }])).toBe(false)
  })

  it('false con un único pago de cuenta corriente', () => {
    expect(pagosAdmitidosOffline([{ comportamiento: 'CuentaCorriente' }])).toBe(false)
  })
})

describe('clienteAdmitidoOffline — regla dura "offline solo cotiza al Consumidor Final"', () => {
  it('true para el Consumidor Final', () => {
    expect(clienteAdmitidoOffline({ esConsumidorFinal: true })).toBe(true)
  })

  it('false para cualquier otro cliente', () => {
    expect(clienteAdmitidoOffline({ esConsumidorFinal: false })).toBe(false)
  })

  it('false sin cliente seleccionado (null)', () => {
    expect(clienteAdmitidoOffline(null)).toBe(false)
  })
})
