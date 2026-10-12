import { describe, expect, it } from 'vitest'
import {
  aSolicitudDeAjuste,
  aSolicitudDeHoras,
  aSolicitudDePago,
  avisoDeUbicacionDelPago,
  deudaDeCliente,
  etiquetaDeOrigen,
  etiquetaDeTipo,
  formularioDeAjusteVacio,
  formularioDeHorasVacio,
  formularioDePagoVacio,
  importeDeHoras,
  importeFirmadoDeAjuste,
  MAXIMO_DE_LINEAS,
  mediosOfrecidos,
  saldoTrasMovimiento,
  saldoTrasPago,
  textoDeLineaDeDesglose,
  textoDeSaldo,
  totalDelPago,
  validarAjuste,
  validarHoras,
  validarPago,
  type ContextoDePago,
  type FormularioDePago,
  type LineaDeFormulario,
} from './cuentaDePersonalFormularios'
import type { MedioPagoListado } from '../api/tipos'

const HOY = '2026-10-12'

describe('importeDeHoras', () => {
  it('multiplica y redondea al centavo con los empates alejándose de cero, como el servidor', () => {
    // 0,7 × 1,15 = 0,805 exacto: en punto flotante daría 0,8049999… y bajaría.
    expect(importeDeHoras(0.7, 1.15)).toBe(0.81)
    expect(importeDeHoras(7.5, 3500)).toBe(26250)
    expect(importeDeHoras(0.01, 0.5)).toBe(0.01)
  })

  it('no hay vista previa mientras falte alguno de los dos datos', () => {
    expect(importeDeHoras(null, 100)).toBeNull()
    expect(importeDeHoras(8, null)).toBeNull()
  })
})

describe('formularioDeHorasVacio y validarHoras', () => {
  const valido = { ...formularioDeHorasVacio(HOY, 3500), horas: 8 }

  it('arranca con la fecha de hoy, el valor hora del empleado y sin horas', () => {
    expect(formularioDeHorasVacio(HOY, 3500)).toEqual({ fecha: HOY, horas: null, valorHora: 3500, detalle: '' })
  })

  it('acepta un formulario completo', () => {
    expect(validarHoras(valido)).toBeNull()
    expect(validarHoras({ ...valido, horas: 24, valorHora: 0 })).toBeNull()
  })

  it('exige la fecha', () => {
    expect(validarHoras({ ...valido, fecha: '' })?.campo).toBe('fecha')
  })

  it.each([null, 0, -1, 24.01])('rechaza las horas %s', (horas) => {
    expect(validarHoras({ ...valido, horas })?.campo).toBe('horas')
  })

  it('exige el valor hora y no admite uno negativo', () => {
    expect(validarHoras({ ...valido, valorHora: null })?.campo).toBe('valorHora')
    expect(validarHoras({ ...valido, valorHora: -1 })?.campo).toBe('valorHora')
  })

  it('rechaza un detalle de más de 500 caracteres y acepta uno de 500', () => {
    expect(validarHoras({ ...valido, detalle: 'x'.repeat(501) })?.campo).toBe('detalle')
    expect(validarHoras({ ...valido, detalle: 'x'.repeat(500) })).toBeNull()
  })
})

describe('aSolicitudDeHoras', () => {
  it('manda fecha, horas y valor hora, y el detalle recortado solo si hay', () => {
    const f = { ...formularioDeHorasVacio(HOY, 3500), horas: 7.5, detalle: '  turno tarde ' }

    expect(aSolicitudDeHoras(f)).toEqual({ fecha: HOY, horas: 7.5, valorHora: 3500, detalle: 'turno tarde' })
  })

  it('omite el detalle vacío', () => {
    const solicitud = aSolicitudDeHoras({ ...formularioDeHorasVacio(HOY, 3500), horas: 8, detalle: '   ' })

    expect(solicitud).toEqual({ fecha: HOY, horas: 8, valorHora: 3500 })
    expect('detalle' in solicitud).toBe(false)
  })

  it('no inventa datos: sin horas o sin valor hora lanza', () => {
    expect(() => aSolicitudDeHoras(formularioDeHorasVacio(HOY, 3500))).toThrow()
    expect(() => aSolicitudDeHoras({ ...formularioDeHorasVacio(HOY, 3500), horas: 8, valorHora: null })).toThrow()
  })
})

