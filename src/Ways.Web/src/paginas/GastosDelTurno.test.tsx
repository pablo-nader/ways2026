import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ErrorApi } from '../api/cliente'
import { pulsoDeCajon } from '../impresion/plantillas'
import { GastosDelTurno } from './GastosDelTurno'
import type { CajaDeEscritorio } from './Pos'
import type { AreaListado, DetalleDeTurno, GastoDeTurno, MedioPagoListado, OpcionDeProveedor, PuntoVentaListado, TurnoResumen } from '../api/tipos'
import type { EstadoDePuntoVenta } from '../puntoVenta/PuntoVentaContext'

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
}

function proveedorFixture(sobrescribir: Partial<OpcionDeProveedor> = {}): OpcionDeProveedor {
  return {
    id: 1,
    razonSocial: 'Distribuidora Sur SRL',
    nombreFantasia: null,
    ...sobrescribir,
  }
}

function gastoFixture(sobrescribir: Partial<GastoDeTurno> = {}): GastoDeTurno {
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
    ...sobrescribir,
  }
}

function detalleFixture(gastos: GastoDeTurno[] = []): DetalleDeTurno {
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
    gastos,
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

describe('GastosDelTurno — sin turno abierto', () => {
  it('muestra un aviso y no ofrece el formulario cuando el punto de venta no tiene turno abierto', async () => {
    mockearRutas({ turno: null })
    render(<GastosDelTurno />)

    await screen.findByText('No hay un turno abierto en este punto de venta.')
    expect(screen.queryByLabelText('Importe')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Registrar/ })).not.toBeInTheDocument()
  })
})

describe('GastosDelTurno — medio de pago', () => {
  it('excluye del selector los medios de comportamiento CuentaCorriente', async () => {
    mockearRutas({ medios: [medioEfectivo, medioCuentaCorriente] })
    render(<GastosDelTurno />)

    await screen.findByLabelText('Importe')
    expect(screen.getByRole('option', { name: 'Efectivo' })).toBeInTheDocument()
    expect(screen.queryByRole('option', { name: 'Cuenta corriente' })).not.toBeInTheDocument()
  })
})

describe('GastosDelTurno — categoría auto-switch', () => {
  it('elegir un proveedor cambia la categoría a Proveedores', async () => {
    mockearRutas({})
    render(<GastosDelTurno />)

    await screen.findByLabelText('Importe')
    expect(screen.getByLabelText('Categoría')).toHaveValue('Otros')

    await userEvent.selectOptions(screen.getByLabelText('Proveedor (opcional)'), 'Distribuidora Sur SRL')

    expect(screen.getByLabelText('Categoría')).toHaveValue('Proveedor')
  })

  it('limpiar el proveedor mientras la categoría sigue en Proveedores la vuelve a Otros', async () => {
    mockearRutas({})
    render(<GastosDelTurno />)

    await screen.findByLabelText('Importe')
    await userEvent.selectOptions(screen.getByLabelText('Proveedor (opcional)'), 'Distribuidora Sur SRL')
    expect(screen.getByLabelText('Categoría')).toHaveValue('Proveedor')

    await userEvent.selectOptions(screen.getByLabelText('Proveedor (opcional)'), 'Sin proveedor')

    expect(screen.getByLabelText('Categoría')).toHaveValue('Otros')
  })

  it('cambiar la categoría a mano después de elegir un proveedor y luego limpiarlo no la resetea', async () => {
    mockearRutas({})
    render(<GastosDelTurno />)

    await screen.findByLabelText('Importe')
    await userEvent.selectOptions(screen.getByLabelText('Proveedor (opcional)'), 'Distribuidora Sur SRL')
    await userEvent.selectOptions(screen.getByLabelText('Categoría'), 'Servicios')

    await userEvent.selectOptions(screen.getByLabelText('Proveedor (opcional)'), 'Sin proveedor')

    expect(screen.getByLabelText('Categoría')).toHaveValue('Servicios')
  })
})

