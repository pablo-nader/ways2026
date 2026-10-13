import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { Clientes } from './Clientes'
import type { ClienteListado, PaginaDe } from '../api/tipos'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()
const apiPutMock = vi.fn()
const apiDeleteMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown])),
    put: (...args: unknown[]) => apiPutMock(...(args as [string, unknown])),
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

function clienteFixture(sobrescribir: Partial<ClienteListado> = {}): ClienteListado {
  return {
    id: 7,
    numero: 7,
    nombre: 'Luis',
    apellido: 'Pérez',
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

function paginaFixture(items: ClienteListado[]): PaginaDe<ClienteListado> {
  return { items, total: items.length, pagina: 1, tamanio: 25 }
}

function renderClientes() {
  return render(<Clientes />, { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> })
}

function rutasDeClientes() {
  return apiGetMock.mock.calls.map(([ruta]) => String(ruta)).filter((r) => r.startsWith('/clientes'))
}

const BUSCADOR = 'Buscar por nombre, apellido, razón social o documento…'

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiPutMock.mockReset()
  apiDeleteMock.mockReset()
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta.startsWith('/clientes')) return Promise.resolve(paginaFixture([clienteFixture()]))
    if (ruta === '/catalogos-fiscales/condiciones-fiscales') {
      return Promise.resolve([{ id: 1, codigo: 'CF', nombre: 'Consumidor final', codigoAfip: 5, activo: true }])
    }
    if (ruta === '/listas-precio') return Promise.resolve([{ id: 1, nombre: 'Minorista', esDefault: true }])

    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
  apiPostMock.mockResolvedValue(clienteFixture({ id: 8 }))
  apiDeleteMock.mockResolvedValue(undefined)
  vi.spyOn(window, 'confirm').mockReturnValue(true)
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe('Clientes — el refresco tras una escritura usa el término aplicado', () => {
  /** Deja la pantalla con `enviado` aplicado (o sin búsqueda) y `tipeado` escrito en la caja sin enviar. */
  async function prepararBusqueda(usuario: ReturnType<typeof userEvent.setup>, enviado: string, tipeado: string) {
    renderClientes()
    await screen.findByText('Luis Pérez')
    if (enviado) {
      await usuario.type(screen.getByPlaceholderText(BUSCADOR), enviado)
      await usuario.click(screen.getByRole('button', { name: 'Buscar' }))
      await waitFor(() => expect(rutasDeClientes().at(-1)).toBe(`/clientes?busqueda=${enviado}`))
      await screen.findByText('Luis Pérez')
    }
    await usuario.clear(screen.getByPlaceholderText(BUSCADOR))
    await usuario.type(screen.getByPlaceholderText(BUSCADOR), tipeado)
  }

  const casos = [
    { enviado: '', tipeado: 'zzz', esperada: '/clientes' },
    { enviado: 'luis', tipeado: 'luis zzz', esperada: '/clientes?busqueda=luis' },
  ]

  /** Cláusula bajo prueba: el término del refresco de `guardar`. El texto tipeado y no enviado no viaja. */
  it.each(casos)(
    'tras guardar recarga con el término aplicado "$enviado", no con "$tipeado"',
    async ({ enviado, tipeado, esperada }) => {
      const usuario = userEvent.setup()
      await prepararBusqueda(usuario, enviado, tipeado)
      await usuario.click(screen.getByRole('button', { name: 'Agregar' }))
      await usuario.type(await screen.findByLabelText('Nombre'), 'Ana')
      const condicion = screen.getByLabelText('Condición fiscal')
      await within(condicion).findByRole('option', { name: 'Consumidor final' })
      await usuario.selectOptions(condicion, '1')
      await waitFor(() => expect(screen.getByLabelText('Lista de precios')).toHaveValue('1'))
      const antes = rutasDeClientes().length

      await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

      await waitFor(() => expect(rutasDeClientes()).toHaveLength(antes + 1))
      expect(apiPostMock).toHaveBeenCalledTimes(1)
      expect(rutasDeClientes().at(-1)).toBe(esperada)
      expect(rutasDeClientes().some((r) => r.includes('zzz'))).toBe(false)
    },
  )

  /** Cláusula bajo prueba: el término del refresco de `eliminar`. */
  it.each(casos)(
    'tras dar de baja recarga con el término aplicado "$enviado", no con "$tipeado"',
    async ({ enviado, tipeado, esperada }) => {
      const usuario = userEvent.setup()
      await prepararBusqueda(usuario, enviado, tipeado)
      const antes = rutasDeClientes().length

      await usuario.click(screen.getByRole('button', { name: 'Eliminar' }))

      await waitFor(() => expect(rutasDeClientes()).toHaveLength(antes + 1))
      expect(apiDeleteMock).toHaveBeenCalledWith('/clientes/7')
      expect(rutasDeClientes().at(-1)).toBe(esperada)
      expect(rutasDeClientes().some((r) => r.includes('zzz'))).toBe(false)
    },
  )
})
