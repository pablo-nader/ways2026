import { describe, expect, it } from 'vitest'
import { clienteAdmitidoOffline, medioAdmitidoOffline, pagosAdmitidosOffline } from './reglasOffline'
import type { ClienteDeInstantanea, ComportamientoMedioPago } from '../api/tipos'

describe('medioAdmitidoOffline — efectivo y electrónico siempre, cuenta corriente solo para un cliente identificado', () => {
  it.each([
    ['Efectivo', true, true],
    ['Electronico', true, true],
    ['CuentaCorriente', true, false],
    ['Efectivo', false, true],
    ['Electronico', false, true],
    ['CuentaCorriente', false, true],
  ] as [ComportamientoMedioPago, boolean, boolean][])('%s con esConsumidorFinal=%s → %s', (comportamiento, esConsumidorFinal, esperado) => {
    expect(medioAdmitidoOffline(comportamiento, esConsumidorFinal)).toBe(esperado)
  })
})

describe('pagosAdmitidosOffline', () => {
  it('true con un solo pago en efectivo', () => {
    expect(pagosAdmitidosOffline([{ comportamiento: 'Efectivo' }], true)).toBe(true)
  })

  it('false con la lista vacía (nunca "todos" vacuamente true)', () => {
    expect(pagosAdmitidosOffline([], false)).toBe(false)
  })

  it('true con un pago dividido entre efectivo y electrónico', () => {
    expect(pagosAdmitidosOffline([{ comportamiento: 'Efectivo' }, { comportamiento: 'Electronico' }], true)).toBe(true)
  })

  it('false si el Consumidor Final mezcla cuenta corriente con un medio admitido', () => {
    expect(pagosAdmitidosOffline([{ comportamiento: 'Efectivo' }, { comportamiento: 'CuentaCorriente' }], true)).toBe(false)
  })

  it('true si un cliente identificado mezcla cuenta corriente con efectivo', () => {
    expect(pagosAdmitidosOffline([{ comportamiento: 'Efectivo' }, { comportamiento: 'CuentaCorriente' }], false)).toBe(true)
  })
})

describe('clienteAdmitidoOffline — el Consumidor Final siempre, otro cliente solo si está en la instantánea', () => {
  const enInstantanea: ClienteDeInstantanea = {
    idCliente: 42,
    numero: 42,
    nombre: 'Cliente con cuenta',
    apellido: null,
    razonSocial: null,
    tipoDocumento: null,
    numeroDocumento: null,
    idCondicionFiscal: 1,
    idEmpresa: null,
    idListaPrecio: 3,
    esConsumidorFinal: false,
    saldo: 0,
    limiteCredito: 1000,
    creditoIlimitado: false,
  }
  const instantanea = { clientes: [enInstantanea] }

  it('true para el Consumidor Final, aun sin instantánea', () => {
    expect(clienteAdmitidoOffline({ id: 1, esConsumidorFinal: true }, null)).toBe(true)
  })

  it('true para un cliente identificado presente en la instantánea', () => {
    expect(clienteAdmitidoOffline({ id: 42, esConsumidorFinal: false }, instantanea)).toBe(true)
  })

  it('false para un cliente identificado que no está en la instantánea', () => {
    expect(clienteAdmitidoOffline({ id: 43, esConsumidorFinal: false }, instantanea)).toBe(false)
  })

  it('false para un cliente identificado sin instantánea', () => {
    expect(clienteAdmitidoOffline({ id: 42, esConsumidorFinal: false }, null)).toBe(false)
  })

  it('false para un cliente identificado sin id', () => {
    expect(clienteAdmitidoOffline({ esConsumidorFinal: false }, instantanea)).toBe(false)
  })

  it('false sin cliente seleccionado (null)', () => {
    expect(clienteAdmitidoOffline(null, instantanea)).toBe(false)
  })
})
