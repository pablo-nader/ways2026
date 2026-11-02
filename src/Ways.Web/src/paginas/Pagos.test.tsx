import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, useLocation } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Pagos } from './Pagos'
import { Toaster } from '../toasts/Toaster'
import { ErrorApi } from '../api/cliente'
import type {
  AreaListado,
  EmpresaListado,
  PagoDeAdministracionListado,
  MedioPagoListado,
  OpcionDeProveedor,
  PaginaDePagosDeAdministracion,
  PersonalListado,
  PuntoVentaListado,
} from '../api/tipos'

// stage-gasto-a-compra (PR4): Pagos.tsx ahora usa useNavigate (acciones "Vincular a compra" /
// "Crear compra") — necesita un Router alrededor, mismo criterio que CompraEditor.test.tsx.
function renderPagos() {
  return render(
    <MemoryRouter initialEntries={['/pagos']}>
      <Pagos />
      <Toaster />
    </MemoryRouter>,
  )
}

/** El fallo de carga queda en el banner persistente de la pantalla: ni es un toast ni hay uno con su texto. */
async function bannerDeCargaPersistente(texto: string) {
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

const medioTransferencia: MedioPagoListado = {
  id: 4,
  nombre: 'Transferencia',
  activo: true,
  idEmpresa: null,
  orden: 3,
  comportamiento: 'Electronico',
  admiteVuelto: false,
  requiereReferencia: false,
  recargoPorcentaje: null,
  porcentajeFacturacion: 0,
}

function areaFixture(sobrescribir: Partial<AreaListado> = {}): AreaListado {
  return { id: 1, nombre: 'General', activo: true, idEmpresa: null, orden: 1, ...sobrescribir }
}

function proveedorFixture(sobrescribir: Partial<OpcionDeProveedor> = {}): OpcionDeProveedor {
  return { id: 1, razonSocial: 'Distribuidora Sur SRL', nombreFantasia: null, ...sobrescribir }
}

function pagoFixture(sobrescribir: Partial<PagoDeAdministracionListado> = {}): PagoDeAdministracionListado {
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
    idMovimientoPersonal: null,
    lineas: [{ idItem: 1, idMedioPago: 1, nombreMedioPago: 'Efectivo', importe: sobrescribir.importe ?? 1000, referencia: null }],
    ...sobrescribir,
  }
}

function paginaFixture(items: PagoDeAdministracionListado[] = []): PaginaDePagosDeAdministracion {
  return { items, total: items.length, pagina: 1, tamanio: 25 }
}

function mockearRutas(opciones: {
  empresas?: EmpresaListado[]
  puntosVenta?: PuntoVentaListado[]
  medios?: MedioPagoListado[]
  areas?: AreaListado[]
  proveedores?: OpcionDeProveedor[]
  personal?: PersonalListado[]
  pagina?: PaginaDePagosDeAdministracion
}) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/empresas') return Promise.resolve(opciones.empresas ?? [empresaFixture()])
    if (ruta === '/puntos-venta') return Promise.resolve(opciones.puntosVenta ?? [puntoVentaFixture()])
    if (ruta === '/catalogos/medios-pago') return Promise.resolve(opciones.medios ?? [medioEfectivo, medioCuentaCorriente])
    if (ruta === '/catalogos/areas') return Promise.resolve(opciones.areas ?? [areaFixture()])
    if (ruta === '/proveedores/opciones') return Promise.resolve(opciones.proveedores ?? [proveedorFixture()])
    if (ruta.startsWith('/personal')) {
      const items = opciones.personal ?? []
      return Promise.resolve({ items, total: items.length, pagina: 1, tamanio: 200 })
    }
    if (ruta.startsWith('/pagos/administracion')) return Promise.resolve(opciones.pagina ?? paginaFixture())
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiPutMock.mockReset()
  apiDeleteMock.mockReset()
})