describe('GastosDelTurno — pagado desde (origenFondos)', () => {
  it('por default el selector queda en "Caja del turno" y el POST manda origenFondos CajaTurno', async () => {
    mockearRutas({})
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    render(<GastosDelTurno />)

    await screen.findByLabelText('Importe')
    expect(screen.getByLabelText('Pagado desde')).toHaveValue('CajaTurno')

    await userEvent.type(screen.getByLabelText('Importe'), '500')
    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), 'Efectivo')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await waitFor(() =>
      expect(apiPostMock).toHaveBeenCalledWith('/gastos', expect.objectContaining({ origenFondos: 'CajaTurno' })),
    )
  })

  it('elegir "Caja general" manda origenFondos Tesoreria', async () => {
    mockearRutas({})
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    render(<GastosDelTurno />)

    await screen.findByLabelText('Importe')
    await userEvent.type(screen.getByLabelText('Importe'), '500')
    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), 'Efectivo')
    await userEvent.selectOptions(screen.getByLabelText('Pagado desde'), 'Caja general')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await waitFor(() =>
      expect(apiPostMock).toHaveBeenCalledWith('/gastos', expect.objectContaining({ origenFondos: 'Tesoreria' })),
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
    render(<GastosDelTurno />)

    await screen.findByLabelText('Importe')
    await userEvent.type(screen.getByLabelText('Importe'), '500')
    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), 'Efectivo')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await waitFor(() => expect(screen.getByLabelText('Pagado desde')).toBeDisabled())
    resolverPost({ id: 1 })
  })
})

describe('GastosDelTurno — alta', () => {
  async function completarFormulario() {
    await screen.findByLabelText('Importe')
    await userEvent.type(screen.getByLabelText('Importe'), '500')
    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), 'Efectivo')
  }

  it('arma el cuerpo del POST con el concepto de las observaciones recortadas', async () => {
    mockearRutas({})
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    render(<GastosDelTurno />)

    await completarFormulario()
    await userEvent.type(screen.getByLabelText('Observaciones (opcional)'), '  Flete de mercadería  ')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await waitFor(() =>
      expect(apiPostMock).toHaveBeenCalledWith(
        '/gastos',
        expect.objectContaining({
          idPuntoVenta: 7,
          importe: 500,
          idMedioPago: 1,
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

  it('sin observaciones, el concepto cae al texto fijo "Gasto del turno"', async () => {
    mockearRutas({})
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    render(<GastosDelTurno />)

    await completarFormulario()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await waitFor(() =>
      expect(apiPostMock).toHaveBeenCalledWith('/gastos', expect.objectContaining({ concepto: 'Gasto del turno' })),
    )
  })

  it('al confirmar con éxito limpia el formulario, muestra la confirmación y refresca el listado', async () => {
    mockearRutas({ detalle: detalleFixture() })
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    render(<GastosDelTurno />)

    await completarFormulario()
    await userEvent.type(screen.getByLabelText('Observaciones (opcional)'), 'Pago de flete')

    // El refresco posterior al alta trae el gasto recién creado.
    mockearRutas({ detalle: detalleFixture([gastoFixture({ id: 9, importe: 500 })]) })
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await screen.findByText('Gasto registrado.')
    expect(screen.getByLabelText('Importe')).toHaveValue('')
    expect(screen.getByLabelText('Observaciones (opcional)')).toHaveValue('')
    expect(screen.getByLabelText('Categoría')).toHaveValue('Otros')
    await screen.findByText('$ 500,00')
  })

  // RDD-W1 (judgment-day): cláusula bajo prueba: el `setAviso('')` movido ANTES de las
  // validaciones client-side en `registrarGasto`. Mutación verificada (mutation-proof-tests):
  // devolviendo ese `setAviso('')` a después de los `return` de validación, este test vuelve a
  // ver "Gasto registrado." conviviendo con el error — restaurado el orden, vuelve a verde.
  it('un segundo submit con el formulario vacío después de un alta exitosa limpia el aviso de éxito', async () => {
    mockearRutas({ detalle: detalleFixture() })
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    render(<GastosDelTurno />)

    await completarFormulario()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))
    await screen.findByText('Gasto registrado.')

    // `limpiarFormulario` dejó el importe en blanco: este segundo submit falla la validación
    // client-side, sin llegar a pegarle al servidor.
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await screen.findByText('El importe tiene que ser mayor a 0.')
    expect(screen.queryByText('Gasto registrado.')).not.toBeInTheDocument()
  })

  it('un error del servidor se muestra inline y el formulario no se limpia', async () => {
    const { ErrorApi } = await import('../api/cliente')
    mockearRutas({})
    apiPostMock.mockRejectedValueOnce(new ErrorApi(409, 'turno_no_abierto', 'El turno ya no está abierto.'))
    render(<GastosDelTurno />)

    await completarFormulario()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await screen.findByText('El turno ya no está abierto.')
    expect(screen.getByLabelText('Importe')).toHaveValue('500,00')
  })

  it('un importe en blanco o en cero se rechaza sin llegar a pegarle al servidor', async () => {
    mockearRutas({})
    render(<GastosDelTurno />)

    await screen.findByLabelText('Importe')
    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), 'Efectivo')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await screen.findByText('El importe tiene que ser mayor a 0.')
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
    render(<GastosDelTurno />)

    await completarFormulario()
    const boton = screen.getByRole('button', { name: 'Registrar' })
    fireEvent.click(boton)
    fireEvent.click(boton)

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    resolverPost({ id: 1 })
  })
})

