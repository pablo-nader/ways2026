import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  aSolicitudDeAjuste,
  aSolicitudDePagoACuenta,
  aSolicitudDeReliquidacion,
  calcularImporteAplicado,
  construirQueryEstadoDeCuenta,
  disponibilidadPrevia,
  etiquetaDeMovimiento,
  filaPagoACuentaVacia,
  filasAPagosACuentaParaCalculo,
  medioFisicoParaPagoACuenta,
  parsearDetalleDeActualizacionPrecios,
  rangoUltimoMes,
  reliquidacionEsNoOp,
  saldoResultanteDeAjuste,
  validarAjusteLocal,
  validarPagoACuentaLocal,
  type FilaPagoACuenta,
  type PagoACuentaParaCalculo,
} from './cuentaCorriente'
import type { MedioPagoListado, MovimientoDeCuentaCorriente, ResultadoDeReliquidacion } from './tipos'

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

// Espeja `desplazamientoUtcLocal` (no exportada) para calcular el offset esperado con el MISMO
// criterio que la implementación, sin fijar una zona horaria — el offset real depende de la
// máquina donde corre el test (regresión: los strings sin offset fallaban en ART pero pasaban en
// UTC, un test que fija "-03:00" a mano repetiría el mismo error en otra zona horaria).
function offsetEsperado(anio: number, mes: number, dia: number): string {
  const minutos = new Date(anio, mes - 1, dia).getTimezoneOffset()
  const signo = minutos > 0 ? '-' : '+'
  const minutosAbsolutos = Math.abs(minutos)
  const horas = String(Math.floor(minutosAbsolutos / 60)).padStart(2, '0')
  const restoMinutos = String(minutosAbsolutos % 60).padStart(2, '0')
  return `${signo}${horas}:${restoMinutos}`
}

describe('construirQueryEstadoDeCuenta', () => {
  it('sin ningún filtro no manda query string — el servidor aplica su default de último mes', () => {
    expect(construirQueryEstadoDeCuenta('', '', false)).toBe('')
  })

  it('desde solo expande al borde inicial del día, con el offset horario local del navegador', () => {
    const offset = offsetEsperado(2026, 7, 1)
    const esperado = new URLSearchParams({ desde: `2026-07-01T00:00:00${offset}` }).toString()
    expect(construirQueryEstadoDeCuenta('2026-07-01', '', false)).toBe(`?${esperado}`)
  })

  it('hasta solo expande al borde final del día, con el offset horario local del navegador', () => {
    const offset = offsetEsperado(2026, 7, 31)
    const esperado = new URLSearchParams({ hasta: `2026-07-31T23:59:59.999${offset}` }).toString()
    expect(construirQueryEstadoDeCuenta('', '2026-07-31', false)).toBe(`?${esperado}`)
  })

  it('desde y hasta juntos, cada uno con su propio offset', () => {
    const offsetDesde = offsetEsperado(2026, 7, 1)
    const offsetHasta = offsetEsperado(2026, 7, 31)
    const query = decodeURIComponent(construirQueryEstadoDeCuenta('2026-07-01', '2026-07-31', false))
    expect(query).toContain(`desde=2026-07-01T00:00:00${offsetDesde}`)
    expect(query).toContain(`hasta=2026-07-31T23:59:59.999${offsetHasta}`)
  })

  it('historico gana sobre desde/hasta — ninguno de los dos viaja', () => {
    expect(construirQueryEstadoDeCuenta('2026-07-01', '2026-07-31', true)).toBe('?historico=true')
  })
})

// Los tests de arriba espejan la fórmula de la implementación con `offsetEsperado` para no
// depender de la zona horaria de la máquina que corre el test — pero eso deja pasar un flip de
// signo circular. Estos dos fijan `getTimezoneOffset` a un valor conocido y assertan el literal
// esperado, sin espejar ninguna fórmula.
describe('construirQueryEstadoDeCuenta — offset fijo (sin espejar la fórmula de la implementación)', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('minutos=180 (UTC-3) produce el literal -03:00', () => {
    vi.spyOn(Date.prototype, 'getTimezoneOffset').mockReturnValue(180)
    const query = decodeURIComponent(construirQueryEstadoDeCuenta('2026-07-01', '2026-07-31', false))
    expect(query).toContain('desde=2026-07-01T00:00:00-03:00')
    expect(query).toContain('hasta=2026-07-31T23:59:59.999-03:00')
  })

  it('minutos=-330 (UTC+5:30) produce +05:30, codificado como %2B05%3A30', () => {
    vi.spyOn(Date.prototype, 'getTimezoneOffset').mockReturnValue(-330)
    const query = construirQueryEstadoDeCuenta('2026-07-01', '', false)
    expect(query).toContain('%2B05%3A30')
    expect(decodeURIComponent(query)).toContain('desde=2026-07-01T00:00:00+05:30')
  })
})

