import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  clienteDeUsuarios,
  COLUMNAS_ORDENABLES_DE_USUARIOS,
  esColumnaOrdenableDeUsuarios,
  FILTROS_DE_USUARIOS_VACIOS,
} from './usuarios'

const getMock = vi.fn()

vi.mock('./cliente', () => ({
  api: { get: (...args: unknown[]) => getMock(...args) },
}))

beforeEach(() => getMock.mockReset())

describe('clienteDeUsuarios', () => {
  it('listarGrilla sin filtros ni orden pide /usuarios pelado', () => {
    clienteDeUsuarios.listarGrilla({ filtros: FILTROS_DE_USUARIOS_VACIOS, orden: null, pagina: 1, tamanio: 25 })

    expect(getMock).toHaveBeenCalledWith('/usuarios')
  })

  it('listarGrilla manda cada filtro con el nombre de parámetro del servidor, el orden y la página', () => {
    clienteDeUsuarios.listarGrilla({
      filtros: {
        busqueda: 'ana',
        id: '12',
        usuario: 'lu',
        mail: '@x.com',
        tenant: 'sin-tenant',
        rolId: '2',
        estado: 'Bloqueado',
        conectado: 'false',
      },
      orden: { columna: 'ultimaConexion', direccion: 'descendente' },
      pagina: 2,
      tamanio: 100,
    })

    const ruta = String(getMock.mock.calls[0][0])

    expect(ruta.startsWith('/usuarios?')).toBe(true)
    expect(Object.fromEntries(new URLSearchParams(ruta.split('?')[1]))).toEqual({
      busqueda: 'ana',
      id: '12',
      usuario: 'lu',
      mail: '@x.com',
      tenant: 'sin-tenant',
      rolId: '2',
      estado: 'Bloqueado',
      conectado: 'false',
      orden: 'ultimaConexion',
      direccion: 'descendente',
      pagina: '2',
      tamanio: '100',
    })
  })

  it('las claves de orden son las del servidor y Acciones no ordena', () => {
    expect([...COLUMNAS_ORDENABLES_DE_USUARIOS]).toEqual([
      'id',
      'usuario',
      'mail',
      'tenant',
      'rol',
      'estado',
      'ultimaConexion',
    ])
    expect(esColumnaOrdenableDeUsuarios('acciones')).toBe(false)
  })
})
