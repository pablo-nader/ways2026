import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ErrorApi } from './cliente'
import {
  aSolicitudDeAjusteDeEfectivo,
  clienteDeTesoreria,
  errorAlAjustarEfectivo,
  esResultadoInciertoDeEfectivo,
  formularioDeAjusteDeEfectivoVacio,
  validarAjusteDeEfectivo,
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