describe('rangoUltimoMes', () => {
  it('desde = hoy − 1 mes, hasta = hoy (fechas locales, mismo default que el servidor)', () => {
    const ahora = new Date(2026, 7, 15) // 15 de agosto de 2026 (mes 0-indexado)
    expect(rangoUltimoMes(ahora)).toEqual({ desde: '2026-07-15', hasta: '2026-08-15' })
  })

  it('cruza el año cuando el mes actual es enero', () => {
    const ahora = new Date(2026, 0, 10)
    expect(rangoUltimoMes(ahora)).toEqual({ desde: '2025-12-10', hasta: '2026-01-10' })
  })

  it('fin de mes: 31 de marzo → 28 de febrero (semántica AddMonths-clamp, sin overflow a marzo)', () => {
    const ahora = new Date(2026, 2, 31) // 2026 no es bisiesto
    expect(rangoUltimoMes(ahora)).toEqual({ desde: '2026-02-28', hasta: '2026-03-31' })
  })

  it('fin de mes en año bisiesto: 31 de marzo → 29 de febrero', () => {
    const ahora = new Date(2028, 2, 31) // 2028 es bisiesto
    expect(rangoUltimoMes(ahora)).toEqual({ desde: '2028-02-29', hasta: '2028-03-31' })
  })

  it('fin de mes: 31 de julio → 30 de junio (junio tiene 30 días)', () => {
    const ahora = new Date(2026, 6, 31)
    expect(rangoUltimoMes(ahora)).toEqual({ desde: '2026-06-30', hasta: '2026-07-31' })
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

describe('validarAjusteLocal — espejo de ReglaDeAjusteDeCuenta.Validar', () => {
  it('acepta un ajuste válido', () => {
    expect(validarAjusteLocal({ importe: 40, detalle: 'Descuento por reclamo' })).toBeNull()
  })

  it('importe cero se rechaza con ajuste_importe_invalido', () => {
    expect(validarAjusteLocal({ importe: 0, detalle: 'Detalle válido' })).toEqual({
      codigo: 'ajuste_importe_invalido',
      mensaje: 'El importe del ajuste no puede ser cero.',
    })
  })

  it('importe no numérico (campo vacío) se rechaza como ajuste_importe_invalido', () => {
    expect(validarAjusteLocal({ importe: Number.NaN, detalle: 'Detalle válido' })).toEqual({
      codigo: 'ajuste_importe_invalido',
      mensaje: 'El importe del ajuste no puede ser cero.',
    })
  })

  it('detalle vacío se rechaza con ajuste_detalle_requerido', () => {
    expect(validarAjusteLocal({ importe: 40, detalle: '' })).toEqual({
      codigo: 'ajuste_detalle_requerido',
      mensaje: 'El detalle del ajuste es obligatorio y tiene que tener al menos 5 caracteres.',
    })
  })

  it('detalle de 4 caracteres (recortado) se rechaza — un carácter por debajo del piso', () => {
    expect(validarAjusteLocal({ importe: 40, detalle: '  abcd  ' })?.codigo).toBe('ajuste_detalle_requerido')
  })

  it('detalle de exactamente 5 caracteres (recortado) se acepta — el límite inclusive', () => {
    expect(validarAjusteLocal({ importe: 40, detalle: '  abcde  ' })).toBeNull()
  })

  it('un detalle de solo espacios se rechaza como vacío', () => {
    expect(validarAjusteLocal({ importe: 40, detalle: '     ' })?.codigo).toBe('ajuste_detalle_requerido')
  })

  it('importe negativo con detalle válido se acepta — el signo no lo decide esta regla', () => {
    expect(validarAjusteLocal({ importe: -50, detalle: 'Descuento por reclamo' })).toBeNull()
  })
})

describe('saldoResultanteDeAjuste', () => {
  it('un ajuste positivo aumenta el saldo (aumenta la deuda)', () => {
    expect(saldoResultanteDeAjuste(100, 40)).toBe(140)
  })

  it('un ajuste negativo reduce el saldo (reduce la deuda)', () => {
    expect(saldoResultanteDeAjuste(300, -50)).toBe(250)
  })

  it('redondea a 2 decimales', () => {
    expect(saldoResultanteDeAjuste(10.005, 0.005)).toBe(10.01)
  })
})

describe('aSolicitudDeAjuste', () => {
  it('arma el cuerpo del POST con la forma exacta del contrato, detalle recortado', () => {
    expect(aSolicitudDeAjuste(7, -50, '  Descuento por reclamo  ')).toEqual({
      idPuntoVenta: 7,
      importe: -50,
      detalle: 'Descuento por reclamo',
    })
  })
})

describe('aSolicitudDeReliquidacion', () => {
  it('arma el cuerpo del POST con la forma exacta del contrato — solo idPuntoVenta', () => {
    expect(aSolicitudDeReliquidacion(7)).toEqual({ idPuntoVenta: 7 })
  })
})

describe('reliquidacionEsNoOp', () => {
  function resultadoFixture(sobrescribir: Partial<ResultadoDeReliquidacion> = {}): ResultadoDeReliquidacion {
    return { delta: 0, idsMovimientosCubiertos: [], detalle: [], hayMas: false, ...sobrescribir }
  }

  it('sin consumos cubiertos es un no-op limpio', () => {
    expect(reliquidacionEsNoOp(resultadoFixture())).toBe(true)
  })

  it('con al menos un consumo cubierto NUNCA es un no-op, aunque el delta total sea 0', () => {
    // Un consumo cubierto con líneas no-precificables aporta delta 0 sin que la corrida sea "nada
    // para hacer" — está PROCESADO, solo que su delta neto es cero.
    expect(reliquidacionEsNoOp(resultadoFixture({ delta: 0, idsMovimientosCubiertos: [1] }))).toBe(false)
  })

  it('con delta distinto de cero y consumos cubiertos no es un no-op', () => {
    expect(reliquidacionEsNoOp(resultadoFixture({ delta: 45, idsMovimientosCubiertos: [1, 2] }))).toBe(false)
  })
})

describe('parsearDetalleDeActualizacionPrecios', () => {
  it('detalle PascalCase real (lo que emite JsonSerializer.Serialize en el backend, sin naming policy) se parsea completo, incl. líneas y motivo', () => {
    const crudo = JSON.stringify([
      {
        IdMovimiento: 3,
        IdComprobanteVenta: 10,
        Delta: 55,
        Lineas: [
          {
            IdArticulo: 1,
            Cantidad: 2,
            PrecioHistorico: 100,
            PrecioActual: 120,
            TotalHistorico: 200,
            TotalDelDia: 240,
            Delta: 40,
            Motivo: null,
          },
          {
            IdArticulo: null,
            Cantidad: 1,
            PrecioHistorico: 50,
            PrecioActual: null,
            TotalHistorico: 50,
            TotalDelDia: null,
            Delta: 0,
            Motivo: 'Línea de concepto libre (sin artículo) — no re-precificable.',
          },
        ],
      },
    ])

    expect(parsearDetalleDeActualizacionPrecios(crudo)).toEqual([
      {
        idMovimiento: 3,
        idComprobanteVenta: 10,
        delta: 55,
        lineas: [
          {
            idArticulo: 1,
            cantidad: 2,
            precioHistorico: 100,
            precioActual: 120,
            totalHistorico: 200,
            totalDelDia: 240,
            delta: 40,
            motivo: null,
          },
          {
            idArticulo: null,
            cantidad: 1,
            precioHistorico: 50,
            precioActual: null,
            totalHistorico: 50,
            totalDelDia: null,
            delta: 0,
            motivo: 'Línea de concepto libre (sin artículo) — no re-precificable.',
          },
        ],
      },
    ])
  })

  it('detalle camelCase (forma de la API, aceptada por robustez) también se parsea', () => {
    const crudo = JSON.stringify([
      {
        idMovimiento: 3,
        idComprobanteVenta: 10,
        delta: 40,
        lineas: [
          {
            idArticulo: 1,
            cantidad: 2,
            precioHistorico: 100,
            precioActual: 120,
            totalHistorico: 200,
            totalDelDia: 240,
            delta: 40,
            motivo: null,
          },
        ],
      },
    ])

    expect(parsearDetalleDeActualizacionPrecios(crudo)).toEqual([
      {
        idMovimiento: 3,
        idComprobanteVenta: 10,
        delta: 40,
        lineas: [
          {
            idArticulo: 1,
            cantidad: 2,
            precioHistorico: 100,
            precioActual: 120,
            totalHistorico: 200,
            totalDelDia: 240,
            delta: 40,
            motivo: null,
          },
        ],
      },
    ])
  })

  it('un campo requerido ausente (ni PascalCase ni camelCase) da null, nunca lanza', () => {
    const crudo = JSON.stringify([{ IdMovimiento: 3, Delta: 55, Lineas: [] }]) // falta IdComprobanteVenta
    expect(parsearDetalleDeActualizacionPrecios(crudo)).toBeNull()
  })

  it('un nivel superior que no es array da null', () => {
    const crudo = JSON.stringify({ IdMovimiento: 3, IdComprobanteVenta: 10, Delta: 55, Lineas: [] })
    expect(parsearDetalleDeActualizacionPrecios(crudo)).toBeNull()
  })

  it('un JSON malformado da null, nunca lanza', () => {
    expect(parsearDetalleDeActualizacionPrecios('{esto no es json')).toBeNull()
  })

  it('Lineas ausente da null', () => {
    const crudo = JSON.stringify([{ IdMovimiento: 3, IdComprobanteVenta: 10, Delta: 55 }])
    expect(parsearDetalleDeActualizacionPrecios(crudo)).toBeNull()
  })

  it('Lineas que no es array da null', () => {
    const crudo = JSON.stringify([{ IdMovimiento: 3, IdComprobanteVenta: 10, Delta: 55, Lineas: 'no-es-array' }])
    expect(parsearDetalleDeActualizacionPrecios(crudo)).toBeNull()
  })
})
