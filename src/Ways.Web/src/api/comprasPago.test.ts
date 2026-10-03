import { describe, expect, it } from 'vitest'
import { fechaDeHoyParaPago, prepararPagoDeCompra, sePuedePagarLaCompra, type PagoDeCompraFormulario } from './compras'

const HOY = '2026-10-03'

function formulario(sobrescribir: Partial<PagoDeCompraFormulario> = {}): PagoDeCompraFormulario {
  return { importe: 400, fecha: HOY, idMedioPago: 7, concepto: '', ...sobrescribir }
}

function error(f: PagoDeCompraFormulario, saldo = 1000): string | null {
  const resultado = prepararPagoDeCompra(f, saldo, HOY)
  return 'error' in resultado ? resultado.error : null
}

describe('prepararPagoDeCompra', () => {
  it('arma la solicitud con el concepto en blanco como null', () => {
    expect(prepararPagoDeCompra(formulario({ concepto: '   ' }), 1000, HOY)).toEqual({
      solicitud: { fecha: HOY, importe: 400, idMedioPago: 7, concepto: null },
    })
  })

  it('recorta el concepto escrito', () => {
    expect(prepararPagoDeCompra(formulario({ concepto: '  Saldo final ' }), 1000, HOY)).toEqual({
      solicitud: { fecha: HOY, importe: 400, idMedioPago: 7, concepto: 'Saldo final' },
    })
  })

  it('acepta pagar exactamente el saldo pendiente y una fecha retroactiva', () => {
    expect(error(formulario({ importe: 1000, fecha: '2026-09-01' }))).toBeNull()
  })

  it.each([null, 0, -5])('rechaza un importe que no es positivo (%s)', (importe) => {
    expect(error(formulario({ importe }))).toBe('Ingresá un importe mayor a 0.')
  })

  it('rechaza un importe mayor al saldo pendiente', () => {
    expect(error(formulario({ importe: 1000.01 }))).toBe('El importe no puede superar el saldo pendiente de la compra.')
  })

  it('rechaza una fecha vacía y una fecha futura', () => {
    expect(error(formulario({ fecha: '' }))).toBe('Elegí la fecha del pago.')
    expect(error(formulario({ fecha: '2026-10-04' }))).toBe('La fecha del pago no puede ser futura.')
  })

  it('rechaza un pago sin medio de pago', () => {
    expect(error(formulario({ idMedioPago: null }))).toBe('Elegí el medio de pago.')
  })
})

describe('sePuedePagarLaCompra', () => {
  it('solo una compra confirmada con saldo pendiente se paga', () => {
    expect(sePuedePagarLaCompra({ estado: 'Confirmada', saldoPendiente: 10 })).toBe(true)
    expect(sePuedePagarLaCompra({ estado: 'Confirmada', saldoPendiente: 0 })).toBe(false)
    expect(sePuedePagarLaCompra({ estado: 'Borrador', saldoPendiente: 10 })).toBe(false)
    expect(sePuedePagarLaCompra({ estado: 'Anulada', saldoPendiente: 10 })).toBe(false)
  })
})

describe('fechaDeHoyParaPago', () => {
  it('formatea el día local con mes y día de dos dígitos', () => {
    expect(fechaDeHoyParaPago(new Date(2026, 0, 5, 23, 59))).toBe('2026-01-05')
    expect(fechaDeHoyParaPago(new Date(2026, 9, 3, 0, 1))).toBe('2026-10-03')
  })
})
