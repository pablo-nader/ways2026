import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
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

// stage-gasto-a-compra (PR4): Gastos.tsx ahora usa useNavigate (acciones "Vincular a compra" /
// "Crear compra") — necesita un Router alrededor, mismo criterio que CompraEditor.test.tsx.
function renderGastos() {
  return render(
    <MemoryRouter initialEntries={['/gastos']}>
      <Gastos />
    </MemoryRouter>,
  )
}

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()
const apiPutMock = vi.fn()
const apiDeleteMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown?])),
    put: (...args: unknown[]) => apiPutMock(...(args as [string, unknown?])),
    delete: (...args: unknown[]) => apiDeleteMock(...(args as [string])),
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
    alicuotaPercepcionIibb: null,
    alicuotaPercepcionIva: null,
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
    turnoAbierto: false,
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
  apiPutMock.mockReset()
  apiDeleteMock.mockReset()
})

describe('Gastos (administración) — carga inicial', () => {
  it('con una única empresa, la preselecciona en el filtro y en el formulario de alta', async () => {
    mockearRutas({})
    renderGastos()

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
    renderGastos()

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
    renderGastos()

    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo gasto' }))
    await screen.findByLabelText('Concepto')
    expect(categoriaDelFormulario()).toHaveValue('Otros')

    await userEvent.selectOptions(screen.getByLabelText('Proveedor (opcional)'), 'Distribuidora Sur SRL')

    expect(categoriaDelFormulario()).toHaveValue('Proveedor')
  })

  it('limpiar el proveedor mientras la categoría sigue en Proveedor la vuelve a Otros', async () => {
    mockearRutas({})
    renderGastos()

    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo gasto' }))
    await screen.findByLabelText('Concepto')
    await userEvent.selectOptions(screen.getByLabelText('Proveedor (opcional)'), 'Distribuidora Sur SRL')

    await userEvent.selectOptions(screen.getByLabelText('Proveedor (opcional)'), 'Sin proveedor')

    expect(categoriaDelFormulario()).toHaveValue('Otros')
  })
})

function fechaLocal(fecha: Date): string {
  return [
    fecha.getFullYear(),
    String(fecha.getMonth() + 1).padStart(2, '0'),
    String(fecha.getDate()).padStart(2, '0'),
  ].join('-')
}

describe('Gastos (administración) — fecha', () => {
  it('el selector de fecha no admite un valor posterior a hoy (atributo max)', async () => {
    mockearRutas({})
    renderGastos()

    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo gasto' }))
    const campoFecha = await screen.findByLabelText('Fecha')

    const selector = campoFecha.closest('.position-relative')!.querySelector('input[type="date"]')
    expect(selector).toHaveAttribute('max', fechaLocal(new Date()))
  })

  it('una fecha futura tipeada en el campo visible deja el gasto sin guardar y el campo inválido', async () => {
    mockearRutas({})
    renderGastos()

    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo gasto' }))
    await screen.findByLabelText('Concepto')

    const maniana = new Date()
    maniana.setDate(maniana.getDate() + 1)
    const [anio, mes, dia] = fechaLocal(maniana).split('-')
    const campoFecha = screen.getByLabelText('Fecha')
    await userEvent.clear(campoFecha)
    await userEvent.type(campoFecha, `${dia}/${mes}/${anio}`)
    await userEvent.type(screen.getByLabelText('Concepto'), 'Gasto futuro')
    await userEvent.type(screen.getByLabelText('Importe'), '500')
    await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), 'Efectivo')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    expect(campoFecha).toHaveClass('is-invalid')
    expect(await screen.findByText('Completá fecha, empresa, medio de pago, concepto e importe (mayor a 0).')).toBeInTheDocument()
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
    renderGastos()

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
    renderGastos()

    await abrirYCompletarFormulario()
    fireEvent.change(screen.getByLabelText('Fecha'), { target: { value: '15/01/2026' } })
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/gastos/administracion', expect.objectContaining({ fecha: '2026-01-15' })))
  })

  it('al confirmar con éxito, limpia y cierra el formulario, y refresca el listado', async () => {
    mockearRutas({})
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    renderGastos()

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
    renderGastos()

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
    renderGastos()

    await abrirYCompletarFormulario()
    const boton = screen.getByRole('button', { name: 'Registrar' })
    fireEvent.click(boton)
    fireEvent.click(boton)

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    resolverPost({ id: 1 })
  })

  it('un concepto en blanco se rechaza sin llegar a pegarle al servidor', async () => {
    mockearRutas({})
    renderGastos()

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
    renderGastos()

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
    renderGastos()

    const tabla = await screen.findByRole('table')
    expect(within(tabla).getByText('Distribuidora Sur SRL')).toBeInTheDocument()
    expect(within(tabla).getAllByText('Caja general').length).toBe(2)
  })

  it('un proveedor dado de baja lógica (nombreProveedor null) se muestra como "(no disponible)" sin perder la fila', async () => {
    mockearRutas({
      pagina: paginaFixture([gastoFixture({ id: 1, categoria: 'Proveedor', idProveedor: 5, nombreProveedor: null })]),
    })
    renderGastos()

    const tabla = await screen.findByRole('table')
    expect(within(tabla).getByText('(no disponible)')).toBeInTheDocument()
  })

  it('cambiar el filtro de categoría dispara una nueva consulta con ese filtro', async () => {
    mockearRutas({})
    renderGastos()

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
    renderGastos()

    await screen.findByText('No hay gastos que coincidan con los filtros.')
  })
})

