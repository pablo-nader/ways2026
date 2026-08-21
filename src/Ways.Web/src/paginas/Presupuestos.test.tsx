import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Presupuestos } from './Presupuestos'
import type { ClienteListado, PaginaDePresupuestos, PresupuestoListado, PuntoVentaListado } from '../api/tipos'

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

function presupuestoFixture(sobrescribir: Partial<PresupuestoListado> = {}): PresupuestoListado {
  return {
    id: 1,
    idPuntoVenta: 7,
    idCliente: 1,
    numero: 12,
    numeroFormateado: '0007-00000012',
    fechaEmision: '2026-08-01T12:00:00Z',
    vencimiento: '2026-09-30',
    vencido: false,
    convertible: true,
    total: 1500,
    estado: 'Enviado',
    ...sobrescribir,
  }
}

function paginaFixture(sobrescribir: Partial<PaginaDePresupuestos> = {}): PaginaDePresupuestos {
  return { items: [presupuestoFixture()], total: 1, pagina: 1, tamanio: 25, ...sobrescribir }
}

function mockearRutasBase(sobrescribirGet?: (ruta: string) => Promise<unknown> | undefined) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/puntos-venta') return Promise.resolve([puntoVentaFixture()])
    if (ruta === '/clientes') return Promise.resolve({ items: [clienteFixture()], total: 1, pagina: 1, tamanio: 25 })
    const propia = sobrescribirGet?.(ruta)
    if (propia) return propia
    if (ruta.startsWith('/presupuestos?')) return Promise.resolve(paginaFixture())
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

function renderPantalla() {
  return render(<Presupuestos />, { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> })
}

beforeEach(() => {
  apiGetMock.mockReset()
})

describe('Presupuestos — listado', () => {
  it('muestra la fila con número, cliente, punto de venta, estado y vencimiento', async () => {
    mockearRutasBase()
    renderPantalla()

    await screen.findByText('0007-00000012')
    const fila = screen.getByText('0007-00000012').closest('tr')!
    expect(within(fila).getByText(/Consumidor Final/)).toBeInTheDocument()
    expect(within(fila).getByText('Local Centro')).toBeInTheDocument()
    expect(within(fila).getByText('Enviado')).toBeInTheDocument()
    expect(within(fila).getByText(/Vence/)).toBeInTheDocument()
  })

  it('un presupuesto sin número muestra #id', async () => {
    mockearRutasBase((ruta) => (ruta.startsWith('/presupuestos?') ? Promise.resolve(paginaFixture({ items: [presupuestoFixture({ numeroFormateado: null })] })) : undefined))
    renderPantalla()

    expect(await screen.findByText('#1')).toBeInTheDocument()
  })

  it('sin resultados muestra el estado vacío', async () => {
    mockearRutasBase((ruta) => (ruta.startsWith('/presupuestos?') ? Promise.resolve(paginaFixture({ items: [], total: 0 })) : undefined))
    renderPantalla()

    expect(await screen.findByText('No hay presupuestos que coincidan con los filtros.')).toBeInTheDocument()
  })

  it('un presupuesto Borrador (sin vencimiento) muestra — en la columna de vencimiento', async () => {
    mockearRutasBase((ruta) =>
      ruta.startsWith('/presupuestos?')
        ? Promise.resolve(paginaFixture({ items: [presupuestoFixture({ estado: 'Borrador', vencimiento: null, numeroFormateado: null })] }))
        : undefined,
    )
    renderPantalla()

    const fila = (await screen.findByText('#1')).closest('tr')!
    expect(within(fila).getByText('—')).toBeInTheDocument()
  })
})

describe('Presupuestos — toggle "Solo vencidos" (design decisión 16, tarea 7.11)', () => {
  it('queda deshabilitado hasta elegir un punto de venta', async () => {
    mockearRutasBase()
    renderPantalla()

    await screen.findByText('0007-00000012')
    expect(screen.getByLabelText('Solo vencidos')).toBeDisabled()
  })

  it('se habilita al elegir un punto de venta y su query nunca viaja sin idPuntoVenta', async () => {
    mockearRutasBase()
    renderPantalla()
    await screen.findByText('0007-00000012')

    await userEvent.selectOptions(screen.getByLabelText('Punto de venta'), 'Local Centro')
    await waitFor(() => expect(screen.getByLabelText('Solo vencidos')).toBeEnabled())

    await userEvent.click(screen.getByLabelText('Solo vencidos'))
    await waitFor(() => expect(apiGetMock).toHaveBeenCalledWith(expect.stringContaining('vencido=true')))

    // Volver a "Todos" los puntos de venta deshabilita el toggle de nuevo y limpia el filtro —
    // nunca queda un `vencido` huérfano en el estado (construirQueryDePresupuestos ya lo filtra,
    // esto además prueba que la UI lo refleja).
    await userEvent.selectOptions(screen.getByLabelText('Punto de venta'), 'Todos')
    await waitFor(() => expect(screen.getByLabelText('Solo vencidos')).toBeDisabled())
    await waitFor(() => expect(screen.getByLabelText('Solo vencidos')).not.toBeChecked())
  })
})

describe('Presupuestos — pager (tarea 7.11)', () => {
  it('está deshabilitado en ambos bordes cuando hay una sola página', async () => {
    mockearRutasBase()
    renderPantalla()

    await screen.findByText('0007-00000012')
    expect(screen.getByRole('button', { name: 'Anterior' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeDisabled()
  })

  it('navega entre páginas, con "Anterior"/"Siguiente" habilitados solo del lado correcto', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/presupuestos?')) {
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

describe('Presupuestos — filtros (react-async-state regla 2, mutation-proof-tests regla 7)', () => {
  it('una respuesta desactualizada nunca pisa la más reciente', async () => {
    let resolverPrimera: (v: PaginaDePresupuestos) => void = () => {}
    const primeraPendiente = new Promise<PaginaDePresupuestos>((resolve) => {
      resolverPrimera = resolve
    })
    let llamadas = 0

    mockearRutasBase((ruta) => {
      if (!ruta.startsWith('/presupuestos?')) return undefined
      llamadas += 1
      if (llamadas === 1) return Promise.resolve(paginaFixture())
      if (llamadas === 2) return primeraPendiente
      if (llamadas === 3) return Promise.resolve(paginaFixture({ items: [presupuestoFixture({ id: 3, numeroFormateado: '0007-00000999' })] }))
      return Promise.reject(new Error('llamada inesperada'))
    })

    renderPantalla()
    await screen.findByLabelText('Estado')

    await userEvent.selectOptions(screen.getByLabelText('Estado'), 'Enviado')
    await userEvent.selectOptions(screen.getByLabelText('Estado'), 'Convertido')

    await waitFor(() => expect(screen.getByText('0007-00000999')).toBeInTheDocument())

    await act(async () => {
      resolverPrimera(paginaFixture({ items: [presupuestoFixture({ id: 2, numeroFormateado: '0007-00000777' })] }))
      await primeraPendiente
    })
    expect(screen.getByText('0007-00000999')).toBeInTheDocument()
    expect(screen.queryByText('0007-00000777')).not.toBeInTheDocument()
  })
})

describe('Presupuestos — "Nuevo presupuesto" (design decisión 17: sin restricción admin-only)', () => {
  it('el botón siempre está visible — cualquier rol que llega a la pantalla puede quotear', async () => {
    mockearRutasBase()
    renderPantalla()

    expect(await screen.findByRole('button', { name: 'Nuevo presupuesto' })).toBeInTheDocument()
  })
})
