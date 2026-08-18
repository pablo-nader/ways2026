import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { describe, expect, it, vi, beforeEach } from 'vitest'
import { Proveedores } from './Proveedores'
import type { PaginaDe, ProveedorListado, SaldoDeProveedor } from '../api/tipos'

function renderProveedores() {
  return render(<Proveedores />, { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> })
}

const apiGetMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: vi.fn(),
    put: vi.fn(),
    delete: vi.fn(),
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

function proveedorFixture(sobrescribir: Partial<ProveedorListado> = {}): ProveedorListado {
  return {
    id: 1,
    razonSocial: 'Proveedor Uno SA',
    nombreFantasia: null,
    cuit: null,
    idCondicionFiscal: 1,
    domicilio: null,
    telefono: null,
    email: null,
    vendedor: null,
    celularVendedor: null,
    supervisor: null,
    celularSupervisor: null,
    margen: null,
    observaciones: null,
    activo: true,
    idEmpresa: null,
    ...sobrescribir,
  }
}

function paginaFixture(items: ProveedorListado[]): PaginaDe<ProveedorListado> {
  return { items, total: items.length, pagina: 1, tamanio: 25 }
}

function mockearRutasBase(sobrescribir?: (ruta: string) => Promise<unknown> | undefined) {
  apiGetMock.mockImplementation((ruta: string) => {
    const propia = sobrescribir?.(ruta)
    if (propia) return propia
    if (ruta === '/proveedores') return Promise.resolve(paginaFixture([proveedorFixture()]))
    if (ruta === '/catalogos-fiscales/condiciones-fiscales') return Promise.resolve([])
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

beforeEach(() => {
  apiGetMock.mockReset()
})

describe('Proveedores — listado', () => {
  it('renderiza las filas con el botón "Ver saldo"', async () => {
    mockearRutasBase()
    renderProveedores()

    expect(await screen.findByText('Proveedor Uno SA')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Ver saldo' })).toBeInTheDocument()
  })
})

describe('Proveedores — panel de saldo', () => {
  it('muestra el saldo derivado y el estado de pago por compra', async () => {
    const saldo: SaldoDeProveedor = {
      idProveedor: 1,
      saldo: 2000,
      compras: [{ idComprobanteCompra: 1, numeroExterno: '0003-00012345', total: 5000, pagado: 3000, estadoPago: 'Parcial' }],
    }
    mockearRutasBase((ruta) => (ruta === '/proveedores/1/saldo' ? Promise.resolve(saldo) : undefined))
    const usuario = userEvent.setup()

    renderProveedores()
    await screen.findByText('Proveedor Uno SA')
    await usuario.click(screen.getByRole('button', { name: 'Ver saldo' }))

    expect(await screen.findByText('Saldo de Proveedor Uno SA')).toBeInTheDocument()
    expect(screen.getByText('$2.000,00')).toBeInTheDocument()
    expect(screen.getByText('0003-00012345')).toBeInTheDocument()
    expect(screen.getByText('Parcial')).toBeInTheDocument()
  })

  // stage-15-cc-proveedores-ledger (Slice 6): `ResumenSaldoDeProveedor` fue re-apuntada al ledger —
  // el callout "aproximación, no invariante" describía la fórmula RETIRADA por esta etapa, ahora
  // dice simplemente "Saldo a favor." (mutation target #28, `ResumenSaldoDeProveedor.test.tsx`).
  it('un saldo negativo se muestra tal cual, con el callout de saldo a favor', async () => {
    const saldo: SaldoDeProveedor = {
      idProveedor: 1,
      saldo: -500,
      compras: [{ idComprobanteCompra: 1, numeroExterno: '0003-00012345', total: 2000, pagado: 0, estadoPago: 'Impaga' }],
    }
    mockearRutasBase((ruta) => (ruta === '/proveedores/1/saldo' ? Promise.resolve(saldo) : undefined))
    const usuario = userEvent.setup()

    renderProveedores()
    await screen.findByText('Proveedor Uno SA')
    await usuario.click(screen.getByRole('button', { name: 'Ver saldo' }))

    expect(await screen.findByText('-$500,00')).toBeInTheDocument()
    expect(screen.getByText('Saldo a favor.')).toBeInTheDocument()
    // la compra sigue impaga individualmente aunque el saldo total ya sea negativo — honesto, no invariante.
    expect(screen.getByText('Impaga')).toBeInTheDocument()
  })

  it('un proveedor sin compras confirmadas muestra el estado vacío de la tabla', async () => {
    const saldo: SaldoDeProveedor = { idProveedor: 1, saldo: 0, compras: [] }
    mockearRutasBase((ruta) => (ruta === '/proveedores/1/saldo' ? Promise.resolve(saldo) : undefined))
    const usuario = userEvent.setup()

    renderProveedores()
    await screen.findByText('Proveedor Uno SA')
    await usuario.click(screen.getByRole('button', { name: 'Ver saldo' }))

    expect(await screen.findByText('Este proveedor no tiene compras confirmadas.')).toBeInTheDocument()
  })

  it('cerrar el panel lo desmonta', async () => {
    const saldo: SaldoDeProveedor = { idProveedor: 1, saldo: 0, compras: [] }
    mockearRutasBase((ruta) => (ruta === '/proveedores/1/saldo' ? Promise.resolve(saldo) : undefined))
    const usuario = userEvent.setup()

    renderProveedores()
    await screen.findByText('Proveedor Uno SA')
    await usuario.click(screen.getByRole('button', { name: 'Ver saldo' }))
    await screen.findByText('Saldo de Proveedor Uno SA')

    await usuario.click(screen.getByRole('button', { name: 'Cerrar' }))
    expect(screen.queryByText('Saldo de Proveedor Uno SA')).not.toBeInTheDocument()
  })
})
