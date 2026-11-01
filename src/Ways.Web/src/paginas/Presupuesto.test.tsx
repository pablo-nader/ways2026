import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Presupuesto } from './Presupuesto'
import { ErrorApi } from '../api/cliente'
import { Toaster } from '../toasts/Toaster'
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
    nombreTenant: 'Tenant Demo',
    razonSocialEmpresa: 'Empresa Demo',
    modo: 'Web',
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
      <Toaster />
    </MemoryRouter>,
  )
}

/** El resultado de una escritura se avisa con un toast de éxito, nunca con un banner. */
async function toastDeExito(texto: string) {
  const aviso = await screen.findByText(texto)
  expect(aviso.closest('.ws-toaster')).not.toBeNull()
  expect(aviso.closest('[role="status"]')).not.toBeNull()
  expect(document.body.querySelector('.alert-success')).toBeNull()
}

/** Un fallo de escritura queda en un banner junto a la acción: ni es un toast ni hay uno con su texto. */
async function bannerPersistente(texto: string) {
  const aviso = await screen.findByText(texto)
  expect(aviso.closest('.alert-danger')).not.toBeNull()
  expect(aviso.closest('.ws-toaster')).toBeNull()
  expect(screen.getAllByText(texto)).toHaveLength(1)
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
    expect(screen.getAllByText('$ 200,00').length).toBeGreaterThan(0)
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
        <Toaster />
      </MemoryRouter>,
    )

    // El `<select>` se renderiza vacío y deshabilitado hasta que llega la referencia
    // (`referenciaOk`): esperar solo al elemento deja a `selectOptions` corriendo contra un select
    // sin opciones — "Value 9 not found in options" en cuanto la máquina está cargada.
    const puntoVenta = await screen.findByLabelText('Punto de venta')
    await waitFor(() => expect(puntoVenta).toBeEnabled())

    await userEvent.selectOptions(puntoVenta, '9')
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
    await userEvent.type(inputVencimiento, '15/10/2026')

    await userEvent.click(screen.getByRole('button', { name: 'Enviar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/presupuestos/30/enviar', { vencimiento: '2026-10-15' }))
  })
})

describe('Presupuesto — Anular pide confirmación en un modal', () => {
  const RUTA_ANULAR = '/presupuestos/30/anular'

  function mockearDetalle() {
    mockearReferencia((ruta) => (ruta === '/presupuestos/30' ? Promise.resolve(detalleFixture()) : undefined))
  }

  async function abrirConfirmacion() {
    await userEvent.click(await screen.findByRole('button', { name: 'Anular' }))
    return screen.findByRole('alertdialog', { name: 'Confirmar anulación' })
  }

  it('abre un diálogo que nombra el presupuesto y no llama a la API hasta confirmar', async () => {
    mockearDetalle()
    renderPantalla()

    const dialogo = await abrirConfirmacion()

    expect(within(dialogo).getByText(/¿Anular el presupuesto 0009-00000012\?/)).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('Cancelar cierra el diálogo y no llama a la API', async () => {
    mockearDetalle()
    renderPantalla()

    const dialogo = await abrirConfirmacion()
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('doble click en confirmar dispara exactamente un POST y deja la pantalla inerte mientras está en vuelo', async () => {
    mockearDetalle()
    let resolverAnular: (v: PresupuestoDetalle) => void = () => {}
    const anularPendiente = new Promise<PresupuestoDetalle>((resolve) => {
      resolverAnular = resolve
    })
    apiPostMock.mockImplementation((ruta: string) => (ruta === RUTA_ANULAR ? anularPendiente : Promise.reject(new Error(`ruta no mockeada: ${ruta}`))))

    renderPantalla()
    const dialogo = await abrirConfirmacion()
    const confirmar = within(dialogo).getByRole('button', { name: 'Confirmar anulación' })

    act(() => {
      confirmar.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }))
      confirmar.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }))
    })

    expect(apiPostMock.mock.calls.filter((c) => c[0] === RUTA_ANULAR)).toHaveLength(1)
    expect(within(dialogo).getByRole('button', { name: 'Anulando…' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Convertir en venta' })).toBeDisabled()

    await act(async () => {
      resolverAnular(detalleFixture({ estado: 'Anulado', convertible: false }))
      await anularPendiente
    })
  })

  it('un rechazo muestra el motivo dentro del diálogo, que sigue abierto', async () => {
    mockearDetalle()
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'presupuesto_convertido', 'El presupuesto ya fue convertido.'))

    renderPantalla()
    const dialogo = await abrirConfirmacion()
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Confirmar anulación' }))

    expect(await within(dialogo).findByText('El presupuesto ya fue convertido.')).toBeInTheDocument()
    expect(screen.getByRole('alertdialog')).toBeInTheDocument()
  })

  it('al salir bien cierra el diálogo, avisa y relee el detalle', async () => {
    let anulado = false
    mockearReferencia((ruta) => (ruta === '/presupuestos/30' ? Promise.resolve(anulado ? detalleFixture({ estado: 'Anulado', convertible: false }) : detalleFixture()) : undefined))
    apiPostMock.mockImplementation(async () => {
      anulado = true
      return detalleFixture({ estado: 'Anulado', convertible: false })
    })

    renderPantalla()
    const dialogo = await abrirConfirmacion()
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Confirmar anulación' }))

    await toastDeExito('Presupuesto anulado.')
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(await screen.findByText('Anulado')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Anular' })).not.toBeInTheDocument()
  })
})