describe('ajuste', () => {
  const valido = { ...formularioDeAjusteVacio(HOY), importe: 100, detalle: 'Corrección de carga' }

  it('arranca con hoy, a favor del empleado y sin importe', () => {
    expect(formularioDeAjusteVacio(HOY)).toEqual({ fecha: HOY, sentido: 'aFavor', importe: null, detalle: '' })
  })

  it('el sentido pone el signo: a favor suma, en contra resta', () => {
    expect(importeFirmadoDeAjuste(valido)).toBe(100)
    expect(importeFirmadoDeAjuste({ ...valido, sentido: 'enContra' })).toBe(-100)
    expect(importeFirmadoDeAjuste({ ...valido, importe: null })).toBeNull()
  })

  it('acepta un ajuste completo en cualquiera de los dos sentidos', () => {
    expect(validarAjuste(valido)).toBeNull()
    expect(validarAjuste({ ...valido, sentido: 'enContra' })).toBeNull()
  })

  it('exige la fecha', () => {
    expect(validarAjuste({ ...valido, fecha: '' })?.campo).toBe('fecha')
  })

  it.each([null, 0, -5])('rechaza el importe %s: el signo no se escribe, se elige', (importe) => {
    expect(validarAjuste({ ...valido, importe })?.campo).toBe('importe')
  })

  it('exige un detalle de al menos cinco caracteres sin contar los espacios de los bordes', () => {
    expect(validarAjuste({ ...valido, detalle: '  ab  ' })?.campo).toBe('detalle')
    expect(validarAjuste({ ...valido, detalle: ' abcd ' })?.campo).toBe('detalle')
    expect(validarAjuste({ ...valido, detalle: ' abcde ' })).toBeNull()
  })

  it('rechaza un detalle de más de 500 caracteres', () => {
    expect(validarAjuste({ ...valido, detalle: 'x'.repeat(501) })?.campo).toBe('detalle')
  })

  it('arma la solicitud con el importe con signo y el detalle recortado', () => {
    expect(aSolicitudDeAjuste({ ...valido, sentido: 'enContra', detalle: ' Corrección de carga ' })).toEqual({
      fecha: HOY,
      importe: -100,
      detalle: 'Corrección de carga',
    })
  })

  it('sin importe la solicitud lanza', () => {
    expect(() => aSolicitudDeAjuste({ ...valido, importe: null })).toThrow()
  })
})

describe('saldoTrasMovimiento', () => {
  it('suma el importe con signo y redondea al centavo sin arrastrar error de punto flotante', () => {
    expect(saldoTrasMovimiento(0.1, 0.2)).toBe(0.3)
    expect(saldoTrasMovimiento(100, -250.5)).toBe(-150.5)
  })
})

const linea = (parcial: Partial<LineaDeFormulario> = {}): LineaDeFormulario => ({
  clave: 1,
  idMedioPago: 4,
  importe: 1000,
  referencia: '',
  ...parcial,
})

const contexto = (parcial: Partial<ContextoDePago> = {}): ContextoDePago => ({
  deudaDelCliente: 500,
  esAdmin: true,
  idPuntoVenta: 9,
  idEmpresaDelPuntoVenta: 2,
  ...parcial,
})

const pago = (parcial: Partial<FormularioDePago> = {}): FormularioDePago => ({
  ...formularioDePagoVacio(HOY),
  ...parcial,
})

describe('formularioDePagoVacio', () => {
  it('arranca con hoy, sin descuento, sin líneas y sin origen', () => {
    expect(formularioDePagoVacio(HOY)).toEqual({
      fecha: HOY,
      descuento: null,
      lineas: [],
      origen: '',
      idEmpresa: null,
      detalle: '',
    })
  })
})

