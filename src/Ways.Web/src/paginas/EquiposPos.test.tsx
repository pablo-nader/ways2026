import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { EquiposPos, NOTA_DE_REVOCACION } from './EquiposPos'
import { ErrorApi } from '../api/cliente'
import type { DispositivoListado } from '../api/dispositivos'

const apiGetMock = vi.fn()
const apiDeleteMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: vi.fn(),
    put: vi.fn(),
    delete: (...args: unknown[]) => apiDeleteMock(...(args as [string])),
  },
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

const caja1: DispositivoListado = {
  id: 11,
  nombre: 'Caja 1',
  puntoVenta: { numero: 100, nombre: 'PV Centro' },
  createdAt: '2026-03-02T13:15:00Z',
  ultimoUsoAt: '2026-08-20T21:40:00Z',
}
const caja2: DispositivoListado = {
  id: 12,
  nombre: 'Caja 2',
  puntoVenta: { numero: 101, nombre: 'PV Anexo' },
  createdAt: '2026-05-11T09:05:00Z',
  ultimoUsoAt: null,
}

function fecha(iso: string) {
  return new Date(iso).toLocaleString('es-AR')
}

function montar(items: DispositivoListado[] = [caja1, caja2]) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/dispositivos') return Promise.resolve(items)

    return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
  })

  return render(<EquiposPos />)
}

function botonRevocarDe(nombre: string) {
  return within(screen.getByRole('row', { name: new RegExp(nombre) })).getByRole('button', { name: 'Revocar' })
}

function celdas(nombre: string) {
  return within(screen.getByRole('row', { name: new RegExp(nombre) }))
    .getAllByRole('cell')
    .map((c) => c.textContent)
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiDeleteMock.mockReset()
  apiDeleteMock.mockResolvedValue(undefined)
})

describe('EquiposPos — listado', () => {
  it('rinde nombre, punto de venta, fecha de vinculación y último uso de cada equipo', async () => {
    montar()
    await waitFor(() => expect(screen.getByText('Caja 1')).toBeInTheDocument())

    expect(apiGetMock).toHaveBeenCalledWith('/dispositivos')
    expect(screen.getAllByRole('columnheader').map((c) => c.textContent)).toEqual([
      'Nombre',
      'Punto de venta',
      'Vinculado el',
      'Último uso',
      'Acciones',
    ])
    expect(celdas('Caja 1')).toEqual(['Caja 1', 'PV Centro', fecha(caja1.createdAt), fecha('2026-08-20T21:40:00Z'), 'Revocar'])
    expect(celdas('Caja 2')).toEqual(['Caja 2', 'PV Anexo', fecha(caja2.createdAt), 'Nunca', 'Revocar'])
  })

  it('sin equipos rinde el aviso de listado vacío', async () => {
    montar([])

    await waitFor(() => expect(screen.getByText('No hay equipos vinculados.')).toBeInTheDocument())
  })

  it('un fallo de carga rinde el mensaje del servidor', async () => {
    apiGetMock.mockRejectedValue(new ErrorApi(500, 'error_interno', 'Se cayó el listado.'))
    render(<EquiposPos />)

    await waitFor(() => expect(screen.getByText('Se cayó el listado.')).toBeInTheDocument())
  })
})

