import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  clienteDePersonal,
  COLUMNAS_ORDENABLES_DE_PERSONAL,
  esColumnaOrdenableDePersonal,
  FILTROS_DE_PERSONAL_VACIOS,
} from './personal'
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
}

beforeEach(() => {
  getMock.mockReset()
  postMock.mockReset()
  putMock.mockReset()
  deleteMock.mockReset()
})

describe('clienteDePersonal', () => {
  it('listarGrilla sin filtros ni orden pide /personal pelado: el tamaño de página es el del servidor', () => {
    clienteDePersonal.listarGrilla({ filtros: FILTROS_DE_PERSONAL_VACIOS, orden: null, pagina: 1, tamanio: 25 })

    expect(getMock).toHaveBeenCalledWith('/personal')
  })

  it('listarGrilla manda la búsqueda, los filtros, los rangos, el orden y la página', () => {
    clienteDePersonal.listarGrilla({
      filtros: {
        ...FILTROS_DE_PERSONAL_VACIOS,
        busqueda: 'ana gómez',
        nombre: ' ana ',
        valorHoraDesde: 100,
        valorHoraHasta: 250.5,
        saldoDesde: -30,
        saldoHasta: 0,
        usuario: 'lu',
        cliente: 'paz',
      },
      orden: { columna: 'valorHora', direccion: 'descendente' },
      pagina: 3,
      tamanio: 50,
    })

    const ruta = String(getMock.mock.calls[0][0])
    const parametros = new URLSearchParams(ruta.split('?')[1])

    expect(ruta.startsWith('/personal?')).toBe(true)
    expect(Object.fromEntries(parametros)).toEqual({
      busqueda: 'ana gómez',
      nombre: 'ana',
      valorHoraDesde: '100',
      valorHoraHasta: '250.5',
      saldoDesde: '-30',
      saldoHasta: '0',
      usuario: 'lu',
      cliente: 'paz',
      orden: 'valorHora',
      direccion: 'descendente',
      pagina: '3',
      tamanio: '50',
    })
  })

  it('las claves de orden son las columnas de datos del servidor y nada más', () => {
    expect(COLUMNAS_ORDENABLES_DE_PERSONAL.every(esColumnaOrdenableDePersonal)).toBe(true)
    expect(esColumnaOrdenableDePersonal('acciones')).toBe(false)
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
