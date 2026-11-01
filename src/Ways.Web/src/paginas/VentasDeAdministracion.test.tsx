import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { VentasDeAdministracion } from './VentasDeAdministracion'
import { COLUMNAS_ORDENABLES_DE_VENTAS_DE_ADMINISTRACION } from '../api/ventas'
import type {
  ComprobanteEmitido,
  FilaDeVentaDeAdministracion,
  MedioPagoListado,
  PaginaDeVentasDeAdministracion,
  PuntoVentaListado,
  TipoComprobanteListado,
  UsuarioAutenticado,
} from '../api/tipos'
import { ROL } from '../api/tipos'
import { Toaster } from '../toasts/Toaster'

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
      this.name = 'ErrorApi'
      this.estado = estado
      this.codigo = codigo
    }
  },
}))

const { ErrorApi } = await import('../api/cliente')

let usuarioActual: UsuarioAutenticado
vi.mock('../auth/useAuth', () => ({
  useAuth: () => ({ usuario: usuarioActual, cargando: false, iniciarSesion: vi.fn(), cerrarSesion: vi.fn() }),
}))

type PropsDelModal = {
  venta: { id: number; numeroVisible: string }
  medios: readonly MedioPagoListado[]
  modo?: string
  onGuardado: (comprobante: ComprobanteEmitido) => void
  onCerrar: () => void
}
let propsDelModal: PropsDelModal | null = null
vi.mock('../componentes/ModalDeCambioDeMedios', () => ({
  ModalDeCambioDeMedios: (props: PropsDelModal) => {
    propsDelModal = props
    return (
      <div role="dialog" aria-label="Cambio de medios simulado">
        <button type="button" onClick={() => props.onGuardado({ numeroVisible: props.venta.numeroVisible } as ComprobanteEmitido)}>
          Simular guardado
        </button>
        <button type="button" onClick={props.onCerrar}>
          Simular cierre
        </button>
      </div>
    )
  },
}))

function usuarioFixture(rolId: number): UsuarioAutenticado {
  return { id: 1, usuario: 'admin', mail: 'a@ways.test', rolId, rol: rolId === ROL.Admin ? 'Admin' : 'Supervisor', ultimaConexion: null, idTenant: 1 }
}

function ventaFixture(sobrescribir: Partial<FilaDeVentaDeAdministracion> = {}): FilaDeVentaDeAdministracion {
  return {
    id: 1,
    numero: 1,
    numeroVisible: '0007-00000001',
    fecha: '2026-10-30T15:00:00Z',
    idTipoComprobante: 6,
    codigoTipo: 'FB',
    tipo: 'Factura B',
    idPuntoVenta: 7,
    puntoVenta: 'Local Centro',
    idTurnoCaja: 55,
    turnoCerrado: false,
    idCliente: 3,
    cliente: 'Cliente Uno',
    idEmpleado: 3,
    empleado: 'cajera1',
    total: 1500,
    estado: 'Emitido',
    estadoFiscal: 'sin_factura',
    medios: 'Efectivo',
    conciliado: false,
    medioEditable: true,
    motivoMedioNoEditable: null,
    ...sobrescribir,
  }
}

function paginaFixture(
  items: FilaDeVentaDeAdministracion[],
  sobrescribir: Partial<PaginaDeVentasDeAdministracion> = {},
): PaginaDeVentasDeAdministracion {
  return { items, total: items.length, pagina: 1, tamanio: 25, ...sobrescribir }
}

function medioFixture(id: number, nombre: string): MedioPagoListado {
  return {
    id,
    nombre,
    activo: true,
    idEmpresa: null,
    orden: id,
    comportamiento: 'Electronico',
    admiteVuelto: false,
    requiereReferencia: false,
    recargoPorcentaje: null,
    porcentajeFacturacion: 0,
  }
}

function tipoFixture(id: number, nombre: string, clase: 'Venta' | 'Compra'): TipoComprobanteListado {
  return {
    id,
    clase,
    codigo: nombre,
    nombre,
    letra: null,
    signo: 1,
    discriminaIva: false,
    esFiscal: false,
    afectaStock: true,
    codigoAfip: null,
    activo: true,
    registraLibroIva: false,
  }
}

