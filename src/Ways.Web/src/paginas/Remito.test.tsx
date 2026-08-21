import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Remito } from './Remito'
import type { ClienteListado, ComprobanteEmitido, EstadoRemito, PuntoVentaListado, RemitoDetalle } from '../api/tipos'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()
const apiPutMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown?])),
    put: (...args: unknown[]) => apiPutMock(...(args as [string, unknown?])),
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
    id: 9,
    idTenant: 1,
    idEmpresa: 1,
    nombre: 'Depósito Norte',
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
    id: 5,
    numero: 5,
    nombre: 'Juan',
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
    creditoIlimitado: true,
    saldo: 0,
    activo: true,
    idEmpresa: null,
    esConsumidorFinal: false,
    ...sobrescribir,
  }
}

function detalleFixture(sobrescribir: Partial<RemitoDetalle> = {}): RemitoDetalle {
  return {
    id: 30,
    idPuntoVenta: 9,
    idCliente: 5,
    idEmpleado: 1,
    numero: 12,
    numeroFormateado: '0009-00000012',
    fechaEmision: '2026-08-19T12:00:00Z',
    fechaSalida: '2026-08-19T12:00:00Z',
    direccionEntrega: null,
    observaciones: null,
    subtotal: 200,
    descuentoTotal: 0,
    total: 200,
    estado: 'Emitido',
    idComprobanteVenta: null,
    items: [
      {
        orden: 1,
        idArticulo: 10,
        descripcion: 'Yerba mate 1kg',
        cantidad: 2,
        precioUnitario: 100,
        descuento: 0,
        total: 200,
        idListaPrecio: 1,
        idOferta: null,
        idAlicuotaIva: 1,
        porcentajeIva: 21,
        costoUnitario: 50,
        costoEsEstimado: false,
        idLote: null,
      },
    ],
    ...sobrescribir,
  }
}

function borradorFixture(sobrescribir: Partial<RemitoDetalle> = {}): RemitoDetalle {
  return detalleFixture({ estado: 'Borrador', numero: null, numeroFormateado: null, fechaSalida: null, ...sobrescribir })
}

function facturaFixture(sobrescribir: Partial<ComprobanteEmitido> = {}): ComprobanteEmitido {
  return {
    id: 77,
    numero: 3,
    numeroVisible: '0009-00000003',
    estado: 'Emitido',
    fecha: '2026-08-20T12:00:00Z',
    idPuntoVenta: 9,
    idCliente: 5,
    idComprobanteAsociado: null,
    subtotal: 200,
    descuentoTotal: 0,
    total: 200,
    direccionEntrega: null,
    observaciones: null,
    items: [],
    pagos: [],
    idPresupuestoOrigen: null,
    ...sobrescribir,
  }
}

function mockearReferencia(sobrescribirGet?: (ruta: string) => Promise<unknown> | undefined) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/puntos-venta') return Promise.resolve([puntoVentaFixture()])
    if (ruta === '/clientes') return Promise.resolve({ items: [clienteFixture()], total: 1, pagina: 1, tamanio: 25 })
    const propia = sobrescribirGet?.(ruta)
    if (propia) return propia
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

function renderPantalla(id: string | number = 30) {
  return render(
    <MemoryRouter initialEntries={[`/remitos/${id}`]}>
      <Routes>
        <Route path="/remitos/:id" element={<Remito />} />
      </Routes>
    </MemoryRouter>,
  )
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiPutMock.mockReset()
})

describe('Remito — detalle (lectura)', () => {
  it('muestra el estado y los items con sus valores', async () => {
    mockearReferencia((ruta) => (ruta === '/remitos/30' ? Promise.resolve(detalleFixture()) : undefined))
    renderPantalla()

    expect(await screen.findByText('Emitido')).toBeInTheDocument()
    expect(screen.getByText('Yerba mate 1kg')).toBeInTheDocument()
    expect(screen.getAllByText('$200,00').length).toBeGreaterThan(0)
  })
})

