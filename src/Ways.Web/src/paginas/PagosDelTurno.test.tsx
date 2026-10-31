import { act, fireEvent, render as renderizar, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { ReactElement } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ErrorApi } from '../api/cliente'
import { pulsoDeCajon } from '../impresion/plantillas'
import { PagosDelTurno } from './PagosDelTurno'
import { Toaster } from '../toasts/Toaster'
import type { CajaDeEscritorio } from './Pos'
import type { AreaListado, DetalleDeTurno, PagoDeTurno, MedioPagoListado, OpcionDeProveedor, PuntoVentaListado, TurnoResumen } from '../api/tipos'
import type { EstadoDePuntoVenta } from '../puntoVenta/PuntoVentaContext'

/** Las pantallas avisan el resultado con toasts: el `Toaster` va siempre junto a la pantalla. */
function render(ui: ReactElement) {
  return renderizar(
    <>
      {ui}
      <Toaster />
    </>,
  )
}

/** Un aviso que no se puede perder queda en un banner de la pantalla: ni es un toast ni hay uno con su texto. */
async function bannerPersistente(texto: string) {
  const aviso = await screen.findByText(texto)
  expect(aviso.closest('.alert-danger')).not.toBeNull()
  expect(aviso.closest('.ws-toaster')).toBeNull()
  expect(screen.getAllByText(texto)).toHaveLength(1)
}

/** El resultado de una escritura se avisa con un toast de éxito, nunca con un banner. */
async function toastDeExito(texto: string) {
  const aviso = await screen.findByText(texto)
  expect(aviso.closest('[role="status"]')).not.toBeNull()
  expect(document.body.querySelector('.alert-success')).toBeNull()
}

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
      this.name = 'ErrorApi'
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
    modo: 'Web',
    ...sobrescribir,
  }
}

let estadoDePuntoVenta: EstadoDePuntoVenta
function estadoDePuntoVentaPorDefecto(): EstadoDePuntoVenta {
  const pv = puntoVentaFixture()
  return { puntosVenta: [pv], puntoVenta: pv, elegir: () => undefined, recargar: () => Promise.resolve() }
}

vi.mock('../puntoVenta/usePuntoVenta', () => ({
  usePuntoVenta: () => estadoDePuntoVenta,
}))

function turnoFixture(sobrescribir: Partial<TurnoResumen> = {}): TurnoResumen {
  return {
    id: 55,
    idPuntoVenta: 7,
    idEmpleadoApertura: 1,
    idEmpleadoCierre: null,
    fechaApertura: '2026-09-19T12:00:00Z',
    fechaCierre: null,
    fondoInicial: 0,
    estado: 'Abierto',
    observaciones: null,
    ...sobrescribir,
  }
}

const medioEfectivo: MedioPagoListado = {
  id: 1,
  nombre: 'Efectivo',
  activo: true,
  idEmpresa: null,
  orden: 1,
  comportamiento: 'Efectivo',
  admiteVuelto: true,
  requiereReferencia: false,
  recargoPorcentaje: null,
  porcentajeFacturacion: 0,
}

const medioCuentaCorriente: MedioPagoListado = {
  id: 2,
  nombre: 'Cuenta corriente',
  activo: true,
  idEmpresa: null,
  orden: 2,
  comportamiento: 'CuentaCorriente',
  admiteVuelto: false,
  requiereReferencia: false,
  recargoPorcentaje: null,
  porcentajeFacturacion: 0,
}

function proveedorFixture(sobrescribir: Partial<OpcionDeProveedor> = {}): OpcionDeProveedor {
  return {
    id: 1,
    razonSocial: 'Distribuidora Sur SRL',
    nombreFantasia: null,
    ...sobrescribir,
  }
}

function pagoFixture(sobrescribir: Partial<PagoDeTurno> = {}): PagoDeTurno {
  return {
    id: 1,
    idPuntoVenta: 7,
    fecha: '2026-09-19T15:30:00Z',
    categoria: 'Otros',
    idMedioPago: 1,
    importe: 300,
    origenFondos: 'CajaTurno',
    idTurnoCaja: 55,
    turnoAbierto: true,
    idProveedor: null,
    idArea: null,
    concepto: 'Flete',
    detalle: null,
    numeroFactura: null,
    idComprobanteCompra: null,
    idMovimientoPersonal: null,
    ...sobrescribir,
  }
}

function detalleFixture(pagos: PagoDeTurno[] = []): DetalleDeTurno {
  return {
    resumen: {
      idTurnoCaja: 55,
      idMedioAncla: 1,
      medios: [],
      cantidadTickets: 0,
      primerTicket: null,
      ultimoTicket: null,
      ingresosPorArea: [],
      egresos: { porCategoria: [], porArea: [], retiros: 0 },
    },
    tickets: [],
    gastos: pagos,
    fechaRecalculo: null,
    idEmpleadoRecalculo: null,
  }
}

function mockearRutas(opciones: {
  turno?: TurnoResumen | null
  medios?: MedioPagoListado[]
  proveedores?: OpcionDeProveedor[]
  detalle?: DetalleDeTurno
  errorTurno?: unknown
  errorMedios?: unknown
  errorProveedores?: unknown
  errorDetalle?: unknown
  areas?: AreaListado[]
}) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta.startsWith('/caja/turnos/abierto')) {
      if (opciones.errorTurno) return Promise.reject(opciones.errorTurno)
      return Promise.resolve(opciones.turno === undefined ? turnoFixture() : opciones.turno)
    }
    if (ruta === '/catalogos/medios-pago') {
      if (opciones.errorMedios) return Promise.reject(opciones.errorMedios)
      return Promise.resolve(opciones.medios ?? [medioEfectivo, medioCuentaCorriente])
    }
    if (ruta === '/catalogos/areas') return Promise.resolve(opciones.areas ?? [])
    if (ruta === '/proveedores/opciones') {
      if (opciones.errorProveedores) return Promise.reject(opciones.errorProveedores)
      return Promise.resolve(opciones.proveedores ?? [proveedorFixture()])
    }
    if (ruta.startsWith('/caja/turnos/') && ruta.endsWith('/detalle')) {
      if (opciones.errorDetalle) return Promise.reject(opciones.errorDetalle)
      return Promise.resolve(opciones.detalle ?? detalleFixture())
    }
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiPutMock.mockReset()
  estadoDePuntoVenta = estadoDePuntoVentaPorDefecto()
})

describe('PagosDelTurno — sin turno abierto', () => {
  it('muestra un aviso y no ofrece el formulario cuando el punto de venta no tiene turno abierto', async () => {
    mockearRutas({ turno: null })
    render(<PagosDelTurno />)

    await screen.findByText('No hay un turno abierto en este punto de venta.')
    expect(screen.queryByLabelText('Importe de la línea 1')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Registrar/ })).not.toBeInTheDocument()
  })
})

