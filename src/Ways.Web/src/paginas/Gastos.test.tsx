import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Gastos } from './Gastos'
import type {
  AreaListado,
  EmpresaListado,
  GastoDeAdministracionListado,
  MedioPagoListado,
  OpcionDeProveedor,
  PaginaDeGastosDeAdministracion,
  PuntoVentaListado,
} from '../api/tipos'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown?])),
    put: vi.fn(),
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

function empresaFixture(sobrescribir: Partial<EmpresaListado> = {}): EmpresaListado {
  return {
    id: 1,
    idTenant: 1,
    razonSocial: 'Empresa Demo SA',
    nombreTenant: 'Tenant Demo',
    ...sobrescribir,
  } as EmpresaListado
}

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
    razonSocialEmpresa: 'Empresa Demo SA',
    modo: 'Web',
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

function areaFixture(sobrescribir: Partial<AreaListado> = {}): AreaListado {
  return { id: 1, nombre: 'General', activo: true, idEmpresa: null, orden: 1, ...sobrescribir }
}

function proveedorFixture(sobrescribir: Partial<OpcionDeProveedor> = {}): OpcionDeProveedor {
  return { id: 1, razonSocial: 'Distribuidora Sur SRL', nombreFantasia: null, ...sobrescribir }
}

function gastoFixture(sobrescribir: Partial<GastoDeAdministracionListado> = {}): GastoDeAdministracionListado {
  return {
    id: 1,
    fecha: '2026-09-01T00:00:00Z',
    idEmpresa: 1,
    idPuntoVenta: null,
    idTurnoCaja: null,
    categoria: 'Otros',
    idProveedor: null,
    nombreProveedor: null,
    idArea: null,
    nombreArea: null,
    concepto: 'Alquiler',
    detalle: null,
    idMedioPago: 1,
    nombreMedioPago: 'Efectivo',
    numeroFactura: null,
    importe: 1000,
    origenFondos: 'Tesoreria',
    idComprobanteCompra: null,
    ...sobrescribir,
  }
}

function paginaFixture(items: GastoDeAdministracionListado[] = []): PaginaDeGastosDeAdministracion {
  return { items, total: items.length, pagina: 1, tamanio: 25 }
}

function mockearRutas(opciones: {
  empresas?: EmpresaListado[]
  puntosVenta?: PuntoVentaListado[]
  medios?: MedioPagoListado[]
  areas?: AreaListado[]
  proveedores?: OpcionDeProveedor[]
  pagina?: PaginaDeGastosDeAdministracion
}) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/empresas') return Promise.resolve(opciones.empresas ?? [empresaFixture()])
    if (ruta === '/puntos-venta') return Promise.resolve(opciones.puntosVenta ?? [puntoVentaFixture()])
    if (ruta === '/catalogos/medios-pago') return Promise.resolve(opciones.medios ?? [medioEfectivo, medioCuentaCorriente])
    if (ruta === '/catalogos/areas') return Promise.resolve(opciones.areas ?? [areaFixture()])
    if (ruta === '/proveedores/opciones') return Promise.resolve(opciones.proveedores ?? [proveedorFixture()])
    if (ruta.startsWith('/gastos/administracion')) return Promise.resolve(opciones.pagina ?? paginaFixture())
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
})

describe('Gastos (administración) — carga inicial', () => {
  it('con una única empresa, la preselecciona en el filtro y en el formulario de alta', async () => {
    mockearRutas({})
    render(<Gastos />)

    // web-test-data-gates: esperar a que la opción de la empresa (el DATO) llegue, no a que el
    // <select> exista de entrada vacío.
    await screen.findByRole('option', { name: 'Empresa Demo SA' })
    expect(screen.getByLabelText('Empresa')).toHaveValue('1')

    await userEvent.click(screen.getByRole('button', { name: 'Nuevo gasto' }))
    // El select de empresa del FORMULARIO precede al del filtro en el DOM (el formulario se
    // pinta antes que la fila de filtros) — índice 0.
    expect(screen.getAllByLabelText('Empresa')[0]).toHaveValue('1')
  })
})

describe('Gastos (administración) — medio de pago', () => {
  it('excluye del selector del formulario los medios de comportamiento CuentaCorriente', async () => {
    mockearRutas({})
    render(<Gastos />)

    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo gasto' }))
    await screen.findByLabelText('Concepto')

    expect(screen.getByRole('option', { name: 'Efectivo' })).toBeInTheDocument()
    expect(screen.queryByRole('option', { name: 'Cuenta corriente' })).not.toBeInTheDocument()
  })
})

