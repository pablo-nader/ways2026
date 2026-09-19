import { act, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { describe, expect, it, vi, beforeEach } from 'vitest'
import { Proveedores } from './Proveedores'
import { ErrorApi } from '../api/cliente'
import type { PaginaDe, ProveedorListado, SaldoDeProveedor } from '../api/tipos'

function renderProveedores() {
  return render(<Proveedores />, { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> })
}

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
  apiPostMock.mockReset()
  apiPutMock.mockReset()
  apiDeleteMock.mockReset()
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
    expect(screen.getByText('$ 2.000,00')).toBeInTheDocument()
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

    expect(await screen.findByText('-$ 500,00')).toBeInTheDocument()
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

// fix/web-bajas-catalogos: la baja de un proveedor reusa `ConfirmacionDeBaja` + `copiaDeFalloDeBaja`,
// mismo patrón que `Empresas.test.tsx`/`PaginaCatalogo.test.tsx` (`react-async-state` regla 10).

function bajaDe(razonSocial: string) {
  return within(screen.getByRole('row', { name: new RegExp(razonSocial) })).getByRole('button', { name: 'Baja' })
}

describe('Proveedores — baja lógica (fix/web-bajas-catalogos)', () => {
  beforeEach(() => {
    apiDeleteMock.mockResolvedValue(undefined)
  })

  it('el botón de baja no llama a la API hasta que se confirma', async () => {
    const usuario = userEvent.setup()
    mockearRutasBase()
    renderProveedores()

    await screen.findByText('Proveedor Uno SA')
    await usuario.click(bajaDe('Proveedor Uno SA'))
    expect(apiDeleteMock).not.toHaveBeenCalled()
    expect(screen.getByRole('alertdialog', { name: 'Confirmar baja' })).toHaveTextContent(
      '¿Dar de baja el proveedor "Proveedor Uno SA"?',
    )

    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))
    await screen.findByText('Proveedor "Proveedor Uno SA" dado de baja.')
    expect(apiDeleteMock).toHaveBeenCalledWith('/proveedores/1')
  })

  it('cancelar cierra la puerta y no llama nunca a la API', async () => {
    const usuario = userEvent.setup()
    mockearRutasBase()
    renderProveedores()

    await screen.findByText('Proveedor Uno SA')
    await usuario.click(bajaDe('Proveedor Uno SA'))
    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(apiDeleteMock).not.toHaveBeenCalled()
  })

  /** Cláusula bajo prueba: `ocupadoRef`, la guarda de re-entrancia del mismo tick. */
  it('un segundo click sobre la confirmación en vuelo se descarta', async () => {
    mockearRutasBase()
    apiDeleteMock.mockImplementation(() => new Promise<void>(() => {}))
    renderProveedores()

    await screen.findByText('Proveedor Uno SA')
    await userEvent.setup().click(bajaDe('Proveedor Uno SA'))
    const confirmar = screen.getByRole('button', { name: 'Confirmar baja' })
    await act(async () => {
      confirmar.click()
      confirmar.click()
      await Promise.resolve()
    })

    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
  })

  /** Cláusula bajo prueba: la ventana inerte completa (`bloqueado`) — ver `Empresas.test.tsx`. */
  it('durante el DELETE y su refresco no queda ninguna acción alcanzable', async () => {
    const usuario = userEvent.setup()
    let resolverDelete!: () => void
    let resolverRefresco!: (pagina: PaginaDe<ProveedorListado>) => void

    const otro = proveedorFixture({ id: 2, razonSocial: 'Proveedor Dos SA' })
    let cargas = 0
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/catalogos-fiscales/condiciones-fiscales') return Promise.resolve([])
      if (ruta !== '/proveedores') return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
      cargas += 1
      if (cargas === 1) return Promise.resolve(paginaFixture([proveedorFixture(), otro]))

      return new Promise<PaginaDe<ProveedorListado>>((resolver) => {
        resolverRefresco = resolver
      })
    })
    apiDeleteMock.mockImplementation(
      () =>
        new Promise<void>((resolver) => {
          resolverDelete = resolver
        }),
    )

    renderProveedores()
    await screen.findByText('Proveedor Uno SA')

    await usuario.click(bajaDe('Proveedor Uno SA'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    expect(screen.getByRole('button', { name: 'Dando de baja…' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Nuevo' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Buscar' })).toBeDisabled()
    for (const boton of [
      ...screen.getAllByRole('button', { name: 'Editar' }),
      ...screen.getAllByRole('button', { name: 'Baja' }),
      ...screen.getAllByRole('button', { name: 'Ver saldo' }),
    ]) {
      expect(boton).toBeDisabled()
    }

    await act(async () => {
      resolverDelete()
      await Promise.resolve()
    })

    await screen.findByText('Proveedor "Proveedor Uno SA" dado de baja.')
    expect(screen.getByText('Cargando…')).toBeInTheDocument()

    await act(async () => {
      resolverRefresco(paginaFixture([otro]))
      await Promise.resolve()
    })

    await screen.findByText('Proveedor Dos SA')
    expect(screen.queryByText('Proveedor Uno SA')).not.toBeInTheDocument()
    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
  })

  /** Cláusula bajo prueba: la elección de copia por `codigo` vía `copiaDeFalloDeBaja`, con el
   * sujeto `'el proveedor'`. */
  it('un 409 proveedor_en_uso rinde el mensaje del servidor y su guía, y deja la puerta abierta', async () => {
    const usuario = userEvent.setup()
    mockearRutasBase()
    apiDeleteMock.mockRejectedValue(
      new ErrorApi(409, 'proveedor_en_uso', 'No se puede dar de baja el proveedor porque tiene compras.'),
    )
    renderProveedores()

    await screen.findByText('Proveedor Uno SA')
    await usuario.click(bajaDe('Proveedor Uno SA'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await screen.findByText(/porque tiene compras/)
    expect(screen.getByText(/Reasigná esos datos o desactivá el proveedor/)).toBeInTheDocument()
    expect(screen.getByRole('alertdialog', { name: 'Confirmar baja' })).toBeInTheDocument()
  })

  /** Cláusula bajo prueba: el `setError('')` de `cancelarBaja` — ver `Empresas.test.tsx`. */
  it('cancelar después de un rechazo se lleva el motivo con la puerta', async () => {
    const usuario = userEvent.setup()
    mockearRutasBase()
    apiDeleteMock.mockRejectedValue(
      new ErrorApi(409, 'proveedor_en_uso', 'No se puede dar de baja el proveedor porque tiene compras.'),
    )
    renderProveedores()

    await screen.findByText('Proveedor Uno SA')
    await usuario.click(bajaDe('Proveedor Uno SA'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))
    await screen.findByText(/porque tiene compras/)

    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.queryByText(/porque tiene compras/)).not.toBeInTheDocument()
  })

  it('la baja del proveedor que se está editando se lleva también su formulario', async () => {
    const usuario = userEvent.setup()
    mockearRutasBase()
    renderProveedores()

    await screen.findByText('Proveedor Uno SA')
    await usuario.click(screen.getByRole('button', { name: 'Editar' }))
    expect(screen.getByText('Editando proveedor 1')).toBeInTheDocument()

    await usuario.click(bajaDe('Proveedor Uno SA'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await screen.findByText('Proveedor "Proveedor Uno SA" dado de baja.')
    expect(screen.queryByText('Editando proveedor 1')).not.toBeInTheDocument()
  })

  it('la baja del proveedor cuyo saldo está a la vista cierra también ese panel', async () => {
    const usuario = userEvent.setup()
    mockearRutasBase((ruta) =>
      ruta === '/proveedores/1/saldo' ? Promise.resolve({ idProveedor: 1, saldo: 0, compras: [] }) : undefined,
    )
    renderProveedores()

    await screen.findByText('Proveedor Uno SA')
    await usuario.click(screen.getByRole('button', { name: 'Ver saldo' }))
    await screen.findByText('Saldo de Proveedor Uno SA')

    await usuario.click(bajaDe('Proveedor Uno SA'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await screen.findByText('Proveedor "Proveedor Uno SA" dado de baja.')
    expect(screen.queryByText('Saldo de Proveedor Uno SA')).not.toBeInTheDocument()
  })
})
