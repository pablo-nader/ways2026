import { describe, expect, it } from 'vitest'
import {
  cobradoDePagos,
  evaluarCambioDeMedios,
  filasDesdePagos,
  MENSAJES_DE_CAMBIO_DE_MEDIOS,
  mediosParaCambiar,
  mensajeDeErrorDeCambioDeMedios,
  motivoDeNoCambiarMedios,
  repartoIgual,
} from './cambioDeMedios'
import { ErrorApi } from './cliente'
import type { MedioPagoListado, PagoEmitido } from './tipos'

function medio(id: number, sobrescribir: Partial<MedioPagoListado> = {}): MedioPagoListado {
  return {
    id,
    nombre: `Medio ${id}`,
    activo: true,
    idEmpresa: null,
    orden: id,
    comportamiento: 'Electronico',
    admiteVuelto: false,
    requiereReferencia: false,
    recargoPorcentaje: null,
    porcentajeFacturacion: 0,
    ...sobrescribir,
  }
}

const EFECTIVO = medio(1, { nombre: 'Efectivo', comportamiento: 'Efectivo', admiteVuelto: true })
const TARJETA = medio(2, { nombre: 'Tarjeta', requiereReferencia: true })
const TRANSFERENCIA = medio(3, { nombre: 'Transferencia' })
const CUENTA_CORRIENTE = medio(4, { nombre: 'Cuenta corriente', comportamiento: 'CuentaCorriente' })
const CATALOGO = [EFECTIVO, TARJETA, TRANSFERENCIA, CUENTA_CORRIENTE]

describe('mensajeDeErrorDeCambioDeMedios', () => {
  it.each(Object.keys(MENSAJES_DE_CAMBIO_DE_MEDIOS))('traduce el código %s al texto fijo, no al del servidor', (codigo) => {
    const texto = mensajeDeErrorDeCambioDeMedios(new ErrorApi(409, codigo, 'texto del servidor'))
    expect(texto).toBe(MENSAJES_DE_CAMBIO_DE_MEDIOS[codigo])
    expect(texto).not.toBe('texto del servidor')
  })

  it('cubre exactamente los nueve códigos del contrato del servidor', () => {
    expect(Object.keys(MENSAJES_DE_CAMBIO_DE_MEDIOS).sort()).toEqual(
      [
        'cambio_de_medios_con_cuenta_corriente',
        'cambio_de_medios_sin_diferencia',
        'comprobante_sin_cambio_de_medios',
        'periodo_conciliado',
        'total_de_cobros_distinto',
        'venta_anulada',
        'venta_con_cae',
        'venta_con_factura_en_tramite',
        'venta_turno_cerrado',
      ].sort(),
    )
  })

  it('un código ajeno (regla de pago) conserva el mensaje del servidor', () => {
    expect(mensajeDeErrorDeCambioDeMedios(new ErrorApi(400, 'referencia_de_pago_requerida', 'El medio requiere referencia.'))).toBe(
      'El medio requiere referencia.',
    )
  })

  it('un error que no es de la API da el mensaje genérico', () => {
    expect(mensajeDeErrorDeCambioDeMedios(new Error('boom'))).toBe('No se pudo cambiar el medio de pago.')
  })
})

describe('cobradoDePagos', () => {
  it('es el importe entregado menos el vuelto', () => {
    const pagos: PagoEmitido[] = [
      { idMedioPago: 1, importe: 1000, referencia: null, vuelto: 150 },
      { idMedioPago: 2, importe: 50.5, referencia: 'x', vuelto: 0 },
    ]
    expect(cobradoDePagos(pagos)).toBe(900.5)
  })
})

