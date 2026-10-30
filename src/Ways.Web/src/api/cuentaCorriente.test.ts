import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  aSolicitudDeAjuste,
  aSolicitudDePagoACuenta,
  aSolicitudDeReliquidacion,
  calcularImporteAplicado,
  construirQueryEstadoDeCuenta,
  clienteParaElTicketDeVenta,
  derivarSaldoParaChip,
  disponibilidadPrevia,
  esSaldoEstimado,
  etiquetaDeMovimiento,
  filaPagoACuentaVacia,
  filasAPagosACuentaParaCalculo,
  medioFisicoParaPagoACuenta,
  motivoDeRangoInvalido,
  nombreCompletoDeCliente,
  parsearDetalleDeActualizacionPrecios,
  rangoDeLosUltimosDias,
  rangoUltimoMes,
  reliquidacionEsNoOp,
  saldoResultanteDeAjuste,
  saldosDeUnaVenta,
  saldosDeUnPagoACuenta,
  validarAjusteLocal,
  validarPagoACuentaLocal,
  type FilaPagoACuenta,
  type PagoACuentaParaCalculo,
  clienteDeCobros,
  clienteDeCuentaCorriente,
} from './cuentaCorriente'
import { api } from './cliente'
import type { ClienteListado, MedioPagoListado, MovimientoDeCuentaCorriente, ResultadoDeReliquidacion, TipoMovimientoCc } from './tipos'

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

describe('rangoDeLosUltimosDias', () => {
  it('desde = hoy − N días, hasta = hoy (fechas locales)', () => {
    expect(rangoDeLosUltimosDias(7, new Date(2026, 7, 15))).toEqual({ desde: '2026-08-08', hasta: '2026-08-15' })
  })

  it('cruza el mes y el año sin desbordar', () => {
    expect(rangoDeLosUltimosDias(7, new Date(2026, 0, 3))).toEqual({ desde: '2025-12-27', hasta: '2026-01-03' })
  })

  it('resta en días de calendario aunque el rango atraviese un 29 de febrero', () => {
    expect(rangoDeLosUltimosDias(7, new Date(2028, 2, 3))).toEqual({ desde: '2028-02-25', hasta: '2028-03-03' })
  })
})

describe('motivoDeRangoInvalido', () => {
  it('un rango ordenado, o de un solo día, es válido', () => {
    expect(motivoDeRangoInvalido('2026-08-01', '2026-08-15')).toBeNull()
    expect(motivoDeRangoInvalido('2026-08-15', '2026-08-15')).toBeNull()
  })

  it('con alguna fecha vacía pide completar las dos', () => {
    expect(motivoDeRangoInvalido('', '2026-08-15')).toBe('Completá las fechas Desde y Hasta para ver los movimientos.')
    expect(motivoDeRangoInvalido('2026-08-01', '')).toBe('Completá las fechas Desde y Hasta para ver los movimientos.')
  })

  it('con Desde posterior a Hasta lo dice', () => {
    expect(motivoDeRangoInvalido('2026-08-16', '2026-08-15')).toBe('La fecha Desde no puede ser posterior a Hasta.')
  })
})