describe('PagosDelTurno — actualizar', () => {
  it('"Actualizar" es un botón de ícono con tooltip y vuelve a pedir el turno abierto', async () => {
    mockearRutas({})
    render(<PagosDelTurno />)
    const turnosPedidos = () => apiGetMock.mock.calls.filter(([ruta]) => String(ruta).startsWith('/caja/turnos/abierto')).length
    const boton = await screen.findByRole('button', { name: 'Actualizar' })
    await waitFor(() => expect(boton).toBeEnabled())
    expect(boton).toHaveAttribute('title', 'Actualizar')
    expect(screen.queryByRole('button', { name: 'Refrescar' })).not.toBeInTheDocument()
    const antes = turnosPedidos()

    await userEvent.click(boton)

    await waitFor(() => expect(turnosPedidos()).toBe(antes + 1))
  })
})

describe('PagosDelTurno — medio de pago', () => {
  it('excluye del selector los medios de comportamiento CuentaCorriente', async () => {
    mockearRutas({ medios: [medioEfectivo, medioCuentaCorriente] })
    render(<PagosDelTurno />)

    await screen.findByLabelText('Importe de la línea 1')
    expect(screen.getByRole('option', { name: 'Efectivo' })).toBeInTheDocument()
    expect(screen.queryByRole('option', { name: 'Cuenta corriente' })).not.toBeInTheDocument()
  })
})

describe('PagosDelTurno — categoría auto-switch', () => {
  it('elegir un proveedor cambia la categoría a Proveedores', async () => {
    mockearRutas({})
    render(<PagosDelTurno />)

    await screen.findByLabelText('Importe de la línea 1')
    expect(screen.getByLabelText('Categoría')).toHaveValue('Otros')

    await userEvent.selectOptions(screen.getByLabelText('Proveedor (opcional)'), 'Distribuidora Sur SRL')

    expect(screen.getByLabelText('Categoría')).toHaveValue('Proveedor')
  })

  it('limpiar el proveedor mientras la categoría sigue en Proveedores la vuelve a Otros', async () => {
    mockearRutas({})
    render(<PagosDelTurno />)

    await screen.findByLabelText('Importe de la línea 1')
    await userEvent.selectOptions(screen.getByLabelText('Proveedor (opcional)'), 'Distribuidora Sur SRL')
    expect(screen.getByLabelText('Categoría')).toHaveValue('Proveedor')

    await userEvent.selectOptions(screen.getByLabelText('Proveedor (opcional)'), 'Sin proveedor')

    expect(screen.getByLabelText('Categoría')).toHaveValue('Otros')
  })

  it('cambiar la categoría a mano después de elegir un proveedor y luego limpiarlo no la resetea', async () => {
    mockearRutas({})
    render(<PagosDelTurno />)

    await screen.findByLabelText('Importe de la línea 1')
    await userEvent.selectOptions(screen.getByLabelText('Proveedor (opcional)'), 'Distribuidora Sur SRL')
    await userEvent.selectOptions(screen.getByLabelText('Categoría'), 'Servicios')

    await userEvent.selectOptions(screen.getByLabelText('Proveedor (opcional)'), 'Sin proveedor')

    expect(screen.getByLabelText('Categoría')).toHaveValue('Servicios')
  })
})

describe('PagosDelTurno — pagado desde (origenFondos)', () => {
  it('por default el selector queda en "Caja del turno" y el POST manda origenFondos CajaTurno', async () => {
    mockearRutas({})
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    render(<PagosDelTurno />)

    await screen.findByLabelText('Importe de la línea 1')
    expect(screen.getByLabelText('Pagado desde')).toHaveValue('CajaTurno')

    await userEvent.type(screen.getByLabelText('Importe de la línea 1'), '500')
    await userEvent.selectOptions(screen.getByLabelText('Medio de la línea 1'), 'Efectivo')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await waitFor(() =>
      expect(apiPostMock).toHaveBeenCalledWith('/pagos', expect.objectContaining({ origenFondos: 'CajaTurno' })),
    )
  })

  it('elegir "Caja general" manda origenFondos Tesoreria', async () => {
    mockearRutas({})
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    render(<PagosDelTurno />)

    await screen.findByLabelText('Importe de la línea 1')
    await userEvent.type(screen.getByLabelText('Importe de la línea 1'), '500')
    await userEvent.selectOptions(screen.getByLabelText('Medio de la línea 1'), 'Efectivo')
    await userEvent.selectOptions(screen.getByLabelText('Pagado desde'), 'Caja general')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await waitFor(() =>
      expect(apiPostMock).toHaveBeenCalledWith('/pagos', expect.objectContaining({ origenFondos: 'Tesoreria' })),
    )
  })

  it('el selector "Pagado desde" queda deshabilitado mientras el alta está en vuelo', async () => {
    mockearRutas({})
    let resolverPost: (valor: unknown) => void = () => undefined
    apiPostMock.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolverPost = resolve
        }),
    )
    render(<PagosDelTurno />)

    await screen.findByLabelText('Importe de la línea 1')
    await userEvent.type(screen.getByLabelText('Importe de la línea 1'), '500')
    await userEvent.selectOptions(screen.getByLabelText('Medio de la línea 1'), 'Efectivo')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await waitFor(() => expect(screen.getByLabelText('Pagado desde')).toBeDisabled())
    resolverPost({ id: 1 })
  })
})