describe('Gastos (administración) — proveedor cambia la categoría', () => {
  // El formulario de alta y el filtro de arriba comparten la etiqueta "Categoría" — con el
  // formulario abierto, el select del FORMULARIO es el primero en el DOM (índice 0).
  function categoriaDelFormulario() {
    return screen.getAllByLabelText('Categoría')[0]
  }

  it('elegir un proveedor cambia la categoría a Proveedor', async () => {
    mockearRutas({})
    render(<Gastos />)

    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo gasto' }))
    await screen.findByLabelText('Concepto')
    expect(categoriaDelFormulario()).toHaveValue('Otros')

    await userEvent.selectOptions(screen.getByLabelText('Proveedor (opcional)'), 'Distribuidora Sur SRL')

    expect(categoriaDelFormulario()).toHaveValue('Proveedor')
  })

  it('limpiar el proveedor mientras la categoría sigue en Proveedor la vuelve a Otros', async () => {
    mockearRutas({})
    render(<Gastos />)

    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo gasto' }))
    await screen.findByLabelText('Concepto')
    await userEvent.selectOptions(screen.getByLabelText('Proveedor (opcional)'), 'Distribuidora Sur SRL')

    await userEvent.selectOptions(screen.getByLabelText('Proveedor (opcional)'), 'Sin proveedor')

    expect(categoriaDelFormulario()).toHaveValue('Otros')
  })
})

describe('Gastos (administración) — fecha', () => {
  it('el campo de fecha no admite un valor posterior a hoy (atributo max)', async () => {
    mockearRutas({})
    render(<Gastos />)

    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo gasto' }))
    const campoFecha = await screen.findByLabelText('Fecha')

    const hoy = new Date().toISOString().slice(0, 10)
    expect(campoFecha).toHaveAttribute('max', hoy)
  })

  it('una fecha futura (bypaseando el atributo max) se rechaza sin llegar a pegarle al servidor', async () => {
    mockearRutas({})
    render(<Gastos />)

    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo gasto' }))
    await screen.findByLabelText('Concepto')

    const manianaISO = new Date(Date.now() + 24 * 60 * 60 * 1000).toISOString().slice(0, 10)
    fireEvent.change(screen.getByLabelText('Fecha'), { target: { value: manianaISO } })
    await userEvent.type(screen.getByLabelText('Concepto'), 'Gasto futuro')
    await userEvent.type(screen.getByLabelText('Importe'), '500')
    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), 'Efectivo')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await screen.findByText('La fecha del gasto no puede ser futura.')
    expect(apiPostMock).not.toHaveBeenCalled()
  })
})