const puntoVenta = { id: 7, nombre: 'Local Centro' } as PuntoVentaListado

type Rutas = {
  grilla: (url: string) => Promise<PaginaDeVentasDeAdministracion>
  puntosVenta?: () => Promise<PuntoVentaListado[]>
}

function mockearRutas(rutas: Rutas) {
  apiGetMock.mockImplementation((url: string) => {
    if (url.startsWith('/ventas/administracion?')) return rutas.grilla(url)
    if (url.startsWith('/puntos-venta')) return rutas.puntosVenta ? rutas.puntosVenta() : Promise.resolve([puntoVenta])
    if (url.startsWith('/catalogos/medios-pago')) return Promise.resolve([medioFixture(1, 'Efectivo'), medioFixture(2, 'Tarjeta')])
    if (url.startsWith('/catalogos-fiscales/tipos-comprobante')) {
      return Promise.resolve([tipoFixture(6, 'Factura B', 'Venta'), tipoFixture(40, 'Factura de compra', 'Compra')])
    }
    if (url.startsWith('/usuarios')) {
      return Promise.resolve({ items: [{ id: 3, usuario: 'cajera1' }, { id: 4, usuario: 'cajero2' }], total: 2, pagina: 1, tamanio: 200 })
    }
    return Promise.reject(new Error(`ruta no mockeada en el test: ${url}`))
  })
}

/** Con `eco` la respuesta repite la página y el tamaño pedidos, para recorrer páginas. */
function mockearGrilla(pagina: PaginaDeVentasDeAdministracion, eco = false) {
  mockearRutas({
    grilla: (url) => {
      if (!eco) return Promise.resolve(pagina)
      const parametros = new URLSearchParams(url.split('?')[1])

      return Promise.resolve({ ...pagina, pagina: Number(parametros.get('pagina')), tamanio: Number(parametros.get('tamanio')) })
    },
  })
}

function consultasDeLaGrilla(): string[] {
  return apiGetMock.mock.calls.map((c) => c[0] as string).filter((u) => u.startsWith('/ventas/administracion?'))
}

function ultimosParametros(): URLSearchParams {
  const consultas = consultasDeLaGrilla()

  return new URLSearchParams(consultas[consultas.length - 1].split('?')[1])
}

function diferido<T>() {
  let resolver!: (valor: T) => void
  let rechazar!: (error: unknown) => void
  const promesa = new Promise<T>((resolve, reject) => {
    resolver = resolve
    rechazar = reject
  })

  return { promesa, resolver, rechazar }
}

async function renderizar() {
  render(
    <>
      <Toaster />
      <VentasDeAdministracion />
    </>,
  )
  await screen.findByText('0007-00000001')
}

/** Los selects de filtro arrancan sin opciones: se espera a la opción concreta, no al select (web-test-data-gates). */
async function elegir(nombreDelSelect: string, opcion: string) {
  const select = screen.getByLabelText(nombreDelSelect)
  await within(select).findByRole('option', { name: opcion })
  await userEvent.selectOptions(select, opcion)
}

beforeEach(() => {
  apiGetMock.mockReset()
  propsDelModal = null
  usuarioActual = usuarioFixture(ROL.Admin)
})

