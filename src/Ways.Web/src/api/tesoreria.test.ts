import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ErrorApi } from './cliente'
import {
  aInstanteConOffset,
  aSolicitudDeAjusteDeEfectivo,
  aSolicitudDeConciliacion,
  clienteDeTesoreria,
  errorAlAjustarEfectivo,
  errorAlConciliar,
  esResultadoInciertoDeConciliacion,
  formularioDeConciliacionInicial,
  esResultadoInciertoDeEfectivo,
  formularioDeAjusteDeEfectivoVacio,
  validarAjusteDeEfectivo,
  validarConciliacion,
  valorLocalDeAhora,
} from './tesoreria'

const apiPostMock = vi.fn()

vi.mock('./cliente', () => ({
  api: { post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown])) },
  ErrorApi: class ErrorApiMock extends Error {
    estado: number
    codigo: string
    constructor(estado: number, codigo: string, mensaje: string) {
      super(mensaje)
      this.estado = estado
      this.codigo = codigo
    }
  },
}))

beforeEach(() => {
  apiPostMock.mockReset()
})

describe('validarAjusteDeEfectivo', () => {
  it('exige concepto, importe y rechaza el cero', () => {
    expect(validarAjusteDeEfectivo(formularioDeAjusteDeEfectivoVacio())?.campo).toBe('concepto')
    expect(validarAjusteDeEfectivo({ importe: null, concepto: 'x' })?.campo).toBe('importe')
    expect(validarAjusteDeEfectivo({ importe: 0, concepto: 'x' })?.campo).toBe('importe')
    expect(validarAjusteDeEfectivo({ importe: 0.001, concepto: 'x' })?.campo).toBe('importe')
  })

  it('acepta un importe negativo y topa el concepto en 200 caracteres', () => {
    expect(validarAjusteDeEfectivo({ importe: -5, concepto: 'x' })).toBeNull()
    expect(validarAjusteDeEfectivo({ importe: 5, concepto: 'x'.repeat(201) })?.campo).toBe('concepto')
    expect(validarAjusteDeEfectivo({ importe: 5, concepto: 'x'.repeat(200) })).toBeNull()
  })
})

describe('aSolicitudDeAjusteDeEfectivo y cliente', () => {
  it('recorta el concepto y manda idEmpresa, concepto e importe a /tesoreria/efectivo/ajustes', async () => {
    const solicitud = aSolicitudDeAjusteDeEfectivo(4, { importe: -10.5, concepto: '  Conteo ' })
    await clienteDeTesoreria.ajustarEfectivo(solicitud)

    expect(apiPostMock).toHaveBeenCalledWith('/tesoreria/efectivo/ajustes', { idEmpresa: 4, concepto: 'Conteo', importe: -10.5 })
  })
})

describe('errorAlAjustarEfectivo y esResultadoInciertoDeEfectivo', () => {
  it.each([
    [new ErrorApi(400, 'concepto_requerido', 'a'), 'concepto'],
    [new ErrorApi(400, 'concepto_muy_largo', 'a'), 'concepto'],
    [new ErrorApi(400, 'importe_invalido', 'a'), 'importe'],
    [new ErrorApi(404, 'no_encontrado', 'a'), 'general'],
    [new ErrorApi(403, 'prohibido', 'a'), 'general'],
    [new ErrorApi(409, 'otro', 'Mensaje propio'), 'general'],
  ])('ubica el rechazo %# en su campo', (error, campo) => {
    expect(errorAlAjustarEfectivo(error).campo).toBe(campo)
  })

  it('un rechazo desconocido conserva el mensaje del servidor', () => {
    expect(errorAlAjustarEfectivo(new ErrorApi(409, 'otro', 'Mensaje propio')).mensaje).toBe('Mensaje propio')
  })

  it('red cortada y 5xx son inciertos; un 4xx no', () => {
    expect(esResultadoInciertoDeEfectivo(new Error('red'))).toBe(true)
    expect(esResultadoInciertoDeEfectivo(new ErrorApi(503, 'x', 'x'))).toBe(true)
    expect(esResultadoInciertoDeEfectivo(new ErrorApi(400, 'x', 'x'))).toBe(false)
  })
})

describe('valorLocalDeAhora y formularioDeConciliacionInicial', () => {
  it('redondea hacia abajo al minuto, con ceros a la izquierda', () => {
    expect(valorLocalDeAhora(new Date(2026, 0, 5, 7, 3, 59, 999))).toBe('2026-01-05T07:03')
    expect(formularioDeConciliacionInicial(new Date(2026, 10, 1, 23, 59, 30))).toEqual({ hasta: '2026-11-01T23:59', nota: '' })
  })
})