// stage-gasto-a-compra (PR4): acciones por fila para un gasto sin compra ligada — "Vincular a
// compra" abre un picker filtrado por el proveedor del gasto (si tiene), "Crear compra" navega.
describe('Gastos (administración) — vincular a una compra existente', () => {
  function compraListadaFixture(sobrescribir: Partial<{ id: number; idProveedor: number; numeroExterno: string | null; estado: string; fechaRecepcion: string | null; total: number; saldoPendiente: number }> = {}) {
    return { id: 9, idProveedor: 2, idTipoComprobante: 5, numeroExterno: '0003-00000009', estado: 'Confirmada', fechaRecepcion: '2026-08-20T12:00:00Z', total: 500, saldoPendiente: 320, ...sobrescribir }
  }

  function mockearConCompras(opciones: { pagina?: PaginaDeGastosDeAdministracion; compras?: ReturnType<typeof compraListadaFixture>[] } = {}) {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/empresas') return Promise.resolve([empresaFixture()])
      if (ruta === '/puntos-venta') return Promise.resolve([puntoVentaFixture()])
      if (ruta === '/catalogos/medios-pago') return Promise.resolve([medioEfectivo, medioCuentaCorriente])
      if (ruta === '/catalogos/areas') return Promise.resolve([areaFixture()])
      if (ruta === '/proveedores/opciones') return Promise.resolve([proveedorFixture()])
      if (ruta.startsWith('/gastos/administracion')) return Promise.resolve(opciones.pagina ?? paginaFixture())
      if (ruta.startsWith('/compras')) {
        const items = opciones.compras ?? [compraListadaFixture()]
        return Promise.resolve({ items, total: items.length, pagina: 1, tamanio: 25 })
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })
  }

  it('un gasto con proveedor abre el picker filtrado por ESE proveedor', async () => {
    mockearConCompras({ pagina: paginaFixture([gastoFixture({ id: 3, idProveedor: 1, nombreProveedor: 'Distribuidora Sur SRL' })]) })
    renderGastos()

    await userEvent.click(await screen.findByRole('button', { name: 'Vincular a compra' }))

    await waitFor(() =>
      expect(apiGetMock).toHaveBeenCalledWith(expect.stringContaining('idProveedor=1')),
    )
    expect(apiGetMock).toHaveBeenCalledWith(expect.stringContaining('estado=Confirmada'))
    expect(await screen.findByText('0003-00000009')).toBeInTheDocument()
  })

  // stage-tesoreria-por-empresa (PR5): el picker filtra por la EMPRESA del gasto (join PV→empresa
  // del lado del servidor) para no ofrecer nunca una compra de otra empresa.
  it('el picker filtra las compras por la empresa del gasto', async () => {
    mockearConCompras({ pagina: paginaFixture([gastoFixture({ id: 3, idEmpresa: 1, idProveedor: 1, nombreProveedor: 'Distribuidora Sur SRL' })]) })
    renderGastos()

    await userEvent.click(await screen.findByRole('button', { name: 'Vincular a compra' }))

    await waitFor(() => expect(apiGetMock).toHaveBeenCalledWith(expect.stringContaining('idEmpresa=1')))
  })

  it('el picker muestra el saldo pendiente de cada compra junto a su total', async () => {
    mockearConCompras({
      pagina: paginaFixture([gastoFixture({ id: 3 })]),
      compras: [
        compraListadaFixture({ id: 9, numeroExterno: '0003-00000009', total: 500, saldoPendiente: 320 }),
        compraListadaFixture({ id: 10, numeroExterno: '0003-00000010', total: 700, saldoPendiente: 0 }),
      ],
    })
    renderGastos()

    await userEvent.click(await screen.findByRole('button', { name: 'Vincular a compra' }))

    const fila9 = (await screen.findByText('0003-00000009')).closest('tr') as HTMLElement
    const fila10 = screen.getByText('0003-00000010').closest('tr') as HTMLElement
    expect(screen.getByRole('columnheader', { name: 'Saldo pendiente' })).toBeInTheDocument()
    expect(within(fila9).getByText('$ 500,00')).toBeInTheDocument()
    expect(within(fila9).getByText('$ 320,00')).toBeInTheDocument()
    expect(within(fila10).getByText('$ 700,00')).toBeInTheDocument()
    expect(within(fila10).getByText('$ 0,00')).toBeInTheDocument()
  })

  it('elegir una compra del picker vincula el gasto y refresca el listado', async () => {
    mockearConCompras({ pagina: paginaFixture([gastoFixture({ id: 3 })]) })
    apiPostMock.mockResolvedValue({ id: 3, idComprobanteCompra: 9 })
    renderGastos()

    await userEvent.click(await screen.findByRole('button', { name: 'Vincular a compra' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Elegir' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/gastos/3/vincular-compra', { idComprobanteCompra: 9 }))
    expect(await screen.findByText('Gasto vinculado a la compra.')).toBeInTheDocument()
    // El picker se cierra tras el éxito.
    expect(screen.queryByText('Vincular a compra', { selector: 'h5' })).not.toBeInTheDocument()
  })

  it('un gasto ya vinculado muestra el link a la compra en vez de las acciones', async () => {
    mockearConCompras({ pagina: paginaFixture([gastoFixture({ id: 3, idComprobanteCompra: 9 })]) })
    renderGastos()

    expect(await screen.findByRole('link', { name: 'Compra #9' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Vincular a compra' })).not.toBeInTheDocument()
  })

  // stage-tesoreria-por-empresa (PR5, react-async-state regla 10): `vincularACompra` usaba el
  // `generacionRef` COMPARTIDO con el listado — un refresco del listado disparado mientras el
  // vínculo está en vuelo bumpeaba ese mismo contador y hacía que la finalización del vínculo se
  // tratara como "obsoleta" (nunca cerraba el picker ni mostraba el aviso), aunque el POST hubiera
  // tenido éxito. Con el guard AISLADO (`generacionVinculoRef`), un refresco del listado en el
  // medio nunca puede pisar la finalización de este vínculo puntual.
  it('un refresco del listado en vuelo durante el vínculo no le impide cerrar el picker ni avisar', async () => {
    mockearConCompras({ pagina: paginaFixture([gastoFixture({ id: 3 })]) })
    let resolverPost: (valor: unknown) => void = () => undefined
    apiPostMock.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolverPost = resolve
        }),
    )
    renderGastos()

    await userEvent.click(await screen.findByRole('button', { name: 'Vincular a compra' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Elegir' }))

    // Dispara un refresco del listado (bumpea el `generacionRef` COMPARTIDO del listado) mientras
    // el vínculo sigue en vuelo — con el bug pre-PR5 esto haría que la finalización de abajo se
    // descartara como "obsoleta".
    await userEvent.selectOptions(screen.getByLabelText('Categoría'), 'Otros')

    resolverPost({ id: 3, idComprobanteCompra: 9 })

    expect(await screen.findByText('Gasto vinculado a la compra.')).toBeInTheDocument()
    expect(screen.queryByText('Vincular a compra', { selector: 'h5' })).not.toBeInTheDocument()
  })
})

describe('Gastos (administración) — edición y baja', () => {
  const gastoDeCajaCerrado = () =>
    gastoFixture({ id: 31, concepto: 'Flete', origenFondos: 'CajaTurno', idTurnoCaja: 9, turnoAbierto: false })

  function dialogo() {
    return screen.getByRole('dialog', { name: 'Editar gasto' })
  }

  function llamadasAlListado() {
    return apiGetMock.mock.calls.filter((c) => (c[0] as string).startsWith('/gastos/administracion')).length
  }

  it('editar abre el modal precargado y guarda con PUT /gastos/administracion/{id}, refrescando el listado', async () => {
    mockearRutas({ pagina: paginaFixture([gastoFixture({ id: 31, concepto: 'Alquiler', importe: 1000 })]) })
    apiPutMock.mockResolvedValueOnce({ id: 31 })
    renderGastos()

    await userEvent.click(await screen.findByRole('button', { name: 'Editar gasto Alquiler' }))
    const d = within(await screen.findByRole('dialog', { name: 'Editar gasto' }))
    expect(d.getByLabelText('Concepto')).toHaveValue('Alquiler')
    expect(d.getByLabelText('Importe')).toHaveValue('1.000,00')

    await userEvent.clear(d.getByLabelText('Importe'))
    await userEvent.type(d.getByLabelText('Importe'), '1200')
    const antes = llamadasAlListado()
    await userEvent.click(d.getByRole('button', { name: 'Guardar cambios' }))

    await waitFor(() =>
      expect(apiPutMock).toHaveBeenCalledWith(
        '/gastos/administracion/31',
        expect.objectContaining({ importe: 1200, concepto: 'Alquiler', idMedioPago: 1, categoria: 'Otros' }),
      ),
    )
    await screen.findByText('Gasto actualizado.')
    expect(screen.queryByRole('dialog', { name: 'Editar gasto' })).not.toBeInTheDocument()
    await waitFor(() => expect(llamadasAlListado()).toBeGreaterThan(antes))
  })

  it('editar un gasto de caja de un turno cerrado muestra el aviso de recálculo del arqueo', async () => {
    mockearRutas({ pagina: paginaFixture([gastoDeCajaCerrado()]) })
    renderGastos()

    await userEvent.click(await screen.findByRole('button', { name: 'Editar gasto Flete' }))

    expect(
      await within(dialogo()).findByText(
        'Este gasto pertenece a un turno cerrado: se recalculará el arqueo del turno y quedará marcado como recalculado.',
      ),
    ).toBeInTheDocument()
  })

  it('un gasto de caja general avisa del ajuste compensatorio y no del arqueo', async () => {
    mockearRutas({ pagina: paginaFixture([gastoFixture({ id: 31, concepto: 'Alquiler', origenFondos: 'Tesoreria' })]) })
    renderGastos()

    await userEvent.click(await screen.findByRole('button', { name: 'Editar gasto Alquiler' }))

    expect(await within(dialogo()).findByText(/ajuste compensatorio sobre la caja general/)).toBeInTheDocument()
    expect(within(dialogo()).queryByText(/turno cerrado/)).not.toBeInTheDocument()
  })

  it('un gasto ligado a una compra no deja cambiar categoría ni proveedor', async () => {
    mockearRutas({
      pagina: paginaFixture([
        gastoFixture({
          id: 31,
          concepto: 'Mercadería',
          categoria: 'Proveedor',
          idProveedor: 1,
          nombreProveedor: 'Distribuidora Sur SRL',
          idComprobanteCompra: 44,
        }),
      ]),
    })
    renderGastos()

    await userEvent.click(await screen.findByRole('button', { name: 'Editar gasto Mercadería' }))

    const d = within(dialogo())
    expect(d.getByLabelText('Categoría')).toBeDisabled()
    expect(d.getByLabelText('Proveedor (opcional)')).toBeDisabled()
    expect(d.getByLabelText('Concepto')).toBeEnabled()
  })

  it('un error del servidor al editar se muestra en el modal y no lo cierra', async () => {
    mockearRutas({ pagina: paginaFixture([gastoFixture({ id: 31, concepto: 'Alquiler' })]) })
    const { ErrorApi } = await import('../api/cliente')
    apiPutMock.mockRejectedValueOnce(new ErrorApi(400, 'gasto_proveedor_ligado', 'El proveedor no se puede cambiar.'))
    renderGastos()

    await userEvent.click(await screen.findByRole('button', { name: 'Editar gasto Alquiler' }))
    await userEvent.click(within(dialogo()).getByRole('button', { name: 'Guardar cambios' }))

    expect(await within(dialogo()).findByText('El proveedor no se puede cambiar.')).toBeInTheDocument()
  })

  it('eliminar pide confirmación y solo al confirmar manda el DELETE y refresca', async () => {
    mockearRutas({ pagina: paginaFixture([gastoFixture({ id: 31, concepto: 'Alquiler' })]) })
    apiDeleteMock.mockResolvedValueOnce(undefined)
    renderGastos()

    await userEvent.click(await screen.findByRole('button', { name: 'Eliminar gasto Alquiler' }))
    const confirmacion = await screen.findByRole('alertdialog', { name: 'Confirmar eliminación' })
    expect(apiDeleteMock).not.toHaveBeenCalled()

    const antes = llamadasAlListado()
    await userEvent.click(within(confirmacion).getByRole('button', { name: 'Confirmar eliminación' }))

    await waitFor(() => expect(apiDeleteMock).toHaveBeenCalledWith('/gastos/administracion/31'))
    await screen.findByText('Gasto eliminado.')
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    await waitFor(() => expect(llamadasAlListado()).toBeGreaterThan(antes))
  })

  it('cancelar la confirmación no elimina nada', async () => {
    mockearRutas({ pagina: paginaFixture([gastoFixture({ id: 31, concepto: 'Alquiler' })]) })
    renderGastos()

    await userEvent.click(await screen.findByRole('button', { name: 'Eliminar gasto Alquiler' }))
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(apiDeleteMock).not.toHaveBeenCalled()
  })

  it('la confirmación de baja de un gasto de un turno cerrado advierte del recálculo del arqueo', async () => {
    mockearRutas({ pagina: paginaFixture([gastoDeCajaCerrado()]) })
    renderGastos()

    await userEvent.click(await screen.findByRole('button', { name: 'Eliminar gasto Flete' }))

    expect(
      await within(await screen.findByRole('alertdialog')).findByText(
        /Este gasto pertenece a un turno cerrado: se recalculará el arqueo del turno y quedará marcado como recalculado\./,
      ),
    ).toBeInTheDocument()
  })

  it('un fallo del DELETE se muestra y la confirmación sigue abierta', async () => {
    mockearRutas({ pagina: paginaFixture([gastoFixture({ id: 31, concepto: 'Alquiler' })]) })
    const { ErrorApi } = await import('../api/cliente')
    apiDeleteMock.mockRejectedValueOnce(new ErrorApi(404, 'no_encontrado', 'No existe el gasto 31.'))
    renderGastos()

    await userEvent.click(await screen.findByRole('button', { name: 'Eliminar gasto Alquiler' }))
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Confirmar eliminación' }))

    expect(await screen.findByText('No existe el gasto 31.')).toBeInTheDocument()
    expect(screen.getByRole('alertdialog')).toBeInTheDocument()
  })

  it('un doble click sobre "Confirmar eliminación" en el mismo tick manda un solo DELETE', async () => {
    mockearRutas({ pagina: paginaFixture([gastoFixture({ id: 31, concepto: 'Alquiler' })]) })
    let resolver: (v: unknown) => void = () => undefined
    apiDeleteMock.mockImplementationOnce(() => new Promise((r) => (resolver = r)))
    renderGastos()

    await userEvent.click(await screen.findByRole('button', { name: 'Eliminar gasto Alquiler' }))
    const confirmar = within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Confirmar eliminación' })
    act(() => {
      confirmar.click()
      confirmar.click()
    })

    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
    await act(async () => {
      resolver(undefined)
    })
  })
})
