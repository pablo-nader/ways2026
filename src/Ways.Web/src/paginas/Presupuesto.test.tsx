import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Presupuesto } from './Presupuesto'
import type { ClienteListado, PresupuestoDetalle, PuntoVentaListado } from '../api/tipos'

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

function detalleFixture(sobrescribir: Partial<PresupuestoDetalle> = {}): PresupuestoDetalle {
  return {
    id: 30,
    idPuntoVenta: 9,
    idCliente: 5,
    idEmpleado: 1,
    numero: 12,
    numeroFormateado: '0009-00000012',
    fechaEmision: '2026-08-19T12:00:00Z',
    fechaEnvio: '2026-08-19T12:00:00Z',
    vencimiento: '2026-09-30',
    vencido: false,
    convertible: true,
    zonaId: 'America/Argentina/Buenos_Aires',
    observaciones: null,
    subtotal: 200,
    descuentoTotal: 0,
    total: 200,
    estado: 'Enviado',
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
      },
    ],
    ...sobrescribir,
  }
}

function borradorFixture(sobrescribir: Partial<PresupuestoDetalle> = {}): PresupuestoDetalle {
  return detalleFixture({ estado: 'Borrador', numero: null, numeroFormateado: null, fechaEnvio: null, vencimiento: null, convertible: false, ...sobrescribir })
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
    <MemoryRouter initialEntries={[`/presupuestos/${id}`]}>
      <Routes>
        <Route path="/presupuestos/:id" element={<Presupuesto />} />
      </Routes>
    </MemoryRouter>,
  )
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiPutMock.mockReset()
})

describe('Presupuesto — detalle (lectura)', () => {
  it('muestra el estado, el badge de vencimiento y los items con sus valores', async () => {
    mockearReferencia((ruta) => (ruta === '/presupuestos/30' ? Promise.resolve(detalleFixture()) : undefined))
    renderPantalla()

    expect(await screen.findByText('Enviado')).toBeInTheDocument()
    expect(screen.getByText(/Vence/)).toBeInTheDocument()
    expect(screen.getByText('Yerba mate 1kg')).toBeInTheDocument()
    expect(screen.getAllByText('$200,00').length).toBeGreaterThan(0)
  })

  it('un presupuesto vencido muestra el badge "Venció"', async () => {
    mockearReferencia((ruta) => (ruta === '/presupuestos/30' ? Promise.resolve(detalleFixture({ vencido: true, convertible: false })) : undefined))
    renderPantalla()

    expect(await screen.findByText(/Venció/)).toBeInTheDocument()
  })

  it('convertido en venta muestra el link a la venta', async () => {
    mockearReferencia((ruta) => (ruta === '/presupuestos/30' ? Promise.resolve(detalleFixture({ estado: 'Convertido', idComprobanteVenta: 77, convertible: false })) : undefined))
    renderPantalla()

    expect(await screen.findByText('Convertido en venta #77')).toBeInTheDocument()
  })
})

describe('Presupuesto — "Convertir en venta" (design decisión 4, tarea 7.8)', () => {
  it('renderiza SOLO cuando el servidor reporta Convertible: true', async () => {
    mockearReferencia((ruta) => (ruta === '/presupuestos/30' ? Promise.resolve(detalleFixture({ convertible: true })) : undefined))
    renderPantalla()

    expect(await screen.findByRole('button', { name: 'Convertir en venta' })).toBeInTheDocument()
  })

  it('un presupuesto Enviado pero NO convertible (p. ej. vencido) no ofrece la acción', async () => {
    mockearReferencia((ruta) => (ruta === '/presupuestos/30' ? Promise.resolve(detalleFixture({ vencido: true, convertible: false })) : undefined))
    renderPantalla()

    await screen.findByText('Enviado')
    expect(screen.queryByRole('button', { name: 'Convertir en venta' })).not.toBeInTheDocument()
  })

  it('un presupuesto Convertido no ofrece la acción (terminal — decisión 9)', async () => {
    mockearReferencia((ruta) => (ruta === '/presupuestos/30' ? Promise.resolve(detalleFixture({ estado: 'Convertido', idComprobanteVenta: 77, convertible: false })) : undefined))
    renderPantalla()

    await screen.findByText('Convertido')
    expect(screen.queryByRole('button', { name: 'Convertir en venta' })).not.toBeInTheDocument()
  })
})