describe('motivoDeNoCambiarMedios', () => {
  const base = { estado: 'Emitido' as const, mediosDePago: [{ idMedioPago: 1 }] }

  it('una venta emitida sin factura ni cuenta corriente se puede intentar', () => {
    expect(motivoDeNoCambiarMedios(base, CATALOGO)).toBeNull()
    expect(motivoDeNoCambiarMedios({ ...base, estadoFiscal: 'no_corresponde' }, CATALOGO)).toBeNull()
    expect(motivoDeNoCambiarMedios({ ...base, estadoFiscal: 'rechazado' }, CATALOGO)).toBeNull()
  })

  it('anulada, con CAE, en trámite y con cuenta corriente dan su propio motivo', () => {
    expect(motivoDeNoCambiarMedios({ ...base, estado: 'Anulado' }, CATALOGO)).toBe(MENSAJES_DE_CAMBIO_DE_MEDIOS.venta_anulada)
    expect(motivoDeNoCambiarMedios({ ...base, estadoFiscal: 'aprobado' }, CATALOGO)).toBe(MENSAJES_DE_CAMBIO_DE_MEDIOS.venta_con_cae)
    expect(motivoDeNoCambiarMedios({ ...base, estadoFiscal: 'pendiente' }, CATALOGO)).toBe(
      MENSAJES_DE_CAMBIO_DE_MEDIOS.venta_con_factura_en_tramite,
    )
    expect(motivoDeNoCambiarMedios({ ...base, mediosDePago: [{ idMedioPago: 1 }, { idMedioPago: 4 }] }, CATALOGO)).toBe(
      MENSAJES_DE_CAMBIO_DE_MEDIOS.cambio_de_medios_con_cuenta_corriente,
    )
  })

  it('un medio que el catálogo no conoce no se toma por cuenta corriente', () => {
    expect(motivoDeNoCambiarMedios({ ...base, mediosDePago: [{ idMedioPago: 99 }] }, CATALOGO)).toBeNull()
  })
})

describe('mediosParaCambiar', () => {
  it('descarta la cuenta corriente y conserva el resto en su orden', () => {
    expect(mediosParaCambiar(CATALOGO).map((m) => m.id)).toEqual([1, 2, 3])
  })
})

describe('filasDesdePagos', () => {
  it('precarga medio, importe y referencia; el vuelto solo viaja como manual si lo hubo', () => {
    const filas = filasDesdePagos([
      { idMedioPago: 1, importe: 1000, referencia: null, vuelto: 150 },
      { idMedioPago: 2, importe: 50, referencia: 'cupón 7', vuelto: 0 },
    ])
    expect(filas).toEqual([
      { id: 1, idMedioPago: 1, importe: 1000, referencia: '', vueltoManual: 150 },
      { id: 2, idMedioPago: 2, importe: 50, referencia: 'cupón 7', vueltoManual: null },
    ])
  })
})

describe('repartoIgual', () => {
  const a = [
    { idMedioPago: 1, importe: 100, referencia: null, vuelto: 0 },
    { idMedioPago: 2, importe: 50, referencia: 'x', vuelto: 0 },
  ]

  it('no depende del orden ni de una referencia en blanco frente a null', () => {
    expect(repartoIgual(a, [a[1], { ...a[0], referencia: '  ' }])).toBe(true)
  })

  it.each([
    ['otro medio', { idMedioPago: 3 }],
    ['otro importe', { importe: 101 }],
    ['otro vuelto', { vuelto: 1 }],
    ['otra referencia', { referencia: 'y' }],
  ])('%s es un reparto distinto', (_nombre, cambio) => {
    expect(repartoIgual(a, [{ ...a[0], ...cambio }, a[1]])).toBe(false)
  })

  it('una línea de más o de menos es un reparto distinto', () => {
    expect(repartoIgual(a, [a[0]])).toBe(false)
  })
})

