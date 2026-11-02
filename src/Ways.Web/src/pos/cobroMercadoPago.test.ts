import { describe, expect, it } from 'vitest'
import { ErrorApi, ErrorDeRed } from '../api/cliente'
import type { CobroMercadoPago } from '../api/cobrosMercadoPago'
import type { MedioPagoListado } from '../api/tipos'
import {
  esperaHastaLaProximaConsulta,
  formatearCuentaRegresiva,
  habilitaAprobacionManual,
  INTERVALO_DE_CONSULTA_MS,
  INTERVALO_TRAS_FALLO_MS,
  medioConCobroIntegrado,
  planDeCobroMercadoPago,
  segundosParaExpirar,
  TEXTO_POR_CIERRE_SIN_APROBAR,
  vistaDelCobro,
} from './cobroMercadoPago'

function cobro(sobrescribir: Partial<CobroMercadoPago> = {}): CobroMercadoPago {
  return {
    idCobro: 1,
    estado: 'pendiente',
    qrData: '00020101021243650016com.mercadolibre',
    expiraEn: '2026-11-01T12:10:00Z',
    importe: 1500,
    importePagado: null,
    importeDistinto: false,
    verificandoPago: false,
    pagoInformadoSinDatos: false,
    usadoEnVenta: false,
    tipo: 'qr',
    idTerminal: null,
    enTerminal: false,
    revisarTerminal: false,
    ...sobrescribir,
  }
}

function medio(id: number, proveedor: string | null): MedioPagoListado {
  return {
    id,
    nombre: `Medio ${id}`,
    activo: true,
    idEmpresa: null,
    orden: id,
    comportamiento: 'Electronico',
    admiteVuelto: false,
    requiereReferencia: true,
    recargoPorcentaje: null,
    porcentajeFacturacion: 0,
    proveedor,
  }
}

describe('vistaDelCobro', () => {
  it('un cobro pendiente con QR muestra el QR', () => {
    expect(vistaDelCobro(cobro())).toEqual({ tipo: 'qr', qrData: '00020101021243650016com.mercadolibre' })
  })

  it('"verificando el pago" no muestra el QR aunque el servidor lo mande', () => {
    expect(vistaDelCobro(cobro({ verificandoPago: true }))).toEqual({ tipo: 'verificando' })
  })

  it('un pendiente sin QR (o con QR vacío) tampoco lo muestra', () => {
    expect(vistaDelCobro(cobro({ qrData: null })).tipo).toBe('verificando')
    expect(vistaDelCobro(cobro({ qrData: '' })).tipo).toBe('verificando')
  })

  it('el pago informado sin datos manda sobre "verificando" y no muestra el QR', () => {
    expect(vistaDelCobro(cobro({ pagoInformadoSinDatos: true, verificandoPago: true, qrData: null }))).toEqual({ tipo: 'informado_sin_datos' })
    expect(vistaDelCobro(cobro({ pagoInformadoSinDatos: true })).tipo).toBe('informado_sin_datos')
  })

  it('aprobado con el importe pedido cierra la venta', () => {
    expect(vistaDelCobro(cobro({ estado: 'aprobado', importePagado: 1500, qrData: null }))).toEqual({ tipo: 'aprobado' })
  })

  it('aprobado con otro importe pagado nunca cierra: lo marca el servidor o lo detecta la diferencia', () => {
    expect(vistaDelCobro(cobro({ estado: 'aprobado', importePagado: 1400, importeDistinto: true }))).toEqual({
      tipo: 'importe_distinto',
      importe: 1500,
      importePagado: 1400,
    })
    expect(vistaDelCobro(cobro({ estado: 'aprobado', importePagado: 1400, importeDistinto: false })).tipo).toBe('importe_distinto')
    expect(vistaDelCobro(cobro({ estado: 'aprobado', importePagado: 1500, importeDistinto: true })).tipo).toBe('importe_distinto')
  })

  it('un cobro Point pendiente espera en la terminal (nunca "verificando" por no tener QR) y nombra la terminal', () => {
    const point = { tipo: 'point', idTerminal: 'PAX_A910__SMARTPOS1', qrData: null } as const
    expect(vistaDelCobro(cobro(point))).toEqual({ tipo: 'cobrando_en_terminal', idTerminal: 'PAX_A910__SMARTPOS1', enTerminal: false })
    expect(vistaDelCobro(cobro({ ...point, enTerminal: true }))).toEqual({
      tipo: 'cobrando_en_terminal',
      idTerminal: 'PAX_A910__SMARTPOS1',
      enTerminal: true,
    })
  })

  it('un cobro Point que pide mirar la terminal muestra "revisar la terminal"', () => {
    expect(vistaDelCobro(cobro({ tipo: 'point', idTerminal: 'T', qrData: null, revisarTerminal: true }))).toEqual({ tipo: 'revisar_terminal' })
  })

  it('un cobro Point con el pago informado sin datos o verificando no muestra la terminal', () => {
    const point = { tipo: 'point', idTerminal: 'T', qrData: null } as const
    expect(vistaDelCobro(cobro({ ...point, pagoInformadoSinDatos: true, verificandoPago: true })).tipo).toBe('informado_sin_datos')
    expect(vistaDelCobro(cobro({ ...point, verificandoPago: true })).tipo).toBe('verificando')
  })

  it('un cobro Point aprobado con el importe pedido cierra la venta', () => {
    expect(vistaDelCobro(cobro({ tipo: 'point', idTerminal: 'T', estado: 'aprobado', importePagado: 1500, qrData: null }))).toEqual({ tipo: 'aprobado' })
  })

  it('un cobro Point fallido vuelve al pago con el texto del rechazo en la terminal', () => {
    expect(vistaDelCobro(cobro({ tipo: 'point', idTerminal: 'T', estado: 'fallido', qrData: null }))).toEqual({ tipo: 'sin_aprobar', estado: 'fallido' })
    expect(TEXTO_POR_CIERRE_SIN_APROBAR.fallido).toBe('El pago fue rechazado en la terminal: probá de nuevo u otro medio.')
  })

  it.each(['cancelado', 'expirado', 'devuelto', 'sin_venta', 'fallido'] as const)('un cobro %s vuelve al pago', (estado) => {
    expect(vistaDelCobro(cobro({ estado, qrData: null }))).toEqual({ tipo: 'sin_aprobar', estado })
  })

  it('un cobro todavía "creando" espera', () => {
    expect(vistaDelCobro(cobro({ estado: 'creando', qrData: null })).tipo).toBe('esperando')
  })
})