describe('Presupuesto — crear borrador', () => {
  it('crea el borrador y navega a la ruta real del presupuesto recién creado', async () => {
    mockearReferencia()
    apiPostMock.mockResolvedValue(borradorFixture({ id: 99 }))

    render(
      <MemoryRouter initialEntries={['/presupuestos/nuevo']}>
        <Routes>
          <Route path="/presupuestos/nuevo" element={<Presupuesto />} />
          <Route path="/presupuestos/:id" element={<Presupuesto />} />
        </Routes>
      </MemoryRouter>,
    )

    await userEvent.selectOptions(await screen.findByLabelText('Punto de venta'), '9')
    await userEvent.click(screen.getByRole('button', { name: 'Crear borrador' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/presupuestos', expect.objectContaining({ idPuntoVenta: 9, idCliente: null })))
  })
})

describe('Presupuesto — doble click en "Enviar" (react-async-state regla 9, tarea 7.9)', () => {
  it('dispara exactamente un POST', async () => {
    mockearReferencia((ruta) => (ruta === '/presupuestos/30' ? Promise.resolve(borradorFixture()) : undefined))

    let resolverEnviar: (v: PresupuestoDetalle) => void = () => {}
    const enviarPendiente = new Promise<PresupuestoDetalle>((resolve) => {
      resolverEnviar = resolve
    })
    apiPostMock.mockImplementation((ruta: string) => (ruta === '/presupuestos/30/enviar' ? enviarPendiente : Promise.reject(new Error(`ruta no mockeada: ${ruta}`))))

    renderPantalla()
    const boton = await screen.findByRole('button', { name: 'Enviar' })

    // Los dos dispatchEvent viajan DENTRO de un mismo act() — jsdom no no-opea un click sobre un
    // elemento disabled (lección vigente del programa), así que probar el guard exige dos clicks
    // reales en el mismo tick, mismo patrón que OrdenDeCompra.test.tsx.
    act(() => {
      boton.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }))
      boton.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }))
    })

    expect(apiPostMock.mock.calls.filter((c) => c[0] === '/presupuestos/30/enviar')).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'Enviando…' })).toBeDisabled()

    await act(async () => {
      resolverEnviar(detalleFixture())
      await enviarPendiente
    })
  })

  it('manda el vencimiento tipeado por el operador', async () => {
    mockearReferencia((ruta) => (ruta === '/presupuestos/30' ? Promise.resolve(borradorFixture()) : undefined))
    apiPostMock.mockImplementation((ruta: string) => (ruta === '/presupuestos/30/enviar' ? Promise.resolve(detalleFixture()) : Promise.reject(new Error(`ruta no mockeada: ${ruta}`))))

    renderPantalla()
    const inputVencimiento = await screen.findByLabelText('Vence el')
    await userEvent.clear(inputVencimiento)
    await userEvent.type(inputVencimiento, '2026-10-15')

    await userEvent.click(screen.getByRole('button', { name: 'Enviar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/presupuestos/30/enviar', { vencimiento: '2026-10-15' }))
  })
})

describe('Presupuesto — doble click en "Anular"', () => {
  it('dispara exactamente un POST', async () => {
    mockearReferencia((ruta) => (ruta === '/presupuestos/30' ? Promise.resolve(detalleFixture()) : undefined))

    let resolverAnular: (v: PresupuestoDetalle) => void = () => {}
    const anularPendiente = new Promise<PresupuestoDetalle>((resolve) => {
      resolverAnular = resolve
    })
    apiPostMock.mockImplementation((ruta: string) => (ruta === '/presupuestos/30/anular' ? anularPendiente : Promise.reject(new Error(`ruta no mockeada: ${ruta}`))))

    renderPantalla()
    const boton = await screen.findByRole('button', { name: 'Anular' })

    act(() => {
      boton.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }))
      boton.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }))
    })

    expect(apiPostMock.mock.calls.filter((c) => c[0] === '/presupuestos/30/anular')).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'Anulando…' })).toBeDisabled()

    await act(async () => {
      resolverAnular(detalleFixture({ estado: 'Anulado', convertible: false }))
      await anularPendiente
    })
  })

  it('mientras "Anular" está en vuelo, "Convertir en venta" también queda deshabilitado (misma ventana de ocupado, judgment-day slice-7 ronda 2)', async () => {
    mockearReferencia((ruta) => (ruta === '/presupuestos/30' ? Promise.resolve(detalleFixture()) : undefined))

    let resolverAnular: (v: PresupuestoDetalle) => void = () => {}
    const anularPendiente = new Promise<PresupuestoDetalle>((resolve) => {
      resolverAnular = resolve
    })
    apiPostMock.mockImplementation((ruta: string) => (ruta === '/presupuestos/30/anular' ? anularPendiente : Promise.reject(new Error(`ruta no mockeada: ${ruta}`))))

    renderPantalla()
    const botonAnular = await screen.findByRole('button', { name: 'Anular' })
    const botonConvertir = screen.getByRole('button', { name: 'Convertir en venta' })
    expect(botonConvertir).not.toBeDisabled()

    await userEvent.click(botonAnular)

    expect(screen.getByRole('button', { name: 'Anulando…' })).toBeDisabled()
    expect(botonConvertir).toBeDisabled()

    await act(async () => {
      resolverAnular(detalleFixture({ estado: 'Anulado', convertible: false }))
      await anularPendiente
    })
  })
})

describe('Presupuesto — role gating (mismo gate que /pos: Politicas.OperacionDePos, sin distinción admin-only)', () => {
  it('la pantalla no oculta ninguna acción de escritura por rol — no hay `puedeEscribir` (design decisión 17)', async () => {
    mockearReferencia((ruta) => (ruta === '/presupuestos/30' ? Promise.resolve(detalleFixture({ convertible: true })) : undefined))
    renderPantalla()

    await screen.findByText('Enviado')
    expect(screen.getByRole('button', { name: 'Convertir en venta' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Anular' })).toBeInTheDocument()
  })
})