describe('etiquetaDeMovimiento', () => {
  const casos: { movimiento: Pick<MovimientoDeCuentaCorriente, 'tipo' | 'etiqueta'>; esperado: string }[] = [
    { movimiento: { tipo: 'Consumo', etiqueta: null }, esperado: 'Consumo' },
    { movimiento: { tipo: 'Pago', etiqueta: null }, esperado: 'Pago' },
    { movimiento: { tipo: 'ActualizacionPrecios', etiqueta: null }, esperado: 'Actualización de precios' },
    { movimiento: { tipo: 'DescuentoPersonal', etiqueta: null }, esperado: 'Descuento de personal' },
    { movimiento: { tipo: 'Ajuste', etiqueta: 'Manual' }, esperado: 'Ajuste manual' },
    { movimiento: { tipo: 'Ajuste', etiqueta: 'AnulacionContramovimiento' }, esperado: 'Contramov. de anulación' },
  ]

  for (const { movimiento, esperado } of casos) {
    it(`${movimiento.tipo}${movimiento.etiqueta ? ` (${movimiento.etiqueta})` : ''} → "${esperado}"`, () => {
      expect(etiquetaDeMovimiento(movimiento)).toBe(esperado)
    })
  }

  // El Record obliga, al compilar, a listar TODOS los miembros de la unión: un tipo nuevo sin entrada acá
  // rompe `tsc -b`, y uno sin etiqueta en el switch devolvería undefined y rompería este test.
  const ETIQUETA_POR_TIPO: Record<TipoMovimientoCc, string> = {
    Consumo: 'Consumo',
    Pago: 'Pago',
    Ajuste: 'Ajuste manual',
    ActualizacionPrecios: 'Actualización de precios',
    DescuentoPersonal: 'Descuento de personal',
  }

  for (const [tipo, esperado] of Object.entries(ETIQUETA_POR_TIPO) as [TipoMovimientoCc, string][]) {
    it(`todo miembro de TipoMovimientoCc tiene etiqueta: ${tipo}`, () => {
      expect(etiquetaDeMovimiento({ tipo, etiqueta: null })).toBe(esperado)
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
    const filas: FilaPagoACuenta[] = [{ id: 1, idMedioPago: '', importe: 100, referencia: '', vuelto: null }]
    expect(filasAPagosACuentaParaCalculo(filas, medioPorId)).toEqual([])
  })

  it('descarta una fila sin importe positivo', () => {
    const filas: FilaPagoACuenta[] = [{ id: 1, idMedioPago: 1, importe: 0, referencia: '', vuelto: null }]
    expect(filasAPagosACuentaParaCalculo(filas, medioPorId)).toEqual([])
  })

  it('descarta una fila con importe null', () => {
    const filas: FilaPagoACuenta[] = [{ id: 1, idMedioPago: 1, importe: null, referencia: '', vuelto: null }]
    expect(filasAPagosACuentaParaCalculo(filas, medioPorId)).toEqual([])
  })

  it('una fila completa se convierte a pago de cálculo, vuelto null ⇒ 0', () => {
    const filas: FilaPagoACuenta[] = [{ id: 1, idMedioPago: 1, importe: 500, referencia: '', vuelto: null }]
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
    const filas: FilaPagoACuenta[] = [{ id: 1, idMedioPago: 1, importe: 500, referencia: '  ref-1  ', vuelto: 20 }]
    const resultado = filasAPagosACuentaParaCalculo(filas, medioPorId)
    expect(resultado[0].referencia).toBe('ref-1')
    expect(resultado[0].vuelto).toBe(20)
  })
})

describe('filaPagoACuentaVacia', () => {
  it('arma una fila vacía con el id dado', () => {
    expect(filaPagoACuentaVacia(3)).toEqual({ id: 3, idMedioPago: '', importe: null, referencia: '', vuelto: null })
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
    expect(validarPagoACuentaLocal({ pagos: [pagoFixture()] })).toBeNull()
  })

  it('regla 1: importe negativo', () => {
    expect(validarPagoACuentaLocal({ pagos: [pagoFixture({ importe: -10 })] })).toEqual({
      codigo: 'pago_importe_negativo',
      mensaje: 'El importe de un pago no puede ser negativo.',
    })
  })

  it('regla 2: vuelto negativo', () => {
    expect(validarPagoACuentaLocal({ pagos: [pagoFixture({ vuelto: -5 })] })).toEqual({
      codigo: 'vuelto_negativo',
      mensaje: 'El vuelto de un pago no puede ser negativo.',
    })
  })

  it('regla 3: cuenta corriente nunca es un medio válido para una RC', () => {
    expect(
      validarPagoACuentaLocal({
        pagos: [pagoFixture({ comportamiento: 'CuentaCorriente' })],
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
      }),
    ).toEqual({ codigo: 'medio_no_admite_vuelto', mensaje: 'El medio de pago elegido no admite vuelto.' })
  })

  // ---- regla 5: vuelto_no_justificado (decisión del dueño, 2026-09-16 — reemplaza el
  // vuelto_maximo fijo/parametrizado: mismo criterio que la regla 3 de validarPagosLocal) -----

  it('regla 5: ejemplo del dueño — entrega 10000, vuelto 4500, un billete de 10000 alcanza', () => {
    expect(validarPagoACuentaLocal({ pagos: [pagoFixture({ importe: 10000, vuelto: 4500 })] })).toBeNull()
  })

  it('regla 5: ejemplo del dueño — entrega 5520, vuelto 20 no formable, se rechaza', () => {
    expect(validarPagoACuentaLocal({ pagos: [pagoFixture({ importe: 5520, vuelto: 20 })] })).toEqual({
      codigo: 'vuelto_no_justificado',
      mensaje: 'El vuelto de $20 no se justifica con los billetes entregados.',
    })
  })

  it('regla 5: ejemplo del dueño — entrega 30000, vuelto 24500 sin billete suficiente, se rechaza', () => {
    expect(validarPagoACuentaLocal({ pagos: [pagoFixture({ importe: 30000, vuelto: 24500 })] })?.codigo).toBe(
      'vuelto_no_justificado',
    )
  })

  it('regla 5: solo el efectivo cuenta como billetes, aunque un medio Electronico admita vuelto por configuración', () => {
    // Mismo criterio que pagos.ts: admiteVuelto es un flag de catálogo (ABM), no atado a
    // comportamiento. El importe de un medio Electronico nunca cuenta como "billetes" acá.
    expect(
      validarPagoACuentaLocal({
        pagos: [pagoFixture({ comportamiento: 'Electronico', admiteVuelto: true, importe: 10000, vuelto: 4500 })],
      })?.codigo,
    ).toBe('vuelto_no_justificado')
  })

  it('regla 6: referencia requerida y ausente', () => {
    expect(
      validarPagoACuentaLocal({
        pagos: [pagoFixture({ requiereReferencia: true, referencia: null })],
      }),
    ).toEqual({ codigo: 'referencia_de_pago_requerida', mensaje: 'Este medio de pago requiere una referencia.' })
  })

  it('regla 7: importeAplicado <= 0, aislado de la regla 5 (vuelto = 0 siempre justificado)', () => {
    expect(
      validarPagoACuentaLocal({
        pagos: [pagoFixture({ importe: 0, vuelto: 0 })],
      }),
    ).toEqual({ codigo: 'pago_a_cuenta_sin_importe', mensaje: 'Tenés que ingresar al menos un pago a cuenta.' })
  })

  it('un vuelto igual al importe entregado se rechaza por la regla 5 antes de llegar a la regla 7', () => {
    // Antes (vuelto_maximo): vuelto == importe pasaba la regla 5 y llegaba a la regla 7
    // (importeAplicado == 0) como pago_a_cuenta_sin_importe. Con la regla de billetes, ningún
    // billete estrictamente mayor a 100 arma exactamente 100 -> se rechaza antes.
    expect(validarPagoACuentaLocal({ pagos: [pagoFixture({ importe: 100, vuelto: 100 })] })?.codigo).toBe(
      'vuelto_no_justificado',
    )
  })

  it('sin ningún pago, importeAplicado es 0 ⇒ rechazado', () => {
    expect(validarPagoACuentaLocal({ pagos: [] })).toEqual({
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
            ajusteManualPorcentaje: null,
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
            ajusteManualPorcentaje: null,
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
            ajusteManualPorcentaje: -12.5,
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
            ajusteManualPorcentaje: -12.5,
          },
        ],
      },
    ])
  })

  it('AjusteManualPorcentaje PascalCase se conserva con su signo en SU línea; la clave ausente da null', () => {
    const lineaBase = {
      IdArticulo: 1,
      Cantidad: 1,
      PrecioHistorico: 100,
      PrecioActual: 120,
      TotalHistorico: 100,
      TotalDelDia: 108,
      Delta: 8,
      Motivo: null,
    }
    const crudo = JSON.stringify([
      {
        IdMovimiento: 3,
        IdComprobanteVenta: 10,
        Delta: 23,
        Lineas: [
          { ...lineaBase, AjusteManualPorcentaje: -10 },
          { ...lineaBase, TotalDelDia: 120, Delta: 20 },
          { ...lineaBase, TotalDelDia: 138, Delta: 38, AjusteManualPorcentaje: 15 },
        ],
      },
    ])

    const lineas = parsearDetalleDeActualizacionPrecios(crudo)?.[0].lineas
    expect(lineas?.map((linea) => linea.ajusteManualPorcentaje)).toEqual([-10, null, 15])
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

describe('derivarSaldoParaChip', () => {
  const limitado = (saldo: number, limiteCredito: number) => derivarSaldoParaChip({ saldo, limiteCredito, creditoIlimitado: false })

  it('saldo 0 con límite: barra vacía y todo el límite disponible', () => {
    expect(limitado(0, 80000)).toEqual({ saldo: 0, limite: 80000, disponible: 80000, proporcionUsada: 0, excedido: false })
  })

  it('saldo dentro del límite: la barra se llena en saldo / límite', () => {
    expect(limitado(5000, 80000)).toEqual({ saldo: 5000, limite: 80000, disponible: 75000, proporcionUsada: 0.0625, excedido: false })
  })

  it('saldo igual al límite: barra llena, disponible 0 y todavía no excedido', () => {
    expect(limitado(80000, 80000)).toEqual({ saldo: 80000, limite: 80000, disponible: 0, proporcionUsada: 1, excedido: false })
  })

  it('saldo por encima del límite: excedido, barra llena y disponible negativo', () => {
    expect(limitado(90000, 80000)).toEqual({ saldo: 90000, limite: 80000, disponible: -10000, proporcionUsada: 1, excedido: true })
  })

  it('límite 0 con deuda: excedido sin dividir por cero', () => {
    expect(limitado(100, 0)).toEqual({ saldo: 100, limite: 0, disponible: -100, proporcionUsada: 1, excedido: true })
  })

  it('límite 0 sin deuda: barra vacía y no excedido', () => {
    expect(limitado(0, 0)).toEqual({ saldo: 0, limite: 0, disponible: 0, proporcionUsada: 0, excedido: false })
  })

  it('saldo a favor del cliente: barra vacía, saldo tal cual y disponible por encima del límite', () => {
    expect(limitado(-2000, 80000)).toEqual({ saldo: -2000, limite: 80000, disponible: 82000, proporcionUsada: 0, excedido: false })
  })

  it('crédito ilimitado: sin límite, sin disponible y sin barra, aunque el saldo sea enorme', () => {
    expect(derivarSaldoParaChip({ saldo: 9_000_000, limiteCredito: 0, creditoIlimitado: true })).toEqual({
      saldo: 9_000_000,
      limite: null,
      disponible: null,
      proporcionUsada: null,
      excedido: false,
    })
  })
})

describe('saldosDeUnPagoACuenta', () => {
  const pagos = [
    { idMedioPago: 1, importe: 500, referencia: null, vuelto: 50 },
    { idMedioPago: 2, importe: 200, referencia: 'AUT1', vuelto: 0 },
  ]

  it('con el saldo posterior del servidor, ese es el nuevo y el anterior se reconstruye sumándole lo aplicado (importes menos vueltos)', () => {
    expect(saldosDeUnPagoACuenta({ pagos, saldoEnPantalla: 9999, saldoPosterior: 300 })).toEqual({ anterior: 950, nuevo: 300 })
  })

  it('sin saldo posterior, el anterior es el de pantalla y el nuevo le resta lo aplicado', () => {
    expect(saldosDeUnPagoACuenta({ pagos, saldoEnPantalla: 1000, saldoPosterior: null })).toEqual({ anterior: 1000, nuevo: 350 })
  })

  it('con saldo posterior 0 (no confundido con ausente) usa ese 0', () => {
    expect(saldosDeUnPagoACuenta({ pagos, saldoEnPantalla: 1000, saldoPosterior: 0 })).toEqual({ anterior: 650, nuevo: 0 })
  })

  it('con 100 pendientes en cola, el saldo posterior del servidor 150 y 650 aplicados: nuevo 250 y anterior 900 (anterior − aplicado = nuevo)', () => {
    expect(saldosDeUnPagoACuenta({ pagos, saldoEnPantalla: 9999, saldoPosterior: 150, consumoPendiente: 100 })).toEqual({ anterior: 900, nuevo: 250 })
  })

  it('sin saldo posterior y con 100 pendientes: anterior 1100 (pantalla 1000 + 100) y nuevo 450', () => {
    expect(saldosDeUnPagoACuenta({ pagos, saldoEnPantalla: 1000, saldoPosterior: null, consumoPendiente: 100 })).toEqual({ anterior: 1100, nuevo: 450 })
  })

  it('los decimales se redondean y siempre cumplen anterior - aplicado = nuevo', () => {
    const decimales = [{ idMedioPago: 1, importe: 0.1, referencia: null, vuelto: 0 }, { idMedioPago: 2, importe: 0.2, referencia: null, vuelto: 0 }]
    const conPosterior = saldosDeUnPagoACuenta({ pagos: decimales, saldoEnPantalla: 0, saldoPosterior: 10.15 })
    const sinPosterior = saldosDeUnPagoACuenta({ pagos: decimales, saldoEnPantalla: 10.45, saldoPosterior: null })
    expect(conPosterior).toEqual({ anterior: 10.45, nuevo: 10.15 })
    expect(sinPosterior).toEqual({ anterior: 10.45, nuevo: 10.15 })
  })
})

describe('saldosDeUnaVenta', () => {
  const medios = [
    medioFixture({ id: 1, nombre: 'Efectivo', comportamiento: 'Efectivo' }),
    medioFixture({ id: 2, nombre: 'Tarjeta', comportamiento: 'Electronico' }),
    medioFixture({ id: 3, nombre: 'Cuenta corriente', comportamiento: 'CuentaCorriente', admiteVuelto: false }),
  ]

  it('una venta toda en efectivo deja el saldo igual', () => {
    const pagos = [{ idMedioPago: 1, importe: 1000, referencia: null, vuelto: 0 }]
    expect(saldosDeUnaVenta({ saldoAntes: 500, pagos, medios })).toEqual({ antes: 500, despues: 500 })
  })

  it('suma al saldo solo la parte cobrada a cuenta corriente', () => {
    const pagos = [
      { idMedioPago: 1, importe: 300, referencia: null, vuelto: 0 },
      { idMedioPago: 3, importe: 700, referencia: null, vuelto: 0 },
    ]
    expect(saldosDeUnaVenta({ saldoAntes: 500, pagos, medios })).toEqual({ antes: 500, despues: 1200 })
  })

  it('un medio que no está en la lista no se cuenta como cuenta corriente', () => {
    const pagos = [{ idMedioPago: 99, importe: 700, referencia: null, vuelto: 0 }]
    expect(saldosDeUnaVenta({ saldoAntes: 500, pagos, medios })).toEqual({ antes: 500, despues: 500 })
  })

  it('suma al saldo de pantalla lo que el cliente tiene en la cola sin enviar: 500 + 100 pendiente + 50 de esta venta = 600 → 650', () => {
    const pagos = [{ idMedioPago: 3, importe: 50, referencia: null, vuelto: 0 }]
    expect(saldosDeUnaVenta({ saldoAntes: 500, consumoPendiente: 100, pagos, medios })).toEqual({ antes: 600, despues: 650 })
  })

  it('con la cola vacía (venta en línea) no cambia: 500 + 50 = 500 → 550', () => {
    const pagos = [{ idMedioPago: 3, importe: 50, referencia: null, vuelto: 0 }]
    expect(saldosDeUnaVenta({ saldoAntes: 500, consumoPendiente: 0, pagos, medios })).toEqual({ antes: 500, despues: 550 })
  })

  it('una venta en efectivo con pendiente en cola deja antes = después = 600', () => {
    const pagos = [{ idMedioPago: 1, importe: 50, referencia: null, vuelto: 0 }]
    expect(saldosDeUnaVenta({ saldoAntes: 500, consumoPendiente: 100, pagos, medios })).toEqual({ antes: 600, despues: 600 })
  })

  it('redondea la suma', () => {
    const pagos = [{ idMedioPago: 3, importe: 0.2, referencia: null, vuelto: 0 }]
    expect(saldosDeUnaVenta({ saldoAntes: 0.1, pagos, medios })).toEqual({ antes: 0.1, despues: 0.3 })
  })
})

describe('nombreCompletoDeCliente', () => {
  it('prefiere la razón social', () => {
    expect(nombreCompletoDeCliente({ razonSocial: 'Perez Hermanos S.A.', nombre: 'Juan', apellido: 'Perez' })).toBe('Perez Hermanos S.A.')
  })

  it('sin razón social junta nombre y apellido, omitiendo el apellido ausente', () => {
    expect(nombreCompletoDeCliente({ razonSocial: null, nombre: 'Juan', apellido: 'Perez' })).toBe('Juan Perez')
    expect(nombreCompletoDeCliente({ razonSocial: null, nombre: 'Juan', apellido: null })).toBe('Juan')
  })
})

describe('esSaldoEstimado', () => {
  it('confirmado y sin pendientes es un dato; cualquier otra combinación es estimada', () => {
    expect(esSaldoEstimado({ confirmado: true, consumoPendiente: 0 })).toBe(false)
    expect(esSaldoEstimado({ confirmado: false, consumoPendiente: 0 })).toBe(true)
    expect(esSaldoEstimado({ confirmado: true, consumoPendiente: 100 })).toBe(true)
    expect(esSaldoEstimado({ confirmado: true, consumoPendiente: null })).toBe(true)
  })
})

describe('clienteParaElTicketDeVenta', () => {
  const medios = [
    medioFixture({ id: 1, nombre: 'Efectivo', comportamiento: 'Efectivo' }),
    medioFixture({ id: 3, nombre: 'Cuenta corriente', comportamiento: 'CuentaCorriente', admiteVuelto: false }),
  ]
  const cliente = { numero: 2, nombre: 'Juan', apellido: 'Perez', razonSocial: null, saldo: 500, esConsumidorFinal: false } as ClienteListado
  const venta50 = [{ idMedioPago: 3, importe: 50, vuelto: 0 }]

  it('online con la cola vacía y saldo confirmado: 500 → 550, sin estimar', () => {
    expect(clienteParaElTicketDeVenta({ cliente, pagos: venta50, medios, consumoPendiente: 0, saldoConfirmado: true })).toEqual({
      numero: 2,
      nombre: 'Juan Perez',
      saldoAntes: 500,
      saldoDespues: 550,
      estimado: false,
    })
  })

  it('segunda venta a cuenta con 100 en la cola: 600 → 650, estimado', () => {
    expect(clienteParaElTicketDeVenta({ cliente, pagos: venta50, medios, consumoPendiente: 100, saldoConfirmado: true })).toMatchObject({
      saldoAntes: 600,
      saldoDespues: 650,
      estimado: true,
    })
  })

  it('sin poder leer la cola cuenta 0 pero queda estimado', () => {
    expect(clienteParaElTicketDeVenta({ cliente, pagos: venta50, medios, consumoPendiente: null, saldoConfirmado: true })).toMatchObject({
      saldoAntes: 500,
      saldoDespues: 550,
      estimado: true,
    })
  })

  it('un saldo no confirmado queda estimado aunque la cola esté vacía', () => {
    expect(clienteParaElTicketDeVenta({ cliente, pagos: venta50, medios, consumoPendiente: 0, saldoConfirmado: false })).toMatchObject({ estimado: true })
  })

  it('el Consumidor Final no lleva cliente', () => {
    const consumidorFinal = { ...cliente, esConsumidorFinal: true }
    expect(clienteParaElTicketDeVenta({ cliente: consumidorFinal, pagos: venta50, medios, consumoPendiente: 0, saldoConfirmado: true })).toBeNull()
  })
})

describe('clientes HTTP de cobros (E7-S3)', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('listarVentasPendientes pide GET .../ventas-pendientes del cliente', async () => {
    const get = vi.spyOn(api, 'get').mockResolvedValue([])

    await clienteDeCuentaCorriente.listarVentasPendientes(7)

    expect(get).toHaveBeenCalledTimes(1)
    expect(get).toHaveBeenCalledWith('/clientes/7/cuenta-corriente/ventas-pendientes')
  })

  it('listarRecibos sin conDisponible no manda query', async () => {
    const get = vi.spyOn(api, 'get').mockResolvedValue([])

    await clienteDeCuentaCorriente.listarRecibos(7)

    expect(get).toHaveBeenCalledWith('/clientes/7/cuenta-corriente/recibos')
  })

  it('listarRecibos con conDisponible manda ?conDisponible=true', async () => {
    const get = vi.spyOn(api, 'get').mockResolvedValue([])

    await clienteDeCuentaCorriente.listarRecibos(7, true)

    expect(get).toHaveBeenCalledWith('/clientes/7/cuenta-corriente/recibos?conDisponible=true')
  })

  it('aplicar manda POST /cobros/{idRecibo}/aplicaciones con la solicitud tal cual', async () => {
    const post = vi.spyOn(api, 'post').mockResolvedValue({})
    const solicitud = { aplicaciones: [{ idComprobanteVenta: 11, importe: 30 }] }

    await clienteDeCobros.aplicar(5, solicitud)

    expect(post).toHaveBeenCalledTimes(1)
    expect(post).toHaveBeenCalledWith('/cobros/5/aplicaciones', solicitud)
  })

  it('desaplicar manda DELETE /cobros/{idRecibo}/aplicaciones/{idAplicacion}', async () => {
    const borrar = vi.spyOn(api, 'delete').mockResolvedValue({})

    await clienteDeCobros.desaplicar(5, 9)

    expect(borrar).toHaveBeenCalledTimes(1)
    expect(borrar).toHaveBeenCalledWith('/cobros/5/aplicaciones/9')
  })
})