describe('totalDelPago y saldoTrasPago', () => {
  it('suma el descuento y todas las líneas sin error de punto flotante', () => {
    const f = pago({ descuento: 0.1, lineas: [linea({ importe: 0.2 }), linea({ clave: 2, importe: 0.3 })] })

    expect(totalDelPago(f)).toBe(0.6)
  })

  it('las líneas sin importe no suman', () => {
    expect(totalDelPago(pago({ lineas: [linea({ importe: null })] }))).toBe(0)
  })

  it('el saldo que queda es el actual menos el total, y puede ser negativo (adelanto)', () => {
    expect(saldoTrasPago(5000, pago({ lineas: [linea({ importe: 1500 })] }))).toBe(3500)
    expect(saldoTrasPago(1000, pago({ lineas: [linea({ importe: 1500 })] }))).toBe(-500)
  })
})

describe('deudaDeCliente', () => {
  it('es el saldo del cliente, y cero si tiene saldo a favor', () => {
    expect(deudaDeCliente(750.5)).toBe(750.5)
    expect(deudaDeCliente(-20)).toBe(0)
    expect(deudaDeCliente(0)).toBe(0)
  })
})

describe('mediosOfrecidos', () => {
  const medio = (id: number, comportamiento: MedioPagoListado['comportamiento']) =>
    ({ id, nombre: `Medio ${id}`, comportamiento }) as MedioPagoListado

  it('deja afuera la cuenta corriente: no es plata que sale de una caja', () => {
    const medios = [medio(1, 'Efectivo'), medio(2, 'CuentaCorriente'), medio(3, 'Electronico')]

    expect(mediosOfrecidos(medios).map((m) => m.id)).toEqual([1, 3])
  })
})

describe('validarPago', () => {
  const conTesoreria = (parcial: Partial<FormularioDePago> = {}) =>
    pago({ lineas: [linea()], origen: 'Tesoreria', idEmpresa: 2, ...parcial })

  it('acepta un pago de tesorería, uno del cajón y uno solo de descuento', () => {
    expect(validarPago(conTesoreria(), contexto())).toBeNull()
    expect(validarPago(pago({ lineas: [linea()], origen: 'CajaTurno' }), contexto())).toBeNull()
    expect(validarPago(pago({ descuento: 500 }), contexto())).toBeNull()
  })

  it('exige la fecha', () => {
    expect(validarPago(conTesoreria({ fecha: '' }), contexto())?.campo).toBe('fecha')
  })

  it('rechaza un pago vacío', () => {
    expect(validarPago(pago(), contexto())).toMatchObject({ campo: 'general' })
  })

  it.each([0, -1])('rechaza el descuento %s', (descuento) => {
    expect(validarPago(pago({ descuento }), contexto())?.campo).toBe('descuento')
  })

  it('no deja pasar de la deuda del cliente pero sí igualarla', () => {
    expect(validarPago(pago({ descuento: 500.01 }), contexto())?.campo).toBe('descuento')
    expect(validarPago(pago({ descuento: 500 }), contexto())).toBeNull()
  })

  it('un descuento necesita el punto de venta de la sesión', () => {
    expect(validarPago(pago({ descuento: 100 }), contexto({ idPuntoVenta: null }))).toMatchObject({
      campo: 'descuento',
      mensaje: expect.stringContaining('punto de venta'),
    })
  })

  it('rechaza más de veinte líneas y acepta exactamente veinte', () => {
    const lineas = (n: number) => Array.from({ length: n }, (_, i) => linea({ clave: i + 1, importe: 1 }))

    expect(validarPago(conTesoreria({ lineas: lineas(MAXIMO_DE_LINEAS + 1) }), contexto())?.campo).toBe('lineas')
    expect(validarPago(conTesoreria({ lineas: lineas(MAXIMO_DE_LINEAS) }), contexto())).toBeNull()
  })

  it('cada línea necesita medio de pago y un importe mayor que cero', () => {
    expect(validarPago(conTesoreria({ lineas: [linea({ idMedioPago: null })] }), contexto())?.campo).toBe('lineas')
    expect(validarPago(conTesoreria({ lineas: [linea({ importe: null })] }), contexto())?.campo).toBe('lineas')
    expect(validarPago(conTesoreria({ lineas: [linea({ importe: 0 })] }), contexto())?.campo).toBe('lineas')
  })

  it('la referencia admite hasta cien caracteres', () => {
    expect(validarPago(conTesoreria({ lineas: [linea({ referencia: 'x'.repeat(101) })] }), contexto())?.campo).toBe(
      'lineas',
    )
    expect(validarPago(conTesoreria({ lineas: [linea({ referencia: 'x'.repeat(100) })] }), contexto())).toBeNull()
  })

  it('con líneas el origen es obligatorio', () => {
    expect(validarPago(pago({ lineas: [linea()] }), contexto())?.campo).toBe('origen')
  })

  it('la tesorería es solo del Admin', () => {
    expect(validarPago(conTesoreria(), contexto({ esAdmin: false }))?.campo).toBe('origen')
  })

  it('la tesorería necesita empresa', () => {
    expect(validarPago(conTesoreria({ idEmpresa: null }), contexto())?.campo).toBe('empresa')
  })

  it('con tesorería y descuento, la empresa tiene que ser la del punto de venta de la sesión', () => {
    const f = conTesoreria({ descuento: 100, idEmpresa: 3 })

    expect(validarPago(f, contexto())?.campo).toBe('empresa')
    expect(validarPago({ ...f, idEmpresa: 2 }, contexto())).toBeNull()
  })

  it('el cajón necesita el punto de venta de la sesión', () => {
    const f = pago({ lineas: [linea()], origen: 'CajaTurno' })

    expect(validarPago(f, contexto({ idPuntoVenta: null }))).toMatchObject({
      campo: 'origen',
      mensaje: expect.stringContaining('punto de venta'),
    })
  })

  it('rechaza un detalle de más de 500 caracteres', () => {
    expect(validarPago(conTesoreria({ detalle: 'x'.repeat(501) }), contexto())?.campo).toBe('detalle')
  })
})