describe('GastosDelTurno — efectivo en caja', () => {
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
    await screen.findByLabelText('Importe')
    await screen.findByText('Este turno todavía no tiene gastos.')
  }

  function mockearEfectivo(disponible: number) {
    mockearRutas({ medios: [medioEfectivo, medioTransferencia], detalle: detalleConEfectivo(disponible) })
  }

  describe('efectivo disponible y faltante', () => {
    it('con Efectivo desde la caja del turno muestra el efectivo disponible del ancla', async () => {
      mockearEfectivo(400)
      render(<GastosDelTurno />)
      await esperarDetalle()

      await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), 'Efectivo')

      expect(screen.getByText('Disponible en caja: $ 400,00')).toBeInTheDocument()
    })

    it.each([
      ['un medio electrónico', 'Transferencia', 'Caja del turno'],
      ['Efectivo pagado desde Caja general', 'Efectivo', 'Caja general'],
    ])('no muestra disponible ni faltante con %s', async (_caso, medio, origen) => {
      mockearEfectivo(400)
      render(<GastosDelTurno />)
      await esperarDetalle()

      await userEvent.type(screen.getByLabelText('Importe'), '1000')
      await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), medio)
      await userEvent.selectOptions(screen.getByLabelText('Pagado desde'), origen)

      expect(screen.queryByText(/Disponible en caja/)).not.toBeInTheDocument()
      expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    })

    it('sin medio elegido no muestra disponible', async () => {
      mockearEfectivo(400)
      render(<GastosDelTurno />)
      await esperarDetalle()

      expect(screen.queryByText(/Disponible en caja/)).not.toBeInTheDocument()
    })

    it('si el importe supera el disponible avisa cuánto falta, sin bloquear el registro', async () => {
      mockearEfectivo(400)
      apiPostMock.mockResolvedValueOnce({ id: 1 })
      render(<GastosDelTurno />)
      await esperarDetalle()

      await userEvent.type(screen.getByLabelText('Importe'), '1000')
      await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), 'Efectivo')

      expect(screen.getByRole('alert')).toHaveTextContent('Falta $ 600,00 en la caja para pagar este gasto.')
      expect(screen.getByRole('button', { name: 'Registrar' })).toBeEnabled()

      await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))
      await waitFor(() =>
        expect(apiPostMock).toHaveBeenCalledWith('/gastos', expect.objectContaining({ importe: 1000, origenFondos: 'CajaTurno' })),
      )
    })

    it('con un importe que no supera el disponible no hay aviso', async () => {
      mockearEfectivo(400)
      render(<GastosDelTurno />)
      await esperarDetalle()

      await userEvent.type(screen.getByLabelText('Importe'), '400')
      await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), 'Efectivo')

      expect(screen.getByText('Disponible en caja: $ 400,00')).toBeInTheDocument()
      expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    })
  })

  describe('apertura del cajón', () => {
    async function registrar(medio: string, origen?: string) {
      await esperarDetalle()
      await userEvent.type(screen.getByLabelText('Importe'), '100')
      await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), medio)
      if (origen) await userEvent.selectOptions(screen.getByLabelText('Pagado desde'), origen)
      await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))
    }

    it('un gasto en Efectivo desde la caja del turno, ya registrado, encola el pulso del cajón', async () => {
      mockearEfectivo(400)
      apiPostMock.mockResolvedValueOnce({ id: 1 })
      const cajaDeEscritorio = cajaDeEscritorioFixture()
      render(<GastosDelTurno cajaDeEscritorio={cajaDeEscritorio} />)

      await registrar('Efectivo')

      await screen.findByText('Gasto registrado.')
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
    render(<GastosDelTurno cajaDeEscritorio={cajaDeEscritorio} />)

    await registrar('Efectivo')

    await screen.findByText('Gasto registrado.')
    expect(screen.queryByText('No se pudo registrar el gasto.')).not.toBeInTheDocument()
    expect(screen.getByLabelText('Importe')).toHaveValue('')
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
      render(<GastosDelTurno cajaDeEscritorio={cajaDeEscritorio} />)

      await registrar('Efectivo')
      await waitFor(() => expect(apiPostMock).toHaveBeenCalled())
      expect(cajaDeEscritorio.encolarImpresion).not.toHaveBeenCalled()

      await act(async () => resolverPost({ id: 1 }))
      expect(cajaDeEscritorio.encolarImpresion).toHaveBeenCalledTimes(1)
    })

    it('si el POST falla no encola el pulso', async () => {
      mockearEfectivo(400)
      apiPostMock.mockRejectedValueOnce(new ErrorApi(500, 'error', 'No se pudo registrar el gasto.'))
      const cajaDeEscritorio = cajaDeEscritorioFixture()
      render(<GastosDelTurno cajaDeEscritorio={cajaDeEscritorio} />)

      await registrar('Efectivo')

      await screen.findByText('No se pudo registrar el gasto.')
      expect(cajaDeEscritorio.encolarImpresion).not.toHaveBeenCalled()
    })

    it.each([
      ['Efectivo pagado desde Caja general', 'Efectivo', 'Caja general'],
      ['un medio electrónico', 'Transferencia', undefined],
    ])('con %s no encola el pulso', async (_caso, medio, origen) => {
      mockearEfectivo(400)
      apiPostMock.mockResolvedValueOnce({ id: 1 })
      const cajaDeEscritorio = cajaDeEscritorioFixture()
      render(<GastosDelTurno cajaDeEscritorio={cajaDeEscritorio} />)

      await registrar(medio, origen)

      await screen.findByText('Gasto registrado.')
      expect(cajaDeEscritorio.encolarImpresion).not.toHaveBeenCalled()
    })

    it('sin el seam de escritorio registra igual y no falla', async () => {
      mockearEfectivo(400)
      apiPostMock.mockResolvedValueOnce({ id: 1 })
      render(<GastosDelTurno />)

      await registrar('Efectivo')

      expect(await screen.findByText('Gasto registrado.')).toBeInTheDocument()
    })
  })
})