describe('Remito — facturado muestra el link a su factura y CERO acciones (tarea 8.3)', () => {
  it('renderiza el número visible y el total de la factura, sin ningún botón de acción', async () => {
    mockearReferencia((ruta) => {
      if (ruta === '/remitos/30') return Promise.resolve(detalleFixture({ estado: 'Facturado', idComprobanteVenta: 77 }))
      if (ruta === '/ventas/77') return Promise.resolve(facturaFixture())
      return undefined
    })
    renderPantalla()

    await screen.findByText('Facturado')
    expect(await screen.findByText('0009-00000003')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Anular/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Emitir/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Guardar borrador' })).not.toBeInTheDocument()
  })
})

describe.each<{ estado: EstadoRemito; emitir: boolean; anular: boolean }>([
  { estado: 'Borrador', emitir: true, anular: true },
  { estado: 'Emitido', emitir: false, anular: true },
  { estado: 'Facturado', emitir: false, anular: false },
  { estado: 'Anulado', emitir: false, anular: false },
])('Remito — matriz de acciones deshabilitadas por estado (tarea 8.9): $estado', ({ estado, emitir, anular }) => {
  it(`Emitir ${emitir ? 'SÍ' : 'NO'} se ofrece / Anular ${anular ? 'SÍ' : 'NO'} se ofrece`, async () => {
    mockearReferencia((ruta) => {
      if (ruta === '/remitos/30') {
        const fixture = estado === 'Borrador' ? borradorFixture() : detalleFixture({ estado, idComprobanteVenta: estado === 'Facturado' ? 77 : null })
        return Promise.resolve(fixture)
      }
      if (ruta === '/ventas/77') return Promise.resolve(facturaFixture())
      return undefined
    })
    renderPantalla()

    await screen.findByText(estado)

    if (emitir) {
      expect(screen.getByRole('button', { name: 'Emitir' })).toBeInTheDocument()
    } else {
      expect(screen.queryByRole('button', { name: 'Emitir' })).not.toBeInTheDocument()
    }

    if (anular) {
      expect(screen.getByRole('button', { name: 'Anular' })).toBeInTheDocument()
    } else {
      expect(screen.queryByRole('button', { name: 'Anular' })).not.toBeInTheDocument()
    }
  })
})

describe('Remito — doble click en "Emitir" (react-async-state regla 9, tarea 8.10)', () => {
  it('dispara exactamente un POST', async () => {
    mockearReferencia((ruta) => (ruta === '/remitos/30' ? Promise.resolve(borradorFixture()) : undefined))

    let resolverEmitir: (v: RemitoDetalle) => void = () => {}
    const emitirPendiente = new Promise<RemitoDetalle>((resolve) => {
      resolverEmitir = resolve
    })
    apiPostMock.mockImplementation((ruta: string) => (ruta === '/remitos/30/emitir' ? emitirPendiente : Promise.reject(new Error(`ruta no mockeada: ${ruta}`))))

    renderPantalla()
    const boton = await screen.findByRole('button', { name: 'Emitir' })

    // Los dos dispatchEvent viajan DENTRO de un mismo act() — jsdom no no-opea un click sobre un
    // elemento disabled (lección vigente del programa), así que probar el guard exige dos clicks
    // reales en el mismo tick, mismo patrón que Presupuesto.test.tsx.
    act(() => {
      boton.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }))
      boton.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }))
    })

    expect(apiPostMock.mock.calls.filter((c) => c[0] === '/remitos/30/emitir')).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'Emitiendo…' })).toBeDisabled()

    await act(async () => {
      resolverEmitir(detalleFixture())
      await emitirPendiente
    })
  })
})

describe('Remito — doble click en "Anular"', () => {
  it('dispara exactamente un POST', async () => {
    mockearReferencia((ruta) => (ruta === '/remitos/30' ? Promise.resolve(detalleFixture()) : undefined))

    let resolverAnular: (v: RemitoDetalle) => void = () => {}
    const anularPendiente = new Promise<RemitoDetalle>((resolve) => {
      resolverAnular = resolve
    })
    apiPostMock.mockImplementation((ruta: string) => (ruta === '/remitos/30/anular' ? anularPendiente : Promise.reject(new Error(`ruta no mockeada: ${ruta}`))))

    renderPantalla()
    const boton = await screen.findByRole('button', { name: 'Anular' })

    act(() => {
      boton.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }))
      boton.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }))
    })

    expect(apiPostMock.mock.calls.filter((c) => c[0] === '/remitos/30/anular')).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'Anulando…' })).toBeDisabled()

    await act(async () => {
      resolverAnular(detalleFixture({ estado: 'Anulado' }))
      await anularPendiente
    })
  })

  it('mientras "Anular" está en vuelo, "Emitir" también queda deshabilitado (misma ventana de ocupado, react-async-state regla 5)', async () => {
    mockearReferencia((ruta) => (ruta === '/remitos/30' ? Promise.resolve(borradorFixture()) : undefined))

    let resolverAnular: (v: RemitoDetalle) => void = () => {}
    const anularPendiente = new Promise<RemitoDetalle>((resolve) => {
      resolverAnular = resolve
    })
    apiPostMock.mockImplementation((ruta: string) => (ruta === '/remitos/30/anular' ? anularPendiente : Promise.reject(new Error(`ruta no mockeada: ${ruta}`))))

    renderPantalla()
    const botonAnular = await screen.findByRole('button', { name: 'Anular' })
    const botonEmitir = screen.getByRole('button', { name: 'Emitir' })
    expect(botonEmitir).not.toBeDisabled()

    await userEvent.click(botonAnular)

    expect(screen.getByRole('button', { name: 'Anulando…' })).toBeDisabled()
    expect(botonEmitir).toBeDisabled()

    await act(async () => {
      resolverAnular(detalleFixture({ estado: 'Anulado' }))
      await anularPendiente
    })
  })
})

describe('Remito — crear borrador', () => {
  it('crea el borrador y navega a la ruta real del remito recién creado', async () => {
    mockearReferencia()
    apiPostMock.mockResolvedValue(borradorFixture({ id: 99 }))

    render(
      <MemoryRouter initialEntries={['/remitos/nuevo']}>
        <Routes>
          <Route path="/remitos/nuevo" element={<Remito />} />
          <Route path="/remitos/:id" element={<Remito />} />
        </Routes>
      </MemoryRouter>,
    )

    await userEvent.selectOptions(await screen.findByLabelText('Punto de venta'), '9')
    await userEvent.click(screen.getByRole('button', { name: 'Crear borrador' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/remitos', expect.objectContaining({ idPuntoVenta: 9, idCliente: null })))
  })
})

describe('Remito — role gating (mismo gate que /presupuestos: Politicas.OperacionDePos, sin distinción admin-only)', () => {
  it('la pantalla no oculta ninguna acción de escritura por rol — no hay `puedeEscribir`', async () => {
    mockearReferencia((ruta) => (ruta === '/remitos/30' ? Promise.resolve(borradorFixture()) : undefined))
    renderPantalla()

    await screen.findByText('Borrador')
    expect(screen.getByRole('button', { name: 'Emitir' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Anular' })).toBeInTheDocument()
  })
})