describe('PagosDelTurno — alta', () => {
  async function completarFormulario() {
    await screen.findByLabelText('Importe de la línea 1')
    await userEvent.type(screen.getByLabelText('Importe de la línea 1'), '500')
    await userEvent.selectOptions(screen.getByLabelText('Medio de la línea 1'), 'Efectivo')
  }

  it('arma el cuerpo del POST con el concepto de las observaciones recortadas', async () => {
    mockearRutas({})
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    render(<PagosDelTurno />)

    await completarFormulario()
    await userEvent.type(screen.getByLabelText('Observaciones (opcional)'), '  Flete de mercadería  ')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await waitFor(() =>
      expect(apiPostMock).toHaveBeenCalledWith(
        '/pagos',
        expect.objectContaining({
          idPuntoVenta: 7,
          lineas: [{ idMedioPago: 1, importe: 500, referencia: null }],
          categoria: 'Otros',
          idProveedor: null,
          concepto: 'Flete de mercadería',
          detalle: null,
          idArea: null,
          numeroFactura: null,
          idComprobanteCompra: null,
          origenFondos: 'CajaTurno',
        }),
      ),
    )
  })

  it('sin observaciones, el concepto cae al texto fijo "Pago del turno"', async () => {
    mockearRutas({})
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    render(<PagosDelTurno />)

    await completarFormulario()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await waitFor(() =>
      expect(apiPostMock).toHaveBeenCalledWith('/pagos', expect.objectContaining({ concepto: 'Pago del turno' })),
    )
  })

  it('al confirmar con éxito limpia el formulario, muestra la confirmación y refresca el listado', async () => {
    mockearRutas({ detalle: detalleFixture() })
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    render(<PagosDelTurno />)

    await completarFormulario()
    await userEvent.type(screen.getByLabelText('Observaciones (opcional)'), 'Pago de flete')

    // El refresco posterior al alta trae el pago recién creado.
    mockearRutas({ detalle: detalleFixture([pagoFixture({ id: 9, importe: 500 })]) })
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await toastDeExito('Pago registrado.')
    expect(screen.getByLabelText('Importe de la línea 1')).toHaveValue('')
    expect(screen.getByLabelText('Observaciones (opcional)')).toHaveValue('')
    expect(screen.getByLabelText('Categoría')).toHaveValue('Otros')
    await screen.findByText('$ 500,00')
  })

  it('si el detalle no se puede refrescar tras el alta, avisa el éxito con un toast y el fallo con el banner de carga persistente', async () => {
    mockearRutas({ detalle: detalleFixture() })
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    render(<PagosDelTurno />)

    await completarFormulario()
    const rutasOk = apiGetMock.getMockImplementation()!
    apiGetMock.mockImplementation((ruta: string) =>
      ruta.endsWith('/detalle') ? Promise.reject(new ErrorApi(503, 'no_disponible', 'El detalle no responde.')) : rutasOk(ruta),
    )
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await toastDeExito('Pago registrado.')
    await bannerPersistente('El detalle no responde.')
  })

  it('un error de carga previo no se pierde cuando el refresco posterior al alta también falla', async () => {
    mockearRutas({ errorDetalle: new ErrorApi(503, 'no_disponible', 'El detalle no responde.') })
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    render(<PagosDelTurno />)

    await bannerPersistente('El detalle no responde.')
    await completarFormulario()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await toastDeExito('Pago registrado.')
    await waitFor(() => expect(apiGetMock.mock.calls.filter((c) => (c[0] as string).endsWith('/detalle')).length).toBeGreaterThanOrEqual(2))
    await bannerPersistente('El detalle no responde.')
  })

  it('un error del servidor se avisa con un banner persistente y el formulario no se limpia', async () => {
    const { ErrorApi } = await import('../api/cliente')
    mockearRutas({})
    apiPostMock.mockRejectedValueOnce(new ErrorApi(409, 'turno_no_abierto', 'El turno ya no está abierto.'))
    render(<PagosDelTurno />)

    await completarFormulario()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await bannerPersistente('El turno ya no está abierto.')
    expect(screen.getByLabelText('Importe de la línea 1')).toHaveValue('500,00')
  })

  it('un 503 resultado_incierto del alta queda en un banner persistente, no en un toast que se va solo', async () => {
    mockearRutas({})
    apiPostMock.mockRejectedValueOnce(new ErrorApi(503, 'resultado_incierto', 'No sabemos si el pago se registró. Revisá el listado antes de reintentar.'))
    render(<PagosDelTurno />)

    await completarFormulario()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await bannerPersistente('No sabemos si el pago se registró. Revisá el listado antes de reintentar.')
    expect(screen.getByLabelText('Importe de la línea 1')).toHaveValue('500,00')
  })

  it('una falla de red del alta queda en el banner persistente con el texto genérico', async () => {
    mockearRutas({})
    apiPostMock.mockRejectedValueOnce(new Error('Failed to fetch'))
    render(<PagosDelTurno />)

    await completarFormulario()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await bannerPersistente('No se pudo registrar el pago.')
  })

  it('un importe en blanco o en cero se rechaza sin llegar a pegarle al servidor', async () => {
    mockearRutas({})
    render(<PagosDelTurno />)

    await screen.findByLabelText('Importe de la línea 1')
    await userEvent.selectOptions(screen.getByLabelText('Medio de la línea 1'), 'Efectivo')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await screen.findByText('Línea 1: ingresá un importe mayor a 0.')
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('un doble click sobre "Registrar" en el mismo tick dispara una sola solicitud (guarda de reentrancia)', async () => {
    mockearRutas({})
    let resolverPost: (valor: unknown) => void = () => undefined
    apiPostMock.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolverPost = resolve
        }),
    )
    render(<PagosDelTurno />)

    await completarFormulario()
    const boton = screen.getByRole('button', { name: 'Registrar' })
    fireEvent.click(boton)
    fireEvent.click(boton)

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    resolverPost({ id: 1 })
  })
})