describe('GastosDelTurno — área del alta', () => {
  const general = { id: 10, nombre: 'General', activo: true, idEmpresa: null, orden: 1 }
  const cocina = { id: 11, nombre: 'Cocina', activo: true, idEmpresa: null, orden: 2 }

  async function esperarAreas() {
    await screen.findByLabelText('Importe')
    await screen.findByRole('option', { name: 'General' })
  }

  async function completar() {
    await userEvent.type(screen.getByLabelText('Importe'), '500')
    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), 'Efectivo')
  }

  it('el select arranca en la primera área activa, sin opción vacía, y el alta la manda', async () => {
    mockearRutas({ areas: [general, cocina] })
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    render(<GastosDelTurno />)
    await esperarAreas()

    const select = screen.getByLabelText('Área')
    expect(select).toHaveValue('10')
    expect(within(select).getAllByRole('option').map((o) => o.textContent)).toEqual(['General', 'Cocina'])

    await completar()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/gastos', expect.objectContaining({ idArea: 10 })))
  })

  it('el área elegida a mano es la que viaja', async () => {
    mockearRutas({ areas: [general, cocina] })
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    render(<GastosDelTurno />)
    await esperarAreas()

    await userEvent.selectOptions(screen.getByLabelText('Área'), 'Cocina')
    await completar()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/gastos', expect.objectContaining({ idArea: 11 })))
  })

  it('tras un alta exitoso el formulario vuelve a la primera área activa', async () => {
    mockearRutas({ areas: [general, cocina] })
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    render(<GastosDelTurno />)
    await esperarAreas()

    await userEvent.selectOptions(screen.getByLabelText('Área'), 'Cocina')
    await completar()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await screen.findByText('Gasto registrado.')
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
    render(<GastosDelTurno />)
    await screen.findByLabelText('Importe')
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
    render(<GastosDelTurno />)
    await screen.findByLabelText('Importe')
    expect(screen.getByRole('button', { name: 'Registrar' })).toBeDisabled()

    await act(async () => entregarAreas([general, cocina]))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Registrar' })).toBeEnabled())

    await completar()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/gastos', expect.objectContaining({ idArea: 10 })))
  })

  it('si las áreas no cargan avisa que el gasto se registra sin área y deja registrar', async () => {
    mockearRutas({})
    const baseline = apiGetMock.getMockImplementation()!
    apiGetMock.mockImplementation((ruta: string) =>
      ruta === '/catalogos/areas' ? Promise.reject(new ErrorApi(500, 'error', 'boom')) : baseline(ruta),
    )
    render(<GastosDelTurno />)

    expect(await screen.findByText('No se pudieron cargar las áreas: los gastos se registran sin área.')).toBeInTheDocument()
    await waitFor(() => expect(screen.getByRole('button', { name: 'Registrar' })).toBeEnabled())
  })

  it('sin áreas el select muestra una única opción vacía deshabilitada y el alta manda idArea null', async () => {
    mockearRutas({ areas: [] })
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    render(<GastosDelTurno />)
    await screen.findByLabelText('Importe')
    await screen.findByText('Este turno todavía no tiene gastos.')

    const select = screen.getByLabelText('Área')
    const opciones = within(select).getAllByRole('option')
    expect(opciones).toHaveLength(1)
    expect(opciones[0]).toBeDisabled()
    expect(select).toHaveValue('')

    await completar()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/gastos', expect.objectContaining({ idArea: null })))
  })
})

