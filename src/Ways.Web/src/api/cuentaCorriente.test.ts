import { describe, expect, it } from 'vitest'
import {
  aSolicitudDePagoACuenta,
  calcularImporteAplicado,
  construirQueryEstadoDeCuenta,
  disponibilidadPrevia,
  etiquetaDeMovimiento,
  filaPagoACuentaVacia,
  filasAPagosACuentaParaCalculo,
  medioFisicoParaPagoACuenta,
  validarPagoACuentaLocal,
  type FilaPagoACuenta,
  type PagoACuentaParaCalculo,
} from './cuentaCorriente'
import type { MedioPagoListado, MovimientoDeCuentaCorriente } from './tipos'

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

function pagoFixture(sobrescribir: Partial<PagoACuentaParaCalculo> = {}): PagoACuentaParaCalculo {
  return {
    idFila: 1,
    idMedioPago: 1,
    comportamiento: 'Efectivo',
    admiteVuelto: true,
    requiereReferencia: false,
    importe: 100,
    vuelto: 0,
    referencia: null,
    ...sobrescribir,
  }
}

describe('construirQueryEstadoDeCuenta', () => {
  it('sin ningún filtro no manda query string — el servidor aplica su default de último mes', () => {
    expect(construirQueryEstadoDeCuenta('', '', false)).toBe('')
  })

  it('desde solo expande al borde inicial del día', () => {
    expect(construirQueryEstadoDeCuenta('2026-07-01', '', false)).toBe('?desde=2026-07-01T00%3A00%3A00')
  })

  it('hasta solo expande al borde final del día', () => {
    expect(construirQueryEstadoDeCuenta('', '2026-07-31', false)).toBe('?hasta=2026-07-31T23%3A59%3A59.999')
  })

  it('desde y hasta juntos', () => {
    const query = construirQueryEstadoDeCuenta('2026-07-01', '2026-07-31', false)
    expect(query).toContain('desde=2026-07-01T00%3A00%3A00')
    expect(query).toContain('hasta=2026-07-31T23%3A59%3A59.999')
  })

  it('historico gana sobre desde/hasta — ninguno de los dos viaja', () => {
    expect(construirQueryEstadoDeCuenta('2026-07-01', '2026-07-31', true)).toBe('?historico=true')
  })
})

describe('etiquetaDeMovimiento', () => {
  const casos: { movimiento: Pick<MovimientoDeCuentaCorriente, 'tipo' | 'etiqueta'>; esperado: string }[] = [
    { movimiento: { tipo: 'Consumo', etiqueta: null }, esperado: 'Consumo' },
    { movimiento: { tipo: 'Pago', etiqueta: null }, esperado: 'Pago' },
    { movimiento: { tipo: 'ActualizacionPrecios', etiqueta: null }, esperado: 'Actualización de precios' },
    { movimiento: { tipo: 'Ajuste', etiqueta: 'Manual' }, esperado: 'Ajuste manual' },
    { movimiento: { tipo: 'Ajuste', etiqueta: 'AnulacionContramovimiento' }, esperado: 'Contramov. de anulación' },
  ]

  for (const { movimiento, esperado } of casos) {
    it(`${movimiento.tipo}${movimiento.etiqueta ? ` (${movimiento.etiqueta})` : ''} → "${esperado}"`, () => {
      expect(etiquetaDeMovimiento(movimiento)).toBe(esperado)
    })
  }
})

describe('disponibilidadPrevia', () => {
  it('crédito ilimitado siempre da null, sin importar el saldo', () => {
    expect(disponibilidadPrevia(50000, 1000, true)).toBeNull()
  })

  it('crédito limitado deriva límite − saldo', () => {
    expect(disponibilidadPrevia(300, 1000, false)).toBe(700)
  })

  it('un saldo que supera el límite da disponibilidad negativa', () => {
    expect(disponibilidadPrevia(1500, 1000, false)).toBe(-500)
  })
})

describe('medioFisicoParaPagoACuenta', () => {
  it('un medio Efectivo es físico', () => {
    expect(medioFisicoParaPagoACuenta(medioFixture({ comportamiento: 'Efectivo' }))).toBe(true)
  })

  it('un medio Electronico es físico', () => {
    expect(medioFisicoParaPagoACuenta(medioFixture({ comportamiento: 'Electronico' }))).toBe(true)
  })

  it('un medio CuentaCorriente NUNCA es físico — una deuda no puede pagar otra deuda', () => {
    expect(medioFisicoParaPagoACuenta(medioFixture({ comportamiento: 'CuentaCorriente' }))).toBe(false)
  })
})

describe('filasAPagosACuentaParaCalculo', () => {
  const medioPorId = { 1: medioFixture() }

  it('descarta una fila sin medio elegido', () => {
    const filas: FilaPagoACuenta[] = [{ id: 1, idMedioPago: '', importe: '100', referencia: '', vuelto: '' }]
    expect(filasAPagosACuentaParaCalculo(filas, medioPorId)).toEqual([])
  })

  it('descarta una fila sin importe positivo', () => {
    const filas: FilaPagoACuenta[] = [{ id: 1, idMedioPago: 1, importe: '0', referencia: '', vuelto: '' }]
    expect(filasAPagosACuentaParaCalculo(filas, medioPorId)).toEqual([])
  })

  it('una fila completa se convierte a pago de cálculo, vuelto vacío ⇒ 0', () => {
    const filas: FilaPagoACuenta[] = [{ id: 1, idMedioPago: 1, importe: '500', referencia: '', vuelto: '' }]
    expect(filasAPagosACuentaParaCalculo(filas, medioPorId)).toEqual([
      {
        idFila: 1,
        idMedioPago: 1,
        comportamiento: 'Efectivo',
        admiteVuelto: true,
        requiereReferencia: false,
        importe: 500,
        vuelto: 0,
        referencia: null,
      },
    ])
  })

  it('recorta la referencia y respeta el vuelto tipeado', () => {
    const filas: FilaPagoACuenta[] = [{ id: 1, idMedioPago: 1, importe: '500', referencia: '  ref-1  ', vuelto: '20' }]
    const resultado = filasAPagosACuentaParaCalculo(filas, medioPorId)
    expect(resultado[0].referencia).toBe('ref-1')
    expect(resultado[0].vuelto).toBe(20)
  })
})

