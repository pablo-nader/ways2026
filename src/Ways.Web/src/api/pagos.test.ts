import { describe, expect, it } from 'vitest'
import {
  aPagosDeVenta,
  calcularExcedente,
  calcularFaltante,
  calcularPagosConVuelto,
  consumoCuentaCorriente,
  filaPagoVacia,
  filasAPagosConVuelto,
  filasAPagosParaCalculo,
  medioDisponibleParaCliente,
  sumarImportes,
  sumarVueltos,
  validarPagosLocal,
  vueltoDeFila,
  type FilaPago,
  type PagoParaCalculo,
} from './pagos'
import type { MedioPagoListado } from './tipos'

function medioFixture(sobrescribir: Partial<MedioPagoListado> = {}): MedioPagoListado {
  return {
    id: 1,
    nombre: 'Efectivo',
    activo: true,
    idEmpresa: null,
    orden: 1,
    comportamiento: 'Efectivo',
    admiteVuelto: true,
    requiereReferencia: false,
    recargoPorcentaje: null,
    ...sobrescribir,
  }
}

function pagoFixture(sobrescribir: Partial<PagoParaCalculo> = {}): PagoParaCalculo {
  return {
    idMedioPago: 1,
    comportamiento: 'Efectivo',
    admiteVuelto: true,
    requiereReferencia: false,
    importe: 100,
    referencia: null,
    ...sobrescribir,
  }
}

describe('pagos — sumas', () => {
  it('suma importes con redondeo a centavos', () => {
    expect(sumarImportes([{ importe: 10.005 }, { importe: 10.005 }])).toBeCloseTo(20.01, 2)
  })

  it('suma vueltos', () => {
    expect(sumarVueltos([{ vuelto: 5 }, { vuelto: 2.5 }])).toBe(7.5)
  })

  it('calcularFaltante da 0 cuando el pago ya cubre o supera el total', () => {
    expect(calcularFaltante(100, [{ importe: 100 }])).toBe(0)
    expect(calcularFaltante(100, [{ importe: 150 }])).toBe(0)
  })

  it('calcularFaltante reporta lo que falta cuando el pago no alcanza', () => {
    expect(calcularFaltante(100, [{ importe: 60 }])).toBe(40)
  })

  it('calcularExcedente da 0 cuando el pago no supera el total', () => {
    expect(calcularExcedente(100, [{ importe: 100 }])).toBe(0)
    expect(calcularExcedente(100, [{ importe: 60 }])).toBe(0)
  })

  it('calcularExcedente reporta lo que sobra cuando el pago supera el total', () => {
    expect(calcularExcedente(100, [{ importe: 150 }])).toBe(50)
  })
})

describe('pagos — consumoCuentaCorriente', () => {
  it('suma solo los pagos cuyo comportamiento es CuentaCorriente', () => {
    const pagos = [
      pagoFixture({ comportamiento: 'Efectivo', importe: 40 }),
      pagoFixture({ comportamiento: 'CuentaCorriente', importe: 60 }),
      pagoFixture({ comportamiento: 'Electronico', importe: 10 }),
    ]
    expect(consumoCuentaCorriente(pagos)).toBe(60)
  })

  it('da 0 cuando no hay ningún pago por cuenta corriente', () => {
    expect(consumoCuentaCorriente([pagoFixture({ comportamiento: 'Efectivo' })])).toBe(0)
  })
})

describe('pagos — medioDisponibleParaCliente', () => {
  it('CuentaCorriente no está disponible para Consumidor Final', () => {
    const medio = medioFixture({ comportamiento: 'CuentaCorriente' })
    expect(medioDisponibleParaCliente(medio, true)).toBe(false)
  })

  it('CuentaCorriente sí está disponible para un cliente que no es Consumidor Final', () => {
    const medio = medioFixture({ comportamiento: 'CuentaCorriente' })
    expect(medioDisponibleParaCliente(medio, false)).toBe(true)
  })

  it('Efectivo y Electronico siempre están disponibles, sea o no Consumidor Final', () => {
    expect(medioDisponibleParaCliente(medioFixture({ comportamiento: 'Efectivo' }), true)).toBe(true)
    expect(medioDisponibleParaCliente(medioFixture({ comportamiento: 'Electronico' }), true)).toBe(true)
  })
})

