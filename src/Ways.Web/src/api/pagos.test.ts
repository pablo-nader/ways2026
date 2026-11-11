import { describe, expect, it } from 'vitest'
import {
  aPagosDeVenta,
  aplicarImportesSugeridos,
  calcularExcedente,
  calcularFaltante,
  calcularPagosConVuelto,
  consumoCuentaCorriente,
  excedenteParaPagoACuenta,
  filaPagoInicial,
  filaPagoVacia,
  filasAPagosConVuelto,
  filasAPagosParaCalculo,
  filasConMedioInvalidoReseteado,
  idMedioEfectivo,
  medioDisponibleParaCliente,
  sinVuelto,
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
    porcentajeFacturacion: 0,
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
    const filas: FilaPago[] = [{ id: 1, idMedioPago: '', importe: 100, referencia: '', vueltoManual: null }]
    expect(filasAPagosParaCalculo(filas, medioPorId)).toEqual([])
  })

  it('descarta filas con importe null, 0 o negativo', () => {
    const filas: FilaPago[] = [
      { id: 1, idMedioPago: 1, importe: null, referencia: '', vueltoManual: null },
      { id: 3, idMedioPago: 1, importe: 0, referencia: '', vueltoManual: null },
      { id: 4, idMedioPago: 1, importe: -5, referencia: '', vueltoManual: null },
    ]
    expect(filasAPagosParaCalculo(filas, medioPorId)).toEqual([])
  })

  it('descarta filas cuyo medio no existe en el índice (catálogo todavía no cargó)', () => {
    const filas: FilaPago[] = [{ id: 1, idMedioPago: 999, importe: 100, referencia: '', vueltoManual: null }]
    expect(filasAPagosParaCalculo(filas, medioPorId)).toEqual([])
  })

  it('mapea filas completas al shape de cálculo, con referencia vacía convertida a null', () => {
    const filas: FilaPago[] = [
      { id: 1, idMedioPago: 1, importe: 80, referencia: '  ', vueltoManual: null },
      { id: 2, idMedioPago: 2, importe: 20, referencia: 'auth-123', vueltoManual: null },
    ]
    expect(filasAPagosParaCalculo(filas, medioPorId)).toEqual([
      { idFila: 1, idMedioPago: 1, nombre: 'Efectivo', comportamiento: 'Efectivo', admiteVuelto: true, requiereReferencia: false, importe: 80, referencia: null },
      {
        idFila: 2,
        idMedioPago: 2,
        nombre: 'Tarjeta',
        comportamiento: 'Electronico',
        admiteVuelto: false,
        requiereReferencia: true,
        importe: 20,
        referencia: 'auth-123',
      },
    ])
  })
})

