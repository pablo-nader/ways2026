import { describe, expect, it } from 'vitest'
import {
  aPagosDeVenta,
  BILLETES_ARGENTINOS,
  calcularExcedente,
  calcularFaltante,
  calcularPagosConVuelto,
  consumoCuentaCorriente,
  efectivoEntregado,
  esVueltoJustificado,
  filaPagoInicial,
  filaPagoVacia,
  filasAPagosConVuelto,
  filasAPagosParaCalculo,
  idMedioEfectivo,
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
    idFila: 1,
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

describe('pagos — efectivoEntregado', () => {
  it('suma solo los pagos cuyo comportamiento es Efectivo', () => {
    const pagos = [
      pagoFixture({ comportamiento: 'Efectivo', importe: 100 }),
      pagoFixture({ comportamiento: 'Electronico', importe: 50 }),
    ]
    expect(efectivoEntregado(pagos)).toBe(100)
  })

  it('da 0 cuando ningún pago es Efectivo', () => {
    expect(efectivoEntregado([pagoFixture({ comportamiento: 'CuentaCorriente' })])).toBe(0)
  })

  it('ignora un medio no-Efectivo aunque admita vuelto por configuración de catálogo', () => {
    // admiteVuelto es un flag por medio (ABM), no está atado a comportamiento — una Transferencia
    // podría tenerlo prendido, pero nunca es "billetes físicos".
    const pagos = [pagoFixture({ comportamiento: 'Electronico', admiteVuelto: true, importe: 9900 })]
    expect(efectivoEntregado(pagos)).toBe(0)
  })
})

// Tabla compartida con BilletesArgentinosTests (Ways.Domain.Tests): mismas entradas, mismo
// criterio en ambas implementaciones (decisión del dueño, 2026-09-16).
describe('pagos — esVueltoJustificado (espejo de BilletesArgentinos.EsVueltoJustificado)', () => {
  it('expone las diez denominaciones vigentes', () => {
    expect(BILLETES_ARGENTINOS).toEqual([10, 20, 50, 100, 200, 500, 1000, 2000, 10000, 20000])
  })

  it.each([
    [10000, 4500, 'ticket 5500, un billete de 10000'],
    [6000, 500, 'ticket 5500, tres billetes de 2000'],
    [10000, 2000, 'vuelto igual a una denominación, un solo billete alcanza'],
    [10000, 4500.5, 'total con centavos, efectivo múltiplo de 10'],
    [10, 0, 'vuelto cero siempre justificado'],
  ])('%d entregado con vuelto %d es justificado (%s)', (entregado, vuelto) => {
    expect(esVueltoJustificado(entregado, vuelto)).toBe(true)
  })

  it.each([
    [5520, 20, 'todo billete > 20 es múltiplo de 50; 5520 no lo es'],
    [30000, 24500, 'ningún billete supera 24500'],
    [11000, 2000, '11000 con billetes > 2000 no arma exacto'],
    [4000, 2000, 'vuelto igual a un billete pero no alcanza para justificar 4000'],
    [5500.5, 500.5, 'efectivo con centavos nunca es formable'],
    [5505, 500, 'efectivo no múltiplo de 10'],
    [0, 500, 'sin efectivo entregado no hay nada que formar'],
  ])('%d entregado con vuelto %d NO es justificado (%s)', (entregado, vuelto) => {
    expect(esVueltoJustificado(entregado, vuelto)).toBe(false)
  })

  it('rechaza de plano un efectivo por encima del techo acotado', () => {
    expect(esVueltoJustificado(10_000_010, 10_000_009)).toBe(false)
  })

  it('vuelto igual al valor de un billete no alcanza para justificar un efectivo que lo necesitaría', () => {
    // Mutation-proof-tests: la ÚNICA forma de completar 4000 en billetes reales usa como mínimo
    // un billete <= 2000 (dos de 2000, u otras combinaciones más chicas) — ningún billete
    // estrictamente MAYOR a 2000 arma 4000 solo (10000/20000 se pasan).
    //
    // Evidencia de mutación (mutation-proof-tests regla 2): cambiar el filtro de
    // `esVueltoJustificado` de `billete > vuelto` a `billete >= vuelto` admite el billete de
    // 2000 (justo el vuelto) y esta prueba pasaría a fallar (4000 = 2×2000 se aceptaría).
    // Mutado y revertido — ver reporte de la tarea.
    expect(esVueltoJustificado(4000, 2000)).toBe(false)
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
      { idFila: 1, idMedioPago: 1, comportamiento: 'Efectivo', admiteVuelto: true, requiereReferencia: false, importe: 80, referencia: null },
      {
        idFila: 2,
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

  // ---- regla 3: vuelto_no_justificado (decisión del dueño, 2026-09-16 — reemplaza el
  // vuelto_maximo fijo/parametrizado: el vuelto es válido si el efectivo entregado es formable
  // con billetes argentinos válidos, todos estrictamente mayores al vuelto) ------------------

  it('regla 3: ejemplo del dueño — ticket 5500, entrega 10000, vuelto 4500, un billete de 10000 alcanza', () => {
    const pagos = [{ ...pagoFixture({ importe: 10000, admiteVuelto: true }), vuelto: 4500 }]
    expect(validarPagosLocal({ ...base, total: 5500, pagos })).toBeNull()
  })

  it('regla 3: ejemplo del dueño — ticket 5500, entrega 6000, vuelto 500, tres billetes de 2000', () => {
    const pagos = [{ ...pagoFixture({ importe: 6000, admiteVuelto: true }), vuelto: 500 }]
    expect(validarPagosLocal({ ...base, total: 5500, pagos })).toBeNull()
  })

  it('regla 3: ejemplo del dueño — ticket 5500, entrega 5520, vuelto 20 no formable, se rechaza', () => {
    const pagos = [{ ...pagoFixture({ importe: 5520, admiteVuelto: true }), vuelto: 20 }]
    expect(validarPagosLocal({ ...base, total: 5500, pagos })?.codigo).toBe('vuelto_no_justificado')
  })

  it('regla 3: ejemplo del dueño — ticket 5500, entrega 30000, vuelto 24500 sin billete suficiente', () => {
    const pagos = [{ ...pagoFixture({ importe: 30000, admiteVuelto: true }), vuelto: 24500 }]
    expect(validarPagosLocal({ ...base, total: 5500, pagos })?.codigo).toBe('vuelto_no_justificado')
  })

  it('regla 3: sin ningún pago que admita vuelto, un vuelto > 0 nunca es justificado', () => {
    const pagos = [
      { ...pagoFixture({ comportamiento: 'Electronico', importe: 100, admiteVuelto: false }), vuelto: 30 },
    ]
    expect(validarPagosLocal({ ...base, total: 70, pagos })?.codigo).toBe('vuelto_no_justificado')
  })

  it('regla 3: solo el efectivo cuenta como billetes, aunque una Transferencia admita vuelto por configuración', () => {
    // Efectivo entregado REAL: solo 100 (comportamiento Efectivo). La Transferencia aporta 9900
    // y carga el vuelto de 4500 a mano — su medio tiene admiteVuelto=true (config de catálogo
    // atípica: ese flag es por medio, no está atado a comportamiento), pero una transferencia no
    // es un billete físico. Σ importe total (100+9900=10000) SÍ sería formable contra un vuelto
    // de 4500 (un billete de 10000) si se contara por error el importe de la Transferencia como
    // "billetes" — la regla 3 tiene que rechazar igual, porque el efectivo real (100) no alcanza.
    //
    // Mutation-proof-tests: si `efectivoEntregado` volviera a filtrar por `admiteVuelto` en vez
    // de `comportamiento === 'Efectivo'`, este test pasaría a aceptar la venta (falso negativo).
    // Evidencia de mutación: ver reporte de la tarea (mutado y revertido en pagos.ts).
    const pagos = [
      { ...pagoFixture({ idFila: 1, comportamiento: 'Efectivo', admiteVuelto: true, importe: 100 }), vuelto: 0 },
      { ...pagoFixture({ idFila: 2, comportamiento: 'Electronico', admiteVuelto: true, importe: 9900 }), vuelto: 4500 },
    ]
    expect(validarPagosLocal({ ...base, total: 5500, pagos })?.codigo).toBe('vuelto_no_justificado')
  })

  it('regla 4: vuelto sobre un medio que no admite vuelto', () => {
    // Efectivo entrega 200 sin vuelto propio (justificaría hasta 20 de sobra); Tarjeta declara
    // un vuelto de 20 a mano -> la regla 3 no corta (200 es formable contra un vuelto de 20), la
    // que corta es la 4.
    const pagos = [
      { ...pagoFixture({ idFila: 1, comportamiento: 'Efectivo', importe: 200, admiteVuelto: true }), vuelto: 0 },
      { ...pagoFixture({ idFila: 2, comportamiento: 'Electronico', importe: 120, admiteVuelto: false }), vuelto: 20 },
    ]
    expect(validarPagosLocal({ ...base, total: 300, pagos })?.codigo).toBe('medio_no_admite_vuelto')
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
    // 100 pagados sobre 100 de total ⇒ nada sobra, pero se declara un vuelto de 5 a mano (100 sí
    // sería formable contra un vuelto de 5 — la regla 3 no corta, aísla la regla 8).
    const pagos = [{ ...pagoFixture({ importe: 100, admiteVuelto: true }), vuelto: 5 }]
    expect(validarPagosLocal({ ...base, pagos })?.codigo).toBe('vuelto_invalido')
  })

  it('regla 8: el vuelto que sí coincide con lo que sobra del pago se acepta', () => {
    // Un solo billete de 1000 sobre un total de 100 ⇒ excedente 900 == vuelto declarado 900, y
    // 1000 es formable con ese único billete (> 900).
    const pagos = [{ ...pagoFixture({ importe: 1000, admiteVuelto: true }), vuelto: 900 }]
    expect(validarPagosLocal({ ...base, pagos })).toBeNull()
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

describe('pagos — idMedioEfectivo', () => {
  it('devuelve el id del medio con comportamiento Efectivo', () => {
    const efectivo = medioFixture({ id: 1, comportamiento: 'Efectivo' })
    const tarjeta = medioFixture({ id: 2, nombre: 'Tarjeta', comportamiento: 'Electronico' })
    expect(idMedioEfectivo([tarjeta, efectivo])).toBe(1)
  })

  it('sin medios (todavía no cargó) devuelve \'\'', () => {
    expect(idMedioEfectivo(null)).toBe('')
  })

  it('sin ningún medio Efectivo configurado devuelve \'\'', () => {
    const tarjeta = medioFixture({ id: 2, nombre: 'Tarjeta', comportamiento: 'Electronico' })
    expect(idMedioEfectivo([tarjeta])).toBe('')
  })
})

describe('pagos — filaPagoInicial', () => {
  it('con un medio Efectivo configurado, preselecciona su id', () => {
    const efectivo = medioFixture({ id: 3, comportamiento: 'Efectivo' })
    expect(filaPagoInicial(7, [efectivo])).toEqual({ id: 7, idMedioPago: 3, importe: '', referencia: '', vueltoManual: '' })
  })

  it('sin medios cargados (null), se comporta igual que filaPagoVacia', () => {
    expect(filaPagoInicial(7, null)).toEqual(filaPagoVacia(7))
  })

  it('sin ningún medio Efectivo, se comporta igual que filaPagoVacia', () => {
    const tarjeta = medioFixture({ id: 2, nombre: 'Tarjeta', comportamiento: 'Electronico' })
    expect(filaPagoInicial(7, [tarjeta])).toEqual(filaPagoVacia(7))
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
      { idFila: 1, idMedioPago: 1, comportamiento: 'Efectivo', admiteVuelto: true, requiereReferencia: false, importe: 120, referencia: null, vuelto: 20 },
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

  it('regresión: dos filas con el MISMO medio (split de pago) no colapsan — cada una conserva su propio vuelto', () => {
    // Fila 1: sin sobreescritura, se queda con el vuelto sugerido (todo el excedente, por ser
    // la primera fila que admite vuelto). Fila 2: mismo medio, con sobreescritura manual propia.
    // Antes de la corrección, un Map keyed por `idMedioPago` colapsaba ambas filas y las dos
    // terminaban resolviendo al `vueltoManual` de la ÚLTIMA fila registrada.
    const filas: FilaPago[] = [
      { id: 1, idMedioPago: 1, importe: '80', referencia: '', vueltoManual: '' },
      { id: 2, idMedioPago: 1, importe: '50', referencia: '', vueltoManual: '5' },
    ]
    const resultado = filasAPagosConVuelto(filas, medioPorId, 100)

    expect(resultado).toHaveLength(2)
    expect(resultado[0]).toMatchObject({ idFila: 1, importe: 80, vuelto: 30 })
    expect(resultado[1]).toMatchObject({ idFila: 2, importe: 50, vuelto: 5 })
  })
})

describe('pagos — aPagosDeVenta', () => {
  it('mapea al shape del request de checkout', () => {
    const pagos = calcularPagosConVuelto([pagoFixture({ importe: 120, admiteVuelto: true, referencia: 'x' })], 100)
    expect(aPagosDeVenta(pagos)).toEqual([{ idMedioPago: 1, importe: 120, referencia: 'x', vuelto: 20 }])
  })
})
