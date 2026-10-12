import { describe, expect, it } from 'vitest'
import { ErrorApi } from '../../api/cliente'
import { rechazoDeTeclaTomada } from './teclaAtajo'

const importeConF6 = { unidadVenta: 'Importe', teclaAtajo: 'F6' } as const

describe('rechazoDeTeclaTomada', () => {
  it('409 tecla_atajo_duplicada sobre la tecla enviada: la nombra en un mensaje propio, sin hablar del tenant', () => {
    const rechazo = rechazoDeTeclaTomada(new ErrorApi(409, 'tecla_atajo_duplicada', 'Ya hay un artículo con la tecla de atajo F6 en este tenant.'), importeConF6)

    expect(rechazo).toEqual({ tecla: 'F6', mensaje: 'La tecla F6 ya la usa otro artículo. Elegí otra o dejá este sin atajo.' })
  })

  it.each([
    ['otro código', new ErrorApi(409, 'familia_cambio', 'x')],
    ['otro estado', new ErrorApi(400, 'tecla_atajo_duplicada', 'x')],
    ['una falla que no es de la API', new Error('red')],
  ])('%s no es un rechazo de tecla tomada', (_nombre, error) => {
    expect(rechazoDeTeclaTomada(error, importeConF6)).toBeNull()
  })

  it('sin tecla enviada o con otra unidad no hay a qué atribuirlo', () => {
    const duplicada = new ErrorApi(409, 'tecla_atajo_duplicada', 'x')

    expect(rechazoDeTeclaTomada(duplicada, { unidadVenta: 'Importe', teclaAtajo: '' })).toBeNull()
    expect(rechazoDeTeclaTomada(duplicada, { unidadVenta: 'Unidad', teclaAtajo: 'F6' })).toBeNull()
  })
})