describe('pagos — filasConMedioInvalidoReseteado (stage-pos-borrador-persistente)', () => {
  const medioPorId: Record<number, MedioPagoListado> = {
    1: medioFixture({ id: 1, nombre: 'Efectivo', comportamiento: 'Efectivo', admiteVuelto: true }),
  }

  it('resetea a "sin elegir" una fila cuyo medio no existe en el índice', () => {
    const filas: FilaPago[] = [{ id: 1, idMedioPago: 999, importe: 100, referencia: '', vueltoManual: null }]
    expect(filasConMedioInvalidoReseteado(filas, medioPorId)).toEqual([
      { id: 1, idMedioPago: '', importe: 100, referencia: '', vueltoManual: null },
    ])
  })

  it('deja intacta una fila cuyo medio SÍ existe en el índice', () => {
    const filas: FilaPago[] = [{ id: 1, idMedioPago: 1, importe: 100, referencia: '', vueltoManual: null }]
    expect(filasConMedioInvalidoReseteado(filas, medioPorId)).toEqual(filas)
  })

  it('deja intacta una fila sin medio elegido (ya está en "sin elegir")', () => {
    const filas: FilaPago[] = [{ id: 1, idMedioPago: '', importe: null, referencia: '', vueltoManual: null }]
    expect(filasConMedioInvalidoReseteado(filas, medioPorId)).toEqual(filas)
  })

  it('sin ninguna fila para resetear, devuelve la MISMA referencia del array de entrada', () => {
    const filas: FilaPago[] = [{ id: 1, idMedioPago: 1, importe: 100, referencia: '', vueltoManual: null }]
    expect(filasConMedioInvalidoReseteado(filas, medioPorId)).toBe(filas)
  })

  it('en una lista de varias filas, resetea solo la que tiene el medio inválido', () => {
    const filas: FilaPago[] = [
      { id: 1, idMedioPago: 1, importe: 60, referencia: '', vueltoManual: null },
      { id: 2, idMedioPago: 999, importe: 40, referencia: '', vueltoManual: null },
    ]
    expect(filasConMedioInvalidoReseteado(filas, medioPorId)).toEqual([
      { id: 1, idMedioPago: 1, importe: 60, referencia: '', vueltoManual: null },
      { id: 2, idMedioPago: '', importe: 40, referencia: '', vueltoManual: null },
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

  it('regla 0c: un comprobante de total negativo con un pago o un vuelto positivo se rechaza', () => {
    const conPago = calcularPagosConVuelto([pagoFixture({ importe: 100 })], 100)
    expect(validarPagosLocal({ ...base, total: -100, pagos: conPago })?.codigo).toBe('pago_en_comprobante_negativo')
    const conVuelto = [{ ...pagoFixture({ importe: 0 }), vuelto: 5 }]
    expect(validarPagosLocal({ ...base, total: -100, pagos: conVuelto })?.codigo).toBe('pago_en_comprobante_negativo')
  })

  it('regla 0c: con total negativo, sin pagos o con filas en cero se acepta', () => {
    expect(validarPagosLocal({ ...base, total: -100, pagos: [] })).toBeNull()
    const enCero = [{ ...pagoFixture({ importe: 0 }), vuelto: 0 }]
    expect(validarPagosLocal({ ...base, total: -100, pagos: enCero })).toBeNull()
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

  it('regla 3 eliminada: un ticket de 7500 pagado con 10500 y vuelto 3000 se acepta', () => {
    const pagos = [{ ...pagoFixture({ importe: 10500, admiteVuelto: true }), vuelto: 3000 }]
    expect(validarPagosLocal({ ...base, total: 7500, pagos })).toBeNull()
  })

  it('regla 3: un vuelto mayor al importe de su fila se rechaza aunque otra fila cubra el excedente', () => {
    const pagos = [
      { ...pagoFixture({ idFila: 1, comportamiento: 'Efectivo', importe: 10, admiteVuelto: true }), vuelto: 40 },
      { ...pagoFixture({ idFila: 2, comportamiento: 'Electronico', importe: 130, admiteVuelto: false }), vuelto: 0 },
    ]
    expect(validarPagosLocal({ ...base, total: 100, pagos })).toEqual({
      codigo: 'vuelto_supera_el_pago',
      mensaje: 'El vuelto no puede superar el importe del pago que lo da.',
    })
  })

  it('regla 3: un vuelto igual al importe de su fila no se rechaza por esta regla', () => {
    const pagos = [
      { ...pagoFixture({ idFila: 1, comportamiento: 'Efectivo', importe: 50, admiteVuelto: true }), vuelto: 50 },
      { ...pagoFixture({ idFila: 2, comportamiento: 'Electronico', importe: 100, admiteVuelto: false }), vuelto: 0 },
    ]
    expect(validarPagosLocal({ ...base, total: 100, pagos })).toBeNull()
  })

  it('regla 4: vuelto sobre un medio que no admite vuelto', () => {
    // Efectivo entrega 200 sin vuelto propio; Tarjeta declara un vuelto de 20 a mano.
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
    // 100 pagados sobre 100 de total ⇒ nada sobra, pero se declara un vuelto de 5 a mano.
    const pagos = [{ ...pagoFixture({ importe: 100, admiteVuelto: true }), vuelto: 5 }]
    expect(validarPagosLocal({ ...base, pagos })?.codigo).toBe('vuelto_invalido')
  })

  it('regla 8: el vuelto que sí coincide con lo que sobra del pago se acepta', () => {
    // 1000 pagados sobre un total de 100 ⇒ excedente 900 == vuelto declarado 900.
    const pagos = [{ ...pagoFixture({ importe: 1000, admiteVuelto: true }), vuelto: 900 }]
    expect(validarPagosLocal({ ...base, pagos })).toBeNull()
  })

  it('regla 8: un vuelto mayor al excedente se rechaza aunque el efectivo entregado lo cubra', () => {
    const pagos = [{ ...pagoFixture({ importe: 10500, admiteVuelto: true }), vuelto: 3500 }]
    expect(validarPagosLocal({ ...base, total: 7500, pagos })?.codigo).toBe('vuelto_invalido')
  })

  it('regla 9a: un medio sin vuelto por encima del total se rechaza con el nombre del medio (222000 de QR en 22200)', () => {
    const pagos = [{ ...pagoFixture({ comportamiento: 'Electronico', admiteVuelto: false, importe: 222000, nombre: 'QR' }), vuelto: 0 }]
    expect(validarPagosLocal({ ...base, total: 22200, toleranciaPago: 0, pagos })).toEqual({
      codigo: 'importe_supera_el_total',
      mensaje: 'El importe de QR supera el total de la venta.',
    })
  })

  it('regla 9: con exigirCobroSinExcedente en false lo cobrado de más no se rechaza, el resto de las reglas sí', () => {
    const qr = [{ ...pagoFixture({ comportamiento: 'Electronico', admiteVuelto: false, importe: 1000, nombre: 'QR' }), vuelto: 0 }]
    expect(validarPagosLocal({ ...base, total: 900, toleranciaPago: 0, pagos: qr })?.codigo).toBe('importe_supera_el_total')
    expect(validarPagosLocal({ ...base, total: 900, toleranciaPago: 0, pagos: qr, exigirCobroSinExcedente: false })).toBeNull()

    const efectivo = [{ ...pagoFixture({ importe: 1000 }), vuelto: 0 }]
    expect(validarPagosLocal({ ...base, total: 900, toleranciaPago: 0, pagos: efectivo })?.codigo).toBe('cobro_supera_el_total')
    expect(validarPagosLocal({ ...base, total: 900, toleranciaPago: 0, pagos: efectivo, exigirCobroSinExcedente: false })).toBeNull()

    const sinReferencia = [{ ...pagoFixture({ importe: 1000, admiteVuelto: false, requiereReferencia: true }), vuelto: 0 }]
    expect(validarPagosLocal({ ...base, total: 900, pagos: sinReferencia, exigirCobroSinExcedente: false })?.codigo).toBe(
      'referencia_de_pago_requerida',
    )
  })

  it('regla 9a: sin nombre del medio el mensaje usa la forma genérica', () => {
    const pagos = [{ ...pagoFixture({ admiteVuelto: false, importe: 150 }), vuelto: 0 }]
    expect(validarPagosLocal({ ...base, pagos })?.mensaje).toBe('El importe de un pago sin vuelto supera el total de la venta.')
  })

  it('regla 9a: el límite incluye la tolerancia (total + tolerancia pasa, un centavo más no)', () => {
    const justo = [{ ...pagoFixture({ admiteVuelto: false, importe: 110, nombre: 'QR' }), vuelto: 0 }]
    expect(validarPagosLocal({ ...base, pagos: justo })).toBeNull()
    const pasado = [{ ...pagoFixture({ admiteVuelto: false, importe: 110.01, nombre: 'QR' }), vuelto: 0 }]
    expect(validarPagosLocal({ ...base, pagos: pasado })?.codigo).toBe('importe_supera_el_total')
  })

  it('regla 9: efectivo por encima del total con su vuelto se acepta', () => {
    const pagos = [{ ...pagoFixture({ importe: 25000, admiteVuelto: true }), vuelto: 2800 }]
    expect(validarPagosLocal({ ...base, total: 22200, pagos })).toBeNull()
  })

  it('regla 9b: efectivo por encima del total sin vuelto se rechaza por cobrar de más', () => {
    const pagos = [{ ...pagoFixture({ importe: 25000, admiteVuelto: true }), vuelto: 0 }]
    expect(validarPagosLocal({ ...base, total: 22200, pagos })).toEqual({
      codigo: 'cobro_supera_el_total',
      mensaje: 'Lo cobrado supera el total de la venta: lo que sobra del efectivo tiene que volver como vuelto.',
    })
  })

  it('regla 9b: dos medios que juntos superan el total se rechazan aunque cada uno quede bajo el total', () => {
    const pagos = [
      { ...pagoFixture({ idFila: 1, admiteVuelto: false, importe: 80, nombre: 'QR' }), vuelto: 0 },
      { ...pagoFixture({ idFila: 2, admiteVuelto: false, importe: 80, nombre: 'Tarjeta' }), vuelto: 0 },
    ]
    expect(validarPagosLocal({ ...base, pagos })?.codigo).toBe('cobro_supera_el_total')
  })

  it('regla 9b: lo cobrado en el límite con tolerancia se acepta', () => {
    const pagos = [{ ...pagoFixture({ importe: 110, admiteVuelto: true }), vuelto: 0 }]
    expect(validarPagosLocal({ ...base, pagos })).toBeNull()
  })

  it('regla 9 va después de la 8: un vuelto inválido sigue reportándose como vuelto_invalido', () => {
    const pagos = [{ ...pagoFixture({ importe: 100, admiteVuelto: true }), vuelto: 5 }]
    expect(validarPagosLocal({ ...base, pagos })?.codigo).toBe('vuelto_invalido')
  })

  it('regla 9 va después de la 2: un pago que no cubre sigue reportándose como regla 2', () => {
    const pagos = [
      { ...pagoFixture({ idFila: 1, admiteVuelto: false, importe: 500, nombre: 'QR' }), vuelto: 0 },
      { ...pagoFixture({ idFila: 2, admiteVuelto: false, importe: 1, nombre: 'Tarjeta' }), vuelto: 0 },
    ]
    expect(validarPagosLocal({ ...base, total: 1000, toleranciaPago: 0, pagos })?.codigo).toBe('tolerancia_de_pago_superada')
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
    expect(filaPagoVacia(7)).toEqual({ id: 7, idMedioPago: '', importe: null, referencia: '', vueltoManual: null })
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
    expect(filaPagoInicial(7, [efectivo])).toEqual({ id: 7, idMedioPago: 3, importe: null, referencia: '', vueltoManual: null })
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
    const fila: FilaPago = { id: 1, idMedioPago: 1, importe: 120, referencia: '', vueltoManual: 99 }
    expect(vueltoDeFila(fila, false, 20)).toBe(0)
  })

  it('sin tocar el campo (vueltoManual null), usa el sugerido', () => {
    const fila: FilaPago = { id: 1, idMedioPago: 1, importe: 120, referencia: '', vueltoManual: null }
    expect(vueltoDeFila(fila, true, 20)).toBe(20)
  })

  it('con el campo tocado, usa el valor que tipeó el cajero', () => {
    const fila: FilaPago = { id: 1, idMedioPago: 1, importe: 120, referencia: '', vueltoManual: 15 }
    expect(vueltoDeFila(fila, true, 20)).toBe(15)
  })

  it('un vueltoManual en 0 (tocado y borrado) se respeta tal cual, no cae al sugerido', () => {
    const fila: FilaPago = { id: 1, idMedioPago: 1, importe: 120, referencia: '', vueltoManual: 0 }
    expect(vueltoDeFila(fila, true, 20)).toBe(0)
  })
})

describe('pagos — filasAPagosConVuelto', () => {
  const medioPorId: Record<number, MedioPagoListado> = {
    1: medioFixture({ id: 1, nombre: 'Efectivo', comportamiento: 'Efectivo', admiteVuelto: true }),
    2: medioFixture({ id: 2, nombre: 'Tarjeta', comportamiento: 'Electronico', admiteVuelto: false, requiereReferencia: true }),
  }

  it('sin sobreescritura manual, usa el vuelto sugerido por fila', () => {
    const filas: FilaPago[] = [{ id: 1, idMedioPago: 1, importe: 120, referencia: '', vueltoManual: null }]
    const resultado = filasAPagosConVuelto(filas, medioPorId, 100)
    expect(resultado).toEqual([
      { idFila: 1, idMedioPago: 1, nombre: 'Efectivo', comportamiento: 'Efectivo', admiteVuelto: true, requiereReferencia: false, importe: 120, referencia: null, vuelto: 20 },
    ])
  })

  it('con sobreescritura manual sobre un medio que admite vuelto, la respeta', () => {
    const filas: FilaPago[] = [{ id: 1, idMedioPago: 1, importe: 120, referencia: '', vueltoManual: 25 }]
    const resultado = filasAPagosConVuelto(filas, medioPorId, 100)
    expect(resultado[0].vuelto).toBe(25)
  })

  it('una sobreescritura manual sobre un medio sin AdmiteVuelto se ignora, siempre queda en 0', () => {
    const filas: FilaPago[] = [{ id: 1, idMedioPago: 2, importe: 100, referencia: 'auth', vueltoManual: 10 }]
    const resultado = filasAPagosConVuelto(filas, medioPorId, 100)
    expect(resultado[0].vuelto).toBe(0)
  })

  it('regresión: dos filas con el MISMO medio (split de pago) no colapsan — cada una conserva su propio vuelto', () => {
    // Fila 1: sin sobreescritura, se queda con el vuelto sugerido (todo el excedente, por ser
    // la primera fila que admite vuelto). Fila 2: mismo medio, con sobreescritura manual propia.
    // Antes de la corrección, un Map keyed por `idMedioPago` colapsaba ambas filas y las dos
    // terminaban resolviendo al `vueltoManual` de la ÚLTIMA fila registrada.
    const filas: FilaPago[] = [
      { id: 1, idMedioPago: 1, importe: 80, referencia: '', vueltoManual: null },
      { id: 2, idMedioPago: 1, importe: 50, referencia: '', vueltoManual: 5 },
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

describe('pagos — filasAPagosParaCalculo (nombre del medio)', () => {
  it('copia el nombre del medio para los mensajes de rechazo', () => {
    const medio = medioFixture({ id: 5, nombre: 'QR Mercado Pago', comportamiento: 'Electronico' })
    const filas: FilaPago[] = [{ id: 1, idMedioPago: 5, importe: 100, referencia: '', vueltoManual: null }]
    expect(filasAPagosParaCalculo(filas, { 5: medio })[0].nombre).toBe('QR Mercado Pago')
  })
})

describe('pagos — aplicarImportesSugeridos', () => {
  const fila = (id: number, extra: Partial<FilaPago> = {}): FilaPago => ({ ...filaPagoVacia(id), ...extra })

  it('la primera fila sin tocar propone el total completo', () => {
    expect(aplicarImportesSugeridos([fila(1)], 22200)[0].importe).toBe(22200)
  })

  it('una fila nueva con filas anteriores cargadas propone lo que falta', () => {
    const resultado = aplicarImportesSugeridos([fila(1, { importe: 10000, importeEditado: true }), fila(2)], 22200)
    expect(resultado.map((f) => f.importe)).toEqual([10000, 12200])
  })

  it('con las anteriores cubriendo el total no propone nada (nunca negativo)', () => {
    const resultado = aplicarImportesSugeridos([fila(1, { importe: 30000, importeEditado: true }), fila(2)], 22200)
    expect(resultado[1].importe).toBeNull()
  })

  it('una fila tocada y vaciada a mano no vuelve a la propuesta', () => {
    const filas = [fila(1, { importe: null, importeEditado: true })]
    expect(aplicarImportesSugeridos(filas, 22200)).toBe(filas)
  })

  it('un importe ya cargado sin marca (borrador anterior) no se pisa', () => {
    const filas = [fila(1, { importe: 500 })]
    expect(aplicarImportesSugeridos(filas, 22200)[0].importe).toBe(500)
  })

  it('la propuesta sigue al total mientras la fila no se toca', () => {
    const filas = [fila(1)]
    expect(aplicarImportesSugeridos(filas, 100)[0].importe).toBe(100)
    expect(aplicarImportesSugeridos(filas, 250)[0].importe).toBe(250)
  })

  it('sin total confiable o con total 0 devuelve las mismas filas', () => {
    const filas = [fila(1)]
    expect(aplicarImportesSugeridos(filas, null)).toBe(filas)
    expect(aplicarImportesSugeridos(filas, 0)).toBe(filas)
  })

  it('con propuesta, la fila se valida y se cobra por ese importe (efectivo sin vuelto)', () => {
    const efectivo = medioFixture({ id: 1, comportamiento: 'Efectivo', admiteVuelto: true })
    const filas = aplicarImportesSugeridos([fila(1, { idMedioPago: 1 })], 22200)
    const pagos = filasAPagosConVuelto(filas, { 1: efectivo }, 22200)
    expect(pagos[0].importe).toBe(22200)
    expect(pagos[0].vuelto).toBe(0)
  })
})

describe('pagos — excedenteParaPagoACuenta', () => {
  const qr = (importe: number) => pagoFixture({ idFila: 2, idMedioPago: 2, comportamiento: 'Electronico', admiteVuelto: false, importe })

  it('un QR por encima del total deja la diferencia sugerida a cuenta, aunque entre en la tolerancia', () => {
    expect(excedenteParaPagoACuenta({ total: 990, pagos: [qr(1000)], toleranciaPago: 10 })).toEqual({ importe: 10, sugerido: true })
  })

  it('con efectivo en la mezcla la diferencia se ofrece sin sugerir: puede volver como vuelto', () => {
    expect(excedenteParaPagoACuenta({ total: 990, pagos: [pagoFixture({ importe: 500 }), qr(600)], toleranciaPago: 0 })).toEqual({
      importe: 110,
      sugerido: false,
    })
  })

  it('un medio sin vuelto por encima del total más la tolerancia la sugiere aunque haya efectivo', () => {
    expect(excedenteParaPagoACuenta({ total: 990, pagos: [pagoFixture({ importe: 100 }), qr(1001)], toleranciaPago: 10 })).toEqual({
      importe: 111,
      sugerido: true,
    })
  })

  it('sin excedente no hay nada para dejar a cuenta', () => {
    expect(excedenteParaPagoACuenta({ total: 990, pagos: [qr(990)], toleranciaPago: 0 })).toBeNull()
    expect(excedenteParaPagoACuenta({ total: 990, pagos: [qr(900)], toleranciaPago: 100 })).toBeNull()
  })

  it('la cuenta corriente no cede: si el excedente solo sale de ella no se ofrece', () => {
    const cc = pagoFixture({ idFila: 3, idMedioPago: 3, comportamiento: 'CuentaCorriente', admiteVuelto: false, importe: 900 })
    expect(excedenteParaPagoACuenta({ total: 500, pagos: [cc, qr(100)], toleranciaPago: 0 })).toBeNull()
    const ccChica = { ...cc, importe: 400 }
    expect(excedenteParaPagoACuenta({ total: 500, pagos: [ccChica, qr(900)], toleranciaPago: 0 })).toEqual({ importe: 800, sugerido: true })
  })

  it('un medio con proveedor en la mezcla nunca lo ofrece', () => {
    expect(excedenteParaPagoACuenta({ total: 990, pagos: [{ ...qr(1000), proveedor: 'mercadopago_qr' }], toleranciaPago: 0 })).toBeNull()
  })
})

describe('pagos — sinVuelto', () => {
  it('pone el vuelto de cada pago en cero sin tocar el resto', () => {
    const pagos = [
      { ...pagoFixture({ importe: 1000 }), vuelto: 10 },
      { ...pagoFixture({ idFila: 2, importe: 50 }), vuelto: 0 },
    ]
    const resultado = sinVuelto(pagos)
    expect(resultado.map((p) => [p.importe, p.vuelto])).toEqual([
      [1000, 0],
      [50, 0],
    ])
    expect(resultado[1]).toBe(pagos[1])
  })
})