describe('pagos — filasAPagosParaCalculo', () => {
  const medioPorId: Record<number, MedioPagoListado> = {
    1: medioFixture({ id: 1, nombre: 'Efectivo', comportamiento: 'Efectivo', admiteVuelto: true }),
    2: medioFixture({ id: 2, nombre: 'Tarjeta', comportamiento: 'Electronico', admiteVuelto: false, requiereReferencia: true }),
  }

  it('descarta filas sin medio elegido', () => {
    const filas: FilaPago[] = [{ id: 1, idMedioPago: '', importe: '100', referencia: '', vueltoManual: '' }]
    expect(filasAPagosParaCalculo(filas, medioPorId)).toEqual([])
  })

  it('descarta filas con importe vacío, no numérico o <= 0', () => {
    const filas: FilaPago[] = [
      { id: 1, idMedioPago: 1, importe: '', referencia: '', vueltoManual: '' },
      { id: 2, idMedioPago: 1, importe: 'abc', referencia: '', vueltoManual: '' },
      { id: 3, idMedioPago: 1, importe: '0', referencia: '', vueltoManual: '' },
      { id: 4, idMedioPago: 1, importe: '-5', referencia: '', vueltoManual: '' },
    ]
    expect(filasAPagosParaCalculo(filas, medioPorId)).toEqual([])
  })

  it('descarta filas cuyo medio no existe en el índice (catálogo todavía no cargó)', () => {
    const filas: FilaPago[] = [{ id: 1, idMedioPago: 999, importe: '100', referencia: '', vueltoManual: '' }]
    expect(filasAPagosParaCalculo(filas, medioPorId)).toEqual([])
  })

  it('mapea filas completas al shape de cálculo, con referencia vacía convertida a null', () => {
    const filas: FilaPago[] = [
      { id: 1, idMedioPago: 1, importe: '80', referencia: '  ', vueltoManual: '' },
      { id: 2, idMedioPago: 2, importe: '20', referencia: 'auth-123', vueltoManual: '' },
    ]
    expect(filasAPagosParaCalculo(filas, medioPorId)).toEqual([
      { idMedioPago: 1, comportamiento: 'Efectivo', admiteVuelto: true, requiereReferencia: false, importe: 80, referencia: null },
      {
        idMedioPago: 2,
        comportamiento: 'Electronico',
        admiteVuelto: false,
        requiereReferencia: true,
        importe: 20,
        referencia: 'auth-123',
      },
    ])
  })
})

describe('pagos — calcularPagosConVuelto', () => {
  it('sin excedente, todos los pagos quedan con vuelto 0', () => {
    const pagos = [pagoFixture({ importe: 100, admiteVuelto: true })]
    expect(calcularPagosConVuelto(pagos, 100)).toEqual([{ ...pagos[0], vuelto: 0 }])
  })

  it('con excedente, lo asigna íntegro al primer pago que admite vuelto', () => {
    const pagos = [
      pagoFixture({ idMedioPago: 1, comportamiento: 'Electronico', admiteVuelto: false, importe: 50 }),
      pagoFixture({ idMedioPago: 2, comportamiento: 'Efectivo', admiteVuelto: true, importe: 100 }),
    ]
    const resultado = calcularPagosConVuelto(pagos, 100)
    expect(resultado[0].vuelto).toBe(0)
    expect(resultado[1].vuelto).toBe(50)
  })

  it('si ningún medio admite vuelto, el excedente queda sin asignar (todos en 0)', () => {
    const pagos = [pagoFixture({ admiteVuelto: false, importe: 150 })]
    const resultado = calcularPagosConVuelto(pagos, 100)
    expect(resultado[0].vuelto).toBe(0)
  })

  it('nunca reparte el excedente entre dos medios que admiten vuelto — solo el primero lo recibe', () => {
    const pagos = [
      pagoFixture({ idMedioPago: 1, admiteVuelto: true, importe: 100 }),
      pagoFixture({ idMedioPago: 2, admiteVuelto: true, importe: 50 }),
    ]
    const resultado = calcularPagosConVuelto(pagos, 100)
    expect(resultado[0].vuelto).toBe(50)
    expect(resultado[1].vuelto).toBe(0)
  })
})