describe('Presupuesto — resultado de las escrituras', () => {
  it('enviar avisa con un toast de éxito', async () => {
    mockearReferencia((ruta) => (ruta === '/presupuestos/30' ? Promise.resolve(borradorFixture()) : undefined))
    apiPostMock.mockResolvedValue(detalleFixture())

    renderPantalla()
    await userEvent.click(await screen.findByRole('button', { name: 'Enviar' }))

    await toastDeExito('Presupuesto enviado.')
  })

  it('guardar el borrador avisa con un toast de éxito', async () => {
    mockearReferencia((ruta) => (ruta === '/presupuestos/30' ? Promise.resolve(borradorFixture()) : undefined))
    apiPutMock.mockResolvedValue(borradorFixture())

    renderPantalla()
    const boton = await screen.findByRole('button', { name: 'Guardar borrador' })
    await waitFor(() => expect(boton).not.toBeDisabled())
    await userEvent.click(boton)

    await toastDeExito('Borrador guardado.')
  })

  it('un fallo incierto al enviar queda en un banner persistente junto a la acción', async () => {
    mockearReferencia((ruta) => (ruta === '/presupuestos/30' ? Promise.resolve(borradorFixture()) : undefined))
    apiPostMock.mockRejectedValue(new ErrorApi(503, 'resultado_incierto', 'No sabemos si el presupuesto se envió.'))

    renderPantalla()
    await userEvent.click(await screen.findByRole('button', { name: 'Enviar' }))

    await bannerPersistente('No sabemos si el presupuesto se envió.')
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

describe('Presupuesto — cantidad según la unidad de venta', () => {
  it('un borrador reabierto resuelve la unidad de su artículo: por unidad pide enteros y marca una fracción', async () => {
    mockearReferencia((ruta) => {
      if (ruta === '/presupuestos/30') return Promise.resolve(borradorFixture())
      if (ruta === '/articulos/10') return Promise.resolve({ id: 10, unidadVenta: 'Unidad' })
      return undefined
    })
    renderPantalla()

    const cantidad = await screen.findByLabelText('Cantidad')
    await waitFor(() => expect(cantidad).toHaveAttribute('inputmode', 'numeric')
)

    fireEvent.change(cantidad, { target: { value: '0.5' } })

    expect(cantidad).toHaveClass('is-invalid')
    expect(screen.getByText('Este artículo se vende por unidad: la cantidad tiene que ser entera. No se va a guardar.')).toBeInTheDocument()
  })

  it('un artículo por peso, o uno cuya unidad no se pudo resolver, conserva el paso de 0.001', async () => {
    mockearReferencia((ruta) => {
      if (ruta === '/presupuestos/30') return Promise.resolve(borradorFixture())
      if (ruta === '/articulos/10') return Promise.resolve({ id: 10, unidadVenta: 'Peso' })
      return undefined
    })
    renderPantalla()

    const cantidad = await screen.findByLabelText('Cantidad')
    await waitFor(() => expect(apiGetMock).toHaveBeenCalledWith('/articulos/10'))
    fireEvent.change(cantidad, { target: { value: '0.5' } })

    expect(cantidad).toHaveAttribute('inputmode', 'decimal')
    expect(cantidad).not.toHaveClass('is-invalid')
  })
})

describe('Presupuesto — artículos no disponibles a la venta', () => {
  function renderNuevo() {
    return render(
      <MemoryRouter initialEntries={['/presupuestos/nuevo']}>
        <Routes>
          <Route path="/presupuestos/nuevo" element={<Presupuesto />} />
          <Route path="/presupuestos/:id" element={<Presupuesto />} />
        </Routes>
        <Toaster />
      </MemoryRouter>,
    )
  }

  async function elegirPuntoDeVenta(usuario: ReturnType<typeof userEvent.setup>) {
    const puntoVenta = await screen.findByLabelText('Punto de venta')
    await waitFor(() => expect(puntoVenta).toBeEnabled())
    await usuario.selectOptions(puntoVenta, '9')
  }

  /**
   * Cláusula bajo prueba: el buscador de líneas de un presupuesto pide `soloVendibles=true`. Mutación: llamar a
   * `listar` sin la opción hace fallar esta aserción.
   */
  it('el buscador de la línea pide soloVendibles=true', async () => {
    mockearReferencia((ruta) =>
      ruta.startsWith('/articulos?busqueda=') ? Promise.resolve({ items: [], total: 0, pagina: 1, tamanio: 25 }) : undefined,
    )
    const usuario = userEvent.setup()
    renderNuevo()
    await elegirPuntoDeVenta(usuario)

    await usuario.click(screen.getByRole('button', { name: /Agregar línea/ }))
    await usuario.type(screen.getByPlaceholderText('Buscar artículo…'), 'yerba')

    await waitFor(() => expect(apiGetMock).toHaveBeenCalledWith('/articulos?busqueda=yerba&soloVendibles=true'))
  })

  it('un 409 articulo_no_vendible al guardar se explica con el mensaje del servidor y qué hacer', async () => {
    mockearReferencia()
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'articulo_no_vendible', 'El artículo A0007 no está disponible a la venta.'))
    const usuario = userEvent.setup()
    renderNuevo()
    await elegirPuntoDeVenta(usuario)

    await usuario.click(screen.getByRole('button', { name: 'Crear borrador' }))

    await bannerPersistente('El artículo A0007 no está disponible a la venta. Sacalo para poder continuar.')
  })
})