describe('filaPagoACuentaVacia', () => {
  it('arma una fila vacía con el id dado', () => {
    expect(filaPagoACuentaVacia(3)).toEqual({ id: 3, idMedioPago: '', importe: '', referencia: '', vuelto: '' })
  })
})

describe('calcularImporteAplicado', () => {
  it('importeAplicado = Σ importe − Σ vuelto', () => {
    expect(calcularImporteAplicado([{ importe: 1000, vuelto: 200 }, { importe: 500, vuelto: 0 }])).toBe(1300)
  })

  it('redondea a 2 decimales', () => {
    expect(calcularImporteAplicado([{ importe: 10.005, vuelto: 0 }])).toBe(10.01)
  })

  it('un array vacío da 0', () => {
    expect(calcularImporteAplicado([])).toBe(0)
  })
})

describe('validarPagoACuentaLocal — orden observable, espejo de ValidadorDePagoACuenta', () => {
  it('acepta una mezcla válida', () => {
    expect(validarPagoACuentaLocal({ pagos: [pagoFixture()], vueltoMaximo: 20 })).toBeNull()
  })

  it('regla 1: importe negativo', () => {
    expect(validarPagoACuentaLocal({ pagos: [pagoFixture({ importe: -10 })], vueltoMaximo: 20 })).toEqual({
      codigo: 'pago_importe_negativo',
      mensaje: 'El importe de un pago no puede ser negativo.',
    })
  })

  it('regla 2: vuelto negativo', () => {
    expect(validarPagoACuentaLocal({ pagos: [pagoFixture({ vuelto: -5 })], vueltoMaximo: 20 })).toEqual({
      codigo: 'vuelto_negativo',
      mensaje: 'El vuelto de un pago no puede ser negativo.',
    })
  })

  it('regla 3: cuenta corriente nunca es un medio válido para una RC', () => {
    expect(
      validarPagoACuentaLocal({
        pagos: [pagoFixture({ comportamiento: 'CuentaCorriente' })],
        vueltoMaximo: 20,
      }),
    ).toEqual({
      codigo: 'pago_a_cuenta_sin_medios_fisicos',
      mensaje: 'Un pago a cuenta no admite cuenta corriente como medio de pago.',
    })
  })

  it('regla 4: vuelto sobre un medio que no admite vuelto', () => {
    expect(
      validarPagoACuentaLocal({
        pagos: [pagoFixture({ admiteVuelto: false, vuelto: 10 })],
        vueltoMaximo: 20,
      }),
    ).toEqual({ codigo: 'medio_no_admite_vuelto', mensaje: 'El medio de pago elegido no admite vuelto.' })
  })

  it('regla 5: Σ vuelto supera el vuelto máximo', () => {
    expect(validarPagoACuentaLocal({ pagos: [pagoFixture({ vuelto: 25 })], vueltoMaximo: 20 })).toEqual({
      codigo: 'vuelto_excedido',
      mensaje: 'El vuelto supera el máximo permitido.',
    })
  })

  it('regla 6: referencia requerida y ausente', () => {
    expect(
      validarPagoACuentaLocal({
        pagos: [pagoFixture({ requiereReferencia: true, referencia: null })],
        vueltoMaximo: 20,
      }),
    ).toEqual({ codigo: 'referencia_de_pago_requerida', mensaje: 'Este medio de pago requiere una referencia.' })
  })

  it('regla 7: importeAplicado <= 0 (Σ importe == Σ vuelto)', () => {
    expect(
      validarPagoACuentaLocal({
        pagos: [pagoFixture({ importe: 100, vuelto: 100, admiteVuelto: true })],
        vueltoMaximo: 200,
      }),
    ).toEqual({ codigo: 'pago_a_cuenta_sin_importe', mensaje: 'Tenés que ingresar al menos un pago a cuenta.' })
  })

  it('sin ningún pago, importeAplicado es 0 ⇒ rechazado', () => {
    expect(validarPagoACuentaLocal({ pagos: [], vueltoMaximo: 20 })).toEqual({
      codigo: 'pago_a_cuenta_sin_importe',
      mensaje: 'Tenés que ingresar al menos un pago a cuenta.',
    })
  })
})

describe('aSolicitudDePagoACuenta', () => {
  it('arma el cuerpo del POST con la forma exacta del contrato', () => {
    const solicitud = aSolicitudDePagoACuenta(7, [pagoFixture({ importe: 500, vuelto: 0, referencia: 'ref' })], '  nota  ')
    expect(solicitud).toEqual({
      idPuntoVenta: 7,
      pagos: [{ idMedioPago: 1, importe: 500, referencia: 'ref', vuelto: 0 }],
      observaciones: 'nota',
    })
  })

  it('observaciones en blanco se normaliza a null', () => {
    const solicitud = aSolicitudDePagoACuenta(7, [pagoFixture()], '   ')
    expect(solicitud.observaciones).toBeNull()
  })
})