describe('pagos — validarPagosLocal (orden de rechazo, espejo de ValidadorDePagos)', () => {
  const base = {
    total: 100,
    toleranciaPago: 10,
    vueltoMaximo: 20,
    esConsumidorFinal: false,
    saldoCliente: 0,
    limiteCredito: 1000,
    creditoIlimitado: false,
  }

  it('acepta un pago exacto sin rechazo', () => {
    const pagos = calcularPagosConVuelto([pagoFixture({ importe: 100 })], 100)
    expect(validarPagosLocal({ ...base, pagos })).toBeNull()
  })

  it('regla 0: un importe negativo se rechaza antes que cualquier otra regla', () => {
    const pagos = [{ ...pagoFixture({ importe: -50 }), vuelto: 0 }]
    expect(validarPagosLocal({ ...base, pagos })?.codigo).toBe('pago_importe_negativo')
  })

  it('regla 0b: un vuelto negativo se rechaza antes que cualquier otra regla', () => {
    const pagos = [{ ...pagoFixture({ importe: 100 }), vuelto: -1 }]
    expect(validarPagosLocal({ ...base, pagos })?.codigo).toBe('vuelto_negativo')
  })

  it('regla 1: ningún pago ingresado con total > 0', () => {
    expect(validarPagosLocal({ ...base, pagos: [] })?.codigo).toBe('pago_no_ingresado')
  })

  it('regla 2: el pago no cubre el total ni con tolerancia', () => {
    const pagos = calcularPagosConVuelto([pagoFixture({ importe: 85 })], 100)
    expect(validarPagosLocal({ ...base, pagos })?.codigo).toBe('tolerancia_de_pago_superada')
  })

  it('dentro de la tolerancia, se acepta', () => {
    const pagos = calcularPagosConVuelto([pagoFixture({ importe: 95 })], 100)
    expect(validarPagosLocal({ ...base, pagos })).toBeNull()
  })

  it('regla 3: el vuelto supera el máximo permitido', () => {
    const pagos = calcularPagosConVuelto([pagoFixture({ importe: 125, admiteVuelto: true })], 100)
    expect(validarPagosLocal({ ...base, vueltoMaximo: 20, pagos })?.codigo).toBe('vuelto_excedido')
  })

  it('regla 4: vuelto sobre un medio que no admite vuelto', () => {
    // vuelto asignado a mano (no vía calcularPagosConVuelto) sobre un medio sin AdmiteVuelto.
    const pagos = [{ ...pagoFixture({ importe: 120, admiteVuelto: false }), vuelto: 20 }]
    expect(validarPagosLocal({ ...base, pagos })?.codigo).toBe('medio_no_admite_vuelto')
  })

  it('regla 5: cuenta corriente con Consumidor Final se rechaza sin importar el límite', () => {
    const pagos = calcularPagosConVuelto([pagoFixture({ comportamiento: 'CuentaCorriente', importe: 100, admiteVuelto: false })], 100)
    expect(validarPagosLocal({ ...base, esConsumidorFinal: true, pagos })?.codigo).toBe('cuenta_corriente_no_permitida')
  })

  it('regla 6: el consumo de cuenta corriente supera el límite de crédito', () => {
    const pagos = calcularPagosConVuelto(
      [pagoFixture({ comportamiento: 'CuentaCorriente', importe: 300, admiteVuelto: false })],
      100,
    )
    expect(
      validarPagosLocal({ ...base, total: 300, saldoCliente: 800, limiteCredito: 1000, pagos })?.codigo,
    ).toBe('limite_credito_excedido')
  })

  it('creditoIlimitado evita la regla 6 aunque el saldo + consumo supere el límite', () => {
    const pagos = calcularPagosConVuelto(
      [pagoFixture({ comportamiento: 'CuentaCorriente', importe: 2000, admiteVuelto: false })],
      2000,
    )
    expect(
      validarPagosLocal({ ...base, total: 2000, saldoCliente: 5000, limiteCredito: 1000, creditoIlimitado: true, pagos }),
    ).toBeNull()
  })

  it('regla 7: un medio que requiere referencia sin referencia se rechaza', () => {
    const pagos = calcularPagosConVuelto(
      [pagoFixture({ requiereReferencia: true, referencia: null, admiteVuelto: false, importe: 100 })],
      100,
    )
    expect(validarPagosLocal({ ...base, pagos })?.codigo).toBe('referencia_de_pago_requerida')
  })

  it('con referencia presente, un medio que la requiere se acepta', () => {
    const pagos = calcularPagosConVuelto(
      [pagoFixture({ requiereReferencia: true, referencia: 'auth-1', admiteVuelto: false, importe: 100 })],
      100,
    )
    expect(validarPagosLocal({ ...base, pagos })).toBeNull()
  })

  it('regla 8: el vuelto no coincide con lo que sobra del pago', () => {
    // 120 pagados sobre 100 de total ⇒ excedente 20, pero se declara un vuelto de 25 a mano.
    const pagos = [{ ...pagoFixture({ importe: 120, admiteVuelto: true }), vuelto: 25 }]
    expect(validarPagosLocal({ ...base, vueltoMaximo: 30, pagos })?.codigo).toBe('vuelto_invalido')
  })

  it('un payload que viola las reglas 2 y 6 a la vez reporta la 2 (la que corta primero)', () => {
    const pagos = [{ ...pagoFixture({ comportamiento: 'CuentaCorriente', importe: 50, admiteVuelto: false }), vuelto: 0 }]
    expect(
      validarPagosLocal({ ...base, total: 100, saldoCliente: 999, limiteCredito: 1000, pagos })?.codigo,
    ).toBe('tolerancia_de_pago_superada')
  })
})