describe('evaluarCambioDeMedios', () => {
  const filaEfectivo = (importe: number | null, extra = {}) => ({
    id: 1,
    idMedioPago: 1 as number | '',
    importe,
    referencia: '',
    vueltoManual: null,
    ...extra,
  })
  const evaluar = (filas: Parameters<typeof evaluarCambioDeMedios>[0]['filas'], total = 1000, cobradoOriginal = 1000) =>
    evaluarCambioDeMedios({ filas, medios: CATALOGO, totalDeLaVenta: total, cobradoOriginal })

  it('un cambio de medio con el mismo total es válido y arma los pagos del request', () => {
    const e = evaluar([{ id: 1, idMedioPago: 3, importe: 1000, referencia: '  ', vueltoManual: null }])
    expect(e.error).toBeNull()
    expect(e.diferencia).toBe(0)
    expect(e.pagos).toEqual([{ idMedioPago: 3, importe: 1000, referencia: null, vuelto: 0 }])
  })

  it('el reparto entre medios suma lo cobrado: falta y sobra se miden contra el original', () => {
    const faltante = evaluar([filaEfectivo(400), { id: 2, idMedioPago: 3, importe: 500, referencia: '', vueltoManual: null }])
    expect(faltante.diferencia).toBe(100)
    expect(faltante.error).toBe('El pago ingresado no cubre el total, ni siquiera con la tolerancia.')

    const sobrante = evaluar([
      { id: 1, idMedioPago: 3, importe: 600, referencia: '', vueltoManual: null },
      { id: 2, idMedioPago: 2, importe: 500, referencia: 'c', vueltoManual: null },
    ])
    expect(sobrante.diferencia).toBe(-100)
  })

  it('un total distinto sin otra regla violada se rechaza con el mensaje del cambio de medios', () => {
    // Dos medios sin vuelto cuyo total supera lo original: ninguna regla de pago salta, solo la del total.
    const e = evaluar([
      { id: 1, idMedioPago: 3, importe: 600, referencia: '', vueltoManual: null },
      { id: 2, idMedioPago: 2, importe: 500, referencia: 'c', vueltoManual: null },
    ])
    expect(e.error).toBe(MENSAJES_DE_CAMBIO_DE_MEDIOS.total_de_cobros_distinto)
  })

  it('el exceso en efectivo se vuelve vuelto sugerido y el total cobrado se mantiene', () => {
    const e = evaluar([filaEfectivo(1100)])
    expect(e.vueltoPorFila.get(1)).toBe(100)
    expect(e.pagos).toEqual([{ idMedioPago: 1, importe: 1100, referencia: null, vuelto: 100 }])
    expect(e.diferencia).toBe(0)
    expect(e.error).toBeNull()
  })

  it('un vuelto manual que no cierra con lo original deja una diferencia', () => {
    const e = evaluar([filaEfectivo(1100, { vueltoManual: 50 })])
    expect(e.diferencia).toBe(-50)
    expect(e.error).not.toBeNull()
  })

  it('un medio que exige referencia sin referencia se rechaza; con referencia pasa', () => {
    const sin = evaluar([{ id: 1, idMedioPago: 2, importe: 1000, referencia: ' ', vueltoManual: null }])
    expect(sin.error).toBe('Este medio de pago requiere una referencia.')

    const con = evaluar([{ id: 1, idMedioPago: 2, importe: 1000, referencia: 'cupón 1', vueltoManual: null }])
    expect(con.error).toBeNull()
    expect(con.pagos[0].referencia).toBe('cupón 1')
  })

  it('un vuelto manual en un medio que no lo admite se ignora: nunca viaja y la diferencia queda a la vista', () => {
    const e = evaluar([{ id: 1, idMedioPago: 3, importe: 1100, referencia: '', vueltoManual: 100 }])
    expect(e.pagos[0].vuelto).toBe(0)
    expect(e.diferencia).toBe(-100)
    expect(e.error).toBe(MENSAJES_DE_CAMBIO_DE_MEDIOS.total_de_cobros_distinto)
  })

  it('una línea sin medio o sin importe se rechaza con su número de línea', () => {
    expect(evaluar([{ id: 1, idMedioPago: '', importe: 1000, referencia: '', vueltoManual: null }]).error).toBe('Línea 1: elegí el medio de pago.')
    expect(evaluar([filaEfectivo(1000), { id: 2, idMedioPago: 3, importe: null, referencia: '', vueltoManual: null }]).error).toBe(
      'Línea 2: ingresá un importe mayor a 0.',
    )
  })

  it('un medio que ya no está en el catálogo no se puede enviar', () => {
    const e = evaluar([{ id: 1, idMedioPago: 77, importe: 1000, referencia: '', vueltoManual: null }])
    expect(e.error).toBe('Línea 1: elegí un medio de pago disponible.')
  })

  it('una venta cobrada dentro de la tolerancia (cobró menos que el total) no se rechaza de entrada', () => {
    const e = evaluar([{ id: 1, idMedioPago: 3, importe: 995, referencia: '', vueltoManual: null }], 1000, 995)
    expect(e.error).toBeNull()
  })
})
