import { describe, expect, it } from 'vitest'
import { ErrorApi } from '../api/cliente'
import { errorDeEscritura, type CampoDeCuenta } from './cuentaDePersonalErrores'

function rechazo(estado: number, codigo: string, mensaje = 'Mensaje del servidor.') {
  return new ErrorApi(estado, codigo, mensaje)
}

describe('errorDeEscritura — por código, en el campo que corresponde', () => {
  const casos: [string, number, CampoDeCuenta][] = [
    ['fecha_requerida', 400, 'fecha'],
    ['horas_invalidas', 400, 'horas'],
    ['valor_hora_invalido', 400, 'valorHora'],
    ['importe_fuera_de_rango', 400, 'general'],
    ['detalle_muy_largo', 400, 'detalle'],
    ['ajuste_importe_invalido', 400, 'importe'],
    ['ajuste_detalle_requerido', 400, 'detalle'],
    ['pago_personal_origen_invalido', 400, 'origen'],
    ['pago_personal_vacio', 400, 'general'],
    ['pago_personal_descuento_invalido', 400, 'descuento'],
    ['pago_personal_demasiadas_lineas', 400, 'lineas'],
    ['pago_personal_importe_invalido', 400, 'lineas'],
    ['pago_personal_referencia_muy_larga', 400, 'lineas'],
    ['pago_personal_origen_requerido', 400, 'origen'],
    ['pago_personal_origen_sin_lineas', 400, 'origen'],
    ['pago_personal_empresa_requerida', 400, 'empresa'],
    ['pago_personal_empresa_no_aplica', 400, 'empresa'],
    ['pago_personal_punto_venta_requerido', 400, 'general'],
    ['turno_no_abierto', 409, 'origen'],
    ['personal_sin_cliente_vinculado', 409, 'descuento'],
    ['descuento_supera_la_deuda', 409, 'descuento'],
    ['gasto_fecha_futura', 400, 'fecha'],
    ['gasto_medio_pago_cuenta_corriente_invalido', 400, 'lineas'],
    ['punto_venta_no_pertenece_a_la_empresa', 400, 'empresa'],
  ]

  it.each(casos)('%s (%i) va al campo %s con un mensaje propio, no el del servidor', (codigo, estado, campo) => {
    const resultado = errorDeEscritura(rechazo(estado, codigo), 'pago')

    expect(resultado.campo).toBe(campo)
    expect(resultado.mensaje).not.toBe('')
    expect(resultado.mensaje).not.toBe('Mensaje del servidor.')
  })

  it('el código manda aunque el mensaje del servidor diga otra cosa', () => {
    expect(errorDeEscritura(rechazo(409, 'descuento_supera_la_deuda', 'texto cualquiera'), 'pago').mensaje).toContain(
      'deuda',
    )
  })

  it('un turno no abierto dice qué hacer', () => {
    expect(errorDeEscritura(rechazo(409, 'turno_no_abierto'), 'pago').mensaje).toContain('turno abierto')
  })
})

describe('errorDeEscritura — por estado', () => {
  it('un 403 en el pago es la tesorería del Supervisor, en el origen', () => {
    expect(errorDeEscritura(rechazo(403, 'prohibido'), 'pago')).toEqual({
      campo: 'origen',
      mensaje: 'Pagar desde la tesorería requiere ser administrador.',
    })
  })

  it('un 403 en horas o ajuste va arriba del formulario', () => {
    expect(errorDeEscritura(rechazo(403, 'prohibido'), 'horas').campo).toBe('general')
    expect(errorDeEscritura(rechazo(403, 'prohibido'), 'ajuste').campo).toBe('general')
  })

  it('un 404 dice qué pudo faltar según la escritura', () => {
    expect(errorDeEscritura(rechazo(404, 'no_encontrado'), 'horas')).toMatchObject({
      campo: 'general',
      mensaje: expect.stringContaining('empleado'),
    })
    expect(errorDeEscritura(rechazo(404, 'no_encontrado'), 'pago').mensaje).toContain('medio de pago')
  })

  it('un commit ambiguo rinde el texto del servidor, que ya dice qué verificar', () => {
    expect(errorDeEscritura(rechazo(503, 'resultado_incierto', 'Verificá la cuenta del empleado.'), 'pago')).toEqual({
      campo: 'general',
      mensaje: 'Verificá la cuenta del empleado.',
    })
  })

  it('un 5xx sin texto útil, o un fallo que no es del servidor, no promete que no se escribió', () => {
    expect(errorDeEscritura(rechazo(500, 'error', ''), 'pago').mensaje).toContain('estado de cuenta')
    expect(errorDeEscritura(new Error('Failed to fetch'), 'horas').mensaje).toContain('estado de cuenta')
  })

  it('un código desconocido de 4xx muestra el mensaje del servidor, o uno genérico si viene vacío', () => {
    expect(errorDeEscritura(rechazo(400, 'otro', ' Dato inválido. '), 'ajuste')).toEqual({
      campo: 'general',
      mensaje: 'Dato inválido.',
    })
    expect(errorDeEscritura(rechazo(400, 'otro', ''), 'ajuste').mensaje).toBe('No se pudo registrar.')
  })

  it('un código como "constructor" no resuelve contra el prototipo', () => {
    expect(errorDeEscritura(rechazo(400, 'constructor', 'Raro.'), 'horas').mensaje).toBe('Raro.')
  })
})