describe('Pagos (administración) — carga inicial', () => {
  it('con una única empresa, la preselecciona en el filtro y en el formulario de alta', async () => {
    mockearRutas({})
    renderPagos()

    // web-test-data-gates: esperar a que la opción de la empresa (el DATO) llegue, no a que el
    // <select> exista de entrada vacío.
    await screen.findByRole('option', { name: 'Empresa Demo SA' })
    expect(screen.getByLabelText('Empresa')).toHaveValue('1')

    await userEvent.click(screen.getByRole('button', { name: 'Nuevo pago' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Nuevo pago' })
    expect(within(dialogo).getByLabelText('Empresa')).toHaveValue('1')
  })
})

describe('Pagos (administración) — columna de medios', () => {
  it('un pago con varios medios lista el reparto; uno con un solo medio muestra el medio', async () => {
    const repartido = pagoFixture({
      id: 40,
      concepto: 'Flete',
      importe: 1000,
      lineas: [
        { idItem: 1, idMedioPago: 1, nombreMedioPago: 'Efectivo', importe: 600, referencia: null },
        { idItem: 2, idMedioPago: 4, nombreMedioPago: 'Transferencia', importe: 400, referencia: null },
      ],
    })
    const simple = pagoFixture({ id: 41, concepto: 'Alquiler', nombreMedioPago: 'Efectivo' })
    mockearRutas({ pagina: paginaFixture([repartido, simple]) })
    renderPagos()

    expect(await screen.findByText(/Efectivo \$\s600,00 · Transferencia \$\s400,00/)).toBeInTheDocument()
    const filaSimple = screen.getByText('Alquiler').closest('tr')!
    expect(within(filaSimple).getByText('Efectivo')).toBeInTheDocument()
  })
})

describe('Pagos (administración) — disponible', () => {
  it('muestra lo disponible de un pago de proveedor y un guion cuando no aplica', async () => {
    const conCredito = pagoFixture({ id: 50, concepto: 'Pago con crédito', importe: 900, aplicado: 600, disponible: 300 })
    const sinCredito = pagoFixture({ id: 51, concepto: 'Pago de caja', disponible: null })
    mockearRutas({ pagina: paginaFixture([conCredito, sinCredito]) })
    renderPagos()

    expect(await screen.findByRole('columnheader', { name: 'Disponible' })).toBeInTheDocument()
    const filaConCredito = screen.getByText('Pago con crédito').closest('tr')!
    expect(within(filaConCredito).getByText('$ 300,00')).toBeInTheDocument()
    const filaSinCredito = screen.getByText('Pago de caja').closest('tr')!
    expect(within(filaSinCredito).getByText('—', { selector: 'td.text-end' })).toBeInTheDocument()
  })
})

describe('Pagos (administración) — medio de pago', () => {
  it('excluye del selector del formulario los medios de comportamiento CuentaCorriente', async () => {
    mockearRutas({})
    renderPagos()

    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo pago' }))
    await screen.findByLabelText('Concepto')
    await screen.findByRole('option', { name: 'Efectivo' }) // espera al dato: los medios llegan después del modal

    expect(screen.getByRole('option', { name: 'Efectivo' })).toBeInTheDocument()
    expect(screen.queryByRole('option', { name: 'Cuenta corriente' })).not.toBeInTheDocument()
  })
})

describe('Pagos (administración) — proveedor cambia la categoría', () => {
  // El modal de alta y el filtro de la pantalla comparten la etiqueta "Categoría": se acota al diálogo.
  function categoriaDelFormulario() {
    return within(screen.getByRole('dialog', { name: 'Nuevo pago' })).getByLabelText('Categoría')
  }

  it('elegir un proveedor cambia la categoría a Proveedor', async () => {
    mockearRutas({})
    renderPagos()

    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo pago' }))
    await screen.findByLabelText('Concepto')
    await screen.findByRole('option', { name: 'Efectivo' }) // espera al dato: los medios llegan después del modal
    expect(categoriaDelFormulario()).toHaveValue('Otros')

    await userEvent.selectOptions(screen.getByLabelText('Proveedor (opcional)'), 'Distribuidora Sur SRL')

    expect(categoriaDelFormulario()).toHaveValue('Proveedor')
  })

  it('limpiar el proveedor mientras la categoría sigue en Proveedor la vuelve a Otros', async () => {
    mockearRutas({})
    renderPagos()

    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo pago' }))
    await screen.findByLabelText('Concepto')
    await screen.findByRole('option', { name: 'Efectivo' }) // espera al dato: los medios llegan después del modal
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

describe('Pagos (administración) — fecha', () => {
  it('el selector de fecha no admite un valor posterior a hoy (atributo max)', async () => {
    mockearRutas({})
    renderPagos()

    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo pago' }))
    const campoFecha = await screen.findByLabelText('Fecha')

    const selector = campoFecha.closest('.position-relative')!.querySelector('input[type="date"]')
    expect(selector).toHaveAttribute('max', fechaLocal(new Date()))
  })

  it('una fecha futura tipeada en el campo visible deja el pago sin guardar y el campo inválido', async () => {
    mockearRutas({})
    renderPagos()

    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo pago' }))
    await screen.findByLabelText('Concepto')
    await screen.findByRole('option', { name: 'Efectivo' }) // espera al dato: los medios llegan después del modal

    const maniana = new Date()
    maniana.setDate(maniana.getDate() + 1)
    const [anio, mes, dia] = fechaLocal(maniana).split('-')
    const campoFecha = screen.getByLabelText('Fecha')
    await userEvent.clear(campoFecha)
    await userEvent.type(campoFecha, `${dia}/${mes}/${anio}`)
    await userEvent.type(screen.getByLabelText('Concepto'), 'Pago futuro')
    await userEvent.type(screen.getByLabelText('Importe de la línea 1'), '500')
    await userEvent.selectOptions(screen.getByLabelText('Medio de la línea 1'), 'Efectivo')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    expect(campoFecha).toHaveClass('is-invalid')
    expect(await screen.findByText('Completá fecha, empresa, concepto y, en cada medio de pago, el medio y un importe mayor a 0.')).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })
})

describe('Pagos (administración) — alta', () => {
  async function abrirYCompletarFormulario() {
    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo pago' }))
    await screen.findByLabelText('Concepto')
    await screen.findByRole('option', { name: 'Efectivo' }) // espera al dato: los medios llegan después del modal
    await userEvent.type(screen.getByLabelText('Concepto'), 'Alquiler de septiembre')
    await userEvent.type(screen.getByLabelText('Importe de la línea 1'), '1500')
    await userEvent.selectOptions(screen.getByLabelText('Medio de la línea 1'), 'Efectivo')
  }

  it('arma el cuerpo del POST con fecha, empresa y medio de pago elegidos, sin turno', async () => {
    mockearRutas({})
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    renderPagos()

    await abrirYCompletarFormulario()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await waitFor(() =>
      expect(apiPostMock).toHaveBeenCalledWith(
        '/pagos/administracion',
        expect.objectContaining({
          idEmpresa: 1,
          idPuntoVenta: null,
          categoria: 'Otros',
          concepto: 'Alquiler de septiembre',
          lineas: [{ idMedioPago: 1, importe: 1500, referencia: null }],
          idComprobanteCompra: null,
        }),
      ),
    )
  })

  it('reparte el pago entre varios medios: una línea por medio, con su importe y su referencia', async () => {
    mockearRutas({ medios: [medioEfectivo, medioCuentaCorriente, medioTransferencia] })
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    renderPagos()

    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo pago' }))
    await screen.findByRole('option', { name: 'Transferencia' })
    await userEvent.type(screen.getByLabelText('Concepto'), 'Alquiler de septiembre')
    await userEvent.selectOptions(screen.getByLabelText('Medio de la línea 1'), 'Efectivo')
    await userEvent.type(screen.getByLabelText('Importe de la línea 1'), '600')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar medio' }))
    await userEvent.selectOptions(screen.getByLabelText('Medio de la línea 2'), 'Transferencia')
    await userEvent.type(screen.getByLabelText('Importe de la línea 2'), '400')
    await userEvent.type(screen.getByLabelText('Referencia de la línea 2'), 'Op 31')
    expect(screen.getByText(/Total: \$\s1\.000,00/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await waitFor(() =>
      expect(apiPostMock).toHaveBeenCalledWith(
        '/pagos/administracion',
        expect.objectContaining({
          lineas: [
            { idMedioPago: 1, importe: 600, referencia: null },
            { idMedioPago: 4, importe: 400, referencia: 'Op 31' },
          ],
        }),
      ),
    )
  })

  it('una línea inválida se rechaza nombrando la línea, sin llegar al servidor', async () => {
    mockearRutas({ medios: [medioEfectivo, medioCuentaCorriente, medioTransferencia] })
    renderPagos()

    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo pago' }))
    await screen.findByRole('option', { name: 'Transferencia' })
    await userEvent.type(screen.getByLabelText('Concepto'), 'Alquiler')
    await userEvent.selectOptions(screen.getByLabelText('Medio de la línea 1'), 'Efectivo')
    await userEvent.type(screen.getByLabelText('Importe de la línea 1'), '600')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar medio' }))
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    expect(await screen.findByText('Línea 2: elegí el medio de pago.')).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('una fecha retroactiva elegida por el admin viaja tal cual en el POST', async () => {
    mockearRutas({})
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    renderPagos()

    await abrirYCompletarFormulario()
    fireEvent.change(screen.getByLabelText('Fecha'), { target: { value: '15/01/2026' } })
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/pagos/administracion', expect.objectContaining({ fecha: '2026-01-15' })))
  })

  it('si el listado no se puede refrescar tras el alta, el éxito va en un toast y el fallo queda en el banner', async () => {
    mockearRutas({})
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    renderPagos()

    await abrirYCompletarFormulario()
    const rutasOk = apiGetMock.getMockImplementation()!
    apiGetMock.mockImplementation((ruta: string) =>
      ruta.startsWith('/pagos/administracion')
        ? Promise.reject(new ErrorApi(503, 'no_disponible', 'El listado no responde.'))
        : rutasOk(ruta),
    )
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await toastDeExito('Pago registrado.')
    await bannerDeCargaPersistente('El listado no responde.')
  })

  it('un error de carga previo sigue en pantalla, sin falso vacío, cuando el refresco posterior al alta también falla', async () => {
    mockearRutas({})
    const rutasOk = apiGetMock.getMockImplementation()!
    apiGetMock.mockImplementation((ruta: string) =>
      ruta.startsWith('/pagos/administracion')
        ? Promise.reject(new ErrorApi(503, 'no_disponible', 'El listado no responde.'))
        : rutasOk(ruta),
    )
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    renderPagos()

    await bannerDeCargaPersistente('El listado no responde.')
    await abrirYCompletarFormulario()
    const llamadasAlListado = () => apiGetMock.mock.calls.filter((c) => (c[0] as string).startsWith('/pagos/administracion')).length
    const antes = llamadasAlListado()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await toastDeExito('Pago registrado.')
    await waitFor(() => expect(llamadasAlListado()).toBeGreaterThan(antes))
    await bannerDeCargaPersistente('El listado no responde.')
    expect(screen.queryByText('No hay pagos que coincidan con los filtros.')).not.toBeInTheDocument()
  })

  it('al confirmar con éxito, limpia y cierra el formulario, y refresca el listado', async () => {
    mockearRutas({})
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    renderPagos()

    await abrirYCompletarFormulario()
    mockearRutas({ pagina: paginaFixture([pagoFixture({ id: 9, importe: 1500, concepto: 'Alquiler de septiembre' })]) })
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await toastDeExito('Pago registrado.')
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
    renderPagos()

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
    renderPagos()

    await abrirYCompletarFormulario()
    const boton = screen.getByRole('button', { name: 'Registrar' })
    fireEvent.click(boton)
    fireEvent.click(boton)

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    resolverPost({ id: 1 })
  })

  it('un concepto en blanco se rechaza sin llegar a pegarle al servidor', async () => {
    mockearRutas({})
    renderPagos()

    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo pago' }))
    await screen.findByLabelText('Concepto')
    await screen.findByRole('option', { name: 'Efectivo' }) // espera al dato: los medios llegan después del modal
    await userEvent.type(screen.getByLabelText('Importe de la línea 1'), '1500')
    await userEvent.selectOptions(screen.getByLabelText('Medio de la línea 1'), 'Efectivo')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await screen.findByText('Completá fecha, empresa, concepto y, en cada medio de pago, el medio y un importe mayor a 0.')
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('un error del servidor se muestra inline y el formulario no se cierra', async () => {
    const { ErrorApi } = await import('../api/cliente')
    mockearRutas({})
    apiPostMock.mockRejectedValueOnce(new ErrorApi(400, 'gasto_fecha_futura', 'La fecha del pago no puede ser futura.'))
    renderPagos()

    await abrirYCompletarFormulario()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await screen.findByText('La fecha del pago no puede ser futura.')
    expect(screen.getByLabelText('Concepto')).toBeInTheDocument()
  })

  it('el botón "Nuevo pago" abre el formulario en un modal y no en la página', async () => {
    mockearRutas({})
    renderPagos()
    await screen.findByRole('option', { name: 'Empresa Demo SA' })
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Nuevo pago' }))

    const dialogo = await screen.findByRole('dialog', { name: 'Nuevo pago' })
    expect(within(dialogo).getByLabelText('Concepto')).toBeInTheDocument()
  })

  it('el error del servidor y la validación local se rinden dentro del modal', async () => {
    const { ErrorApi } = await import('../api/cliente')
    mockearRutas({})
    apiPostMock.mockRejectedValueOnce(new ErrorApi(400, 'gasto_fecha_futura', 'La fecha del pago no puede ser futura.'))
    renderPagos()
    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo pago' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Nuevo pago' })
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Registrar' }))
    expect(await within(dialogo).findByRole('alert')).toHaveTextContent('Completá fecha, empresa, concepto')

    await userEvent.type(within(dialogo).getByLabelText('Concepto'), 'Alquiler')
    await userEvent.type(within(dialogo).getByLabelText('Importe de la línea 1'), '1500')
    await userEvent.selectOptions(within(dialogo).getByLabelText('Medio de la línea 1'), 'Efectivo')
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Registrar' }))

    expect(await within(dialogo).findByText('La fecha del pago no puede ser futura.')).toBeInTheDocument()
    expect(within(dialogo).getByLabelText('Concepto')).toHaveValue('Alquiler')
    expect(within(dialogo).getByRole('button', { name: 'Registrar' })).toBeEnabled()
  })

  it('cancelar descarta lo escrito sin tocar la API y el próximo alta abre vacío', async () => {
    mockearRutas({})
    renderPagos()
    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo pago' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Nuevo pago' })
    await userEvent.type(within(dialogo).getByLabelText('Concepto'), 'Alquiler')

    await userEvent.click(within(dialogo).getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole('button', { name: 'Nuevo pago' }))
    expect(await screen.findByLabelText('Concepto')).toHaveValue('')
  })

  it('Escape cierra el modal sin guardar y devuelve el foco al disparador', async () => {
    mockearRutas({})
    renderPagos()
    const disparador = await screen.findByRole('button', { name: 'Nuevo pago' })
    await userEvent.click(disparador)
    await screen.findByRole('dialog', { name: 'Nuevo pago' })

    await userEvent.keyboard('{Escape}')

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
    expect(disparador).toHaveFocus()
  })

  it('si otra lectura acuña una generación durante el alta, el modal igual se libera y se cierra al responder', async () => {
    mockearRutas({})
    let resolverPost: (valor: unknown) => void = () => undefined
    apiPostMock.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolverPost = resolve
        }),
    )
    renderPagos()
    await abrirYCompletarFormulario()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Nuevo pago' })
    await waitFor(() => expect(within(dialogo).getByRole('button', { name: 'Cancelar' })).toBeDisabled())

    // El filtro de la pantalla recarga el listado y acuña una generación nueva en pleno alta.
    fireEvent.change(screen.getAllByLabelText('Categoría')[0], { target: { value: 'Proveedor' } })
    await waitFor(() => expect(apiGetMock).toHaveBeenCalledWith(expect.stringContaining('categoria=Proveedor')))
    await act(async () => resolverPost({ id: 1 }))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    await toastDeExito('Pago registrado.')
    expect(screen.getByRole('button', { name: 'Nuevo pago' })).toBeEnabled()
  })

  it('un segundo rechazo local con el mismo mensaje vuelve a traer el error a la vista', async () => {
    const scrollIntoView = vi.fn()
    Object.defineProperty(HTMLElement.prototype, 'scrollIntoView', { configurable: true, value: scrollIntoView })
    try {
      mockearRutas({})
      renderPagos()
      await userEvent.click(await screen.findByRole('button', { name: 'Nuevo pago' }))
      const dialogo = await screen.findByRole('dialog', { name: 'Nuevo pago' })

      await userEvent.click(within(dialogo).getByRole('button', { name: 'Registrar' }))
      await within(dialogo).findByRole('alert')
      expect(scrollIntoView).toHaveBeenCalledTimes(1)

      await userEvent.click(within(dialogo).getByRole('button', { name: 'Registrar' }))

      await waitFor(() => expect(scrollIntoView).toHaveBeenCalledTimes(2))
    } finally {
      delete (HTMLElement.prototype as { scrollIntoView?: unknown }).scrollIntoView
    }
  })

  it('mientras el alta está en vuelo el modal no se puede cerrar ni cancelar', async () => {
    mockearRutas({})
    let resolverPost: (valor: unknown) => void = () => undefined
    apiPostMock.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolverPost = resolve
        }),
    )
    renderPagos()
    await abrirYCompletarFormulario()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Nuevo pago' })

    await waitFor(() => expect(within(dialogo).getByRole('button', { name: 'Cancelar' })).toBeDisabled())
    await userEvent.keyboard('{Escape}')
    expect(screen.getByRole('dialog', { name: 'Nuevo pago' })).toBeInTheDocument()

    await act(async () => resolverPost({ id: 1 }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })
})

describe('Pagos (administración) — área del alta', () => {
  const general = areaFixture({ id: 10, nombre: 'General', orden: 1 })
  const cocina = areaFixture({ id: 11, nombre: 'Cocina', orden: 2 })

  async function abrirFormulario() {
    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo pago' }))
    await screen.findByLabelText('Concepto')
  }

  async function completarYRegistrar() {
    await screen.findByRole('option', { name: 'Efectivo' }) // espera al dato: los medios llegan después del modal
    await userEvent.type(screen.getByLabelText('Concepto'), 'Alquiler de septiembre')
    await userEvent.type(screen.getByLabelText('Importe de la línea 1'), '1500')
    await userEvent.selectOptions(screen.getByLabelText('Medio de la línea 1'), 'Efectivo')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))
  }

  it('el select del alta arranca en la primera área activa, sin opción vacía, y el alta la manda', async () => {
    mockearRutas({ areas: [general, cocina] })
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    renderPagos()
    await abrirFormulario()
    await screen.findByRole('option', { name: 'General' })

    const select = screen.getByLabelText('Área')
    expect(select).toHaveValue('10')
    expect(within(select).getAllByRole('option').map((o) => o.textContent)).toEqual(['General', 'Cocina'])

    await completarYRegistrar()

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/pagos/administracion', expect.objectContaining({ idArea: 10 })))
  })

  it('el área elegida a mano es la que viaja, y al reabrir el formulario vuelve a la primera activa', async () => {
    mockearRutas({ areas: [general, cocina] })
    apiPostMock.mockResolvedValue({ id: 1 })
    renderPagos()
    await abrirFormulario()
    await screen.findByRole('option', { name: 'Cocina' })

    await userEvent.selectOptions(screen.getByLabelText('Área'), 'Cocina')
    await completarYRegistrar()
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/pagos/administracion', expect.objectContaining({ idArea: 11 })))

    await toastDeExito('Pago registrado.')
    await abrirFormulario()
    expect(screen.getByLabelText('Área')).toHaveValue('10')

    // El select puede mostrar la primera opción aunque el estado esté vacío: lo que cuenta es lo que viaja.
    await completarYRegistrar()
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(2))
    expect(apiPostMock).toHaveBeenLastCalledWith('/pagos/administracion', expect.objectContaining({ idArea: 10 }))
  })

  it('si las áreas llegan con el formulario ya abierto, el default se completa cuando aterrizan', async () => {
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
    renderPagos()
    await abrirFormulario()
    expect(screen.getByLabelText('Área')).toHaveValue('')

    await act(async () => entregarAreas([general, cocina]))

    expect(screen.getByLabelText('Área')).toHaveValue('10')
  })

  it('sin áreas el select muestra una única opción vacía deshabilitada y el alta manda idArea null', async () => {
    mockearRutas({ areas: [] })
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    renderPagos()
    await abrirFormulario()
    await screen.findAllByRole('option', { name: 'Empresa Demo SA' })

    const select = screen.getByLabelText('Área')
    const opciones = within(select).getAllByRole('option')
    expect(opciones).toHaveLength(1)
    expect(opciones[0]).toBeDisabled()
    expect(select).toHaveValue('')

    await completarYRegistrar()
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/pagos/administracion', expect.objectContaining({ idArea: null })))
  })
})

describe('Pagos (administración) — listado y filtros', () => {
  it('lista los pagos con empresa, categoría, proveedor/área resueltos y origen', async () => {
    mockearRutas({
      pagina: paginaFixture([
        pagoFixture({ id: 1, categoria: 'Proveedor', idProveedor: 1, nombreProveedor: 'Distribuidora Sur SRL' }),
        pagoFixture({ id: 2, idPuntoVenta: 7, origenFondos: 'Tesoreria' }),
      ]),
    })
    renderPagos()

    const tabla = await screen.findByRole('table')
    expect(within(tabla).getByText('Distribuidora Sur SRL')).toBeInTheDocument()
    expect(within(tabla).getAllByText('Caja general').length).toBe(2)
  })

  it('un proveedor dado de baja lógica (nombreProveedor null) se muestra como "(no disponible)" sin perder la fila', async () => {
    mockearRutas({
      pagina: paginaFixture([pagoFixture({ id: 1, categoria: 'Proveedor', idProveedor: 5, nombreProveedor: null })]),
    })
    renderPagos()

    const tabla = await screen.findByRole('table')
    expect(within(tabla).getByText('(no disponible)')).toBeInTheDocument()
  })

  it('cambiar el filtro de categoría dispara una nueva consulta con ese filtro', async () => {
    mockearRutas({})
    renderPagos()

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
    renderPagos()

    await screen.findByText('No hay pagos que coincidan con los filtros.')
  })
})

// stage-gasto-a-compra (PR4): acciones por fila para un pago sin compra ligada — "Vincular a
// compra" abre un picker filtrado por el proveedor del pago (si tiene), "Crear compra" navega.
describe('Pagos (administración) — vincular a una compra existente', () => {
  function compraListadaFixture(sobrescribir: Partial<{ id: number; idProveedor: number; numeroExterno: string | null; estado: string; fechaRecepcion: string | null; total: number; saldoPendiente: number }> = {}) {
    return { id: 9, idProveedor: 2, idTipoComprobante: 5, numeroExterno: '0003-00000009', estado: 'Confirmada', fechaRecepcion: '2026-08-20T12:00:00Z', total: 500, saldoPendiente: 320, ...sobrescribir }
  }

  function mockearConCompras(opciones: { pagina?: PaginaDePagosDeAdministracion; compras?: ReturnType<typeof compraListadaFixture>[] } = {}) {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/empresas') return Promise.resolve([empresaFixture()])
      if (ruta === '/puntos-venta') return Promise.resolve([puntoVentaFixture()])
      if (ruta === '/catalogos/medios-pago') return Promise.resolve([medioEfectivo, medioCuentaCorriente])
      if (ruta === '/catalogos/areas') return Promise.resolve([areaFixture()])
      if (ruta === '/proveedores/opciones') return Promise.resolve([proveedorFixture()])
      if (ruta.startsWith('/pagos/administracion')) return Promise.resolve(opciones.pagina ?? paginaFixture())
      if (ruta.startsWith('/compras')) {
        const items = opciones.compras ?? [compraListadaFixture()]
        return Promise.resolve({ items, total: items.length, pagina: 1, tamanio: 25 })
      }
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })
  }

  it('un pago con proveedor abre el picker filtrado por ESE proveedor', async () => {
    mockearConCompras({ pagina: paginaFixture([pagoFixture({ id: 3, idProveedor: 1, nombreProveedor: 'Distribuidora Sur SRL' })]) })
    renderPagos()

    await userEvent.click(await screen.findByRole('button', { name: 'Vincular a compra' }))

    await waitFor(() =>
      expect(apiGetMock).toHaveBeenCalledWith(expect.stringContaining('idProveedor=1')),
    )
    expect(apiGetMock).toHaveBeenCalledWith(expect.stringContaining('estado=Confirmada'))
    expect(await screen.findByText('0003-00000009')).toBeInTheDocument()
  })

  // stage-tesoreria-por-empresa (PR5): el picker filtra por la EMPRESA del pago (join PV→empresa
  // del lado del servidor) para no ofrecer nunca una compra de otra empresa.
  it('el picker filtra las compras por la empresa del pago', async () => {
    mockearConCompras({ pagina: paginaFixture([pagoFixture({ id: 3, idEmpresa: 1, idProveedor: 1, nombreProveedor: 'Distribuidora Sur SRL' })]) })
    renderPagos()

    await userEvent.click(await screen.findByRole('button', { name: 'Vincular a compra' }))

    await waitFor(() => expect(apiGetMock).toHaveBeenCalledWith(expect.stringContaining('idEmpresa=1')))
  })

  it('el picker muestra el saldo pendiente de cada compra junto a su total', async () => {
    mockearConCompras({
      pagina: paginaFixture([pagoFixture({ id: 3 })]),
      compras: [
        compraListadaFixture({ id: 9, numeroExterno: '0003-00000009', total: 500, saldoPendiente: 320 }),
        compraListadaFixture({ id: 10, numeroExterno: '0003-00000010', total: 700, saldoPendiente: 0 }),
      ],
    })
    renderPagos()

    await userEvent.click(await screen.findByRole('button', { name: 'Vincular a compra' }))

    const fila9 = (await screen.findByText('0003-00000009')).closest('tr') as HTMLElement
    const fila10 = screen.getByText('0003-00000010').closest('tr') as HTMLElement
    expect(screen.getByRole('columnheader', { name: 'Saldo pendiente' })).toBeInTheDocument()
    expect(within(fila9).getByText('$ 500,00')).toBeInTheDocument()
    expect(within(fila9).getByText('$ 320,00')).toBeInTheDocument()
    expect(within(fila10).getByText('$ 700,00')).toBeInTheDocument()
    expect(within(fila10).getByText('$ 0,00')).toBeInTheDocument()
  })

  it('elegir una compra del picker vincula el pago y refresca el listado', async () => {
    mockearConCompras({ pagina: paginaFixture([pagoFixture({ id: 3 })]) })
    apiPostMock.mockResolvedValue({ id: 3, idComprobanteCompra: 9 })
    renderPagos()

    await userEvent.click(await screen.findByRole('button', { name: 'Vincular a compra' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Elegir' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/pagos/3/vincular-compra', { idComprobanteCompra: 9 }))
    await toastDeExito('Pago vinculado a la compra.')
    // El picker se cierra tras el éxito.
    expect(screen.queryByText('Vincular a compra', { selector: 'h5' })).not.toBeInTheDocument()
  })

  it('un pago ya vinculado muestra el link a la compra en vez de las acciones', async () => {
    mockearConCompras({ pagina: paginaFixture([pagoFixture({ id: 3, idComprobanteCompra: 9 })]) })
    renderPagos()

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
    mockearConCompras({ pagina: paginaFixture([pagoFixture({ id: 3 })]) })
    let resolverPost: (valor: unknown) => void = () => undefined
    apiPostMock.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolverPost = resolve
        }),
    )
    renderPagos()

    await userEvent.click(await screen.findByRole('button', { name: 'Vincular a compra' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Elegir' }))

    // Dispara un refresco del listado (bumpea el `generacionRef` COMPARTIDO del listado) mientras
    // el vínculo sigue en vuelo — con el bug pre-PR5 esto haría que la finalización de abajo se
    // descartara como "obsoleta".
    await userEvent.selectOptions(screen.getByLabelText('Categoría'), 'Otros')

    resolverPost({ id: 3, idComprobanteCompra: 9 })

    await toastDeExito('Pago vinculado a la compra.')
    expect(screen.queryByText('Vincular a compra', { selector: 'h5' })).not.toBeInTheDocument()
  })
})

describe('Pagos (administración) — pago de un pago de personal', () => {
  it('deshabilita editar, eliminar, vincular y crear compra en un pago de un pago y deja habilitados los de un pago común', async () => {
    mockearRutas({
      pagina: paginaFixture([
        pagoFixture({ id: 40, concepto: 'Pago de personal: Ana', idMovimientoPersonal: 9 }),
        pagoFixture({ id: 41, concepto: 'Alquiler' }),
      ]),
    })
    renderPagos()

    // web-test-data-gates: se espera a la fila (el dato), no a la tabla.
    const delPago = await screen.findByRole('button', { name: 'Editar pago Pago de personal: Ana' })
    expect(delPago).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Eliminar pago Pago de personal: Ana' })).toBeDisabled()
    const filaDelPago = delPago.closest('tr') as HTMLElement
    expect(within(filaDelPago).getByRole('button', { name: 'Reasignar' })).toBeEnabled()
    expect(within(filaDelPago).queryByRole('button', { name: 'Vincular a compra' })).not.toBeInTheDocument()
    expect(within(filaDelPago).queryByRole('button', { name: 'Crear compra' })).not.toBeInTheDocument()

    expect(screen.getByRole('button', { name: 'Editar pago Alquiler' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Eliminar pago Alquiler' })).toBeEnabled()
    const filaComun = screen.getByRole('button', { name: 'Editar pago Alquiler' }).closest('tr') as HTMLElement
    expect(within(filaComun).queryByRole('button', { name: 'Reasignar' })).not.toBeInTheDocument()
    expect(within(filaComun).getByRole('button', { name: 'Vincular a compra' })).toBeEnabled()
    expect(within(filaComun).getByRole('button', { name: 'Crear compra' })).toBeEnabled()
  })
})

describe('Pagos (administración) — reasignar un pago de sueldo', () => {
  const personal = [
    { id: 5, nombre: 'Beto' },
    { id: 9, nombre: 'Ana' },
  ]

  function montar() {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta.startsWith('/personal')) return Promise.resolve({ items: personal, total: 2, pagina: 1, tamanio: 200 })
      if (ruta === '/empresas') return Promise.resolve([empresaFixture()])
      if (ruta === '/puntos-venta') return Promise.resolve([puntoVentaFixture()])
      if (ruta === '/catalogos/medios-pago') return Promise.resolve([medioEfectivo, medioCuentaCorriente])
      if (ruta === '/catalogos/areas') return Promise.resolve([areaFixture()])
      if (ruta === '/proveedores/opciones') return Promise.resolve([proveedorFixture()])
      if (ruta.startsWith('/pagos/administracion'))
        return Promise.resolve(
          paginaFixture([pagoFixture({ id: 40, concepto: 'Pago de personal: Ana', idMovimientoPersonal: 9, importe: 2500 })]),
        )
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })
    renderPagos()
  }

  function llamadasAlListado() {
    return apiGetMock.mock.calls.filter((c) => (c[0] as string).startsWith('/pagos/administracion')).length
  }

  async function abrirYElegir(nombre: string) {
    await userEvent.click(await screen.findByRole('button', { name: 'Reasignar' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Reasignar pago' })
    // web-test-data-gates: se espera la opción (el dato), no el <select> que nace vacío.
    await within(dialogo).findByRole('option', { name: nombre })
    await userEvent.selectOptions(within(dialogo).getByLabelText('Empleado'), nombre)
    return dialogo
  }

  it('muestra el pago y los empleados, envía el elegido, cierra y relee el listado', async () => {
    montar()
    apiPostMock.mockResolvedValue({ idPago: 40, idPersonalAnterior: 9, idPersonal: 5, concepto: 'Pago de personal: Beto' })

    const dialogo = await abrirYElegir('Beto')
    expect(within(dialogo).getByText('Pago de personal: Ana')).toBeInTheDocument()
    const antes = llamadasAlListado()
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Reasignar' }))

    await toastDeExito('Pago reasignado: Pago de personal: Beto.')
    expect(apiPostMock).toHaveBeenCalledTimes(1)
    expect(apiPostMock).toHaveBeenCalledWith('/pagos/administracion/40/reasignar-personal', { idPersonal: 5 })
    expect(screen.queryByRole('dialog', { name: 'Reasignar pago' })).not.toBeInTheDocument()
    await waitFor(() => expect(llamadasAlListado()).toBeGreaterThan(antes))
  })

  it('un rechazo cierto muestra el mensaje del servidor y deja el modal abierto', async () => {
    montar()
    const { ErrorApi } = await import('../api/cliente')
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'reasignacion_al_mismo_personal', 'El pago ya es de ese empleado.'))

    const dialogo = await abrirYElegir('Ana')
    const antes = llamadasAlListado()
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Reasignar' }))

    expect(await within(dialogo).findByText('El pago ya es de ese empleado.')).toBeInTheDocument()
    expect(screen.getByRole('dialog', { name: 'Reasignar pago' })).toBeInTheDocument()
    expect(llamadasAlListado()).toBe(antes)
    expect(within(dialogo).getByRole('button', { name: 'Reasignar' })).toBeEnabled()
  })

  it('un resultado incierto cierra el modal y relee el listado', async () => {
    montar()
    const { ErrorApi } = await import('../api/cliente')
    apiPostMock.mockRejectedValue(new ErrorApi(503, 'resultado_incierto', 'No se pudo confirmar.'))

    const dialogo = await abrirYElegir('Beto')
    const antes = llamadasAlListado()
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Reasignar' }))

    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Reasignar pago' })).not.toBeInTheDocument())
    await waitFor(() => expect(llamadasAlListado()).toBeGreaterThan(antes))
    const aviso = screen.getByText(/No se pudo confirmar el resultado/)
    expect(aviso.closest('.alert-warning')).not.toBeNull()
    expect(aviso.closest('.ws-toaster')).toBeNull()
    expect(screen.getAllByText(/No se pudo confirmar el resultado/)).toHaveLength(1)
  })

  it('un doble click en el mismo tick envía una sola solicitud y deja el modal inerte', async () => {
    montar()
    let resolver!: (v: unknown) => void
    apiPostMock.mockReturnValue(new Promise((r) => (resolver = r)))

    const dialogo = await abrirYElegir('Beto')
    const boton = within(dialogo).getByRole('button', { name: 'Reasignar' })
    fireEvent.click(boton)
    fireEvent.click(boton)

    expect(apiPostMock).toHaveBeenCalledTimes(1)
    expect(within(dialogo).getByLabelText('Empleado')).toBeDisabled()
    expect(within(dialogo).getByRole('button', { name: 'Cancelar' })).toBeDisabled()

    await act(async () => {
      resolver({ idPago: 40, idPersonalAnterior: 9, idPersonal: 5, concepto: 'Pago de personal: Beto' })
    })
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Reasignar pago' })).not.toBeInTheDocument())
  })

  it('no habilita Reasignar hasta elegir un empleado', async () => {
    montar()
    await userEvent.click(await screen.findByRole('button', { name: 'Reasignar' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Reasignar pago' })
    await within(dialogo).findByRole('option', { name: 'Beto' })
    expect(within(dialogo).getByRole('button', { name: 'Reasignar' })).toBeDisabled()
    expect(apiPostMock).not.toHaveBeenCalled()
  })
})

describe('Pagos (administración) — edición y baja', () => {
  const pagoDeCajaCerrado = () =>
    pagoFixture({ id: 31, concepto: 'Flete', origenFondos: 'CajaTurno', idTurnoCaja: 9, turnoAbierto: false })

  function dialogo() {
    return screen.getByRole('dialog', { name: 'Editar pago' })
  }

  function llamadasAlListado() {
    return apiGetMock.mock.calls.filter((c) => (c[0] as string).startsWith('/pagos/administracion')).length
  }

  it('editar abre el modal precargado y guarda con PUT /pagos/administracion/{id}, refrescando el listado', async () => {
    mockearRutas({ pagina: paginaFixture([pagoFixture({ id: 31, concepto: 'Alquiler', importe: 1000 })]) })
    apiPutMock.mockResolvedValueOnce({ id: 31 })
    renderPagos()

    await userEvent.click(await screen.findByRole('button', { name: 'Editar pago Alquiler' }))
    const d = within(await screen.findByRole('dialog', { name: 'Editar pago' }))
    expect(d.getByLabelText('Concepto')).toHaveValue('Alquiler')
    expect(d.getByLabelText('Importe de la línea 1')).toHaveValue('1.000,00')
    await d.findByRole('option', { name: 'Efectivo' })
    expect(d.getByLabelText('Medio de la línea 1')).toHaveValue('1')

    await userEvent.clear(d.getByLabelText('Importe de la línea 1'))
    await userEvent.type(d.getByLabelText('Importe de la línea 1'), '1200')
    const antes = llamadasAlListado()
    await userEvent.click(d.getByRole('button', { name: 'Guardar cambios' }))

    await waitFor(() =>
      expect(apiPutMock).toHaveBeenCalledWith(
        '/pagos/administracion/31',
        expect.objectContaining({
          concepto: 'Alquiler',
          categoria: 'Otros',
          lineas: [{ idMedioPago: 1, importe: 1200, referencia: null }],
        }),
      ),
    )
    await toastDeExito('Pago actualizado.')
    expect(screen.queryByRole('dialog', { name: 'Editar pago' })).not.toBeInTheDocument()
    await waitFor(() => expect(llamadasAlListado()).toBeGreaterThan(antes))
  })

  it('editar un pago con varios medios precarga una línea por medio y manda el reparto completo', async () => {
    const repartido = pagoFixture({
      id: 32,
      concepto: 'Flete',
      importe: 1000,
      lineas: [
        { idItem: 1, idMedioPago: 1, nombreMedioPago: 'Efectivo', importe: 600, referencia: null },
        { idItem: 2, idMedioPago: 4, nombreMedioPago: 'Transferencia', importe: 400, referencia: 'Op 5' },
      ],
    })
    mockearRutas({ medios: [medioEfectivo, medioCuentaCorriente, medioTransferencia], pagina: paginaFixture([repartido]) })
    apiPutMock.mockResolvedValueOnce({ id: 32 })
    renderPagos()

    await userEvent.click(await screen.findByRole('button', { name: 'Editar pago Flete' }))
    const d = within(await screen.findByRole('dialog', { name: 'Editar pago' }))
    await d.findAllByRole('option', { name: 'Transferencia' })
    expect(d.getByLabelText('Importe de la línea 1')).toHaveValue('600,00')
    expect(d.getByLabelText('Medio de la línea 2')).toHaveValue('4')
    expect(d.getByLabelText('Referencia de la línea 2')).toHaveValue('Op 5')

    await userEvent.click(d.getByRole('button', { name: 'Quitar línea 2' }))
    await userEvent.click(d.getByRole('button', { name: 'Guardar cambios' }))

    await waitFor(() =>
      expect(apiPutMock).toHaveBeenCalledWith(
        '/pagos/administracion/32',
        expect.objectContaining({ lineas: [{ idMedioPago: 1, importe: 600, referencia: null }] }),
      ),
    )
  })

  it('un pago de caja con un solo medio ofrece "Agregar medio" y guarda las dos líneas', async () => {
    mockearRutas({
      medios: [medioEfectivo, medioCuentaCorriente, medioTransferencia],
      pagina: paginaFixture([pagoFixture({ id: 31, concepto: 'Flete', importe: 1000, origenFondos: 'CajaTurno', idTurnoCaja: 9, turnoAbierto: false })]),
    })
    apiPutMock.mockResolvedValueOnce({ id: 31 })
    renderPagos()

    await userEvent.click(await screen.findByRole('button', { name: 'Editar pago Flete' }))

    const d = within(await screen.findByRole('dialog', { name: 'Editar pago' }))
    await d.findAllByRole('option', { name: 'Transferencia' })
    expect(d.getByRole('button', { name: 'Agregar medio' })).toBeEnabled()

    await userEvent.clear(d.getByLabelText('Importe de la línea 1'))
    await userEvent.type(d.getByLabelText('Importe de la línea 1'), '600')
    await userEvent.click(d.getByRole('button', { name: 'Agregar medio' }))
    await userEvent.selectOptions(d.getByLabelText('Medio de la línea 2'), 'Transferencia')
    await userEvent.type(d.getByLabelText('Importe de la línea 2'), '400')
    await userEvent.click(d.getByRole('button', { name: 'Guardar cambios' }))

    await waitFor(() =>
      expect(apiPutMock).toHaveBeenCalledWith(
        '/pagos/administracion/31',
        expect.objectContaining({
          lineas: [
            { idMedioPago: 1, importe: 600, referencia: null },
            { idMedioPago: 4, importe: 400, referencia: null },
          ],
        }),
      ),
    )
  })

  it('un pago de tesorería se edita con varios medios: ofrece "Agregar medio"', async () => {
    mockearRutas({ pagina: paginaFixture([pagoFixture({ id: 31, concepto: 'Alquiler', origenFondos: 'Tesoreria' })]) })
    renderPagos()

    await userEvent.click(await screen.findByRole('button', { name: 'Editar pago Alquiler' }))

    const d = within(await screen.findByRole('dialog', { name: 'Editar pago' }))
    expect(d.getByRole('button', { name: 'Agregar medio' })).toBeEnabled()
  })

  it('un pago de caja que ya tiene varios medios se edita completo desde la administración', async () => {
    const repartido = pagoFixture({
      id: 33,
      concepto: 'Flete',
      importe: 1000,
      origenFondos: 'CajaTurno',
      idTurnoCaja: 9,
      lineas: [
        { idItem: 1, idMedioPago: 1, nombreMedioPago: 'Efectivo', importe: 600, referencia: null },
        { idItem: 2, idMedioPago: 4, nombreMedioPago: 'Transferencia', importe: 400, referencia: null },
      ],
    })
    mockearRutas({ medios: [medioEfectivo, medioCuentaCorriente, medioTransferencia], pagina: paginaFixture([repartido]) })
    renderPagos()

    await userEvent.click(await screen.findByRole('button', { name: 'Editar pago Flete' }))

    const d = within(await screen.findByRole('dialog', { name: 'Editar pago' }))
    expect(d.getByLabelText('Importe de la línea 2')).toBeEnabled()
    expect(d.getByRole('button', { name: 'Guardar cambios' })).toBeEnabled()
    expect(d.getByRole('button', { name: 'Agregar medio' })).toBeEnabled()
  })

  it('editar un pago de caja de un turno cerrado muestra el aviso de recálculo del arqueo', async () => {
    mockearRutas({ pagina: paginaFixture([pagoDeCajaCerrado()]) })
    renderPagos()

    await userEvent.click(await screen.findByRole('button', { name: 'Editar pago Flete' }))

    expect(
      await within(dialogo()).findByText(
        'Este pago pertenece a un turno cerrado: se recalculará el arqueo del turno y quedará marcado como recalculado.',
      ),
    ).toBeInTheDocument()
  })

  it('un pago de caja general avisa del ajuste compensatorio y no del arqueo', async () => {
    mockearRutas({ pagina: paginaFixture([pagoFixture({ id: 31, concepto: 'Alquiler', origenFondos: 'Tesoreria' })]) })
    renderPagos()

    await userEvent.click(await screen.findByRole('button', { name: 'Editar pago Alquiler' }))

    expect(await within(dialogo()).findByText(/ajuste compensatorio sobre la caja general/)).toBeInTheDocument()
    expect(within(dialogo()).queryByText(/turno cerrado/)).not.toBeInTheDocument()
  })

  it('un pago ligado a una compra no deja cambiar categoría ni proveedor', async () => {
    mockearRutas({
      pagina: paginaFixture([
        pagoFixture({
          id: 31,
          concepto: 'Mercadería',
          categoria: 'Proveedor',
          idProveedor: 1,
          nombreProveedor: 'Distribuidora Sur SRL',
          idComprobanteCompra: 44,
        }),
      ]),
    })
    renderPagos()

    await userEvent.click(await screen.findByRole('button', { name: 'Editar pago Mercadería' }))

    const d = within(dialogo())
    expect(d.getByLabelText('Categoría')).toBeDisabled()
    expect(d.getByLabelText('Proveedor (opcional)')).toBeDisabled()
    expect(d.getByLabelText('Concepto')).toBeEnabled()
  })

  it('un error del servidor al editar se muestra en el modal y no lo cierra', async () => {
    mockearRutas({ pagina: paginaFixture([pagoFixture({ id: 31, concepto: 'Alquiler' })]) })
    const { ErrorApi } = await import('../api/cliente')
    apiPutMock.mockRejectedValueOnce(new ErrorApi(400, 'gasto_proveedor_ligado', 'El proveedor no se puede cambiar.'))
    renderPagos()

    await userEvent.click(await screen.findByRole('button', { name: 'Editar pago Alquiler' }))
    await userEvent.click(within(dialogo()).getByRole('button', { name: 'Guardar cambios' }))

    expect(await within(dialogo()).findByText('El proveedor no se puede cambiar.')).toBeInTheDocument()
  })

  it('eliminar pide confirmación y solo al confirmar manda el DELETE y refresca', async () => {
    mockearRutas({ pagina: paginaFixture([pagoFixture({ id: 31, concepto: 'Alquiler' })]) })
    apiDeleteMock.mockResolvedValueOnce(undefined)
    renderPagos()

    await userEvent.click(await screen.findByRole('button', { name: 'Eliminar pago Alquiler' }))
    const confirmacion = await screen.findByRole('alertdialog', { name: 'Confirmar eliminación' })
    expect(apiDeleteMock).not.toHaveBeenCalled()

    const antes = llamadasAlListado()
    await userEvent.click(within(confirmacion).getByRole('button', { name: 'Confirmar eliminación' }))

    await waitFor(() => expect(apiDeleteMock).toHaveBeenCalledWith('/pagos/administracion/31'))
    await toastDeExito('Pago eliminado.')
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    await waitFor(() => expect(llamadasAlListado()).toBeGreaterThan(antes))
  })

  it('si el listado no se puede refrescar tras eliminar, avisa el éxito con un toast y el fallo con el banner de carga persistente', async () => {
    mockearRutas({ pagina: paginaFixture([pagoFixture({ id: 31, concepto: 'Alquiler' })]) })
    apiDeleteMock.mockResolvedValueOnce(undefined)
    renderPagos()

    await userEvent.click(await screen.findByRole('button', { name: 'Eliminar pago Alquiler' }))
    const confirmacion = await screen.findByRole('alertdialog', { name: 'Confirmar eliminación' })
    const rutasOk = apiGetMock.getMockImplementation()!
    apiGetMock.mockImplementation((ruta: string) =>
      ruta.startsWith('/pagos/administracion')
        ? Promise.reject(new ErrorApi(503, 'no_disponible', 'El listado no responde.'))
        : rutasOk(ruta),
    )
    await userEvent.click(within(confirmacion).getByRole('button', { name: 'Confirmar eliminación' }))

    await toastDeExito('Pago eliminado.')
    await bannerDeCargaPersistente('El listado no responde.')
  })

  it('cancelar la confirmación no elimina nada', async () => {
    mockearRutas({ pagina: paginaFixture([pagoFixture({ id: 31, concepto: 'Alquiler' })]) })
    renderPagos()

    await userEvent.click(await screen.findByRole('button', { name: 'Eliminar pago Alquiler' }))
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(apiDeleteMock).not.toHaveBeenCalled()
  })

  it('la confirmación de baja de un pago de un turno cerrado advierte del recálculo del arqueo', async () => {
    mockearRutas({ pagina: paginaFixture([pagoDeCajaCerrado()]) })
    renderPagos()

    await userEvent.click(await screen.findByRole('button', { name: 'Eliminar pago Flete' }))

    expect(
      await within(await screen.findByRole('alertdialog')).findByText(
        /Este pago pertenece a un turno cerrado: se recalculará el arqueo del turno y quedará marcado como recalculado\./,
      ),
    ).toBeInTheDocument()
  })

  it('un fallo del DELETE se muestra y la confirmación sigue abierta', async () => {
    mockearRutas({ pagina: paginaFixture([pagoFixture({ id: 31, concepto: 'Alquiler' })]) })
    const { ErrorApi } = await import('../api/cliente')
    apiDeleteMock.mockRejectedValueOnce(new ErrorApi(404, 'no_encontrado', 'No existe el pago 31.'))
    renderPagos()

    await userEvent.click(await screen.findByRole('button', { name: 'Eliminar pago Alquiler' }))
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Confirmar eliminación' }))

    expect(await screen.findByText('No existe el pago 31.')).toBeInTheDocument()
    expect(screen.getByRole('alertdialog')).toBeInTheDocument()
  })

  it('un doble click sobre "Confirmar eliminación" en el mismo tick manda un solo DELETE', async () => {
    mockearRutas({ pagina: paginaFixture([pagoFixture({ id: 31, concepto: 'Alquiler' })]) })
    let resolver: (v: unknown) => void = () => undefined
    apiDeleteMock.mockImplementationOnce(() => new Promise((r) => (resolver = r)))
    renderPagos()

    await userEvent.click(await screen.findByRole('button', { name: 'Eliminar pago Alquiler' }))
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

describe('Pagos (administración) — enlace profundo ?id=N', () => {
  function Ubicacion() {
    return <output data-testid="ubicacion">{useLocation().search}</output>
  }

  function renderConId(ruta: string) {
    return render(
      <MemoryRouter initialEntries={[ruta]}>
        <Pagos />
        <Ubicacion />
      </MemoryRouter>,
    )
  }

  function mockearConPago(respuesta: () => Promise<PagoDeAdministracionListado>) {
    mockearRutas({ pagina: paginaFixture([]) })
    const base = apiGetMock.getMockImplementation()!
    apiGetMock.mockImplementation((ruta: string) => (/^\/pagos\/administracion\/\d+$/.test(ruta) ? respuesta() : base(ruta)))
  }

  it('con ?id abre el modal de edición de ese pago y cerrarlo quita el parámetro', async () => {
    mockearConPago(() => Promise.resolve(pagoFixture({ id: 5, concepto: 'Alquiler enlazado' })))
    renderConId('/pagos?id=5')

    const d = within(await screen.findByRole('dialog', { name: 'Editar pago' }))
    expect(d.getByLabelText('Concepto')).toHaveValue('Alquiler enlazado')
    expect(apiGetMock).toHaveBeenCalledWith('/pagos/administracion/5')
    expect(screen.getByTestId('ubicacion')).toHaveTextContent('?id=5')

    await userEvent.click(d.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('dialog', { name: 'Editar pago' })).not.toBeInTheDocument()
    expect(screen.getByTestId('ubicacion').textContent).toBe('')
  })

  it('si el pago no existe muestra el mensaje del servidor, no abre el modal y quita el parámetro', async () => {
    const { ErrorApi } = await import('../api/cliente')
    mockearConPago(() => Promise.reject(new (ErrorApi as unknown as new (e: number, c: string, m: string) => Error)(404, 'pago_no_encontrado', 'El pago no existe.')))
    renderConId('/pagos?id=999')

    expect(await screen.findByText('El pago no existe.')).toBeInTheDocument()
    expect(screen.queryByRole('dialog', { name: 'Editar pago' })).not.toBeInTheDocument()
    await waitFor(() => expect(screen.getByTestId('ubicacion').textContent).toBe(''))
  })

  it('un pago originado en personal no se abre: explica el motivo', async () => {
    mockearConPago(() => Promise.resolve(pagoFixture({ id: 6, concepto: 'Pago de personal: Ana', idMovimientoPersonal: 3 })))
    renderConId('/pagos?id=6')

    expect(await screen.findByText('Nació de un movimiento de personal y no se modifica por separado.')).toBeInTheDocument()
    expect(screen.queryByRole('dialog', { name: 'Editar pago' })).not.toBeInTheDocument()
  })

  it('un identificador que no es entero positivo se ignora sin pedir nada', async () => {
    mockearRutas({})
    renderConId('/pagos?id=abc')

    await screen.findByRole('option', { name: 'Empresa Demo SA' })
    expect(apiGetMock.mock.calls.some((c) => /^\/pagos\/administracion\/\w+$/.test(c[0] as string))).toBe(false)
    expect(screen.queryByRole('dialog', { name: 'Editar pago' })).not.toBeInTheDocument()
  })
})

describe('Pagos (administración) — sueldos a un empleado', () => {
  const empleados = [
    { id: 12, nombre: 'Marta Gómez', idEmpresa: null },
    { id: 13, nombre: 'Ana Pérez', idEmpresa: 1 },
  ] as PersonalListado[]

  function dialogo() {
    return screen.getByRole('dialog', { name: 'Nuevo pago' })
  }

  async function abrirEnSueldos() {
    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo pago' }))
    await screen.findByRole('option', { name: 'Efectivo' }) // espera al dato: los medios llegan después del modal
    await userEvent.selectOptions(within(dialogo()).getByLabelText('Categoría'), 'Sueldos')
  }

  async function cargarImporte() {
    await userEvent.type(screen.getByLabelText('Importe de la línea 1'), '1500')
    await userEvent.selectOptions(screen.getByLabelText('Medio de la línea 1'), 'Efectivo')
  }

  it('con la categoría Sueldos muestra el empleado en lugar del proveedor', async () => {
    mockearRutas({ personal: empleados })
    renderPagos()

    await abrirEnSueldos()
    await within(dialogo()).findByRole('option', { name: 'Ana Pérez' }) // espera al dato: el personal llega después del modal

    expect(within(dialogo()).getByLabelText('Empleado (opcional)')).toBeInTheDocument()
    expect(within(dialogo()).getByRole('option', { name: 'Sin empleado' })).toBeInTheDocument()
    expect(within(dialogo()).queryByLabelText('Proveedor (opcional)')).not.toBeInTheDocument()
  })

  it('con otra categoría sigue el proveedor y no hay empleado', async () => {
    mockearRutas({ personal: empleados })
    renderPagos()

    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo pago' }))
    await screen.findByRole('option', { name: 'Efectivo' })

    expect(within(dialogo()).getByLabelText('Proveedor (opcional)')).toBeInTheDocument()
    expect(within(dialogo()).queryByLabelText('Empleado (opcional)')).not.toBeInTheDocument()
  })

  it('guardar con un empleado registra un pago de personal de la tesorería y no el pago de administración', async () => {
    mockearRutas({ personal: empleados })
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    renderPagos()

    await abrirEnSueldos()
    await within(dialogo()).findByRole('option', { name: 'Ana Pérez' })
    await userEvent.selectOptions(within(dialogo()).getByLabelText('Empleado (opcional)'), 'Ana Pérez')
    await cargarImporte()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await waitFor(() =>
      expect(apiPostMock).toHaveBeenCalledWith('/personal/13/pagos', {
        fecha: fechaLocal(new Date()),
        idEmpresa: 1,
        origenFondos: 'Tesoreria',
        lineas: [{ idMedioPago: 1, importe: 1500 }],
      }),
    )
    expect(apiPostMock).toHaveBeenCalledTimes(1)
    expect(apiPostMock).not.toHaveBeenCalledWith('/pagos/administracion', expect.anything())
    await toastDeExito('Pago registrado.')
  })

  it('con un empleado oculta concepto, área y factura, que no se guardan en un pago de personal', async () => {
    mockearRutas({ personal: empleados })
    renderPagos()

    await abrirEnSueldos()
    await within(dialogo()).findByRole('option', { name: 'Ana Pérez' })
    expect(within(dialogo()).getByLabelText('Concepto')).toBeInTheDocument()

    await userEvent.selectOptions(within(dialogo()).getByLabelText('Empleado (opcional)'), 'Ana Pérez')

    expect(within(dialogo()).queryByLabelText('Concepto')).not.toBeInTheDocument()
    expect(within(dialogo()).queryByLabelText('Área')).not.toBeInTheDocument()
    expect(within(dialogo()).queryByLabelText('N° de factura (opcional)')).not.toBeInTheDocument()
    expect(within(dialogo()).getByLabelText('Detalle (opcional)')).toBeInTheDocument()
  })

  it('"Sin empleado" mantiene el pago de sueldos común contra el endpoint de administración', async () => {
    mockearRutas({ personal: empleados })
    apiPostMock.mockResolvedValueOnce({ id: 1 })
    renderPagos()

    await abrirEnSueldos()
    await within(dialogo()).findByRole('option', { name: 'Ana Pérez' })
    await userEvent.type(screen.getByLabelText('Concepto'), 'Aguinaldo')
    await cargarImporte()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    await waitFor(() =>
      expect(apiPostMock).toHaveBeenCalledWith(
        '/pagos/administracion',
        expect.objectContaining({ categoria: 'Sueldos', concepto: 'Aguinaldo', idProveedor: null }),
      ),
    )
    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })

  it('cambiar la categoría limpia el empleado elegido', async () => {
    mockearRutas({ personal: empleados })
    renderPagos()

    await abrirEnSueldos()
    await within(dialogo()).findByRole('option', { name: 'Ana Pérez' })
    await userEvent.selectOptions(within(dialogo()).getByLabelText('Empleado (opcional)'), 'Ana Pérez')

    await userEvent.selectOptions(within(dialogo()).getByLabelText('Categoría'), 'Otros')
    await userEvent.selectOptions(within(dialogo()).getByLabelText('Categoría'), 'Sueldos')

    expect(within(dialogo()).getByLabelText('Empleado (opcional)')).toHaveValue('')
    expect(within(dialogo()).getByLabelText('Concepto')).toBeInTheDocument()
  })

  it('pasar a Sueldos limpia el proveedor elegido', async () => {
    mockearRutas({ personal: empleados })
    renderPagos()

    await userEvent.click(await screen.findByRole('button', { name: 'Nuevo pago' }))
    await screen.findByRole('option', { name: 'Efectivo' })
    await userEvent.selectOptions(within(dialogo()).getByLabelText('Proveedor (opcional)'), 'Distribuidora Sur SRL')
    await userEvent.selectOptions(within(dialogo()).getByLabelText('Categoría'), 'Sueldos')
    await userEvent.selectOptions(within(dialogo()).getByLabelText('Categoría'), 'Servicios')

    expect(within(dialogo()).getByLabelText('Proveedor (opcional)')).toHaveValue('')
  })

  it('un rechazo del pago de personal se muestra dentro del modal con el copy de la cuenta del personal', async () => {
    mockearRutas({ personal: empleados })
    apiPostMock.mockRejectedValueOnce(new ErrorApi(409, 'turno_no_abierto', 'x'))
    renderPagos()

    await abrirEnSueldos()
    await within(dialogo()).findByRole('option', { name: 'Ana Pérez' })
    await userEvent.selectOptions(within(dialogo()).getByLabelText('Empleado (opcional)'), 'Ana Pérez')
    await cargarImporte()
    await userEvent.click(screen.getByRole('button', { name: 'Registrar' }))

    expect(await within(dialogo()).findByText(/No hay un turno abierto/)).toBeInTheDocument()
  })

  it('si el personal no se pudo leer avisa y deja el pago de sueldos común', async () => {
    mockearRutas({})
    const respuestaBase = apiGetMock.getMockImplementation()!
    apiGetMock.mockImplementation((ruta: string) =>
      ruta.startsWith('/personal') ? Promise.reject(new ErrorApi(500, 'error', 'Falla del servidor')) : respuestaBase(ruta),
    )
    renderPagos()

    await abrirEnSueldos()

    expect(await within(dialogo()).findByText('No se pudo cargar el personal.')).toBeInTheDocument()
    expect(within(dialogo()).getByRole('option', { name: 'Sin empleado' })).toBeInTheDocument()
  })
})

describe('Pagos (administración) — asignar un empleado a un sueldo común al editar', () => {
  const empleados = [
    { id: 12, nombre: 'Marta Gómez', idEmpresa: null },
    { id: 13, nombre: 'Ana Pérez', idEmpresa: 1 },
  ] as PersonalListado[]

  const sueldo = () => pagoFixture({ id: 61, concepto: 'Sueldo suelto', categoria: 'Sueldos', importe: 1000 })

  function dialogo() {
    return screen.getByRole('dialog', { name: 'Editar pago' })
  }

  async function abrirEdicionDelSueldo() {
    await userEvent.click(await screen.findByRole('button', { name: 'Editar pago Sueldo suelto' }))
    const d = within(await screen.findByRole('dialog', { name: 'Editar pago' }))
    await d.findByRole('option', { name: 'Ana Pérez' }) // espera al dato: el personal llega después del modal
    return d
  }

  it('un sueldo común ofrece el empleado en lugar del proveedor, con "Sin empleado" preseleccionado', async () => {
    mockearRutas({ personal: empleados, pagina: paginaFixture([sueldo()]) })
    renderPagos()

    const d = await abrirEdicionDelSueldo()

    expect(d.getByLabelText('Empleado (opcional)')).toHaveValue('')
    expect(d.getByRole('option', { name: 'Sin empleado' })).toBeInTheDocument()
    expect(d.queryByLabelText('Proveedor (opcional)')).not.toBeInTheDocument()
  })

  it('con una categoría distinta de Sueldos sigue el proveedor y no hay empleado', async () => {
    mockearRutas({ personal: empleados, pagina: paginaFixture([pagoFixture({ id: 62, concepto: 'Alquiler' })]) })
    renderPagos()

    await userEvent.click(await screen.findByRole('button', { name: 'Editar pago Alquiler' }))
    const d = within(await screen.findByRole('dialog', { name: 'Editar pago' }))

    expect(d.getByLabelText('Proveedor (opcional)')).toBeInTheDocument()
    expect(d.queryByLabelText('Empleado (opcional)')).not.toBeInTheDocument()
  })

  it('guardar con un empleado manda idPersonal, concepto vacío y sin proveedor a PUT /pagos/administracion/{id}', async () => {
    mockearRutas({ personal: empleados, pagina: paginaFixture([sueldo()]) })
    apiPutMock.mockResolvedValueOnce({ id: 61 })
    renderPagos()

    const d = await abrirEdicionDelSueldo()
    await userEvent.selectOptions(d.getByLabelText('Empleado (opcional)'), 'Ana Pérez')
    expect(d.getByLabelText('Concepto')).toBeDisabled()
    await userEvent.click(d.getByRole('button', { name: 'Guardar cambios' }))

    await waitFor(() =>
      expect(apiPutMock).toHaveBeenCalledWith(
        '/pagos/administracion/61',
        expect.objectContaining({
          categoria: 'Sueldos',
          concepto: '',
          idProveedor: null,
          idPersonal: 13,
          lineas: [{ idMedioPago: 1, importe: 1000, referencia: null }],
        }),
      ),
    )
    await toastDeExito('Pago actualizado.')
  })

  it('con "Sin empleado" guarda la edición común, sin idPersonal', async () => {
    mockearRutas({ personal: empleados, pagina: paginaFixture([sueldo()]) })
    apiPutMock.mockResolvedValueOnce({ id: 61 })
    renderPagos()

    const d = await abrirEdicionDelSueldo()
    await userEvent.click(d.getByRole('button', { name: 'Guardar cambios' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalled())
    const cuerpo = apiPutMock.mock.calls[0][1] as Record<string, unknown>
    expect(cuerpo).not.toHaveProperty('idPersonal')
    expect(cuerpo.concepto).toBe('Sueldo suelto')
  })

  it('cambiar la categoría fuera de Sueldos descarta el empleado elegido', async () => {
    mockearRutas({ personal: empleados, pagina: paginaFixture([sueldo()]) })
    apiPutMock.mockResolvedValueOnce({ id: 61 })
    renderPagos()

    const d = await abrirEdicionDelSueldo()
    await userEvent.selectOptions(d.getByLabelText('Empleado (opcional)'), 'Ana Pérez')
    await userEvent.selectOptions(d.getByLabelText('Categoría'), 'Otros')
    await userEvent.click(d.getByRole('button', { name: 'Guardar cambios' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalled())
    const cuerpo = apiPutMock.mock.calls[0][1] as Record<string, unknown>
    expect(cuerpo).not.toHaveProperty('idPersonal')
    expect(cuerpo.categoria).toBe('Otros')
  })

  it('pasar un pago con proveedor a Sueldos y guardarlo sin empleado conserva el proveedor', async () => {
    mockearRutas({
      personal: empleados,
      pagina: paginaFixture([
        pagoFixture({ id: 63, concepto: 'Honorarios', categoria: 'Otros', idProveedor: 1, nombreProveedor: 'Distribuidora Sur SRL' }),
      ]),
    })
    apiPutMock.mockResolvedValueOnce({ id: 63 })
    renderPagos()

    await userEvent.click(await screen.findByRole('button', { name: 'Editar pago Honorarios' }))
    const d = within(await screen.findByRole('dialog', { name: 'Editar pago' }))
    await userEvent.selectOptions(d.getByLabelText('Categoría'), 'Sueldos')

    expect(d.getByLabelText('Proveedor (opcional)')).toHaveValue('1')
    expect(d.queryByLabelText('Empleado (opcional)')).not.toBeInTheDocument()
    await userEvent.click(d.getByRole('button', { name: 'Guardar cambios' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalled())
    const cuerpo = apiPutMock.mock.calls[0][1] as Record<string, unknown>
    expect(cuerpo.categoria).toBe('Sueldos')
    expect(cuerpo.idProveedor).toBe(1)
    expect(cuerpo).not.toHaveProperty('idPersonal')
  })

  it('un rechazo del servidor se muestra dentro del modal y no lo cierra', async () => {
    mockearRutas({ personal: empleados, pagina: paginaFixture([sueldo()]) })
    apiPutMock.mockRejectedValueOnce(new ErrorApi(404, 'no_encontrado', 'No existe el personal 13.'))
    renderPagos()

    const d = await abrirEdicionDelSueldo()
    await userEvent.selectOptions(d.getByLabelText('Empleado (opcional)'), 'Ana Pérez')
    await userEvent.click(d.getByRole('button', { name: 'Guardar cambios' }))

    expect(await d.findByText('No existe el personal 13.')).toBeInTheDocument()
    expect(dialogo()).toBeInTheDocument()
  })
})