describe('Gastos (administración) — alta', () => {
  async function abrirYCompletarFormulario() {
    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo gasto' }))
    await screen.findByLabelText('Concepto')
    await userEvent.type(screen.getByLabelText('Concepto'), 'Alquiler de septiembre')
    await userEvent.type(screen.getByLabelText('Importe'), '1500')
    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), 'Efectivo')
  }

  it('arma el cuerpo del POST con fecha, empresa y medio de pago elegidos, sin turno', async () => {
    mockearRutas({})
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    render(<Gastos />)

    await abrirYCompletarFormulario()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await waitFor(() =>
      expect(apiPostMock).toHaveBeenCalledWith(
        '/gastos/administracion',
        expect.objectContaining({
          idEmpresa: 1,
          idPuntoVenta: null,
          categoria: 'Otros',
          concepto: 'Alquiler de septiembre',
          idMedioPago: 1,
          importe: 1500,
          idComprobanteCompra: null,
        }),
      ),
    )
  })

  it('una fecha retroactiva elegida por el admin viaja tal cual en el POST', async () => {
    mockearRutas({})
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    render(<Gastos />)

    await abrirYCompletarFormulario()
    fireEvent.change(screen.getByLabelText('Fecha'), { target: { value: '2026-01-15' } })
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/gastos/administracion', expect.objectContaining({ fecha: '2026-01-15' })))
  })

  it('al confirmar con éxito, limpia y cierra el formulario, y refresca el listado', async () => {
    mockearRutas({})
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    render(<Gastos />)

    await abrirYCompletarFormulario()
    mockearRutas({ pagina: paginaFixture([gastoFixture({ id: 9, importe: 1500, concepto: 'Alquiler de septiembre' })]) })
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await screen.findByText('Gasto registrado.')
    expect(screen.queryByLabelText('Concepto')).not.toBeInTheDocument()
    await screen.findByText('Alquiler de septiembre')
  })

  it('el formulario entero queda deshabilitado mientras el alta está en vuelo', async () => {
    mockearRutas({})
    let resolverPost: (valor: unknown) => void = () => undefined
    apiPostMock.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolverPost = resolve
        }),
    )
    render(<Gastos />)

    await abrirYCompletarFormulario()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await waitFor(() => expect(screen.getByLabelText('Concepto')).toBeDisabled())
    resolverPost({ id: 1 })
  })

  it('un doble click sobre "Registrar" en el mismo tick dispara una sola solicitud', async () => {
    mockearRutas({})
    let resolverPost: (valor: unknown) => void = () => undefined
    apiPostMock.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolverPost = resolve
        }),
    )
    render(<Gastos />)

    await abrirYCompletarFormulario()
    const boton = screen.getByRole('button', { name: 'Registrar' })
    fireEvent.click(boton)
    fireEvent.click(boton)

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    resolverPost({ id: 1 })
  })

  it('un concepto en blanco se rechaza sin llegar a pegarle al servidor', async () => {
    mockearRutas({})
    render(<Gastos />)

    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo gasto' }))
    await screen.findByLabelText('Concepto')
    await userEvent.type(screen.getByLabelText('Importe'), '1500')
    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), 'Efectivo')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await screen.findByText('Completá fecha, empresa, medio de pago, concepto e importe (mayor a 0).')
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('un error del servidor se muestra inline y el formulario no se cierra', async () => {
    const { ErrorApi } = await import('../api/cliente')
    mockearRutas({})
    apiPostMock.mockRejectedValueOnce(new ErrorApi(400, 'gasto_fecha_futura', 'La fecha del gasto no puede ser futura.'))
    render(<Gastos />)

    await abrirYCompletarFormulario()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await screen.findByText('La fecha del gasto no puede ser futura.')
    expect(screen.getByLabelText('Concepto')).toBeInTheDocument()
  })
})

describe('Gastos (administración) — listado y filtros', () => {
  it('lista los gastos con empresa, categoría, proveedor/área resueltos y origen', async () => {
    mockearRutas({
      pagina: paginaFixture([
        gastoFixture({ id: 1, categoria: 'Proveedor', idProveedor: 1, nombreProveedor: 'Distribuidora Sur SRL' }),
        gastoFixture({ id: 2, idPuntoVenta: 7, origenFondos: 'Tesoreria' }),
      ]),
    })
    render(<Gastos />)

    const tabla = await screen.findByRole('table')
    expect(within(tabla).getByText('Distribuidora Sur SRL')).toBeInTheDocument()
    expect(within(tabla).getAllByText('Caja general').length).toBe(2)
  })

  it('un proveedor dado de baja lógica (nombreProveedor null) se muestra como "(no disponible)" sin perder la fila', async () => {
    mockearRutas({
      pagina: paginaFixture([gastoFixture({ id: 1, categoria: 'Proveedor', idProveedor: 5, nombreProveedor: null })]),
    })
    render(<Gastos />)

    const tabla = await screen.findByRole('table')
    expect(within(tabla).getByText('(no disponible)')).toBeInTheDocument()
  })

  it('cambiar el filtro de categoría dispara una nueva consulta con ese filtro', async () => {
    mockearRutas({})
    render(<Gastos />)

    await screen.findByRole('table')
    apiGetMock.mockClear()
    mockearRutas({})

    // El formulario de alta está cerrado en este test — "Categoría" es unívoco (el del filtro).
    await userEvent.selectOptions(screen.getByLabelText('Categoría'), 'Proveedor')

    await waitFor(() =>
      expect(apiGetMock).toHaveBeenCalledWith(expect.stringContaining('categoria=Proveedor')),
    )
  })

  it('sin resultados, muestra el mensaje de listado vacío', async () => {
    mockearRutas({ pagina: paginaFixture([]) })
    render(<Gastos />)

    await screen.findByText('No hay gastos que coincidan con los filtros.')
  })
})