describe('esperaHastaLaProximaConsulta', () => {
  it('consulta cada 2 s y espera 5 s tras un fallo', () => {
    expect(esperaHastaLaProximaConsulta(0)).toBe(INTERVALO_DE_CONSULTA_MS)
    expect(INTERVALO_DE_CONSULTA_MS).toBe(2_000)
    expect(esperaHastaLaProximaConsulta(1)).toBe(INTERVALO_TRAS_FALLO_MS)
    expect(esperaHastaLaProximaConsulta(7)).toBe(5_000)
  })
})

describe('planDeCobroMercadoPago', () => {
  const medios = { 1: medio(1, null), 2: medio(2, 'mercadopago_qr'), 3: medio(3, 'mercadopago_qr') }

  it('sin ningún medio con proveedor es una venta de siempre', () => {
    expect(planDeCobroMercadoPago([{ idMedioPago: 1 }], medios)).toEqual({ tipo: 'ninguno' })
  })

  it('un medio con proveedor se cobra con QR y entrega su pago', () => {
    const pago = { idMedioPago: 2, importe: 10 }
    expect(planDeCobroMercadoPago([{ idMedioPago: 1 }, pago], medios)).toEqual({ tipo: 'uno', pago })
  })

  it('dos filas con proveedor (aunque sean el mismo medio) se rechazan', () => {
    expect(planDeCobroMercadoPago([{ idMedioPago: 2 }, { idMedioPago: 2 }], medios)).toEqual({ tipo: 'varios' })
    expect(planDeCobroMercadoPago([{ idMedioPago: 2 }, { idMedioPago: 3 }], medios)).toEqual({ tipo: 'varios' })
  })

  it('un medio Point también cobra con Mercado Pago, y un Point más un QR son dos cobros', () => {
    const conPoint = { ...medios, 4: medio(4, 'mercadopago_point') }
    const pago = { idMedioPago: 4, importe: 10 }
    expect(planDeCobroMercadoPago([{ idMedioPago: 1 }, pago], conPoint)).toEqual({ tipo: 'uno', pago })
    expect(planDeCobroMercadoPago([{ idMedioPago: 2 }, pago], conPoint)).toEqual({ tipo: 'varios' })
    expect(medioConCobroIntegrado(medio(4, 'mercadopago_point'))).toBe(true)
    expect(medioConCobroIntegrado(medio(5, 'otro'))).toBe(false)
  })

  it('un medio que no está en la lista no cuenta', () => {
    expect(planDeCobroMercadoPago([{ idMedioPago: 99 }], medios)).toEqual({ tipo: 'ninguno' })
    expect(medioConCobroIntegrado(undefined)).toBe(false)
  })
})

describe('cuenta regresiva', () => {
  it('cuenta segundos hacia arriba hasta el vencimiento y nunca baja de cero', () => {
    const ahora = Date.parse('2026-11-01T12:00:00.000Z')
    expect(segundosParaExpirar('2026-11-01T12:10:00Z', ahora)).toBe(600)
    expect(segundosParaExpirar('2026-11-01T12:00:00.500Z', ahora)).toBe(1)
    expect(segundosParaExpirar('2026-11-01T11:59:00Z', ahora)).toBe(0)
    expect(segundosParaExpirar('no es una fecha', ahora)).toBeNull()
  })

  it('formatea mm:ss', () => {
    expect(formatearCuentaRegresiva(600)).toBe('10:00')
    expect(formatearCuentaRegresiva(65)).toBe('01:05')
    expect(formatearCuentaRegresiva(0)).toBe('00:00')
  })
})

describe('habilitaAprobacionManual', () => {
  it('Mercado Pago no disponible o sin red habilitan aprobar a mano', () => {
    expect(habilitaAprobacionManual(new ErrorApi(503, 'mercadopago_no_disponible', 'x'))).toBe(true)
    expect(habilitaAprobacionManual(new ErrorDeRed(new TypeError('x')))).toBe(true)
  })

  it.each(['mercadopago_caja_no_registrada', 'mercadopago_caja_sin_terminal', 'mercadopago_terminal_ocupada', 'cobro_mercadopago_medio_invalido'])(
    'el rechazo %s no lo habilita',
    (codigo) => {
      expect(habilitaAprobacionManual(new ErrorApi(409, codigo, 'x'))).toBe(false)
    },
  )

  it('un error desconocido no lo habilita', () => {
    expect(habilitaAprobacionManual(new Error('x'))).toBe(false)
  })
})