describe('pagos — filaPagoVacia', () => {
  it('arma una fila sin medio, importe, referencia ni vuelto manual', () => {
    expect(filaPagoVacia(7)).toEqual({ id: 7, idMedioPago: '', importe: '', referencia: '', vueltoManual: '' })
  })
})

describe('pagos — vueltoDeFila', () => {
  it('un medio sin AdmiteVuelto nunca tiene vuelto, sin importar vueltoManual', () => {
    const fila: FilaPago = { id: 1, idMedioPago: 1, importe: '120', referencia: '', vueltoManual: '99' }
    expect(vueltoDeFila(fila, false, 20)).toBe(0)
  })

  it('sin tocar el campo (vueltoManual vacío), usa el sugerido', () => {
    const fila: FilaPago = { id: 1, idMedioPago: 1, importe: '120', referencia: '', vueltoManual: '' }
    expect(vueltoDeFila(fila, true, 20)).toBe(20)
  })

  it('con el campo tocado, usa el valor que tipeó el cajero', () => {
    const fila: FilaPago = { id: 1, idMedioPago: 1, importe: '120', referencia: '', vueltoManual: '15' }
    expect(vueltoDeFila(fila, true, 20)).toBe(15)
  })

  it('un vueltoManual no numérico cae al sugerido', () => {
    const fila: FilaPago = { id: 1, idMedioPago: 1, importe: '120', referencia: '', vueltoManual: 'abc' }
    expect(vueltoDeFila(fila, true, 20)).toBe(20)
  })
})

describe('pagos — filasAPagosConVuelto', () => {
  const medioPorId: Record<number, MedioPagoListado> = {
    1: medioFixture({ id: 1, nombre: 'Efectivo', comportamiento: 'Efectivo', admiteVuelto: true }),
    2: medioFixture({ id: 2, nombre: 'Tarjeta', comportamiento: 'Electronico', admiteVuelto: false, requiereReferencia: true }),
  }

  it('sin sobreescritura manual, usa el vuelto sugerido por fila', () => {
    const filas: FilaPago[] = [{ id: 1, idMedioPago: 1, importe: '120', referencia: '', vueltoManual: '' }]
    const resultado = filasAPagosConVuelto(filas, medioPorId, 100)
    expect(resultado).toEqual([
      { idMedioPago: 1, comportamiento: 'Efectivo', admiteVuelto: true, requiereReferencia: false, importe: 120, referencia: null, vuelto: 20 },
    ])
  })

  it('con sobreescritura manual sobre un medio que admite vuelto, la respeta', () => {
    const filas: FilaPago[] = [{ id: 1, idMedioPago: 1, importe: '120', referencia: '', vueltoManual: '25' }]
    const resultado = filasAPagosConVuelto(filas, medioPorId, 100)
    expect(resultado[0].vuelto).toBe(25)
  })

  it('una sobreescritura manual sobre un medio sin AdmiteVuelto se ignora, siempre queda en 0', () => {
    const filas: FilaPago[] = [{ id: 1, idMedioPago: 2, importe: '100', referencia: 'auth', vueltoManual: '10' }]
    const resultado = filasAPagosConVuelto(filas, medioPorId, 100)
    expect(resultado[0].vuelto).toBe(0)
  })
})

describe('pagos — aPagosDeVenta', () => {
  it('mapea al shape del request de checkout', () => {
    const pagos = calcularPagosConVuelto([pagoFixture({ importe: 120, admiteVuelto: true, referencia: 'x' })], 100)
    expect(aPagosDeVenta(pagos)).toEqual([{ idMedioPago: 1, importe: 120, referencia: 'x', vuelto: 20 }])
  })
})
