import { describe, expect, it, vi } from 'vitest'

vi.mock('../api/cliente', () => ({
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

const { ErrorApi } = await import('../api/cliente')
const { filaQuedoObsoleta, mensajeDeErrorDeRetiro, motivoDeNoModificarRetiro, validarRetiro } = await import(
  './utilidadesRetirosDeAdministracion'
)

describe('mensajeDeErrorDeRetiro', () => {
  it.each([
    ['retiro_de_cierre_no_editable', 'retiro de cierre'],
    ['movimiento_de_caja_importe_invalido', 'mayor a 0'],
    ['movimiento_de_caja_sin_motivo', 'al menos 5'],
    ['periodo_conciliado', 'conciliada'],
    ['no_encontrado', 'ya no existe'],
  ])('traduce %s', (codigo, fragmento) => {
    expect(mensajeDeErrorDeRetiro(new ErrorApi(409, codigo, 'servidor'), 'editar')).toContain(fragmento)
  })

  it('un código desconocido usa el mensaje del servidor', () => {
    expect(mensajeDeErrorDeRetiro(new ErrorApi(500, 'raro', 'Mensaje del servidor'), 'eliminar')).toBe('Mensaje del servidor')
  })

  it('un error que no es de la API usa un texto según la acción', () => {
    expect(mensajeDeErrorDeRetiro(new Error('x'), 'editar')).toBe('No se pudo guardar el retiro.')
    expect(mensajeDeErrorDeRetiro(new Error('x'), 'eliminar')).toBe('No se pudo eliminar el retiro.')
  })
})

describe('filaQuedoObsoleta', () => {
  it.each([
    [404, true],
    [409, true],
    [400, false],
    [403, false],
    [500, false],
  ])('estado %d -> %s', (estado, esperado) => {
    expect(filaQuedoObsoleta(new ErrorApi(estado, 'x', 'x'))).toBe(esperado)
  })

  it('un error que no es de la API no la deja obsoleta', () => {
    expect(filaQuedoObsoleta(new Error('x'))).toBe(false)
  })
})

describe('motivoDeNoModificarRetiro', () => {
  it('un retiro común se puede modificar', () => {
    expect(motivoDeNoModificarRetiro({ esRetiroDeCierre: false, conciliado: false })).toBeNull()
  })

  it('el retiro de cierre y el conciliado dicen cada uno su motivo, con prioridad del cierre', () => {
    expect(motivoDeNoModificarRetiro({ esRetiroDeCierre: true, conciliado: false })).toContain('retiro de cierre')
    expect(motivoDeNoModificarRetiro({ esRetiroDeCierre: false, conciliado: true })).toContain('conciliada')
    expect(motivoDeNoModificarRetiro({ esRetiroDeCierre: true, conciliado: true })).toContain('retiro de cierre')
  })
})

describe('validarRetiro', () => {
  it('un retiro válido no tiene errores', () => {
    expect(validarRetiro(10, 'Motivo')).toEqual({ importe: '', motivo: '' })
  })

  it.each<[number | null]>([[null], [0], [-5]])('el importe %j es inválido', (importe) => {
    expect(validarRetiro(importe, 'Motivo').importe).toBe('El importe tiene que ser mayor a 0.')
  })

  it('el motivo se mide recortado', () => {
    expect(validarRetiro(10, '  abcd  ').motivo).toBe('El motivo tiene que tener al menos 5 caracteres.')
    expect(validarRetiro(10, '  abcde ').motivo).toBe('')
  })
})