describe('GastosDelTurno — listado', () => {
  it('lista los gastos del turno con hora, categoría, medio y el total', async () => {
    mockearRutas({
      medios: [medioEfectivo],
      detalle: detalleFixture([
        gastoFixture({ id: 1, categoria: 'Servicios', idMedioPago: 1, importe: 300 }),
        gastoFixture({ id: 2, categoria: 'Viaticos', idMedioPago: 1, importe: 150 }),
      ]),
    })
    render(<GastosDelTurno />)

    const tabla = await screen.findByRole('table')
    expect(within(tabla).getByText('Servicios')).toBeInTheDocument()
    expect(within(tabla).getByText('Viáticos')).toBeInTheDocument()
    expect(within(tabla).getAllByText('Efectivo').length).toBe(2)
    expect(within(tabla).getByText('$ 300,00')).toBeInTheDocument()
    expect(within(tabla).getByText('$ 150,00')).toBeInTheDocument()
    expect(screen.getByText('Total: $ 450,00')).toBeInTheDocument()
  })

  it('sin gastos, muestra el mensaje de lista vacía y el total en 0', async () => {
    mockearRutas({ detalle: detalleFixture([]) })
    render(<GastosDelTurno />)

    await screen.findByText('Este turno todavía no tiene gastos.')
    expect(screen.getByText('Total: $ 0,00')).toBeInTheDocument()
  })

  it('un gasto de origen Tesoreria se etiqueta "Caja general"; uno CajaTurno no lleva etiqueta', async () => {
    mockearRutas({
      medios: [medioEfectivo],
      detalle: detalleFixture([
        gastoFixture({ id: 1, categoria: 'Servicios', importe: 300, origenFondos: 'CajaTurno' }),
        gastoFixture({ id: 2, categoria: 'Viaticos', importe: 150, origenFondos: 'Tesoreria' }),
      ]),
    })
    render(<GastosDelTurno />)

    const tabla = await screen.findByRole('table')
    const filaCajaTurno = within(tabla).getByRole('row', { name: /Servicios/ })
    expect(within(filaCajaTurno).queryByText('Caja general')).not.toBeInTheDocument()

    const filaTesoreria = within(tabla).getByRole('row', { name: /Viáticos/ })
    expect(within(filaTesoreria).getByText('Caja general')).toBeInTheDocument()
  })
})

