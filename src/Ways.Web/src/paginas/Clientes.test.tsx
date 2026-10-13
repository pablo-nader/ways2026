import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Clientes } from './Clientes'
import type { ClienteListado, PaginaDe } from '../api/tipos'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()
const apiPutMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown])),
    put: (...args: unknown[]) => apiPutMock(...(args as [string, unknown])),
    delete: vi.fn(),
  },
  ErrorApi: class ErrorApiMock extends Error {},
}))

function clienteFixture(sobrescribir: Partial<ClienteListado> = {}): ClienteListado {
  return {
    id: 2,
    numero: 2,
    nombre: 'Ana',
    apellido: null,
    razonSocial: null,
    tipoDocumento: null,
    numeroDocumento: null,
    idCondicionFiscal: 1,
    nacimiento: null,
    domicilio: null,
    telefono: null,
    celular: null,
    email: null,
    observaciones: null,
    idListaPrecio: 1,
    limiteCredito: 0,
    creditoIlimitado: false,
    saldo: 0,
    activo: true,
    idEmpresa: null,
    esConsumidorFinal: false,
    ...sobrescribir,
  }
}

function responder(clientes: ClienteListado[]) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta.startsWith('/clientes')) {
      const pagina: PaginaDe<ClienteListado> = { items: clientes, total: clientes.length, pagina: 1, tamanio: 25 }
      return Promise.resolve(pagina)
    }
    if (ruta === '/catalogos-fiscales/condiciones-fiscales') {
      return Promise.resolve([{ id: 1, codigo: 'RI', nombre: 'Responsable Inscripto', codigoAfip: 1, activo: true }])
    }
    if (ruta === '/listas-precio') return Promise.resolve([{ id: 1, nombre: 'Minorista', esDefault: true }])
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

function renderClientes() {
  return render(<Clientes />, { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiPutMock.mockReset()
})

describe('Clientes — empresa de la fila al guardar', () => {
  async function abrirEdicion(usuario: ReturnType<typeof userEvent.setup>) {
    await screen.findByText('Ana')
    await usuario.click(screen.getByRole('button', { name: 'Editar' }))
    await screen.findByRole('option', { name: 'Responsable Inscripto' })
    await screen.findByRole('option', { name: 'Minorista (default)' })
  }

  it('editar un cliente de una empresa manda la empresa de la fila en el PUT', async () => {
    const usuario = userEvent.setup()
    responder([clienteFixture({ idEmpresa: 5 })])
    apiPutMock.mockResolvedValue(clienteFixture())
    renderClientes()

    await abrirEdicion(usuario)
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    expect(apiPutMock.mock.calls[0][0]).toBe('/clientes/2')
    expect(apiPutMock.mock.calls[0][1]).toMatchObject({ idEmpresa: 5 })
  })

  it('editar un cliente de todo el tenant manda null', async () => {
    const usuario = userEvent.setup()
    responder([clienteFixture({ idEmpresa: null })])
    apiPutMock.mockResolvedValue(clienteFixture())
    renderClientes()

    await abrirEdicion(usuario)
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    expect(apiPutMock.mock.calls[0][1]).toMatchObject({ idEmpresa: null })
  })

  it('un alta manda null aunque antes se haya abierto la edición de un cliente de una empresa', async () => {
    const usuario = userEvent.setup()
    responder([clienteFixture({ idEmpresa: 5 })])
    apiPostMock.mockResolvedValue(clienteFixture())
    renderClientes()

    await abrirEdicion(usuario)
    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))
    await usuario.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByRole('option', { name: 'Minorista (default)' })
    await usuario.type(screen.getByLabelText('Nombre'), 'Luis')
    await usuario.selectOptions(screen.getByLabelText('Condición fiscal'), '1')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    expect(apiPostMock.mock.calls[0][1]).toMatchObject({ nombre: 'Luis', idEmpresa: null })
  })
})