describe('EquiposPos — revocación', () => {
  it('no llama a la API hasta confirmar, y la puerta advierte qué bajas sigue bloqueando', async () => {
    const usuario = userEvent.setup()
    montar()
    await waitFor(() => expect(screen.getByText('Caja 1')).toBeInTheDocument())

    await usuario.click(botonRevocarDe('Caja 1'))

    expect(apiDeleteMock).not.toHaveBeenCalled()
    const puerta = screen.getByRole('alertdialog', { name: 'Confirmar revocación' })
    expect(puerta).toHaveTextContent('¿Revocar el equipo "Caja 1"?')
    expect(puerta).toHaveTextContent(NOTA_DE_REVOCACION)
    expect(NOTA_DE_REVOCACION).toMatch(/su punto de venta, el tenant y el usuario que lo vinculó no se van a poder dar de baja/)
  })

  it('confirmar revoca ese equipo, cierra la puerta y refresca el listado', async () => {
    const usuario = userEvent.setup()
    let cargas = 0
    apiGetMock.mockImplementation(() => {
      cargas += 1

      return Promise.resolve(cargas === 1 ? [caja1, caja2] : [caja2])
    })
    render(<EquiposPos />)
    await waitFor(() => expect(screen.getByText('Caja 1')).toBeInTheDocument())

    await usuario.click(botonRevocarDe('Caja 1'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar revocación' }))

    await waitFor(() => expect(screen.getByText('Se revocó el equipo "Caja 1".')).toBeInTheDocument())
    expect(apiDeleteMock).toHaveBeenCalledWith('/dispositivos/11')
    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    await waitFor(() => expect(screen.queryByText('Caja 1')).not.toBeInTheDocument())
    expect(screen.getByText('Caja 2')).toBeInTheDocument()
  })

  it('cancelar cierra la puerta sin llamar a la API', async () => {
    const usuario = userEvent.setup()
    montar()
    await waitFor(() => expect(screen.getByText('Caja 1')).toBeInTheDocument())

    await usuario.click(botonRevocarDe('Caja 1'))
    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(apiDeleteMock).not.toHaveBeenCalled()
    expect(botonRevocarDe('Caja 2')).toBeEnabled()
  })

  /** Cláusula bajo prueba: `revocacion !== null` dentro de `bloqueado`. */
  it('con la puerta abierta ningún otro Revocar queda alcanzable', async () => {
    const usuario = userEvent.setup()
    montar()
    await waitFor(() => expect(screen.getByText('Caja 1')).toBeInTheDocument())

    await usuario.click(botonRevocarDe('Caja 1'))

    for (const boton of screen.getAllByRole('button', { name: 'Revocar' })) {
      expect(boton).toBeDisabled()
    }
    expect(screen.getByRole('button', { name: 'Confirmar revocación' })).toBeEnabled()
  })

  /** Cláusula bajo prueba: la ventana inerte cubre el DELETE y también su refresco. */
  it('durante el DELETE y su refresco no queda ninguna acción alcanzable', async () => {
    const usuario = userEvent.setup()
    let resolverDelete!: () => void
    let resolverRefresco!: (items: DispositivoListado[]) => void
    let cargas = 0
    apiGetMock.mockImplementation(() => {
      cargas += 1
      if (cargas === 1) return Promise.resolve([caja1, caja2])

      return new Promise<DispositivoListado[]>((resolver) => {
        resolverRefresco = resolver
      })
    })
    apiDeleteMock.mockImplementation(
      () =>
        new Promise<void>((resolver) => {
          resolverDelete = resolver
        }),
    )
    render(<EquiposPos />)
    await waitFor(() => expect(screen.getByText('Caja 1')).toBeInTheDocument())

    await usuario.click(botonRevocarDe('Caja 1'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar revocación' }))

    expect(screen.getByRole('button', { name: 'Revocando…' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    for (const boton of screen.getAllByRole('button', { name: 'Revocar' })) {
      expect(boton).toBeDisabled()
    }

    await act(async () => {
      resolverDelete()
      await Promise.resolve()
    })
    await waitFor(() => expect(screen.getByText('Se revocó el equipo "Caja 1".')).toBeInTheDocument())
    expect(screen.getByText('Cargando…')).toBeInTheDocument()

    await act(async () => {
      resolverRefresco([caja2])
      await Promise.resolve()
    })
    await waitFor(() => expect(botonRevocarDe('Caja 2')).toBeEnabled())
    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
  })

  /** Cláusula bajo prueba: `ocupadoRef`, la guarda de re-entrancia del mismo tick. */
  it('un segundo click sobre la confirmación en vuelo se descarta', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockImplementation(() => new Promise<void>(() => {}))
    montar()
    await waitFor(() => expect(screen.getByText('Caja 1')).toBeInTheDocument())

    await usuario.click(botonRevocarDe('Caja 1'))
    const confirmar = screen.getByRole('button', { name: 'Confirmar revocación' })
    await act(async () => {
      confirmar.click()
      confirmar.click()
      await Promise.resolve()
    })

    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
  })

  /** Cláusula bajo prueba: el refresco aislado del try/catch del DELETE. */
  it('un refresco fallido después de revocar no reporta la revocación como fallida', async () => {
    const usuario = userEvent.setup()
    let cargas = 0
    apiGetMock.mockImplementation(() => {
      cargas += 1
      if (cargas === 1) return Promise.resolve([caja1])

      return Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó.'))
    })
    render(<EquiposPos />)
    await waitFor(() => expect(screen.getByText('Caja 1')).toBeInTheDocument())

    await usuario.click(botonRevocarDe('Caja 1'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar revocación' }))

    await waitFor(() =>
      expect(
        screen.getByText(
          'Se revocó el equipo "Caja 1". Se revocó, pero no se pudo actualizar la vista. Recargá la pantalla.',
        ),
      ).toBeInTheDocument(),
    )
    expect(screen.queryByText(/No se pudo revocar/)).not.toBeInTheDocument()
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
  })

  it('un 404 rinde la copia neutra con la puerta abierta, y cancelar se lleva el motivo', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'No existe el dispositivo 11.'))
    montar()
    await waitFor(() => expect(screen.getByText('Caja 1')).toBeInTheDocument())

    await usuario.click(botonRevocarDe('Caja 1'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar revocación' }))

    const copia = 'No se pudo revocar el equipo. Ya no existe o no está a tu alcance. Actualizá el listado.'
    await waitFor(() => expect(screen.getByText(copia)).toBeInTheDocument())
    expect(screen.getByRole('alertdialog', { name: 'Confirmar revocación' })).toBeInTheDocument()
    expect(screen.queryByText(/dispositivo 11/)).not.toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByText(copia)).not.toBeInTheDocument()
  })
})