describe('PagosDelTurno — efectivo en caja', () => {
  const medioTransferencia: MedioPagoListado = {
    ...medioEfectivo,
    id: 3,
    nombre: 'Transferencia',
    orden: 3,
    comportamiento: 'Electronico',
    admiteVuelto: false,
  }

  function detalleConEfectivo(importeEsperado: number): DetalleDeTurno {
    const base = detalleFixture()
    return { ...base, resumen: { ...base.resumen, idMedioAncla: 1, medios: [{ idMedioPago: 1, importeEsperado }] } }
  }

  function cajaDeEscritorioFixture(): CajaDeEscritorio {
    return {
      contexto: { empresa: 'Almacén Demo', puntoVenta: 'PV 7 — Local Centro', cajero: 'jperez' },
      encolarImpresion: vi.fn(),
    }
  }

  async function esperarDetalle() {
    await screen.findByLabelText('Importe de la línea 1')
    await screen.findByText('Este turno todavía no tiene pagos.')
  }

  function mockearEfectivo(disponible: number) {
    mockearRutas({ medios: [medioEfectivo, medioTransferencia], detalle: detalleConEfectivo(disponible) })
  }

  describe('efectivo disponible y faltante', () => {
    it('con Efectivo desde la caja del turno muestra el efectivo disponible del ancla', async () => {
      mockearEfectivo(400)
      render(<PagosDelTurno />)
      await esperarDetalle()

      await userEvent.selectOptions(screen.getByLabelText('Medio de la línea 1'), 'Efectivo')

      expect(screen.getByText('Disponible en caja: $ 400,00')).toBeInTheDocument()
    })

    it.each([
      ['un medio electrónico', 'Transferencia', 'Caja del turno'],
      ['Efectivo pagado desde Caja general', 'Efectivo', 'Caja general'],
    ])('no muestra disponible ni faltante con %s', async (_caso, medio, origen) => {
      mockearEfectivo(400)
      render(<PagosDelTurno />)
      await esperarDetalle()

      await userEvent.type(screen.getByLabelText('Importe de la línea 1'), '1000')
      await userEvent.selectOptions(screen.getByLabelText('Medio de la línea 1'), medio)
      await userEvent.selectOptions(screen.getByLabelText('Pagado desde'), origen)

      expect(screen.queryByText(/Disponible en caja/)).not.toBeInTheDocument()
      expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    })

    it('sin medio elegido no muestra disponible', async () => {
      mockearEfectivo(400)
      render(<PagosDelTurno />)
      await esperarDetalle()

      expect(screen.queryByText(/Disponible en caja/)).not.toBeInTheDocument()
    })

    it('si el importe supera el disponible avisa cuánto falta, sin bloquear el registro', async () => {
      mockearEfectivo(400)
      apiPostMock.mockResolvedValueOnce({ id: 1 })
      render(<PagosDelTurno />)
      await esperarDetalle()

      await userEvent.type(screen.getByLabelText('Importe de la línea 1'), '1000')
      await userEvent.selectOptions(screen.getByLabelText('Medio de la línea 1'), 'Efectivo')

      expect(screen.getByRole('alert')).toHaveTextContent('Falta $ 600,00 en la caja para pagar este pago.')
      expect(screen.getByRole('button', { name: 'Registrar' })).toBeEnabled()

      await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))
      await waitFor(() =>
        expect(apiPostMock).toHaveBeenCalledWith('/pagos', expect.objectContaining({ lineas: [{ idMedioPago: 1, importe: 1000, referencia: null }], origenFondos: 'CajaTurno' })),
      )
    })

    it('con un importe que no supera el disponible no hay aviso', async () => {
      mockearEfectivo(400)
      render(<PagosDelTurno />)
      await esperarDetalle()

      await userEvent.type(screen.getByLabelText('Importe de la línea 1'), '400')
      await userEvent.selectOptions(screen.getByLabelText('Medio de la línea 1'), 'Efectivo')

      expect(screen.getByText('Disponible en caja: $ 400,00')).toBeInTheDocument()
      expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    })
  })

  describe('apertura del cajón', () => {
    async function registrar(medio: string, origen?: string) {
      await esperarDetalle()
      await userEvent.type(screen.getByLabelText('Importe de la línea 1'), '100')
      await userEvent.selectOptions(screen.getByLabelText('Medio de la línea 1'), medio)
      if (origen) await userEvent.selectOptions(screen.getByLabelText('Pagado desde'), origen)
      await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))
    }

    it('un pago en Efectivo desde la caja del turno, ya registrado, encola el pulso del cajón', async () => {
      mockearEfectivo(400)
      apiPostMock.mockResolvedValueOnce({ id: 1 })
      const cajaDeEscritorio = cajaDeEscritorioFixture()
      render(<PagosDelTurno cajaDeEscritorio={cajaDeEscritorio} />)

      await registrar('Efectivo')

      await toastDeExito('Pago registrado.')
      expect(cajaDeEscritorio.encolarImpresion).toHaveBeenCalledTimes(1)
      expect(cajaDeEscritorio.encolarImpresion).toHaveBeenCalledWith('la apertura de cajón', pulsoDeCajon())
    })

    it('si el seam del cajón lanza, el alta ya confirmada no se informa como fallida, el formulario se limpia y hay un solo POST', async () => {
      mockearEfectivo(400)
      apiPostMock.mockResolvedValue({ id: 1 })
      const cajaDeEscritorio = cajaDeEscritorioFixture()
      vi.mocked(cajaDeEscritorio.encolarImpresion).mockImplementation(() => {
        throw new Error('cola caída')
      })
      render(<PagosDelTurno cajaDeEscritorio={cajaDeEscritorio} />)

      await registrar('Efectivo')

      await toastDeExito('Pago registrado.')
      expect(cajaDeEscritorio.encolarImpresion).toHaveBeenCalledTimes(1)
      expect(screen.queryByText('No se pudo registrar el pago.')).not.toBeInTheDocument()
      expect(screen.getByLabelText('Importe de la línea 1')).toHaveValue('')
      expect(apiPostMock).toHaveBeenCalledTimes(1)
    })

    it('el pulso se encola recién cuando el POST confirma', async () => {
      mockearEfectivo(400)
      let resolverPost: (valor: unknown) => void = () => undefined
      apiPostMock.mockImplementationOnce(
        () =>
          new Promise((resolve) => {
            resolverPost = resolve
          }),
      )
      const cajaDeEscritorio = cajaDeEscritorioFixture()
      render(<PagosDelTurno cajaDeEscritorio={cajaDeEscritorio} />)

      await registrar('Efectivo')
      await waitFor(() => expect(apiPostMock).toHaveBeenCalled())
      expect(cajaDeEscritorio.encolarImpresion).not.toHaveBeenCalled()

      await act(async () => resolverPost({ id: 1 }))
      expect(cajaDeEscritorio.encolarImpresion).toHaveBeenCalledTimes(1)
    })

    it('si el POST falla no encola el pulso', async () => {
      mockearEfectivo(400)
      apiPostMock.mockRejectedValueOnce(new ErrorApi(500, 'error', 'No se pudo registrar el pago.'))
      const cajaDeEscritorio = cajaDeEscritorioFixture()
      render(<PagosDelTurno cajaDeEscritorio={cajaDeEscritorio} />)

      await registrar('Efectivo')

      await bannerPersistente('No se pudo registrar el pago.')
      expect(cajaDeEscritorio.encolarImpresion).not.toHaveBeenCalled()
    })

    it.each([
      ['Efectivo pagado desde Caja general', 'Efectivo', 'Caja general'],
      ['un medio electrónico', 'Transferencia', undefined],
    ])('con %s no encola el pulso', async (_caso, medio, origen) => {
      mockearEfectivo(400)
      apiPostMock.mockResolvedValueOnce({ id: 1 })
      const cajaDeEscritorio = cajaDeEscritorioFixture()
      render(<PagosDelTurno cajaDeEscritorio={cajaDeEscritorio} />)

      await registrar(medio, origen)

      await toastDeExito('Pago registrado.')
      expect(cajaDeEscritorio.encolarImpresion).not.toHaveBeenCalled()
    })

    it('sin el seam de escritorio registra igual y no falla', async () => {
      mockearEfectivo(400)
      apiPostMock.mockResolvedValueOnce({ id: 1 })
      render(<PagosDelTurno />)

      await registrar('Efectivo')

      await toastDeExito('Pago registrado.')
    })
  })
})