describe('avisoDeUbicacionDelPago', () => {
  it('avisa antes de enviar que el cajón y el descuento necesitan punto de venta', () => {
    const sinPuntoDeVenta = contexto({ idPuntoVenta: null })

    expect(avisoDeUbicacionDelPago(pago({ lineas: [linea()], origen: 'CajaTurno' }), sinPuntoDeVenta)).toContain(
      'cajón del turno',
    )
    expect(avisoDeUbicacionDelPago(pago({ descuento: 10 }), sinPuntoDeVenta)).toContain('descontar')
  })

  it('no avisa con punto de venta elegido, ni con tesorería sin descuento', () => {
    expect(avisoDeUbicacionDelPago(pago({ lineas: [linea()], origen: 'CajaTurno' }), contexto())).toBeNull()
    expect(
      avisoDeUbicacionDelPago(pago({ lineas: [linea()], origen: 'Tesoreria' }), contexto({ idPuntoVenta: null })),
    ).toBeNull()
  })
})

describe('aSolicitudDePago', () => {
  it('tesorería: manda la empresa y no el punto de venta', () => {
    const f = pago({
      lineas: [linea({ referencia: ' op-1 ' }), linea({ clave: 2, idMedioPago: 5, importe: 250.5 })],
      origen: 'Tesoreria',
      idEmpresa: 2,
      detalle: ' Quincena ',
    })

    expect(aSolicitudDePago(f, contexto())).toEqual({
      fecha: HOY,
      origenFondos: 'Tesoreria',
      idEmpresa: 2,
      lineas: [
        { idMedioPago: 4, importe: 1000, referencia: 'op-1' },
        { idMedioPago: 5, importe: 250.5 },
      ],
      detalle: 'Quincena',
    })
  })

  it('cajón: manda el punto de venta y no la empresa, aunque haya una empresa en el formulario', () => {
    const f = pago({ lineas: [linea()], origen: 'CajaTurno', idEmpresa: 2 })

    const solicitud = aSolicitudDePago(f, contexto())

    expect(solicitud).toEqual({
      fecha: HOY,
      origenFondos: 'CajaTurno',
      idPuntoVenta: 9,
      lineas: [{ idMedioPago: 4, importe: 1000 }],
    })
    expect('idEmpresa' in solicitud).toBe(false)
  })

  it('solo descuento: sin origen ni líneas ni empresa, con el punto de venta', () => {
    const solicitud = aSolicitudDePago(pago({ descuento: 300, origen: 'Tesoreria', idEmpresa: 2 }), contexto())

    expect(solicitud).toEqual({ fecha: HOY, descuentoDeCuentaCorriente: 300, idPuntoVenta: 9 })
    expect('origenFondos' in solicitud).toBe(false)
    expect('lineas' in solicitud).toBe(false)
    expect('idEmpresa' in solicitud).toBe(false)
  })

  it('tesorería con descuento manda empresa y punto de venta', () => {
    const f = pago({ descuento: 300, lineas: [linea()], origen: 'Tesoreria', idEmpresa: 2 })

    expect(aSolicitudDePago(f, contexto())).toMatchObject({ idEmpresa: 2, idPuntoVenta: 9 })
  })

  it('no inventa datos: con líneas y sin origen, o sin medio, lanza', () => {
    expect(() => aSolicitudDePago(pago({ lineas: [linea()] }), contexto())).toThrow()
    expect(() =>
      aSolicitudDePago(pago({ lineas: [linea({ idMedioPago: null })], origen: 'CajaTurno' }), contexto()),
    ).toThrow()
    expect(() =>
      aSolicitudDePago(pago({ lineas: [linea({ importe: null })], origen: 'CajaTurno' }), contexto()),
    ).toThrow()
  })

  it('tesorería sin empresa, o cajón sin punto de venta, lanza', () => {
    expect(() => aSolicitudDePago(pago({ lineas: [linea()], origen: 'Tesoreria' }), contexto())).toThrow()
    expect(() =>
      aSolicitudDePago(pago({ lineas: [linea()], origen: 'CajaTurno' }), contexto({ idPuntoVenta: null })),
    ).toThrow()
  })
})

