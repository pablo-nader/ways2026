import { describe, expect, it } from 'vitest'
import { fechaDeHoyParaPago, prepararPagoDeCompra, sePuedePagarLaCompra, type PagoDeCompraFormulario } from './compras'
import type { LineaDePagoEditable } from './lineasDePago'

const HOY = '2026-10-03'

function linea(sobrescribir: Partial<LineaDePagoEditable> = {}): LineaDePagoEditable {
  return { clave: 1, idMedioPago: 7, importe: 400, referencia: '', ...sobrescribir }
}

function formulario(sobrescribir: Partial<PagoDeCompraFormulario> = {}): PagoDeCompraFormulario {
  return { lineas: [linea()], fecha: HOY, concepto: '', ...sobrescribir }
}

function error(f: PagoDeCompraFormulario, saldo = 1000): string | null {
  const resultado = prepararPagoDeCompra(f, saldo, HOY)
  return 'error' in resultado ? resultado.error : null
}

describe('prepararPagoDeCompra', () => {
  it('arma la solicitud con el concepto en blanco como null y la referencia en blanco como null', () => {
    expect(prepararPagoDeCompra(formulario({ concepto: '   ' }), 1000, HOY)).toEqual({
      solicitud: { fecha: HOY, lineas: [{ idMedioPago: 7, importe: 400, referencia: null }], concepto: null },
    })
  })

  it('recorta el concepto escrito', () => {
    expect(prepararPagoDeCompra(formulario({ concepto: '  Saldo final ' }), 1000, HOY)).toEqual({
      solicitud: { fecha: HOY, lineas: [{ idMedioPago: 7, importe: 400, referencia: null }], concepto: 'Saldo final' },
    })
  })

  it('manda una línea por medio, en orden, con su referencia recortada', () => {
    const f = formulario({
      lineas: [linea({ clave: 1, idMedioPago: 7, importe: 600 }), linea({ clave: 2, idMedioPago: 9, importe: 400, referencia: ' Op 55 ' })],
    })
    expect(prepararPagoDeCompra(f, 1000, HOY)).toEqual({
      solicitud: {
        fecha: HOY,
        lineas: [
          { idMedioPago: 7, importe: 600, referencia: null },
          { idMedioPago: 9, importe: 400, referencia: 'Op 55' },
        ],
        concepto: null,
      },
    })
  })

  it('acepta pagar exactamente el saldo pendiente repartido en varias líneas y una fecha retroactiva', () => {
    const f = formulario({
      lineas: [linea({ clave: 1, importe: 600.5 }), linea({ clave: 2, idMedioPago: 9, importe: 399.5 })],
      fecha: '2026-09-01',
    })
    expect(error(f)).toBeNull()
  })

  it.each([null, 0, -5])('rechaza una línea con importe que no es positivo (%s)', (importe) => {
    expect(error(formulario({ lineas: [linea({ importe })] }))).toBe('Línea 1: ingresá un importe mayor a 0.')
  })

  it('rechaza un total mayor al saldo pendiente, aunque cada línea sola no lo supere', () => {
    const f = formulario({ lineas: [linea({ clave: 1, importe: 600 }), linea({ clave: 2, importe: 400.01 })] })
    expect(error(f)).toBe('El total no puede superar el saldo pendiente de la compra.')
  })

  it('rechaza una fecha vacía y una fecha futura', () => {
    expect(error(formulario({ fecha: '' }))).toBe('Elegí la fecha del pago.')
    expect(error(formulario({ fecha: '2026-10-04' }))).toBe('La fecha del pago no puede ser futura.')
  })

  it('rechaza una línea sin medio de pago, nombrando su número', () => {
    const f = formulario({ lineas: [linea({ clave: 1 }), linea({ clave: 2, idMedioPago: '' })] })
    expect(error(f)).toBe('Línea 2: elegí el medio de pago.')
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
