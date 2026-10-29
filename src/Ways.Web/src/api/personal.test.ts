import { beforeEach, describe, expect, it, vi } from 'vitest'
import { clienteDePersonal } from './personal'
import type { DatosDePersonal } from './tipos'

const getMock = vi.fn()
const postMock = vi.fn()
const putMock = vi.fn()
const deleteMock = vi.fn()

vi.mock('./cliente', () => ({
  api: {
    get: (...args: unknown[]) => getMock(...args),
    post: (...args: unknown[]) => postMock(...args),
    put: (...args: unknown[]) => putMock(...args),
    delete: (...args: unknown[]) => deleteMock(...args),
  },
}))

const datos: DatosDePersonal = {
  nombre: 'Ana',
  documento: null,
  telefono: null,
  observaciones: null,
  valorHora: 100,
  idEmpresa: null,
  idUsuario: null,
  idCliente: null,
  horarioHabitual: [8, 8, 8, 8, 8, 0, 0],
}

beforeEach(() => {
  getMock.mockReset()
  postMock.mockReset()
  putMock.mockReset()
  deleteMock.mockReset()
})

describe('clienteDePersonal', () => {
  it('listar pide el tamaño máximo de página y omite la búsqueda vacía', () => {
    clienteDePersonal.listar('')

    expect(getMock).toHaveBeenCalledWith('/personal?tamanio=200')
  })

  it('listar codifica el término de búsqueda', () => {
    clienteDePersonal.listar('ana gómez')

    expect(getMock).toHaveBeenCalledWith('/personal?busqueda=ana+g%C3%B3mez&tamanio=200')
  })

  it('crear, actualizar y eliminar apuntan al recurso y mandan el cuerpo tal cual', () => {
    clienteDePersonal.crear(datos)
    clienteDePersonal.actualizar(7, datos)
    clienteDePersonal.eliminar(7)

    expect(postMock).toHaveBeenCalledWith('/personal', datos)
    expect(putMock).toHaveBeenCalledWith('/personal/7', datos)
    expect(deleteMock).toHaveBeenCalledWith('/personal/7')
  })

  it('las referencias de vínculo piden la página más grande que acepta el servidor', () => {
    clienteDePersonal.usuariosParaVincular()
    clienteDePersonal.clientesParaVincular()

    expect(getMock).toHaveBeenCalledWith('/usuarios?tamanio=200')
    expect(getMock).toHaveBeenCalledWith('/clientes?tamanio=200')
  })

  it('las referencias de vínculo mandan el término de búsqueda al servidor', () => {
    clienteDePersonal.usuariosParaVincular('ana')
    clienteDePersonal.clientesParaVincular('paz')

    expect(getMock).toHaveBeenCalledWith('/usuarios?busqueda=ana&tamanio=200')
    expect(getMock).toHaveBeenCalledWith('/clientes?busqueda=paz&tamanio=200')
  })
})