describe('etiquetas', () => {
  it('el saldo negativo lleva la leyenda de adelanto y el positivo o cero no', () => {
    expect(textoDeSaldo(1500)).toBe('$ 1.500,00')
    expect(textoDeSaldo(0)).toBe('$ 0,00')
    expect(textoDeSaldo(-250)).toBe('-$ 250,00 (adelanto ya pagado)')
  })

  it('traduce el tipo de movimiento', () => {
    expect(etiquetaDeTipo('Horas')).toBe('Horas trabajadas')
    expect(etiquetaDeTipo('Pago')).toBe('Pago')
    expect(etiquetaDeTipo('Ajuste')).toBe('Ajuste')
  })

  it('traduce el origen de los fondos', () => {
    expect(etiquetaDeOrigen('Tesoreria')).toBe('Tesorería')
    expect(etiquetaDeOrigen('CajaTurno')).toBe('Cajón del turno')
  })

  it('describe una línea del desglose con medio, importe, origen y referencia', () => {
    const base = {
      idGasto: 1,
      idMedioPago: 4,
      nombreMedioPago: 'Efectivo',
      importe: 1500,
      origenFondos: 'CajaTurno' as const,
      referencia: null,
    }

    expect(textoDeLineaDeDesglose(base)).toBe('Efectivo: $ 1.500,00 (Cajón del turno)')
    expect(textoDeLineaDeDesglose({ ...base, referencia: 'op-7', origenFondos: 'Tesoreria' })).toBe(
      'Efectivo: $ 1.500,00 (Tesorería), ref. op-7',
    )
    expect(textoDeLineaDeDesglose({ ...base, nombreMedioPago: null })).toBe(
      'Medio de pago dado de baja: $ 1.500,00 (Cajón del turno)',
    )
  })
})