describe('PagosDelTurno — área del alta', () => {
  const general = { id: 10, nombre: 'General', activo: true, idEmpresa: null, orden: 1 }
  const cocina = { id: 11, nombre: 'Cocina', activo: true, idEmpresa: null, orden: 2 }

  async function esperarAreas() {
    await screen.findByLabelText('Importe de la línea 1')
    await screen.findByRole('option', { name: 'General' })
  }

  async function completar() {
    await userEvent.type(screen.getByLabelText('Importe de la línea 1'), '500')
    await userEvent.selectOptions(screen.getByLabelText('Medio de la línea 1'), 'Efectivo')
  }

  it('el select arranca en la primera área activa, sin opción vacía, y el alta la manda', async () => {
    mockearRutas({ areas: [general, cocina] })
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    render(<PagosDelTurno />)
    await esperarAreas()

    const select = screen.getByLabelText('Área')
    expect(select).toHaveValue('10')
    expect(within(select).getAllByRole('option').map((o) => o.textContent)).toEqual(['General', 'Cocina'])

    await completar()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/pagos', expect.objectContaining({ idArea: 10 })))
  })

  it('el área elegida a mano es la que viaja', async () => {
    mockearRutas({ areas: [general, cocina] })
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    render(<PagosDelTurno />)
    await esperarAreas()

    await userEvent.selectOptions(screen.getByLabelText('Área'), 'Cocina')
    await completar()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/pagos', expect.objectContaining({ idArea: 11 })))
  })

  it('tras un alta exitoso el formulario vuelve a la primera área activa', async () => {
    mockearRutas({ areas: [general, cocina] })
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    render(<PagosDelTurno />)
    await esperarAreas()

    await userEvent.selectOptions(screen.getByLabelText('Área'), 'Cocina')
    await completar()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await toastDeExito('Pago registrado.')
    expect(screen.getByLabelText('Área')).toHaveValue('10')
  })

  it('si las áreas llegan después de abrir el formulario, el default se completa cuando aterrizan', async () => {
    mockearRutas({})
    const baseline = apiGetMock.getMockImplementation()!
    let entregarAreas: (lista: AreaListado[]) => void = () => undefined
    apiGetMock.mockImplementation((ruta: string) =>
      ruta === '/catalogos/areas'
        ? new Promise<AreaListado[]>((resolve) => {
            entregarAreas = resolve
          })
        : baseline(ruta),
    )
    render(<PagosDelTurno />)
    await screen.findByLabelText('Importe de la línea 1')
    expect(screen.getByLabelText('Área')).toHaveValue('')

    await act(async () => entregarAreas([general, cocina]))

    expect(screen.getByLabelText('Área')).toHaveValue('10')
  })

  it('con las áreas pendientes "Registrar" queda deshabilitado; al llegar se habilita y el alta lleva el área por defecto', async () => {
    mockearRutas({})
    const baseline = apiGetMock.getMockImplementation()!
    let entregarAreas: (lista: AreaListado[]) => void = () => undefined
    apiGetMock.mockImplementation((ruta: string) =>
      ruta === '/catalogos/areas'
        ? new Promise<AreaListado[]>((resolve) => {
            entregarAreas = resolve
          })
        : baseline(ruta),
    )
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    render(<PagosDelTurno />)
    await screen.findByLabelText('Importe de la línea 1')
    expect(screen.getByRole('button', { name: 'Registrar' })).toBeDisabled()

    await act(async () => entregarAreas([general, cocina]))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Registrar' })).toBeEnabled())

    await completar()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/pagos', expect.objectContaining({ idArea: 10 })))
  })

  it('si las áreas no cargan avisa que el pago se registra sin área y deja registrar', async () => {
    mockearRutas({})
    const baseline = apiGetMock.getMockImplementation()!
    apiGetMock.mockImplementation((ruta: string) =>
      ruta === '/catalogos/areas' ? Promise.reject(new ErrorApi(500, 'error', 'boom')) : baseline(ruta),
    )
    render(<PagosDelTurno />)

    expect(await screen.findByText('No se pudieron cargar las áreas: los pagos se registran sin área.')).toBeInTheDocument()
    await waitFor(() => expect(screen.getByRole('button', { name: 'Registrar' })).toBeEnabled())
  })

  it('sin áreas el select muestra una única opción vacía deshabilitada y el alta manda idArea null', async () => {
    mockearRutas({ areas: [] })
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    render(<PagosDelTurno />)
    await screen.findByLabelText('Importe de la línea 1')
    await screen.findByText('Este turno todavía no tiene pagos.')

    const select = screen.getByLabelText('Área')
    const opciones = within(select).getAllByRole('option')
    expect(opciones).toHaveLength(1)
    expect(opciones[0]).toBeDisabled()
    expect(select).toHaveValue('')

    await completar()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/pagos', expect.objectContaining({ idArea: null })))
  })
})

describe('PagosDelTurno — listado', () => {
  it('lista los pagos del turno con hora, categoría, medio y el total', async () => {
    mockearRutas({
      medios: [medioEfectivo],
      detalle: detalleFixture([
        pagoFixture({ id: 1, categoria: 'Servicios', idMedioPago: 1, importe: 300 }),
        pagoFixture({ id: 2, categoria: 'Viaticos', idMedioPago: 1, importe: 150 }),
      ]),
    })
    render(<PagosDelTurno />)

    const tabla = await screen.findByRole('table')
    expect(within(tabla).getByText('Servicios')).toBeInTheDocument()
    expect(within(tabla).getByText('Viáticos')).toBeInTheDocument()
    expect(within(tabla).getAllByText('Efectivo').length).toBe(2)
    expect(within(tabla).getByText('$ 300,00')).toBeInTheDocument()
    expect(within(tabla).getByText('$ 150,00')).toBeInTheDocument()
    expect(screen.getByText('Total: $ 450,00')).toBeInTheDocument()
  })

  it('sin pagos, muestra el mensaje de lista vacía y el total en 0', async () => {
    mockearRutas({ detalle: detalleFixture([]) })
    render(<PagosDelTurno />)

    await screen.findByText('Este turno todavía no tiene pagos.')
    // El editor de líneas muestra su propio total (el del pago por cargar) además del total del listado.
    expect(screen.getAllByText('Total: $ 0,00')).toHaveLength(2)
  })

  it('un pago de origen Tesoreria se etiqueta "Caja general"; uno CajaTurno no lleva etiqueta', async () => {
    mockearRutas({
      medios: [medioEfectivo],
      detalle: detalleFixture([
        pagoFixture({ id: 1, categoria: 'Servicios', importe: 300, origenFondos: 'CajaTurno' }),
        pagoFixture({ id: 2, categoria: 'Viaticos', importe: 150, origenFondos: 'Tesoreria' }),
      ]),
    })
    render(<PagosDelTurno />)

    const tabla = await screen.findByRole('table')
    const filaCajaTurno = within(tabla).getByRole('row', { name: /Servicios/ })
    expect(within(filaCajaTurno).queryByText('Caja general')).not.toBeInTheDocument()

    const filaTesoreria = within(tabla).getByRole('row', { name: /Viáticos/ })
    expect(within(filaTesoreria).getByText('Caja general')).toBeInTheDocument()
  })
})

