import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Remitos } from './Remitos'
import type { ClienteListado, PaginaDeRemitos, PuntoVentaListado, RemitoListado } from '../api/tipos'

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

function puntoVentaFixture(sobrescribir: Partial<PuntoVentaListado> = {}): PuntoVentaListado {
  return {
    id: 7,
    idTenant: 1,
    idEmpresa: 1,
    nombre: 'Local Centro',
    domicilio: null,
    horario: null,
    whatsapp: null,
    instagram: null,
    facebook: null,
    web: null,
    nombreTenant: 'Tenant Demo',
    razonSocialEmpresa: 'Empresa Demo',
    ...sobrescribir,
  }
}

function clienteFixture(sobrescribir: Partial<ClienteListado> = {}): ClienteListado {
  return {
    id: 1,
    numero: 1,
    nombre: 'Consumidor Final',
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
    creditoIlimitado: true,
    saldo: 0,
    activo: true,
    idEmpresa: null,
    esConsumidorFinal: true,
    ...sobrescribir,
  }
}

function remitoFixture(sobrescribir: Partial<RemitoListado> = {}): RemitoListado {
  return {
    id: 1,
    idPuntoVenta: 7,
    idCliente: 1,
    numero: 12,
    numeroFormateado: '0007-00000012',
    fechaEmision: '2026-08-01T12:00:00Z',
    total: 1500,
    estado: 'Emitido',
    idComprobanteVenta: null,
    ...sobrescribir,
  }
}

function paginaFixture(sobrescribir: Partial<PaginaDeRemitos> = {}): PaginaDeRemitos {
  return { items: [remitoFixture()], total: 1, pagina: 1, tamanio: 25, ...sobrescribir }
}

function mockearRutasBase(sobrescribirGet?: (ruta: string) => Promise<unknown> | undefined) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/puntos-venta') return Promise.resolve([puntoVentaFixture()])
    if (ruta === '/clientes') return Promise.resolve({ items: [clienteFixture()], total: 1, pagina: 1, tamanio: 25 })
    const propia = sobrescribirGet?.(ruta)
    if (propia) return propia
    if (ruta.startsWith('/remitos?')) return Promise.resolve(paginaFixture())
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

function renderPantalla() {
  return render(<Remitos />, { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> })
}

beforeEach(() => {
  apiGetMock.mockReset()
})

describe('Remitos — listado', () => {
  it('muestra la fila con número, cliente, punto de venta y estado', async () => {
    mockearRutasBase()
    renderPantalla()

    await screen.findByText('0007-00000012')
    const fila = screen.getByText('0007-00000012').closest('tr')!
    expect(within(fila).getByText(/Consumidor Final/)).toBeInTheDocument()
    expect(within(fila).getByText('Local Centro')).toBeInTheDocument()
    expect(within(fila).getByText('Emitido')).toBeInTheDocument()
  })

  it('un remito sin número muestra #id', async () => {
    mockearRutasBase((ruta) => (ruta.startsWith('/remitos?') ? Promise.resolve(paginaFixture({ items: [remitoFixture({ numeroFormateado: null })] })) : undefined))
    renderPantalla()

    expect(await screen.findByText('#1')).toBeInTheDocument()
  })

  it('sin resultados muestra el estado vacío', async () => {
    mockearRutasBase((ruta) => (ruta.startsWith('/remitos?') ? Promise.resolve(paginaFixture({ items: [], total: 0 })) : undefined))
    renderPantalla()

    expect(await screen.findByText('No hay remitos que coincidan con los filtros.')).toBeInTheDocument()
  })
})

describe('Remitos — filtro por estado', () => {
  it('cambiar el estado dispara una nueva consulta con el filtro aplicado', async () => {
    mockearRutasBase()
    renderPantalla()
    await screen.findByText('0007-00000012')

    await userEvent.selectOptions(screen.getByLabelText('Estado'), 'Facturado')
    await waitFor(() => expect(apiGetMock).toHaveBeenCalledWith(expect.stringContaining('estado=Facturado')))
  })

  it('una respuesta stale de un filtro anterior no pisa la del filtro más nuevo (mutation-proof-tests regla 7)', async () => {
    let resolverFacturado: (v: PaginaDeRemitos) => void = () => {}
    let promesaFacturado: Promise<PaginaDeRemitos> = Promise.resolve(paginaFixture())
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/remitos?') && ruta.includes('estado=Facturado')) {
        promesaFacturado = new Promise((resolve) => (resolverFacturado = resolve))
        return promesaFacturado
      }
      if (ruta.startsWith('/remitos?') && ruta.includes('estado=Anulado')) {
        return Promise.resolve(paginaFixture({ items: [remitoFixture({ id: 2, numeroFormateado: '0007-00000099' })] }))
      }
      return undefined
    })
    renderPantalla()
    await screen.findByText('0007-00000012')

    await userEvent.selectOptions(screen.getByLabelText('Estado'), 'Facturado')
    // el fetch de "Facturado" queda en vuelo — `resolverFacturado` todavía no se llamó.

    await userEvent.selectOptions(screen.getByLabelText('Estado'), 'Anulado')
    await screen.findByText('0007-00000099')

    // regla 7: el flush del microtask stale va DENTRO de act — un `waitFor` pasaría en su primer
    // tick, antes de que el `.then` stale aterrice, y saldría verde sin probar nada.
    await act(async () => {
      resolverFacturado(paginaFixture({ items: [remitoFixture({ id: 3, numeroFormateado: '0007-00000199' })] }))
      await promesaFacturado
    })

    expect(screen.getByText('0007-00000099')).toBeInTheDocument()
    expect(screen.queryByText('0007-00000199')).not.toBeInTheDocument()
  })
})

describe('Remitos — pager', () => {
  it('está deshabilitado en ambos bordes cuando hay una sola página', async () => {
    mockearRutasBase()
    renderPantalla()

    await screen.findByText('0007-00000012')
    expect(screen.getByRole('button', { name: 'Anterior' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeDisabled()
  })

  it('navega entre páginas, con "Anterior"/"Siguiente" habilitados solo del lado correcto', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/remitos?')) {
        const paginaSolicitada = ruta.includes('pagina=2') ? 2 : 1
        return Promise.resolve(paginaFixture({ total: 50, pagina: paginaSolicitada, tamanio: 25 }))
      }
      return undefined
    })
    const usuario = userEvent.setup()
    renderPantalla()

    await screen.findByText(/Página 1 de 2/)
    expect(screen.getByRole('button', { name: 'Anterior' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeEnabled()

    await usuario.click(screen.getByRole('button', { name: 'Siguiente' }))
    await screen.findByText(/Página 2 de 2/)
    expect(screen.getByRole('button', { name: 'Anterior' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeDisabled()
  })
})

describe('Remitos — herramientas ("Nuevo remito" / "Facturar remitos")', () => {
  it('ambos botones siempre están visibles — cualquier rol que llega a la pantalla puede despachar/facturar', async () => {
    mockearRutasBase()
    renderPantalla()

    expect(await screen.findByRole('button', { name: 'Nuevo remito' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Facturar remitos' })).toBeInTheDocument()
  })
})
