import { describe, expect, it } from 'vitest'
import {
  diferenciaDelPago,
  fechaDeHoyParaPago,
  prepararPagoDeCompra,
  sePuedeAjustarElRedondeo,
  sePuedeCerrarEnCero,
  sePuedePagarLaCompra,
  textoDePagoRegistrado,
  textoDeRedondeo,
  type PagoDeCompraFormulario,
} from './compras'
import type { ResultadoDePagoDeCompra } from './tipos'
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

  it('acepta un total mayor al saldo pendiente: el excedente queda a favor', () => {
    const f = formulario({ lineas: [linea({ clave: 1, importe: 600 }), linea({ clave: 2, importe: 400.01 })] })
    expect(error(f)).toBeNull()
  })

  it('con pagos a cuenta y una línea vacía manda solo las aplicaciones, sin concepto', () => {
    const f = formulario({
      lineas: [linea({ idMedioPago: '', importe: null })],
      concepto: 'Un concepto que no tiene a qué ponerse',
      aplicaciones: [{ idPago: 5, importe: 300 }],
    })
    expect(prepararPagoDeCompra(f, 1000, HOY)).toEqual({
      solicitud: { fecha: HOY, lineas: [], concepto: null, aplicaciones: [{ idPago: 5, importe: 300 }] },
    })
  })

  it('con pagos a cuenta descarta la línea vacía (sin medio y con importe 0) y conserva las demás', () => {
    const f = formulario({
      lineas: [linea({ clave: 1, idMedioPago: '', importe: 0 }), linea({ clave: 2, idMedioPago: 9, importe: 50 })],
      concepto: 'Saldo',
      aplicaciones: [{ idPago: 5, importe: 300 }],
    })
    expect(prepararPagoDeCompra(f, 1000, HOY)).toEqual({
      solicitud: {
        fecha: HOY,
        lineas: [{ idMedioPago: 9, importe: 50, referencia: null }],
        concepto: 'Saldo',
        aplicaciones: [{ idPago: 5, importe: 300 }],
      },
    })
  })

  it('con pagos a cuenta, un importe tipeado sin medio es un error y no se descarta en silencio', () => {
    const f = formulario({
      lineas: [linea({ clave: 1, idMedioPago: '', importe: null }), linea({ clave: 2, idMedioPago: '', importe: 120 })],
      aplicaciones: [{ idPago: 5, importe: 300 }],
    })
    expect(error(f)).toBe('Elegí el medio de la línea 2.')
  })

  it('con pagos a cuenta y un medio elegido manda las líneas y las aplicaciones', () => {
    const f = formulario({ aplicaciones: [{ idPago: 5, importe: 300 }] })
    expect(prepararPagoDeCompra(f, 1000, HOY)).toEqual({
      solicitud: {
        fecha: HOY,
        lineas: [{ idMedioPago: 7, importe: 400, referencia: null }],
        concepto: null,
        aplicaciones: [{ idPago: 5, importe: 300 }],
      },
    })
  })

  it('con un medio elegido y un importe vacío rechaza la línea aunque haya pagos a cuenta', () => {
    const f = formulario({ lineas: [linea({ importe: null })], aplicaciones: [{ idPago: 5, importe: 300 }] })
    expect(error(f)).toBe('Línea 1: ingresá un importe mayor a 0.')
  })

  it('rechaza aplicar más que el saldo pendiente o un importe a aplicar que no es positivo', () => {
    const sinMedio = linea({ idMedioPago: '', importe: null })
    expect(error(formulario({ lineas: [sinMedio], aplicaciones: [{ idPago: 5, importe: 1000.01 }] }))).toBe(
      'Lo aplicado de pagos a cuenta no puede superar el saldo pendiente de la compra.',
    )
    expect(error(formulario({ lineas: [sinMedio], aplicaciones: [{ idPago: 5, importe: 0 }] }))).toBe(
      'El importe de cada pago a aplicar tiene que ser mayor a 0.',
    )
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

describe('prepararPagoDeCompra — cerrar en cero', () => {
  it('manda cerrarEnCero solo cuando se pidió', () => {
    const base = { fecha: HOY, lineas: [{ idMedioPago: 7, importe: 400, referencia: null }], concepto: null }
    expect(prepararPagoDeCompra(formulario({ cerrarEnCero: true }), 1000, HOY)).toEqual({
      solicitud: { ...base, cerrarEnCero: true },
    })
    expect(prepararPagoDeCompra(formulario({ cerrarEnCero: false }), 1000, HOY)).toEqual({ solicitud: base })
    expect(prepararPagoDeCompra(formulario(), 1000, HOY)).toEqual({ solicitud: base })
  })
})

describe('diferenciaDelPago', () => {
  it('es positiva si falta pagar, negativa si se paga de más y cero si cierra justo', () => {
    expect(diferenciaDelPago(1503.5, 0, 1500)).toBe(3.5)
    expect(diferenciaDelPago(1430.25, 0, 1500)).toBe(-69.75)
    expect(diferenciaDelPago(1000, 300, 700)).toBe(0)
  })

  it('descuenta lo aplicado de pagos a cuenta y no arrastra el error binario de los decimales', () => {
    expect(diferenciaDelPago(100.1, 0.1, 100)).toBe(0)
  })
})

describe('sePuedeCerrarEnCero', () => {
  it('exige una diferencia distinta de cero que no supere la tolerancia, en cualquier sentido', () => {
    expect(sePuedeCerrarEnCero(3.5, 500)).toBe(true)
    expect(sePuedeCerrarEnCero(-69.75, 500)).toBe(true)
    expect(sePuedeCerrarEnCero(500, 500)).toBe(true)
    expect(sePuedeCerrarEnCero(-500.01, 500)).toBe(false)
    expect(sePuedeCerrarEnCero(0, 500)).toBe(false)
  })

  it('sin tolerancia conocida no se ofrece', () => {
    expect(sePuedeCerrarEnCero(3.5, null)).toBe(false)
  })

  it('con tolerancia cero nunca aplica', () => {
    expect(sePuedeCerrarEnCero(0.01, 0)).toBe(false)
  })
})

describe('sePuedeAjustarElRedondeo', () => {
  it('solo una confirmada con saldo de hasta la tolerancia', () => {
    expect(sePuedeAjustarElRedondeo({ estado: 'Confirmada', saldoPendiente: 3.5 }, 500)).toBe(true)
    expect(sePuedeAjustarElRedondeo({ estado: 'Confirmada', saldoPendiente: 500 }, 500)).toBe(true)
    expect(sePuedeAjustarElRedondeo({ estado: 'Confirmada', saldoPendiente: 500.01 }, 500)).toBe(false)
    expect(sePuedeAjustarElRedondeo({ estado: 'Confirmada', saldoPendiente: 0 }, 500)).toBe(false)
    expect(sePuedeAjustarElRedondeo({ estado: 'Confirmada', saldoPendiente: 3.5 }, null)).toBe(false)
    expect(sePuedeAjustarElRedondeo({ estado: 'Borrador', saldoPendiente: 3.5 }, 500)).toBe(false)
    expect(sePuedeAjustarElRedondeo({ estado: 'Anulada', saldoPendiente: 3.5 }, 500)).toBe(false)
  })
})

describe('textoDeRedondeo', () => {
  const formatear = (n: number) => `$${n}`

  it('positivo se perdonó, negativo se absorbió y sin redondeo no dice nada', () => {
    expect(textoDeRedondeo(3.5, formatear)).toBe(' Se perdonaron $3.5 de redondeo.')
    expect(textoDeRedondeo(-69.75, formatear)).toBe(' Se absorbieron $69.75 de redondeo.')
    expect(textoDeRedondeo(0, formatear)).toBe('')
    expect(textoDeRedondeo(null, formatear)).toBe('')
  })
})

describe('textoDePagoRegistrado', () => {
  const formatear = (n: number) => `$${n}`

  it('suma la frase del redondeo al aviso, con y sin pago nuevo', () => {
    const pago = { importe: 1500 } as ResultadoDePagoDeCompra['pago']
    expect(textoDePagoRegistrado({ pago, disponibleDelPago: 0, redondeo: 3.5 }, formatear)).toBe(
      'Pago registrado: $1500. Se perdonaron $3.5 de redondeo.',
    )
    expect(textoDePagoRegistrado({ pago, disponibleDelPago: 0, redondeo: -69.75 }, formatear)).toBe(
      'Pago registrado: $1500. Se absorbieron $69.75 de redondeo.',
    )
    expect(textoDePagoRegistrado({ pago: null, disponibleDelPago: null, redondeo: 3.5 }, formatear)).toBe(
      'Pagos a cuenta aplicados a la compra. Se perdonaron $3.5 de redondeo.',
    )
  })

  it('dice cuánto quedó a favor solo cuando el pago nuevo superó el saldo', () => {
    const pago = { importe: 500 } as ResultadoDePagoDeCompra['pago']
    expect(textoDePagoRegistrado({ pago, disponibleDelPago: 0 }, formatear)).toBe('Pago registrado: $500.')
    expect(textoDePagoRegistrado({ pago, disponibleDelPago: 80 }, formatear)).toBe('Pago registrado: $500. Quedaron $80 a favor.')
  })

  it('sin pago nuevo solo se aplicaron pagos a cuenta', () => {
    expect(textoDePagoRegistrado({ pago: null, disponibleDelPago: null }, formatear)).toBe('Pagos a cuenta aplicados a la compra.')
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