describe('PagosDelTurno — edición', () => {
  const areaCocina: AreaListado = { id: 3, nombre: 'Cocina', activo: true, idEmpresa: null } as AreaListado

  function dialogo() {
    return screen.getByRole('dialog', { name: 'Editar pago' })
  }

  async function abrirEdicion(nombre = 'Editar pago Flete') {
    // web-test-data-gates: la fila llega con el detalle (el dato), no antes.
    await userEvent.click(await screen.findByRole('button', { name: nombre }))
    await screen.findByRole('dialog', { name: 'Editar pago' })
  }

  it('no ofrece editar un pago cuyo turno ya no está abierto', async () => {
    mockearRutas({ detalle: detalleFixture([pagoFixture({ turnoAbierto: false })]) })
    render(<PagosDelTurno />)

    // Se espera a la fila (el DATO del detalle) antes de afirmar que no hay acción.
    await screen.findByRole('cell', { name: '$ 300,00' })
    expect(screen.queryByRole('button', { name: /Editar pago/ })).not.toBeInTheDocument()
  })

  it('no ofrece editar un pago de un pago de personal aunque su turno siga abierto, y sí el de un pago común', async () => {
    mockearRutas({
      detalle: detalleFixture([
        pagoFixture({ id: 1, concepto: 'Pago de personal: Ana', importe: 300, idMovimientoPersonal: 9 }),
        pagoFixture({ id: 2, concepto: 'Flete', importe: 450 }),
      ]),
    })
    render(<PagosDelTurno />)

    // Se espera a las dos filas (el DATO del detalle) antes de afirmar que falta una acción.
    await screen.findByRole('cell', { name: '$ 300,00' })
    await screen.findByRole('cell', { name: '$ 450,00' })
    expect(screen.queryByRole('button', { name: 'Editar pago Pago de personal: Ana' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Editar pago Flete' })).toBeEnabled()
  })

  it('precarga el formulario con los valores del pago', async () => {
    mockearRutas({
      detalle: detalleFixture([pagoFixture({ importe: 300, idArea: 3, detalle: 'Entrega', numeroFactura: '0001-9', idProveedor: 1, categoria: 'Proveedor' })]),
      areas: [areaCocina],
    })
    render(<PagosDelTurno />)

    await abrirEdicion()
    const d = within(dialogo())
    await waitFor(() => expect(d.getByLabelText('Área (opcional)')).toHaveValue('3'))
    expect(d.getByLabelText('Importe de la línea 1')).toHaveValue('300,00')
    expect(d.getByLabelText('Medio de la línea 1')).toHaveValue('1')
    expect(d.getByLabelText('Categoría')).toHaveValue('Proveedor')
    expect(d.getByLabelText('Proveedor (opcional)')).toHaveValue('1')
    expect(d.getByLabelText('Concepto')).toHaveValue('Flete')
    expect(d.getByLabelText('Detalle (opcional)')).toHaveValue('Entrega')
    expect(d.getByLabelText('N° de factura (opcional)')).toHaveValue('0001-9')
  })

  it('un pago ligado a una compra abre la edición con categoría y proveedor bloqueados', async () => {
    mockearRutas({
      detalle: detalleFixture([pagoFixture({ categoria: 'Proveedor', idProveedor: 1, idComprobanteCompra: 40 })]),
    })
    render(<PagosDelTurno />)

    await abrirEdicion()
    const d = within(dialogo())
    expect(d.getByLabelText('Categoría')).toBeDisabled()
    expect(d.getByLabelText('Proveedor (opcional)')).toBeDisabled()
    expect(d.getByLabelText('Concepto')).toBeEnabled()
  })

  it('un pago sin compra abre la edición con categoría y proveedor habilitados', async () => {
    mockearRutas({ detalle: detalleFixture([pagoFixture({ idComprobanteCompra: null })]) })
    render(<PagosDelTurno />)

    await abrirEdicion()
    const d = within(dialogo())
    expect(d.getByLabelText('Categoría')).toBeEnabled()
    expect(d.getByLabelText('Proveedor (opcional)')).toBeEnabled()
  })

  it('guarda con PUT /pagos/{id}, cierra el modal, avisa y refresca el detalle', async () => {
    mockearRutas({ detalle: detalleFixture([pagoFixture({ id: 12 })]) })
    apiPutMock.mockResolvedValueOnce({ id: 12 })
    render(<PagosDelTurno />)

    await abrirEdicion()
    const d = within(dialogo())
    await userEvent.clear(d.getByLabelText('Concepto'))
    await userEvent.type(d.getByLabelText('Concepto'), '  Flete corregido ')
    await userEvent.click(d.getByRole('button', { name: 'Guardar cambios' }))

    await waitFor(() =>
      expect(apiPutMock).toHaveBeenCalledWith(
        '/pagos/12',
        expect.objectContaining({
          concepto: 'Flete corregido',
          lineas: [{ idMedioPago: 1, importe: 300, referencia: null }],
          categoria: 'Otros',
          idProveedor: null,
          idArea: null,
        }),
      ),
    )
    await toastDeExito('Pago actualizado.')
    expect(screen.queryByRole('dialog', { name: 'Editar pago' })).not.toBeInTheDocument()
    const llamadasDetalle = apiGetMock.mock.calls.filter((c) => (c[0] as string).endsWith('/detalle'))
    expect(llamadasDetalle.length).toBeGreaterThanOrEqual(2)
  })

  it('un 409 gasto_turno_cerrado deja el modal abierto con el motivo y refresca el detalle', async () => {
    mockearRutas({ detalle: detalleFixture([pagoFixture({ id: 12 })]) })
    const { ErrorApi } = await import('../api/cliente')
    apiPutMock.mockRejectedValueOnce(new ErrorApi(409, 'gasto_turno_cerrado', 'cerrado'))
    render(<PagosDelTurno />)

    await abrirEdicion()
    const antes = apiGetMock.mock.calls.filter((c) => (c[0] as string).endsWith('/detalle')).length
    await userEvent.click(within(dialogo()).getByRole('button', { name: 'Guardar cambios' }))

    await within(dialogo()).findByText(/ya está cerrado/)
    await waitFor(() =>
      expect(apiGetMock.mock.calls.filter((c) => (c[0] as string).endsWith('/detalle')).length).toBeGreaterThan(antes),
    )
    expect(screen.queryByText('Pago actualizado.')).not.toBeInTheDocument()
  })

  it('un importe en cero se rechaza sin llegar al servidor', async () => {
    mockearRutas({ detalle: detalleFixture([pagoFixture()]) })
    render(<PagosDelTurno />)

    await abrirEdicion()
    const d = within(dialogo())
    await userEvent.clear(d.getByLabelText('Importe de la línea 1'))
    await userEvent.click(d.getByRole('button', { name: 'Guardar cambios' }))

    await d.findByText('Línea 1: ingresá un importe mayor a 0.')
    expect(apiPutMock).not.toHaveBeenCalled()
  })

  it('mientras el guardado está en vuelo el modal queda inerte y un doble click manda una sola solicitud', async () => {
    mockearRutas({ detalle: detalleFixture([pagoFixture({ id: 12 })]) })
    let resolver: (v: unknown) => void = () => undefined
    apiPutMock.mockImplementationOnce(() => new Promise((r) => (resolver = r)))
    render(<PagosDelTurno />)

    await abrirEdicion()
    const guardar = within(dialogo()).getByRole('button', { name: 'Guardar cambios' })
    act(() => {
      guardar.click()
      guardar.click()
    })

    await waitFor(() => expect(within(dialogo()).getByLabelText('Concepto')).toBeDisabled())
    expect(within(dialogo()).getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(apiPutMock).toHaveBeenCalledTimes(1)
    await act(async () => {
      resolver({ id: 12 })
    })
  })

  it('cancelar cierra el modal sin escribir', async () => {
    mockearRutas({ detalle: detalleFixture([pagoFixture()]) })
    render(<PagosDelTurno />)

    await abrirEdicion()
    await userEvent.click(within(dialogo()).getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('dialog', { name: 'Editar pago' })).not.toBeInTheDocument()
    expect(apiPutMock).not.toHaveBeenCalled()
  })
})

describe('PagosDelTurno — orden del listado', () => {
  const GASTOS = [
    pagoFixture({ id: 1, categoria: 'Servicios', importe: 300, fecha: '2026-09-19T15:00:00Z' }),
    pagoFixture({ id: 2, categoria: 'Viaticos', importe: 1500, fecha: '2026-09-19T09:00:00Z' }),
    pagoFixture({ id: 3, categoria: 'Otros', importe: 20, fecha: '2026-09-19T12:00:00Z' }),
  ]

  const categorias = (tabla: HTMLElement) =>
    Array.from(tabla.querySelectorAll('tbody tr')).map((fila) => fila.querySelectorAll('td')[1]?.textContent)

  it('el importe ordena por su valor numérico y el total no depende del orden', async () => {
    mockearRutas({ medios: [medioEfectivo], detalle: detalleFixture(GASTOS) })
    render(<PagosDelTurno />)
    const tabla = await screen.findByRole('table')
    await within(tabla).findByText('$ 1.500,00')

    const importe = within(tabla).getByRole('columnheader', { name: /Importe/ })
    await userEvent.click(within(importe).getByRole('button'))

    expect(importe).toHaveAttribute('aria-sort', 'ascending')
    expect(categorias(tabla).map((c) => c?.trim())).toEqual(['Otros', 'Servicios', 'Viáticos'])
    expect(screen.getByText('Total: $ 1.820,00')).toBeInTheDocument()
  })

  it('la hora ordena cronológicamente y el tercer clic vuelve al orden del servidor', async () => {
    mockearRutas({ medios: [medioEfectivo], detalle: detalleFixture(GASTOS) })
    render(<PagosDelTurno />)
    const tabla = await screen.findByRole('table')
    await within(tabla).findByText('$ 1.500,00')

    const hora = within(tabla).getByRole('columnheader', { name: /Hora/ })
    await userEvent.click(within(hora).getByRole('button'))
    expect(categorias(tabla).map((c) => c?.trim())).toEqual(['Viáticos', 'Otros', 'Servicios'])

    await userEvent.click(within(hora).getByRole('button'))
    await userEvent.click(within(hora).getByRole('button'))
    expect(hora).toHaveAttribute('aria-sort', 'none')
    expect(categorias(tabla).map((c) => c?.trim())).toEqual(['Servicios', 'Viáticos', 'Otros'])
  })
})

describe('PagosDelTurno — varios medios', () => {
  const medioTransferencia: MedioPagoListado = {
    ...medioEfectivo,
    id: 3,
    nombre: 'Transferencia',
    orden: 3,
    comportamiento: 'Electronico',
    admiteVuelto: false,
  }

  function detalleConEfectivo(importeEsperado: number, pagos: PagoDeTurno[] = []): DetalleDeTurno {
    const base = detalleFixture(pagos)
    return { ...base, resumen: { ...base.resumen, idMedioAncla: 1, medios: [{ idMedioPago: 1, importeEsperado }] } }
  }

  function pagoConDosMedios(sobrescribir: Partial<PagoDeTurno> = {}): PagoDeTurno {
    return pagoFixture({
      importe: 1000,
      lineas: [
        { idItem: 1, idMedioPago: 1, nombreMedioPago: 'Efectivo', importe: 600, referencia: null },
        { idItem: 2, idMedioPago: 3, nombreMedioPago: 'Transferencia', importe: 400, referencia: 'op 55' },
      ],
      ...sobrescribir,
    })
  }

  function cajaDeEscritorioFixture(): CajaDeEscritorio {
    return {
      contexto: { empresa: 'Almacén Demo', puntoVenta: 'PV 7 — Local Centro', cajero: 'jperez' },
      encolarImpresion: vi.fn(),
    }
  }

  async function cargarDosLineas(efectivo: string, transferencia: string) {
    await screen.findByLabelText('Importe de la línea 1')
    await screen.findByText('Este turno todavía no tiene pagos.')
    await userEvent.type(screen.getByLabelText('Importe de la línea 1'), efectivo)
    await userEvent.selectOptions(screen.getByLabelText('Medio de la línea 1'), 'Efectivo')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar medio' }))
    if (transferencia !== '') await userEvent.type(screen.getByLabelText('Importe de la línea 2'), transferencia)
    await userEvent.selectOptions(screen.getByLabelText('Medio de la línea 2'), 'Transferencia')
  }

  it('el alta con dos líneas manda POST /pagos con una línea por medio y su referencia', async () => {
    mockearRutas({ medios: [medioEfectivo, medioTransferencia], detalle: detalleConEfectivo(5000) })
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    render(<PagosDelTurno />)

    await cargarDosLineas('600', '400')
    await userEvent.type(screen.getByLabelText('Referencia de la línea 2'), ' op 55 ')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await waitFor(() =>
      expect(apiPostMock).toHaveBeenCalledWith(
        '/pagos',
        expect.objectContaining({
          lineas: [
            { idMedioPago: 1, importe: 600, referencia: null },
            { idMedioPago: 3, importe: 400, referencia: 'op 55' },
          ],
        }),
      ),
    )
    await toastDeExito('Pago registrado.')
    expect(screen.queryByLabelText('Importe de la línea 2')).not.toBeInTheDocument()
  })

  it('una línea incompleta se rechaza con su número, sin llegar al servidor', async () => {
    mockearRutas({ medios: [medioEfectivo, medioTransferencia], detalle: detalleConEfectivo(5000) })
    render(<PagosDelTurno />)

    await cargarDosLineas('600', '')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await screen.findByText('Línea 2: ingresá un importe mayor a 0.')
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('el editor no ofrece cuenta corriente y admite hasta 20 medios', async () => {
    mockearRutas({ medios: [medioEfectivo, medioCuentaCorriente, medioTransferencia] })
    render(<PagosDelTurno />)

    await screen.findByLabelText('Importe de la línea 1')
    await screen.findByRole('option', { name: 'Transferencia' })
    const select = screen.getByLabelText('Medio de la línea 1')
    expect(within(select).queryByRole('option', { name: 'Cuenta corriente' })).not.toBeInTheDocument()
    for (let i = 0; i < 19; i += 1) {
      await userEvent.click(screen.getByRole('button', { name: 'Agregar medio' }))
    }
    expect(screen.getAllByRole('group', { name: /^Línea \d+$/ })).toHaveLength(20)
    expect(screen.getByRole('button', { name: 'Agregar medio' })).toBeDisabled()
  })

  it('el faltante del cajón cuenta solo las líneas en efectivo, no el total del pago', async () => {
    mockearRutas({ medios: [medioEfectivo, medioTransferencia], detalle: detalleConEfectivo(400) })
    render(<PagosDelTurno />)

    await cargarDosLineas('500', '700')

    expect(screen.getByText('Disponible en caja: $ 400,00')).toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent('Falta $ 100,00 en la caja para pagar este pago.')
  })

  it('con el efectivo cubierto no hay faltante aunque el total del pago supere el disponible', async () => {
    mockearRutas({ medios: [medioEfectivo, medioTransferencia], detalle: detalleConEfectivo(400) })
    render(<PagosDelTurno />)

    await cargarDosLineas('300', '700')

    expect(screen.getByText('Disponible en caja: $ 400,00')).toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('un pago solo con medios electrónicos no muestra el efectivo del cajón ni encola el pulso', async () => {
    mockearRutas({ medios: [medioEfectivo, medioTransferencia], detalle: detalleConEfectivo(400) })
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    const cajaDeEscritorio = cajaDeEscritorioFixture()
    render(<PagosDelTurno cajaDeEscritorio={cajaDeEscritorio} />)

    await screen.findByText('Este turno todavía no tiene pagos.')
    await userEvent.type(screen.getByLabelText('Importe de la línea 1'), '900')
    await userEvent.selectOptions(screen.getByLabelText('Medio de la línea 1'), 'Transferencia')
    expect(screen.queryByText(/Disponible en caja/)).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await toastDeExito('Pago registrado.')
    expect(cajaDeEscritorio.encolarImpresion).not.toHaveBeenCalled()
  })

  it('un pago con una línea en efectivo y otra electrónica encola el pulso del cajón', async () => {
    mockearRutas({ medios: [medioEfectivo, medioTransferencia], detalle: detalleConEfectivo(5000) })
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    const cajaDeEscritorio = cajaDeEscritorioFixture()
    render(<PagosDelTurno cajaDeEscritorio={cajaDeEscritorio} />)

    await cargarDosLineas('600', '400')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await toastDeExito('Pago registrado.')
    expect(cajaDeEscritorio.encolarImpresion).toHaveBeenCalledWith('la apertura de cajón', pulsoDeCajon())
  })

  it('el listado muestra el reparto de un pago con varios medios y el medio de siempre en uno con uno solo', async () => {
    mockearRutas({
      medios: [medioEfectivo, medioTransferencia],
      detalle: detalleFixture([pagoConDosMedios({ id: 1, concepto: 'Flete' }), pagoFixture({ id: 2, importe: 150, concepto: 'Luz' })]),
    })
    render(<PagosDelTurno />)

    const tabla = await screen.findByRole('table')
    expect(await within(tabla).findByText('Efectivo $ 600,00 · Transferencia $ 400,00')).toBeInTheDocument()
    expect(within(tabla).getByRole('cell', { name: 'Efectivo' })).toBeInTheDocument()
  })

  it('edita un pago con varios medios: precarga las líneas y guarda con PUT /pagos/{id}', async () => {
    mockearRutas({
      medios: [medioEfectivo, medioTransferencia],
      detalle: detalleFixture([pagoConDosMedios({ id: 12 })]),
    })
    apiPutMock.mockResolvedValueOnce({ id: 12 })
    render(<PagosDelTurno />)

    await userEvent.click(await screen.findByRole('button', { name: 'Editar pago Flete' }))
    const d = within(await screen.findByRole('dialog', { name: 'Editar pago' }))
    expect(d.getByLabelText('Importe de la línea 1')).toHaveValue('600,00')
    expect(d.getByLabelText('Medio de la línea 2')).toHaveValue('3')
    expect(d.getByLabelText('Referencia de la línea 2')).toHaveValue('op 55')
    expect(d.queryByText(/se edita desde el POS nuevo/)).not.toBeInTheDocument()

    await userEvent.clear(d.getByLabelText('Importe de la línea 2'))
    await userEvent.type(d.getByLabelText('Importe de la línea 2'), '350')
    await userEvent.click(d.getByRole('button', { name: 'Guardar cambios' }))

    await waitFor(() =>
      expect(apiPutMock).toHaveBeenCalledWith(
        '/pagos/12',
        expect.objectContaining({
          lineas: [
            { idMedioPago: 1, importe: 600, referencia: null },
            { idMedioPago: 3, importe: 350, referencia: 'op 55' },
          ],
        }),
      ),
    )
    await toastDeExito('Pago actualizado.')
  })

  it('en la edición se puede agregar un medio a un pago de un solo medio', async () => {
    mockearRutas({
      medios: [medioEfectivo, medioTransferencia],
      detalle: detalleFixture([pagoFixture({ id: 12, importe: 300 })]),
    })
    apiPutMock.mockResolvedValueOnce({ id: 12 })
    render(<PagosDelTurno />)

    await userEvent.click(await screen.findByRole('button', { name: 'Editar pago Flete' }))
    const d = within(await screen.findByRole('dialog', { name: 'Editar pago' }))
    await userEvent.click(d.getByRole('button', { name: 'Agregar medio' }))
    await userEvent.type(d.getByLabelText('Importe de la línea 2'), '100')
    await userEvent.selectOptions(d.getByLabelText('Medio de la línea 2'), 'Transferencia')
    await userEvent.click(d.getByRole('button', { name: 'Guardar cambios' }))

    await waitFor(() =>
      expect(apiPutMock).toHaveBeenCalledWith(
        '/pagos/12',
        expect.objectContaining({
          lineas: [
            { idMedioPago: 1, importe: 300, referencia: null },
            { idMedioPago: 3, importe: 100, referencia: null },
          ],
        }),
      ),
    )
  })
})