describe('GastosDelTurno — edición', () => {
  const areaCocina: AreaListado = { id: 3, nombre: 'Cocina', activo: true, idEmpresa: null } as AreaListado

  function dialogo() {
    return screen.getByRole('dialog', { name: 'Editar gasto' })
  }

  async function abrirEdicion(nombre = 'Editar gasto Flete') {
    // web-test-data-gates: la fila llega con el detalle (el dato), no antes.
    await userEvent.click(await screen.findByRole('button', { name: nombre }))
    await screen.findByRole('dialog', { name: 'Editar gasto' })
  }

  it('no ofrece editar un gasto cuyo turno ya no está abierto', async () => {
    mockearRutas({ detalle: detalleFixture([gastoFixture({ turnoAbierto: false })]) })
    render(<GastosDelTurno />)

    // Se espera a la fila (el DATO del detalle) antes de afirmar que no hay acción.
    await screen.findByRole('cell', { name: '$ 300,00' })
    expect(screen.queryByRole('button', { name: /Editar gasto/ })).not.toBeInTheDocument()
  })

  it('precarga el formulario con los valores del gasto', async () => {
    mockearRutas({
      detalle: detalleFixture([gastoFixture({ importe: 300, idArea: 3, detalle: 'Entrega', numeroFactura: '0001-9', idProveedor: 1, categoria: 'Proveedor' })]),
      areas: [areaCocina],
    })
    render(<GastosDelTurno />)

    await abrirEdicion()
    const d = within(dialogo())
    await waitFor(() => expect(d.getByLabelText('Área (opcional)')).toHaveValue('3'))
    expect(d.getByLabelText('Importe')).toHaveValue('300,00')
    expect(d.getByLabelText('Medio de pago')).toHaveValue('1')
    expect(d.getByLabelText('Categoría')).toHaveValue('Proveedor')
    expect(d.getByLabelText('Proveedor (opcional)')).toHaveValue('1')
    expect(d.getByLabelText('Concepto')).toHaveValue('Flete')
    expect(d.getByLabelText('Detalle (opcional)')).toHaveValue('Entrega')
    expect(d.getByLabelText('N° de factura (opcional)')).toHaveValue('0001-9')
  })

  it('un gasto ligado a una compra abre la edición con categoría y proveedor bloqueados', async () => {
    mockearRutas({
      detalle: detalleFixture([gastoFixture({ categoria: 'Proveedor', idProveedor: 1, idComprobanteCompra: 40 })]),
    })
    render(<GastosDelTurno />)

    await abrirEdicion()
    const d = within(dialogo())
    expect(d.getByLabelText('Categoría')).toBeDisabled()
    expect(d.getByLabelText('Proveedor (opcional)')).toBeDisabled()
    expect(d.getByLabelText('Concepto')).toBeEnabled()
  })

  it('un gasto sin compra abre la edición con categoría y proveedor habilitados', async () => {
    mockearRutas({ detalle: detalleFixture([gastoFixture({ idComprobanteCompra: null })]) })
    render(<GastosDelTurno />)

    await abrirEdicion()
    const d = within(dialogo())
    expect(d.getByLabelText('Categoría')).toBeEnabled()
    expect(d.getByLabelText('Proveedor (opcional)')).toBeEnabled()
  })

  it('guarda con PUT /gastos/{id}, cierra el modal, avisa y refresca el detalle', async () => {
    mockearRutas({ detalle: detalleFixture([gastoFixture({ id: 12 })]) })
    apiPutMock.mockResolvedValueOnce({ id: 12 })
    render(<GastosDelTurno />)

    await abrirEdicion()
    const d = within(dialogo())
    await userEvent.clear(d.getByLabelText('Concepto'))
    await userEvent.type(d.getByLabelText('Concepto'), '  Flete corregido ')
    await userEvent.click(d.getByRole('button', { name: 'Guardar cambios' }))

    await waitFor(() =>
      expect(apiPutMock).toHaveBeenCalledWith(
        '/gastos/12',
        expect.objectContaining({ concepto: 'Flete corregido', idMedioPago: 1, importe: 300, categoria: 'Otros', idProveedor: null, idArea: null }),
      ),
    )
    await screen.findByText('Gasto actualizado.')
    expect(screen.queryByRole('dialog', { name: 'Editar gasto' })).not.toBeInTheDocument()
    const llamadasDetalle = apiGetMock.mock.calls.filter((c) => (c[0] as string).endsWith('/detalle'))
    expect(llamadasDetalle.length).toBeGreaterThanOrEqual(2)
  })

  it('un 409 gasto_turno_cerrado deja el modal abierto con el motivo y refresca el detalle', async () => {
    mockearRutas({ detalle: detalleFixture([gastoFixture({ id: 12 })]) })
    const { ErrorApi } = await import('../api/cliente')
    apiPutMock.mockRejectedValueOnce(new ErrorApi(409, 'gasto_turno_cerrado', 'cerrado'))
    render(<GastosDelTurno />)

    await abrirEdicion()
    const antes = apiGetMock.mock.calls.filter((c) => (c[0] as string).endsWith('/detalle')).length
    await userEvent.click(within(dialogo()).getByRole('button', { name: 'Guardar cambios' }))

    await within(dialogo()).findByText(/ya está cerrado/)
    await waitFor(() =>
      expect(apiGetMock.mock.calls.filter((c) => (c[0] as string).endsWith('/detalle')).length).toBeGreaterThan(antes),
    )
    expect(screen.queryByText('Gasto actualizado.')).not.toBeInTheDocument()
  })

  it('un importe en cero se rechaza sin llegar al servidor', async () => {
    mockearRutas({ detalle: detalleFixture([gastoFixture()]) })
    render(<GastosDelTurno />)

    await abrirEdicion()
    const d = within(dialogo())
    await userEvent.clear(d.getByLabelText('Importe'))
    await userEvent.click(d.getByRole('button', { name: 'Guardar cambios' }))

    await d.findByText('Completá el medio de pago, el concepto y un importe mayor a 0.')
    expect(apiPutMock).not.toHaveBeenCalled()
  })

  it('mientras el guardado está en vuelo el modal queda inerte y un doble click manda una sola solicitud', async () => {
    mockearRutas({ detalle: detalleFixture([gastoFixture({ id: 12 })]) })
    let resolver: (v: unknown) => void = () => undefined
    apiPutMock.mockImplementationOnce(() => new Promise((r) => (resolver = r)))
    render(<GastosDelTurno />)

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
    mockearRutas({ detalle: detalleFixture([gastoFixture()]) })
    render(<GastosDelTurno />)

    await abrirEdicion()
    await userEvent.click(within(dialogo()).getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('dialog', { name: 'Editar gasto' })).not.toBeInTheDocument()
    expect(apiPutMock).not.toHaveBeenCalled()
  })
})