describe('VentasDeAdministracion — lectura', () => {
  it('pide la primera página sin filtros ni orden y muestra los datos de cada columna', async () => {
    mockearGrilla(paginaFixture([ventaFixture({ idEmpleado: 3, empleado: 'cajera1', turnoCerrado: true, estadoFiscal: 'con_cae', conciliado: true })]))
    await renderizar()

    const parametros = ultimosParametros()
    expect([...parametros.keys()].sort()).toEqual(['pagina', 'tamanio'])
    expect(parametros.get('pagina')).toBe('1')
    expect(parametros.get('tamanio')).toBe('25')

    const fila = screen.getByText('0007-00000001').closest('tr')!
    expect(within(fila).getByText('Factura B')).toBeInTheDocument()
    expect(within(fila).getByText('Local Centro')).toBeInTheDocument()
    expect(within(fila).getByText('55')).toBeInTheDocument()
    expect(within(fila).getByText('Cliente Uno')).toBeInTheDocument()
    expect(within(fila).getByText('cajera1')).toBeInTheDocument()
    expect(within(fila).getByText('Efectivo')).toBeInTheDocument()
    expect(within(fila).getByText(/1\.500/)).toBeInTheDocument()
    expect(within(fila).getByText('Emitido')).toBeInTheDocument()
    expect(within(fila).getByText('Con CAE')).toBeInTheDocument()
    expect(within(fila).getByText('Cerrado')).toBeInTheDocument()
    expect(within(fila).getByText('Sí')).toBeInTheDocument()
  })

  it('un punto de venta o un empleado dado de baja se muestra como "(no disponible)" y una venta sin turno ni cobros, con guion', async () => {
    mockearGrilla(
      paginaFixture([
        ventaFixture({ idPuntoVenta: null, puntoVenta: null, idEmpleado: null, empleado: null, idTurnoCaja: null, turnoCerrado: null, medios: null }),
      ]),
    )
    await renderizar()

    const fila = screen.getByText('0007-00000001').closest('tr')!
    expect(within(fila).getAllByText('(no disponible)')).toHaveLength(2)
    expect(within(fila).getAllByText('—').length).toBeGreaterThanOrEqual(3)
  })

  it('sin ventas: dice que todavía no hay; con filtros activos ofrece limpiarlos', async () => {
    mockearGrilla(paginaFixture([]))
    render(<VentasDeAdministracion />)
    expect(await screen.findByText('Todavía no hay ventas.')).toBeInTheDocument()

    await elegir('Filtrar por estado', 'Anulado')
    expect(await screen.findByText('No hay ventas que coincidan con los filtros.')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Limpiar filtros' }))
    expect(await screen.findByText('Todavía no hay ventas.')).toBeInTheDocument()
  })

  it('si la consulta falla muestra el motivo y Reintentar la vuelve a pedir', async () => {
    let intento = 0
    mockearRutas({
      grilla: () => {
        intento += 1

        return intento === 1 ? Promise.reject(new ErrorApi(500, 'x', 'Falló la consulta de ventas.')) : Promise.resolve(paginaFixture([ventaFixture()]))
      },
    })
    render(<VentasDeAdministracion />)

    expect(await screen.findByText('Falló la consulta de ventas.')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Reintentar' }))
    expect(await screen.findByText('0007-00000001')).toBeInTheDocument()
    expect(screen.queryByText('Falló la consulta de ventas.')).not.toBeInTheDocument()
  })

  it('si un catálogo de filtros no carga lo avisa y el resto de la pantalla sigue operando', async () => {
    mockearRutas({ grilla: () => Promise.resolve(paginaFixture([ventaFixture()])), puntosVenta: () => Promise.reject(new Error('caído')) })
    await renderizar()

    expect(await screen.findByText(/No se pudieron cargar los filtros de puntos de venta/)).toBeInTheDocument()
  })

  it('los tipos del filtro son solo los de venta', async () => {
    mockearGrilla(paginaFixture([ventaFixture()]))
    await renderizar()

    await screen.findByRole('option', { name: 'Factura B' })
    expect(screen.queryByRole('option', { name: 'Factura de compra' })).not.toBeInTheDocument()
  })
})

describe('VentasDeAdministracion — un filtro por columna', () => {
  const porSelect: [string, string, string, string][] = [
    ['tipo', 'Filtrar por tipo', 'Factura B', 'idTipoComprobante=6'],
    ['punto de venta', 'Filtrar por punto de venta', 'Local Centro', 'idPuntoVenta=7'],
    ['empleado', 'Filtrar por empleado', 'cajera1', 'idEmpleado=3'],
    ['medios', 'Filtrar por medio de pago', 'Tarjeta', 'idMedioPago=2'],
    ['estado', 'Filtrar por estado', 'Anulado', 'estado=Anulado'],
    ['estado fiscal', 'Filtrar por estado fiscal', 'Con CAE', 'estadoFiscal=con_cae'],
    ['estado del turno', 'Filtrar por estado del turno', 'Cerrado', 'turnoCerrado=true'],
    ['conciliado', 'Filtrar por conciliado', 'Sí', 'conciliado=true'],
  ]

  it.each(porSelect)('%s: elegir una opción envía solo su parámetro', async (_columna, nombre, opcion, esperado) => {
    mockearGrilla(paginaFixture([ventaFixture()]))
    await renderizar()

    await elegir(nombre, opcion)

    const [clave, valor] = esperado.split('=')
    await waitFor(() => expect(ultimosParametros().get(clave)).toBe(valor))
    expect([...ultimosParametros().keys()].sort()).toEqual([clave, 'pagina', 'tamanio'].sort())
  })

  const porTexto: [string, string, string, string, string][] = [
    ['número', 'Filtrar por número', '123', 'numero', '123'],
    ['turno', 'Filtrar por turno', '55', 'idTurnoCaja', '55'],
    ['cliente', 'Filtrar por cliente', 'Perez', 'cliente', 'Perez'],
    ['total desde', 'Total desde', '100', 'totalDesde', '100'],
    ['total hasta', 'Total hasta', '900', 'totalHasta', '900'],
  ]

  it.each(porTexto)('%s: tipear envía solo su parámetro, con demora', async (_columna, etiqueta, texto, clave, valor) => {
    mockearGrilla(paginaFixture([ventaFixture()]))
    await renderizar()
    const consultasAntes = consultasDeLaGrilla().length

    fireEvent.change(screen.getByLabelText(etiqueta), { target: { value: texto } })
    expect(consultasDeLaGrilla()).toHaveLength(consultasAntes)

    await waitFor(() => expect(ultimosParametros().get(clave)).toBe(valor))
    expect([...ultimosParametros().keys()].sort()).toEqual([clave, 'pagina', 'tamanio'].sort())
  })

  it.each([
    ['desde', 'Fecha desde', '01112026', '2026-11-01'],
    ['hasta', 'Fecha hasta', '30112026', '2026-11-30'],
  ])('fecha %s: una fecha completa envía solo su parámetro', async (clave, etiqueta, tipeado, iso) => {
    mockearGrilla(paginaFixture([ventaFixture()]))
    await renderizar()

    await userEvent.type(screen.getByLabelText(etiqueta), tipeado)

    await waitFor(() => expect(ultimosParametros().get(clave)).toBe(iso))
    expect([...ultimosParametros().keys()].sort()).toEqual([clave, 'pagina', 'tamanio'].sort())
  })

  it('varios tecleos dentro de la ventana de demora emiten una sola consulta, con el último valor', async () => {
    mockearGrilla(paginaFixture([ventaFixture()]))
    await renderizar()
    apiGetMock.mockClear()

    vi.useFakeTimers()
    try {
      fireEvent.change(screen.getByLabelText('Filtrar por cliente'), { target: { value: 'P' } })
      await vi.advanceTimersByTimeAsync(200)
      fireEvent.change(screen.getByLabelText('Filtrar por cliente'), { target: { value: 'Pe' } })
      await vi.advanceTimersByTimeAsync(200)
      expect(consultasDeLaGrilla()).toHaveLength(0)
      await vi.advanceTimersByTimeAsync(100)
    } finally {
      vi.useRealTimers()
    }

    await waitFor(() => expect(consultasDeLaGrilla()).toHaveLength(1))
    expect(ultimosParametros().get('cliente')).toBe('Pe')
  })

  it('un número o un turno que no es un entero no viaja', async () => {
    mockearGrilla(paginaFixture([ventaFixture()]))
    await renderizar()

    fireEvent.change(screen.getByLabelText('Filtrar por número'), { target: { value: 'abc' } })
    fireEvent.change(screen.getByLabelText('Filtrar por turno'), { target: { value: '12x' } })
    await new Promise((r) => setTimeout(r, 400))

    expect(ultimosParametros().has('numero')).toBe(false)
    expect(ultimosParametros().has('idTurnoCaja')).toBe(false)
  })

  it('para Supervisor el filtro de empleado ofrece a los empleados de las páginas vistas, sin repetirlos ni pedir usuarios', async () => {
    usuarioActual = usuarioFixture(ROL.Supervisor)
    mockearGrilla(
      paginaFixture([
        ventaFixture({ id: 1, idEmpleado: 3, empleado: 'cajera1' }),
        ventaFixture({ id: 2, numeroVisible: '0007-00000002', idEmpleado: 3, empleado: 'cajera1' }),
        ventaFixture({ id: 3, numeroVisible: '0007-00000003', idEmpleado: 9, empleado: 'cajero9' }),
      ]),
    )
    await renderizar()

    await screen.findByRole('option', { name: 'cajero9' })
    expect(screen.getAllByRole('option', { name: 'cajera1' })).toHaveLength(1)
    expect(apiGetMock.mock.calls.some((c) => (c[0] as string).startsWith('/usuarios'))).toBe(false)
  })
})

describe('VentasDeAdministracion — orden por columna', () => {
  const columnas: [string, string][] = [
    ['fecha', 'Fecha'],
    ['numero', 'Número'],
    ['tipo', 'Tipo'],
    ['puntoVenta', 'Punto de venta'],
    ['turno', 'Turno'],
    ['cliente', 'Cliente'],
    ['empleado', 'Empleado'],
    ['medios', 'Medios de pago'],
    ['total', 'Total'],
    ['estado', 'Estado'],
    ['estadoFiscal', 'Estado fiscal'],
    ['estadoTurno', 'Estado del turno'],
    ['conciliado', 'Conciliado'],
  ]

  it('cubre todas las columnas que el servidor sabe ordenar', () => {
    expect(columnas.map(([clave]) => clave).sort()).toEqual([...COLUMNAS_ORDENABLES_DE_VENTAS_DE_ADMINISTRACION].sort())
  })

  it.each(columnas)('%s: el encabezado recorre ascendente, descendente y sin orden', async (clave, titulo) => {
    mockearGrilla(paginaFixture([ventaFixture()]))
    await renderizar()
    const boton = () => screen.getByRole('button', { name: titulo })

    await userEvent.click(boton())
    await waitFor(() => expect(ultimosParametros().get('orden')).toBe(clave))
    expect(ultimosParametros().get('direccion')).toBe('ascendente')

    await userEvent.click(boton())
    await waitFor(() => expect(ultimosParametros().get('direccion')).toBe('descendente'))
    expect(ultimosParametros().get('orden')).toBe(clave)

    await userEvent.click(boton())
    await waitFor(() => expect(ultimosParametros().has('orden')).toBe(false))
    expect(ultimosParametros().has('direccion')).toBe(false)
  })

  it('ordenar conserva los filtros y vuelve a la página 1', async () => {
    mockearGrilla(paginaFixture([ventaFixture()], { total: 60 }))
    await renderizar()
    await elegir('Filtrar por estado', 'Anulado')
    await userEvent.click(await screen.findByRole('button', { name: 'Siguiente' }))
    await waitFor(() => expect(ultimosParametros().get('pagina')).toBe('2'))

    await userEvent.click(screen.getByRole('button', { name: /^Total/ }))

    await waitFor(() => expect(ultimosParametros().get('orden')).toBe('total'))
    expect(ultimosParametros().get('pagina')).toBe('1')
    expect(ultimosParametros().get('estado')).toBe('Anulado')
  })
})

describe('VentasDeAdministracion — paginación y limpieza', () => {
  it('cambiar un filtro vuelve a la página 1', async () => {
    mockearGrilla(paginaFixture([ventaFixture()], { total: 60 }))
    await renderizar()
    await userEvent.click(screen.getByRole('button', { name: 'Siguiente' }))
    await waitFor(() => expect(ultimosParametros().get('pagina')).toBe('2'))

    await elegir('Filtrar por conciliado', 'No')

    await waitFor(() => expect(ultimosParametros().get('conciliado')).toBe('false'))
    expect(ultimosParametros().get('pagina')).toBe('1')
  })

  it('un filtro de texto también vuelve a la página 1', async () => {
    mockearGrilla(paginaFixture([ventaFixture()], { total: 60 }))
    await renderizar()
    await userEvent.click(screen.getByRole('button', { name: 'Siguiente' }))
    await waitFor(() => expect(ultimosParametros().get('pagina')).toBe('2'))

    fireEvent.change(screen.getByLabelText('Filtrar por cliente'), { target: { value: 'Perez' } })

    await waitFor(() => expect(ultimosParametros().get('cliente')).toBe('Perez'))
    expect(ultimosParametros().get('pagina')).toBe('1')
  })

  it('cambiar el tamaño de página vuelve a la página 1 y Limpiar lo conserva junto con el orden', async () => {
    mockearGrilla(paginaFixture([ventaFixture()], { total: 60 }))
    await renderizar()
    await userEvent.click(screen.getByRole('button', { name: 'Siguiente' }))
    await userEvent.selectOptions(screen.getByLabelText('Ventas por página'), '50')
    await waitFor(() => expect(ultimosParametros().get('tamanio')).toBe('50'))
    expect(ultimosParametros().get('pagina')).toBe('1')
    await userEvent.click(screen.getByRole('button', { name: /^Fecha/ }))
    await elegir('Filtrar por estado', 'Anulado')
    await waitFor(() => expect(ultimosParametros().get('estado')).toBe('Anulado'))

    await userEvent.click(screen.getByRole('button', { name: 'Limpiar' }))

    await waitFor(() => expect(ultimosParametros().has('estado')).toBe(false))
    expect(ultimosParametros().get('tamanio')).toBe('50')
    expect(ultimosParametros().get('orden')).toBe('fecha')
  })

  it('Limpiar vacía también los campos de texto y está deshabilitado sin filtros', async () => {
    mockearGrilla(paginaFixture([ventaFixture()]))
    await renderizar()
    expect(screen.getByRole('button', { name: 'Limpiar' })).toBeDisabled()

    fireEvent.change(screen.getByLabelText('Filtrar por cliente'), { target: { value: 'Perez' } })
    await waitFor(() => expect(ultimosParametros().get('cliente')).toBe('Perez'))
    await userEvent.click(screen.getByRole('button', { name: 'Limpiar' }))

    await waitFor(() => expect(ultimosParametros().has('cliente')).toBe(false))
    expect(screen.getByLabelText('Filtrar por cliente')).toHaveValue('')
  })

  it('Anterior y Siguiente se deshabilitan en los extremos', async () => {
    mockearGrilla(paginaFixture([ventaFixture()], { total: 30 }), true)
    await renderizar()
    expect(screen.getByRole('button', { name: 'Anterior' })).toBeDisabled()
    expect(screen.getByText(/Página 1 de 2/)).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Siguiente' }))

    await waitFor(() => expect(screen.getByText(/Página 2 de 2/)).toBeInTheDocument())
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Anterior' })).toBeEnabled()
  })

  it('si la página pedida quedó fuera de rango pide la última válida', async () => {
    mockearRutas({
      grilla: (url) =>
        Promise.resolve(
          url.includes('pagina=2') ? paginaFixture([], { total: 10, pagina: 2 }) : paginaFixture([ventaFixture()], { total: 60, pagina: 1 }),
        ),
    })
    await renderizar()

    await userEvent.click(screen.getByRole('button', { name: 'Siguiente' }))

    await waitFor(() => expect(consultasDeLaGrilla().some((u) => u.includes('pagina=2'))).toBe(true))
    await waitFor(() => expect(ultimosParametros().get('pagina')).toBe('1'))
    expect(await screen.findByText('0007-00000001')).toBeInTheDocument()
  })

  it('una respuesta tardía de una consulta anterior no pisa la de los filtros vigentes', async () => {
    const lenta = diferido<PaginaDeVentasDeAdministracion>()
    let llamada = 0
    mockearRutas({
      grilla: () => {
        llamada += 1
        if (llamada === 1) return Promise.resolve(paginaFixture([ventaFixture()]))
        if (llamada === 2) return lenta.promesa

        return Promise.resolve(paginaFixture([ventaFixture({ id: 3, numeroVisible: '0007-00000003' })]))
      },
    })
    await renderizar()

    await elegir('Filtrar por estado', 'Anulado')
    await waitFor(() => expect(llamada).toBe(2))
    await elegir('Filtrar por conciliado', 'No')
    expect(await screen.findByText('0007-00000003')).toBeInTheDocument()

    await act(async () => {
      lenta.resolver(paginaFixture([ventaFixture({ id: 2, numeroVisible: '0007-00000002' })]))
    })

    expect(screen.queryByText('0007-00000002')).not.toBeInTheDocument()
    expect(screen.getByText('0007-00000003')).toBeInTheDocument()
  })
})

describe('VentasDeAdministracion — cambiar el medio de pago', () => {
  it('Supervisor lee pero no ve acciones ni pide usuarios', async () => {
    usuarioActual = usuarioFixture(ROL.Supervisor)
    mockearGrilla(paginaFixture([ventaFixture()]))
    await renderizar()

    expect(screen.queryByRole('button', { name: /Cambiar medio de pago/ })).not.toBeInTheDocument()
  })

  it('Admin ve la acción por fila, habilitada cuando la venta es editable', async () => {
    mockearGrilla(paginaFixture([ventaFixture()]))
    await renderizar()

    const boton = screen.getByRole('button', { name: 'Cambiar medio de pago de la venta 0007-00000001' })
    await waitFor(() => expect(boton).toBeEnabled())
    expect(boton).toHaveAttribute('title', 'Cambiar medio de pago')
  })

  it.each([
    ['venta_con_cae', 'La venta tiene factura con CAE: anulala y volvé a cargarla para cambiar el medio de pago.'],
    ['periodo_conciliado', 'La fecha de esta venta ya está conciliada en tesorería: no se puede cambiar su medio de pago.'],
    ['cambio_de_medios_con_cuenta_corriente', 'No se puede cambiar el medio de una venta cobrada con cuenta corriente, ni pasarla a cuenta corriente.'],
    ['venta_anulada', 'La venta está anulada: no se puede cambiar su medio de pago.'],
    ['motivo_desconocido', 'motivo_desconocido'],
    [null, 'No se puede cambiar el medio de pago de esta venta.'],
  ])('si el servidor dice %s la acción queda deshabilitada y explica por qué', async (motivo, texto) => {
    mockearGrilla(paginaFixture([ventaFixture({ medioEditable: false, motivoMedioNoEditable: motivo })]))
    await renderizar()

    const boton = screen.getByRole('button', { name: /Cambiar medio de pago/ })
    expect(boton).toBeDisabled()
    expect(boton).toHaveAttribute('title', texto)
    expect(boton.parentElement).toHaveAttribute('title', texto)
  })

  it('abre el modal de administración con la venta y el catálogo completo de medios', async () => {
    mockearGrilla(paginaFixture([ventaFixture()]))
    await renderizar()
    const boton = screen.getByRole('button', { name: /Cambiar medio de pago/ })
    await waitFor(() => expect(boton).toBeEnabled())

    await userEvent.click(boton)

    expect(await screen.findByRole('dialog', { name: 'Cambio de medios simulado' })).toBeInTheDocument()
    expect(propsDelModal?.venta).toEqual({ id: 1, numeroVisible: '0007-00000001' })
    expect(propsDelModal?.modo).toBe('administracion')
    expect(propsDelModal?.medios.map((m) => m.nombre)).toEqual(['Efectivo', 'Tarjeta'])
  })

  it('mientras el modal está abierto las filas, el paginador y Limpiar quedan inertes', async () => {
    mockearGrilla(paginaFixture([ventaFixture({ id: 1 }), ventaFixture({ id: 2, numeroVisible: '0007-00000002' })], { total: 60 }))
    await renderizar()
    const botones = () => screen.getAllByRole('button', { name: /Cambiar medio de pago/ })
    await waitFor(() => expect(botones()[0]).toBeEnabled())

    await userEvent.click(botones()[0])

    expect(botones()[1]).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeDisabled()
    expect(screen.getByLabelText('Ventas por página')).toBeDisabled()
  })

  it('al guardar avisa, cierra y vuelve a pedir el listado con los mismos filtros, orden y página', async () => {
    mockearGrilla(paginaFixture([ventaFixture()], { total: 60 }))
    await renderizar()
    await elegir('Filtrar por estado', 'Emitido')
    await userEvent.click(screen.getByRole('button', { name: /^Total/ }))
    await userEvent.click(screen.getByRole('button', { name: 'Siguiente' }))
    await waitFor(() => expect(ultimosParametros().get('pagina')).toBe('2'))
    const boton = screen.getByRole('button', { name: /Cambiar medio de pago/ })
    await waitFor(() => expect(boton).toBeEnabled())
    await userEvent.click(boton)
    const antes = consultasDeLaGrilla().length

    await userEvent.click(screen.getByRole('button', { name: 'Simular guardado' }))

    expect(await screen.findByText('Se cambió el medio de pago de la venta 0007-00000001.')).toBeInTheDocument()
    expect(screen.queryByRole('dialog', { name: 'Cambio de medios simulado' })).not.toBeInTheDocument()
    await waitFor(() => expect(consultasDeLaGrilla()).toHaveLength(antes + 1))
    expect(ultimosParametros().get('estado')).toBe('Emitido')
    expect(ultimosParametros().get('orden')).toBe('total')
    expect(ultimosParametros().get('pagina')).toBe('2')
  })

  it('si el refresco posterior falla, el aviso de éxito se mantiene y el listado ofrece Reintentar', async () => {
    let llamada = 0
    mockearRutas({
      grilla: () => {
        llamada += 1

        return llamada === 2 ? Promise.reject(new ErrorApi(500, 'x', 'No se pudo refrescar.')) : Promise.resolve(paginaFixture([ventaFixture()]))
      },
    })
    await renderizar()
    const boton = screen.getByRole('button', { name: /Cambiar medio de pago/ })
    await waitFor(() => expect(boton).toBeEnabled())
    await userEvent.click(boton)

    await userEvent.click(screen.getByRole('button', { name: 'Simular guardado' }))

    expect(await screen.findByText('No se pudo refrescar.')).toBeInTheDocument()
    expect(screen.getByText('Se cambió el medio de pago de la venta 0007-00000001.')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Reintentar' }))
    expect(await screen.findByText('0007-00000001')).toBeInTheDocument()
  })

  it('cerrar sin guardar no pide el listado y devuelve el foco al botón de la fila', async () => {
    mockearGrilla(paginaFixture([ventaFixture()]))
    await renderizar()
    const boton = screen.getByRole('button', { name: /Cambiar medio de pago/ })
    await waitFor(() => expect(boton).toBeEnabled())
    await userEvent.click(boton)
    const antes = consultasDeLaGrilla().length

    await userEvent.click(screen.getByRole('button', { name: 'Simular cierre' }))

    expect(screen.queryByRole('dialog', { name: 'Cambio de medios simulado' })).not.toBeInTheDocument()
    expect(consultasDeLaGrilla()).toHaveLength(antes)
    await waitFor(() => expect(screen.getByRole('button', { name: /Cambiar medio de pago/ })).toHaveFocus())
  })

  it('con el catálogo de medios sin cargar la acción queda deshabilitada', async () => {
    apiGetMock.mockImplementation((url: string) => {
      if (url.startsWith('/ventas/administracion?')) return Promise.resolve(paginaFixture([ventaFixture()]))
      if (url.startsWith('/catalogos/medios-pago')) return new Promise(() => {})
      if (url.startsWith('/usuarios')) return Promise.resolve({ items: [], total: 0, pagina: 1, tamanio: 200 })

      return Promise.resolve([])
    })
    await renderizar()

    expect(screen.getByRole('button', { name: /Cambiar medio de pago/ })).toBeDisabled()
  })
})