describe('aInstanteConOffset', () => {
  it('agrega segundos y el offset local de ese instante, sin cambiar el instante', () => {
    const iso = aInstanteConOffset('2026-11-01T10:05')
    expect(iso).toMatch(/^2026-11-01T10:05:00[+-]\d{2}:\d{2}$/)
    expect(Date.parse(iso as string)).toBe(new Date(2026, 10, 1, 10, 5).getTime())
  })

  it.each(['', '2026-11-01', '2026-11-01T10:05:00', '2026-02-30T10:05', '2026-11-01T25:00', 'texto'])(
    '%j no es un valor local válido',
    (valor) => {
      expect(aInstanteConOffset(valor)).toBeNull()
    },
  )
})

describe('validarConciliacion y aSolicitudDeConciliacion', () => {
  it('exige una fecha y hora reales y topa la nota en 500 caracteres', () => {
    expect(validarConciliacion({ hasta: '', nota: '' })?.campo).toBe('hasta')
    expect(validarConciliacion({ hasta: '2026-11-01T10:00', nota: 'x'.repeat(501) })?.campo).toBe('nota')
    expect(validarConciliacion({ hasta: '2026-11-01T10:00', nota: 'x'.repeat(500) })).toBeNull()
  })

  it('manda la nota recortada y omite la nota vacía', () => {
    expect(aSolicitudDeConciliacion(4, { hasta: '2026-11-01T10:00', nota: '  Revisado  ' })).toEqual({
      idEmpresa: 4,
      hasta: aInstanteConOffset('2026-11-01T10:00'),
      nota: 'Revisado',
    })
    expect(aSolicitudDeConciliacion(4, { hasta: '2026-11-01T10:00', nota: '   ' })).not.toHaveProperty('nota')
  })

  it('el cliente postea a /tesoreria/conciliaciones', async () => {
    const solicitud = aSolicitudDeConciliacion(4, { hasta: '2026-11-01T10:00', nota: '' })
    await clienteDeTesoreria.conciliar(solicitud)

    expect(apiPostMock).toHaveBeenCalledWith('/tesoreria/conciliaciones', solicitud)
  })
})

describe('errorAlConciliar y esResultadoInciertoDeConciliacion', () => {
  it.each([
    ['hasta_requerido', 400, 'hasta', /fecha y la hora/],
    ['nota_muy_larga', 400, 'nota', /500/],
    ['conciliacion_futura', 409, 'hasta', /^La fecha no puede ser futura\.$/],
    ['conciliacion_con_turno_abierto', 409, 'hasta', /Hay una caja abierta desde antes de esa fecha; cerrala primero\./],
  ])('%s se ubica en su campo con copia propia', (codigo, estado, campo, mensaje) => {
    const error = errorAlConciliar(new ErrorApi(estado, codigo, 'mensaje del servidor'), null)

    expect(error.campo).toBe(campo)
    expect(error.mensaje).toMatch(mensaje)
  })

  it('conciliacion_anterior nombra la marca vigente que mostraba el libro', () => {
    const error = errorAlConciliar(new ErrorApi(409, 'conciliacion_anterior', 'x'), '2026-10-30T12:00:00Z')

    expect(error.campo).toBe('hasta')
    expect(error.mensaje).toMatch(/^Ya está conciliado hasta .+; elegí un instante posterior\.$/)
    expect(error.mensaje).toContain(new Date('2026-10-30T12:00:00Z').toLocaleString('es-AR', { hourCycle: 'h23' }))
  })

  it('404, 403 y un 4xx desconocido usan su copia o el mensaje del servidor', () => {
    expect(errorAlConciliar(new ErrorApi(404, 'no_encontrado', 'a'), null).mensaje).toMatch(/empresa ya no existe/)
    expect(errorAlConciliar(new ErrorApi(403, 'prohibido', 'a'), null).mensaje).toMatch(/administrador/)
    expect(errorAlConciliar(new ErrorApi(409, 'otro', 'Mensaje propio'), null).mensaje).toBe('Mensaje propio')
  })

  it('red cortada y 5xx son inciertos; un 4xx no', () => {
    expect(esResultadoInciertoDeConciliacion(new Error('red'))).toBe(true)
    expect(esResultadoInciertoDeConciliacion(new ErrorApi(503, 'x', 'a'))).toBe(true)
    expect(esResultadoInciertoDeConciliacion(new ErrorApi(409, 'conciliacion_futura', 'a'))).toBe(false)
  })
})
