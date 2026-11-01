import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { RetirosDeAdministracion } from './RetirosDeAdministracion'
import { COLUMNAS_ORDENABLES_DE_RETIROS } from '../api/caja'
import { fechaIsoConOffset } from '../api/fechas'
import type { FilaDeRetiro, PaginaDeRetiros, PuntoVentaListado, UsuarioAutenticado } from '../api/tipos'
import { ROL } from '../api/tipos'
import { Toaster } from '../toasts/Toaster'

const apiGetMock = vi.fn()
const apiPutMock = vi.fn()
const apiDeleteMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: vi.fn(),
    put: (...args: unknown[]) => apiPutMock(...(args as [string, unknown])),
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

const { ErrorApi } = await import('../api/cliente')

let usuarioActual: UsuarioAutenticado
vi.mock('../auth/useAuth', () => ({
  useAuth: () => ({ usuario: usuarioActual, cargando: false, iniciarSesion: vi.fn(), cerrarSesion: vi.fn() }),
}))

function usuarioFixture(rolId: number): UsuarioAutenticado {
  return { id: 1, usuario: 'admin', mail: 'a@ways.test', rolId, rol: rolId === ROL.Admin ? 'Admin' : 'Supervisor', ultimaConexion: null, idTenant: 1 }
}

function retiroFixture(sobrescribir: Partial<FilaDeRetiro> = {}): FilaDeRetiro {
  return {
    id: 9,
    fecha: '2026-10-30T15:00:00Z',
    idTurnoCaja: 55,
    idPuntoVenta: 7,
    puntoVenta: 'Local Centro',
    idEmpleado: 3,
    empleado: 'cajera1',
    importe: 5000,
    motivo: 'Pago a proveedor',
    turnoCerrado: false,
    conciliado: false,
    esRetiroDeCierre: false,
    ...sobrescribir,
  }
}

function paginaFixture(items: FilaDeRetiro[], sobrescribir: Partial<PaginaDeRetiros> = {}): PaginaDeRetiros {
  return { items, total: items.length, pagina: 1, tamanio: 25, ...sobrescribir }
}

const puntoVenta = { id: 7, nombre: 'Local Centro' } as PuntoVentaListado

type Rutas = { grilla: (url: string) => Promise<PaginaDeRetiros>; puntosVenta?: () => Promise<PuntoVentaListado[]> }

function mockearRutas(rutas: Rutas) {
  apiGetMock.mockImplementation((url: string) => {
    if (url.startsWith('/caja/retiros?')) return rutas.grilla(url)
    if (url.startsWith('/puntos-venta')) return rutas.puntosVenta ? rutas.puntosVenta() : Promise.resolve([puntoVenta])
    if (url.startsWith('/usuarios')) {
      return Promise.resolve({ items: [{ id: 3, usuario: 'cajera1' }, { id: 4, usuario: 'cajero2' }], total: 2, pagina: 1, tamanio: 200 })
    }
    return Promise.reject(new Error(`ruta no mockeada en el test: ${url}`))
  })
}

/** Con `eco` la respuesta repite la página y el tamaño pedidos, para recorrer páginas. */
function mockearGrilla(pagina: PaginaDeRetiros, eco = false) {
  mockearRutas({
    grilla: (url) => {
      if (!eco) return Promise.resolve(pagina)
      const parametros = new URLSearchParams(url.split('?')[1])

      return Promise.resolve({ ...pagina, pagina: Number(parametros.get('pagina')), tamanio: Number(parametros.get('tamanio')) })
    },
  })
}

function consultasDeLaGrilla(): string[] {
  return apiGetMock.mock.calls.map((c) => c[0] as string).filter((u) => u.startsWith('/caja/retiros?'))
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
      <RetirosDeAdministracion />
    </>,
  )
  await screen.findByText('Pago a proveedor')
}

async function elegir(nombreDelSelect: string, opcion: string) {
  const select = screen.getByLabelText(nombreDelSelect)
  await within(select).findByRole('option', { name: opcion })
  await userEvent.selectOptions(select, opcion)
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPutMock.mockReset()
  apiDeleteMock.mockReset()
  usuarioActual = usuarioFixture(ROL.Admin)
})

describe('RetirosDeAdministracion — lectura', () => {
  it('pide la primera página sin filtros ni orden y muestra los datos de cada columna', async () => {
    mockearGrilla(paginaFixture([retiroFixture({ turnoCerrado: true, conciliado: true, esRetiroDeCierre: true })]))
    await renderizar()

    expect([...ultimosParametros().keys()].sort()).toEqual(['pagina', 'tamanio'])

    const fila = screen.getByText('Pago a proveedor').closest('tr')!
    expect(within(fila).getByText('Local Centro')).toBeInTheDocument()
    expect(within(fila).getByText('55')).toBeInTheDocument()
    expect(within(fila).getByText('cajera1')).toBeInTheDocument()
    expect(within(fila).getByText(/5\.000/)).toBeInTheDocument()
    expect(within(fila).getByText('Cerrado')).toBeInTheDocument()
    expect(within(fila).getAllByText('Sí')).toHaveLength(2)
  })

  it('un punto de venta o un empleado dado de baja se muestra como "(no disponible)"', async () => {
    mockearGrilla(paginaFixture([retiroFixture({ idPuntoVenta: null, puntoVenta: null, idEmpleado: null, empleado: null })]))
    await renderizar()

    expect(within(screen.getByText('Pago a proveedor').closest('tr')!).getAllByText('(no disponible)')).toHaveLength(2)
  })

  it('sin retiros dice que todavía no hay; con filtros ofrece limpiarlos', async () => {
    mockearGrilla(paginaFixture([]))
    render(<RetirosDeAdministracion />)
    expect(await screen.findByText('Todavía no hay retiros.')).toBeInTheDocument()

    await elegir('Filtrar por conciliado', 'Sí')
    expect(await screen.findByText('No hay retiros que coincidan con los filtros.')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Limpiar filtros' }))
    expect(await screen.findByText('Todavía no hay retiros.')).toBeInTheDocument()
  })

  it('si la consulta falla muestra el motivo y Reintentar la vuelve a pedir', async () => {
    let intento = 0
    mockearRutas({
      grilla: () => {
        intento += 1

        return intento === 1 ? Promise.reject(new ErrorApi(500, 'x', 'Falló la consulta de retiros.')) : Promise.resolve(paginaFixture([retiroFixture()]))
      },
    })
    render(<RetirosDeAdministracion />)

    expect(await screen.findByText('Falló la consulta de retiros.')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Reintentar' }))
    expect(await screen.findByText('Pago a proveedor')).toBeInTheDocument()
  })

  it('si el catálogo de puntos de venta no carga lo avisa', async () => {
    mockearRutas({ grilla: () => Promise.resolve(paginaFixture([retiroFixture()])), puntosVenta: () => Promise.reject(new Error('caído')) })
    await renderizar()

    expect(await screen.findByText(/No se pudieron cargar los puntos de venta/)).toBeInTheDocument()
  })
})

describe('RetirosDeAdministracion — un filtro por columna', () => {
  const porSelect: [string, string, string, string][] = [
    ['punto de venta', 'Filtrar por punto de venta', 'Local Centro', 'idPuntoVenta=7'],
    ['empleado', 'Filtrar por empleado', 'cajero2', 'idEmpleado=4'],
    ['estado del turno', 'Filtrar por estado del turno', 'Cerrado', 'turnoCerrado=true'],
    ['conciliado', 'Filtrar por conciliado', 'No', 'conciliado=false'],
    ['retiro de cierre', 'Filtrar por retiro de cierre', 'Sí', 'esRetiroDeCierre=true'],
  ]

  it.each(porSelect)('%s: elegir una opción envía solo su parámetro', async (_columna, nombre, opcion, esperado) => {
    mockearGrilla(paginaFixture([retiroFixture()]))
    await renderizar()

    await elegir(nombre, opcion)

    const [clave, valor] = esperado.split('=')
    await waitFor(() => expect(ultimosParametros().get(clave)).toBe(valor))
    expect([...ultimosParametros().keys()].sort()).toEqual([clave, 'pagina', 'tamanio'].sort())
  })

  const porTexto: [string, string, string, string, string][] = [
    ['turno', 'Filtrar por turno', '55', 'idTurnoCaja', '55'],
    ['motivo', 'Filtrar por motivo', 'proveedor', 'motivo', 'proveedor'],
    ['importe desde', 'Importe desde', '100', 'importeDesde', '100'],
    ['importe hasta', 'Importe hasta', '900', 'importeHasta', '900'],
  ]

  it.each(porTexto)('%s: tipear envía solo su parámetro, con demora', async (_columna, etiqueta, texto, clave, valor) => {
    mockearGrilla(paginaFixture([retiroFixture()]))
    await renderizar()
    const consultasAntes = consultasDeLaGrilla().length

    fireEvent.change(screen.getByLabelText(etiqueta), { target: { value: texto } })
    expect(consultasDeLaGrilla()).toHaveLength(consultasAntes)

    await waitFor(() => expect(ultimosParametros().get(clave)).toBe(valor))
    expect([...ultimosParametros().keys()].sort()).toEqual([clave, 'pagina', 'tamanio'].sort())
  })

  it.each([
    ['desde', 'Fecha desde', '01112026', fechaIsoConOffset('2026-11-01', '00:00:00')],
    ['hasta', 'Fecha hasta', '30112026', fechaIsoConOffset('2026-11-30', '23:59:59.999')],
  ])('fecha %s: una fecha completa envía solo su parámetro, como límite del día local', async (clave, etiqueta, tipeado, iso) => {
    mockearGrilla(paginaFixture([retiroFixture()]))
    await renderizar()

    await userEvent.type(screen.getByLabelText(etiqueta), tipeado)

    await waitFor(() => expect(ultimosParametros().get(clave)).toBe(iso))
    expect([...ultimosParametros().keys()].sort()).toEqual([clave, 'pagina', 'tamanio'].sort())
  })

  it('varios tecleos dentro de la ventana de demora emiten una sola consulta, con el último valor', async () => {
    mockearGrilla(paginaFixture([retiroFixture()]))
    await renderizar()
    apiGetMock.mockClear()

    vi.useFakeTimers()
    try {
      fireEvent.change(screen.getByLabelText('Filtrar por motivo'), { target: { value: 'p' } })
      await vi.advanceTimersByTimeAsync(200)
      fireEvent.change(screen.getByLabelText('Filtrar por motivo'), { target: { value: 'pa' } })
      await vi.advanceTimersByTimeAsync(200)
      expect(consultasDeLaGrilla()).toHaveLength(0)
      await vi.advanceTimersByTimeAsync(100)
    } finally {
      vi.useRealTimers()
    }

    await waitFor(() => expect(consultasDeLaGrilla()).toHaveLength(1))
    expect(ultimosParametros().get('motivo')).toBe('pa')
  })

  it('un turno que no es un entero no viaja', async () => {
    mockearGrilla(paginaFixture([retiroFixture()]))
    await renderizar()

    fireEvent.change(screen.getByLabelText('Filtrar por turno'), { target: { value: '12x' } })
    await new Promise((r) => setTimeout(r, 400))

    expect(ultimosParametros().has('idTurnoCaja')).toBe(false)
  })

  it('para Supervisor el filtro de empleado ofrece a los empleados de las páginas vistas y no pide usuarios', async () => {
    usuarioActual = usuarioFixture(ROL.Supervisor)
    mockearGrilla(paginaFixture([retiroFixture({ id: 1, idEmpleado: 3, empleado: 'cajera1' }), retiroFixture({ id: 2, motivo: 'Otro', idEmpleado: 3, empleado: 'cajera1' })]))
    await renderizar()

    await screen.findByRole('option', { name: 'cajera1' })
    expect(screen.getAllByRole('option', { name: 'cajera1' })).toHaveLength(1)
    expect(apiGetMock.mock.calls.some((c) => (c[0] as string).startsWith('/usuarios'))).toBe(false)
  })
})

describe('RetirosDeAdministracion — orden por columna', () => {
  const columnas: [string, string][] = [
    ['fecha', 'Fecha'],
    ['puntoVenta', 'Punto de venta'],
    ['turno', 'Turno'],
    ['empleado', 'Empleado'],
    ['importe', 'Importe'],
    ['motivo', 'Motivo'],
    ['estadoTurno', 'Estado del turno'],
    ['conciliado', 'Conciliado'],
    ['esRetiroDeCierre', 'Retiro de cierre'],
  ]

  it('cubre todas las columnas que el servidor sabe ordenar', () => {
    expect(columnas.map(([clave]) => clave).sort()).toEqual([...COLUMNAS_ORDENABLES_DE_RETIROS].sort())
  })

  it.each(columnas)('%s: el encabezado recorre ascendente, descendente y sin orden', async (clave, titulo) => {
    mockearGrilla(paginaFixture([retiroFixture()]))
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
    mockearGrilla(paginaFixture([retiroFixture()], { total: 60 }), true)
    await renderizar()
    await elegir('Filtrar por conciliado', 'No')
    await userEvent.click(await screen.findByRole('button', { name: 'Siguiente' }))
    await waitFor(() => expect(ultimosParametros().get('pagina')).toBe('2'))

    await userEvent.click(screen.getByRole('button', { name: 'Importe' }))

    await waitFor(() => expect(ultimosParametros().get('orden')).toBe('importe'))
    expect(ultimosParametros().get('pagina')).toBe('1')
    expect(ultimosParametros().get('conciliado')).toBe('false')
  })
})

describe('RetirosDeAdministracion — paginación y limpieza', () => {
  it('cambiar un filtro vuelve a la página 1', async () => {
    mockearGrilla(paginaFixture([retiroFixture()], { total: 60 }), true)
    await renderizar()
    await userEvent.click(screen.getByRole('button', { name: 'Siguiente' }))
    await waitFor(() => expect(ultimosParametros().get('pagina')).toBe('2'))

    await elegir('Filtrar por conciliado', 'No')

    await waitFor(() => expect(ultimosParametros().get('conciliado')).toBe('false'))
    expect(ultimosParametros().get('pagina')).toBe('1')
  })

  it('un filtro de texto también vuelve a la página 1', async () => {
    mockearGrilla(paginaFixture([retiroFixture()], { total: 60 }), true)
    await renderizar()
    await userEvent.click(screen.getByRole('button', { name: 'Siguiente' }))
    await waitFor(() => expect(ultimosParametros().get('pagina')).toBe('2'))

    fireEvent.change(screen.getByLabelText('Filtrar por motivo'), { target: { value: 'pago' } })

    await waitFor(() => expect(ultimosParametros().get('motivo')).toBe('pago'))
    expect(ultimosParametros().get('pagina')).toBe('1')
  })

  it('cambiar el tamaño de página vuelve a la página 1 y Limpiar lo conserva junto con el orden', async () => {
    mockearGrilla(paginaFixture([retiroFixture()], { total: 60 }), true)
    await renderizar()
    await userEvent.click(screen.getByRole('button', { name: 'Siguiente' }))
    await userEvent.selectOptions(screen.getByLabelText('Retiros por página'), '50')
    await waitFor(() => expect(ultimosParametros().get('tamanio')).toBe('50'))
    expect(ultimosParametros().get('pagina')).toBe('1')
    await userEvent.click(screen.getByRole('button', { name: 'Fecha' }))
    await elegir('Filtrar por conciliado', 'No')
    await waitFor(() => expect(ultimosParametros().get('conciliado')).toBe('false'))

    await userEvent.click(screen.getByRole('button', { name: 'Limpiar' }))

    await waitFor(() => expect(ultimosParametros().has('conciliado')).toBe(false))
    expect(ultimosParametros().get('tamanio')).toBe('50')
    expect(ultimosParametros().get('orden')).toBe('fecha')
  })

  it('Limpiar vacía también los campos de texto y está deshabilitado sin filtros', async () => {
    mockearGrilla(paginaFixture([retiroFixture()]))
    await renderizar()
    expect(screen.getByRole('button', { name: 'Limpiar' })).toBeDisabled()

    fireEvent.change(screen.getByLabelText('Filtrar por motivo'), { target: { value: 'pago' } })
    await waitFor(() => expect(ultimosParametros().get('motivo')).toBe('pago'))
    await userEvent.click(screen.getByRole('button', { name: 'Limpiar' }))

    await waitFor(() => expect(ultimosParametros().has('motivo')).toBe(false))
    expect(screen.getByLabelText('Filtrar por motivo')).toHaveValue('')
  })

  it('Anterior y Siguiente se deshabilitan en los extremos', async () => {
    mockearGrilla(paginaFixture([retiroFixture()], { total: 30 }), true)
    await renderizar()
    expect(screen.getByRole('button', { name: 'Anterior' })).toBeDisabled()

    await userEvent.click(screen.getByRole('button', { name: 'Siguiente' }))

    await waitFor(() => expect(screen.getByText(/Página 2 de 2/)).toBeInTheDocument())
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeDisabled()
  })

  it('una respuesta tardía de una consulta anterior no pisa la de los filtros vigentes', async () => {
    const lenta = diferido<PaginaDeRetiros>()
    let llamada = 0
    mockearRutas({
      grilla: () => {
        llamada += 1
        if (llamada === 1) return Promise.resolve(paginaFixture([retiroFixture()]))
        if (llamada === 2) return lenta.promesa

        return Promise.resolve(paginaFixture([retiroFixture({ id: 3, motivo: 'Vigente' })]))
      },
    })
    await renderizar()

    await elegir('Filtrar por conciliado', 'No')
    await waitFor(() => expect(llamada).toBe(2))
    await elegir('Filtrar por retiro de cierre', 'No')
    expect(await screen.findByText('Vigente')).toBeInTheDocument()

    await act(async () => {
      lenta.resolver(paginaFixture([retiroFixture({ id: 2, motivo: 'Tardía' })]))
    })

    expect(screen.queryByText('Tardía')).not.toBeInTheDocument()
    expect(screen.getByText('Vigente')).toBeInTheDocument()
  })
})

describe('RetirosDeAdministracion — permisos y estado de las acciones', () => {
  it('Supervisor lee pero no ve acciones', async () => {
    usuarioActual = usuarioFixture(ROL.Supervisor)
    mockearGrilla(paginaFixture([retiroFixture()]))
    await renderizar()

    expect(screen.queryByRole('button', { name: /^Editar/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /^Eliminar/ })).not.toBeInTheDocument()
  })

  it('Admin ve Editar y Eliminar habilitados en un retiro común', async () => {
    mockearGrilla(paginaFixture([retiroFixture()]))
    await renderizar()

    expect(screen.getByRole('button', { name: /^Editar/ })).toBeEnabled()
    expect(screen.getByRole('button', { name: /^Eliminar/ })).toBeEnabled()
  })

  it('el retiro de cierre no se edita ni se elimina y lo explica', async () => {
    mockearGrilla(paginaFixture([retiroFixture({ esRetiroDeCierre: true })]))
    await renderizar()

    for (const nombre of [/^Editar/, /^Eliminar/]) {
      const boton = screen.getByRole('button', { name: nombre })
      expect(boton).toBeDisabled()
      expect(boton).toHaveAttribute('title', expect.stringContaining('retiro de cierre de turno'))
      expect(boton.parentElement).toHaveAttribute('title', expect.stringContaining('retiro de cierre de turno'))
    }
  })

  it('un retiro de una fecha conciliada no se edita ni se elimina y lo explica', async () => {
    mockearGrilla(paginaFixture([retiroFixture({ conciliado: true })]))
    await renderizar()

    for (const nombre of [/^Editar/, /^Eliminar/]) {
      const boton = screen.getByRole('button', { name: nombre })
      expect(boton).toBeDisabled()
      expect(boton).toHaveAttribute('title', expect.stringContaining('conciliada'))
    }
  })

  it('el motivo del retiro de cierre gana sobre el de la conciliación', async () => {
    mockearGrilla(paginaFixture([retiroFixture({ esRetiroDeCierre: true, conciliado: true })]))
    await renderizar()

    expect(screen.getByRole('button', { name: /^Editar/ })).toHaveAttribute('title', expect.stringContaining('retiro de cierre de turno'))
  })
})

describe('RetirosDeAdministracion — editar', () => {
  async function abrirEdicion() {
    await userEvent.click(screen.getByRole('button', { name: /^Editar/ }))

    return within(await screen.findByRole('dialog', { name: 'Editar retiro' }))
  }

  it('abre con el importe y el motivo actuales, y avisa del recálculo solo si el turno está cerrado', async () => {
    mockearGrilla(paginaFixture([retiroFixture({ turnoCerrado: true })]))
    await renderizar()

    const dialogo = await abrirEdicion()

    expect(dialogo.getByLabelText('Importe')).toHaveValue('5.000,00')
    expect(dialogo.getByLabelText('Motivo')).toHaveValue('Pago a proveedor')
    expect(dialogo.getByText(/se recalcula el arqueo/)).toBeInTheDocument()
    expect(dialogo.getByRole('button', { name: 'Guardar cambio' })).toBeDisabled()
  })

  it('con el turno abierto no menciona el arqueo', async () => {
    mockearGrilla(paginaFixture([retiroFixture({ turnoCerrado: false })]))
    await renderizar()

    const dialogo = await abrirEdicion()

    expect(dialogo.queryByText(/recalcula el arqueo/)).not.toBeInTheDocument()
  })

  it('valida importe mayor a 0 y motivo de al menos 5 caracteres antes de dejar guardar', async () => {
    mockearGrilla(paginaFixture([retiroFixture()]))
    await renderizar()
    const dialogo = await abrirEdicion()

    fireEvent.change(dialogo.getByLabelText('Importe'), { target: { value: '0' } })
    expect(dialogo.getByText('El importe tiene que ser mayor a 0.')).toBeInTheDocument()
    expect(dialogo.getByRole('button', { name: 'Guardar cambio' })).toBeDisabled()

    fireEvent.change(dialogo.getByLabelText('Importe'), { target: { value: '100' } })
    fireEvent.change(dialogo.getByLabelText('Motivo'), { target: { value: 'abc ' } })
    expect(dialogo.getByText('El motivo tiene que tener al menos 5 caracteres.')).toBeInTheDocument()
    expect(dialogo.getByRole('button', { name: 'Guardar cambio' })).toBeDisabled()

    fireEvent.change(dialogo.getByLabelText('Motivo'), { target: { value: 'abcde' } })
    expect(dialogo.getByRole('button', { name: 'Guardar cambio' })).toBeEnabled()
  })

  it('guarda con el motivo recortado, avisa y vuelve a pedir el listado con los mismos filtros, orden y página', async () => {
    mockearGrilla(paginaFixture([retiroFixture()], { total: 60 }), true)
    apiPutMock.mockResolvedValue(retiroFixture({ importe: 250 }))
    await renderizar()
    await elegir('Filtrar por conciliado', 'No')
    await userEvent.click(screen.getByRole('button', { name: 'Importe' }))
    await userEvent.click(screen.getByRole('button', { name: 'Siguiente' }))
    await waitFor(() => expect(ultimosParametros().get('pagina')).toBe('2'))
    const dialogo = await abrirEdicion()
    fireEvent.change(dialogo.getByLabelText('Importe'), { target: { value: '250' } })
    fireEvent.change(dialogo.getByLabelText('Motivo'), { target: { value: '  Pago corregido  ' } })
    const antes = consultasDeLaGrilla().length

    await userEvent.click(dialogo.getByRole('button', { name: 'Guardar cambio' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    expect(apiPutMock).toHaveBeenCalledWith('/caja/retiros/9', { importe: 250, motivo: 'Pago corregido' })
    expect(await screen.findByText('Retiro actualizado.')).toBeInTheDocument()
    expect(screen.queryByRole('dialog', { name: 'Editar retiro' })).not.toBeInTheDocument()
    await waitFor(() => expect(consultasDeLaGrilla()).toHaveLength(antes + 1))
    expect(ultimosParametros().get('conciliado')).toBe('false')
    expect(ultimosParametros().get('orden')).toBe('importe')
    expect(ultimosParametros().get('pagina')).toBe('2')
  })

  it('dos clics en el mismo tick envían una sola escritura y el modal queda inerte mientras vuela', async () => {
    mockearGrilla(paginaFixture([retiroFixture()]))
    const escritura = diferido<FilaDeRetiro>()
    apiPutMock.mockReturnValue(escritura.promesa)
    await renderizar()
    const dialogo = await abrirEdicion()
    fireEvent.change(dialogo.getByLabelText('Importe'), { target: { value: '250' } })
    const guardar = dialogo.getByRole('button', { name: 'Guardar cambio' })

    act(() => {
      guardar.click()
      guardar.click()
    })

    expect(apiPutMock).toHaveBeenCalledTimes(1)
    expect(dialogo.getByLabelText('Importe')).toBeDisabled()
    expect(dialogo.getByLabelText('Motivo')).toBeDisabled()
    expect(dialogo.getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    await act(async () => {
      escritura.resolver(retiroFixture({ importe: 250 }))
    })
    await screen.findByText('Retiro actualizado.')
    expect(apiPutMock).toHaveBeenCalledTimes(1)
  })

  const rechazos: [string, number, string][] = [
    ['retiro_de_cierre_no_editable', 409, 'El retiro de cierre de turno lo genera el sistema al cerrar: no se puede modificar ni eliminar.'],
    ['movimiento_de_caja_importe_invalido', 400, 'El importe tiene que ser mayor a 0.'],
    ['movimiento_de_caja_sin_motivo', 400, 'El motivo tiene que tener al menos 5 caracteres.'],
    ['periodo_conciliado', 409, 'La fecha de este retiro ya está conciliada en tesorería: no se puede modificar ni eliminar.'],
    ['no_encontrado', 404, 'El retiro ya no existe: alguien lo eliminó. Se actualizó el listado.'],
  ]

  it.each(rechazos)('el rechazo %s se muestra en español dentro del modal, que sigue abierto', async (codigo, estado, texto) => {
    mockearGrilla(paginaFixture([retiroFixture()]))
    apiPutMock.mockRejectedValue(new ErrorApi(estado, codigo, 'texto del servidor'))
    await renderizar()
    const dialogo = await abrirEdicion()
    fireEvent.change(dialogo.getByLabelText('Importe'), { target: { value: '250' } })

    await userEvent.click(dialogo.getByRole('button', { name: 'Guardar cambio' }))

    expect(await dialogo.findByRole('alert')).toHaveTextContent(texto)
    expect(screen.queryByText('Retiro actualizado.')).not.toBeInTheDocument()
  })

  it.each([[404], [409]])('un %s vuelve a pedir el listado porque la fila quedó vieja', async (estado) => {
    mockearGrilla(paginaFixture([retiroFixture()]))
    apiPutMock.mockRejectedValue(new ErrorApi(estado, 'x', 'x'))
    await renderizar()
    const dialogo = await abrirEdicion()
    fireEvent.change(dialogo.getByLabelText('Importe'), { target: { value: '250' } })
    const antes = consultasDeLaGrilla().length

    await userEvent.click(dialogo.getByRole('button', { name: 'Guardar cambio' }))

    await waitFor(() => expect(consultasDeLaGrilla()).toHaveLength(antes + 1))
  })

  it('un rechazo sin relación con la fila no vuelve a pedir el listado y usa el mensaje del servidor', async () => {
    mockearGrilla(paginaFixture([retiroFixture()]))
    apiPutMock.mockRejectedValue(new ErrorApi(403, 'prohibido', 'No tenés permiso para editar retiros.'))
    await renderizar()
    const dialogo = await abrirEdicion()
    fireEvent.change(dialogo.getByLabelText('Importe'), { target: { value: '250' } })
    const antes = consultasDeLaGrilla().length

    await userEvent.click(dialogo.getByRole('button', { name: 'Guardar cambio' }))

    expect(await dialogo.findByRole('alert')).toHaveTextContent('No tenés permiso para editar retiros.')
    expect(consultasDeLaGrilla()).toHaveLength(antes)
  })

  it('un fallo de red muestra un mensaje genérico', async () => {
    mockearGrilla(paginaFixture([retiroFixture()]))
    apiPutMock.mockRejectedValue(new Error('sin red'))
    await renderizar()
    const dialogo = await abrirEdicion()
    fireEvent.change(dialogo.getByLabelText('Importe'), { target: { value: '250' } })

    await userEvent.click(dialogo.getByRole('button', { name: 'Guardar cambio' }))

    expect(await dialogo.findByRole('alert')).toHaveTextContent('No se pudo guardar el retiro.')
  })

  it('el rechazo de la acción anterior desaparece al tocar un campo y al reintentar', async () => {
    mockearGrilla(paginaFixture([retiroFixture()]))
    apiPutMock.mockRejectedValueOnce(new ErrorApi(403, 'prohibido', 'Rechazo previo.'))
    await renderizar()
    const dialogo = await abrirEdicion()
    fireEvent.change(dialogo.getByLabelText('Importe'), { target: { value: '250' } })
    await userEvent.click(dialogo.getByRole('button', { name: 'Guardar cambio' }))
    expect(await dialogo.findByText('Rechazo previo.')).toBeInTheDocument()

    fireEvent.change(dialogo.getByLabelText('Importe'), { target: { value: '300' } })
    expect(dialogo.queryByText('Rechazo previo.')).not.toBeInTheDocument()

    apiPutMock.mockRejectedValueOnce(new ErrorApi(403, 'prohibido', 'Rechazo previo.'))
    await userEvent.click(dialogo.getByRole('button', { name: 'Guardar cambio' }))
    expect(await dialogo.findByText('Rechazo previo.')).toBeInTheDocument()
    fireEvent.change(dialogo.getByLabelText('Motivo'), { target: { value: 'Motivo nuevo' } })
    expect(dialogo.queryByText('Rechazo previo.')).not.toBeInTheDocument()

    const pendiente = diferido<FilaDeRetiro>()
    apiPutMock.mockRejectedValueOnce(new ErrorApi(403, 'prohibido', 'Rechazo previo.'))
    await userEvent.click(dialogo.getByRole('button', { name: 'Guardar cambio' }))
    expect(await dialogo.findByText('Rechazo previo.')).toBeInTheDocument()
    apiPutMock.mockReturnValueOnce(pendiente.promesa)
    fireEvent.change(dialogo.getByLabelText('Motivo'), { target: { value: 'Motivo otra vez' } })
    await userEvent.click(dialogo.getByRole('button', { name: 'Guardar cambio' }))
    expect(dialogo.queryByText('Rechazo previo.')).not.toBeInTheDocument()
    await act(async () => {
      pendiente.resolver(retiroFixture())
    })
  })

  it('cancelar no escribe ni pide el listado y devuelve el foco al botón de la fila', async () => {
    mockearGrilla(paginaFixture([retiroFixture()]))
    await renderizar()
    const dialogo = await abrirEdicion()
    const antes = consultasDeLaGrilla().length

    await userEvent.click(dialogo.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('dialog', { name: 'Editar retiro' })).not.toBeInTheDocument()
    expect(apiPutMock).not.toHaveBeenCalled()
    expect(consultasDeLaGrilla()).toHaveLength(antes)
    await waitFor(() => expect(screen.getByRole('button', { name: /^Editar/ })).toHaveFocus())
  })

  it('mientras el modal está abierto el resto de las filas y el paginador quedan inertes', async () => {
    mockearGrilla(paginaFixture([retiroFixture({ id: 1 }), retiroFixture({ id: 2, motivo: 'Otro retiro' })], { total: 60 }))
    await renderizar()

    await userEvent.click(screen.getAllByRole('button', { name: /^Editar/ })[0])

    expect(screen.getAllByRole('button', { name: /^Editar/ })[1]).toBeDisabled()
    expect(screen.getAllByRole('button', { name: /^Eliminar/ })[1]).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeDisabled()
  })
})

describe('RetirosDeAdministracion — eliminar', () => {
  async function pedirBaja() {
    await userEvent.click(screen.getByRole('button', { name: /^Eliminar/ }))

    return within(await screen.findByRole('alertdialog'))
  }

  it('pide confirmación nombrando el retiro y cancelar no escribe', async () => {
    mockearGrilla(paginaFixture([retiroFixture()]))
    await renderizar()

    const confirmacion = await pedirBaja()
    expect(confirmacion.getByText(/Pago a proveedor/)).toBeInTheDocument()

    await userEvent.click(confirmacion.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(apiDeleteMock).not.toHaveBeenCalled()
    await waitFor(() => expect(screen.getByRole('button', { name: /^Eliminar/ })).toHaveFocus())
  })

  it('avisa del recálculo del arqueo solo con el turno cerrado', async () => {
    mockearGrilla(paginaFixture([retiroFixture({ turnoCerrado: true })]))
    await renderizar()
    expect((await pedirBaja()).getByText(/se recalcula el arqueo/)).toBeInTheDocument()
  })

  it('con el turno abierto no menciona el arqueo', async () => {
    mockearGrilla(paginaFixture([retiroFixture({ turnoCerrado: false })]))
    await renderizar()
    expect((await pedirBaja()).queryByText(/recalcula el arqueo/)).not.toBeInTheDocument()
  })

  it('confirmar elimina, avisa y vuelve a pedir el listado con los mismos filtros', async () => {
    mockearGrilla(paginaFixture([retiroFixture()]))
    apiDeleteMock.mockResolvedValue(undefined)
    await renderizar()
    await elegir('Filtrar por conciliado', 'No')
    await waitFor(() => expect(ultimosParametros().get('conciliado')).toBe('false'))
    const confirmacion = await pedirBaja()
    const antes = consultasDeLaGrilla().length

    await userEvent.click(confirmacion.getByRole('button', { name: 'Confirmar eliminación' }))

    await waitFor(() => expect(apiDeleteMock).toHaveBeenCalledWith('/caja/retiros/9'))
    expect(await screen.findByText('Retiro eliminado.')).toBeInTheDocument()
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    await waitFor(() => expect(consultasDeLaGrilla()).toHaveLength(antes + 1))
    expect(ultimosParametros().get('conciliado')).toBe('false')
  })

  it('dos clics en el mismo tick sobre Confirmar envían un solo DELETE y la pantalla queda inerte hasta el refresco', async () => {
    mockearGrilla(paginaFixture([retiroFixture({ id: 1 }), retiroFixture({ id: 2, motivo: 'Otro retiro' })]))
    const escritura = diferido<void>()
    apiDeleteMock.mockReturnValue(escritura.promesa)
    await renderizar()
    await userEvent.click(screen.getAllByRole('button', { name: /^Eliminar/ })[0])
    const confirmar = within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Confirmar eliminación' })

    act(() => {
      confirmar.click()
      confirmar.click()
    })

    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
    expect(screen.getAllByRole('button', { name: /^Eliminar/ })[1]).toBeDisabled()
    await act(async () => {
      escritura.resolver()
    })
    await screen.findByText('Retiro eliminado.')
    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
  })

  it('si el refresco posterior falla, el aviso de éxito se mantiene y aparece Reintentar', async () => {
    let llamada = 0
    mockearRutas({
      grilla: () => {
        llamada += 1

        return llamada === 2 ? Promise.reject(new ErrorApi(500, 'x', 'No se pudo refrescar.')) : Promise.resolve(paginaFixture([retiroFixture()]))
      },
    })
    apiDeleteMock.mockResolvedValue(undefined)
    await renderizar()
    const confirmacion = await pedirBaja()

    await userEvent.click(confirmacion.getByRole('button', { name: 'Confirmar eliminación' }))

    expect(await screen.findByText('No se pudo refrescar.')).toBeInTheDocument()
    expect(screen.getByText('Retiro eliminado.')).toBeInTheDocument()
  })

  it.each(rechazosDeBaja())('el rechazo %s se muestra en español dentro de la confirmación, que sigue abierta', async (codigo, estado, texto) => {
    mockearGrilla(paginaFixture([retiroFixture()]))
    apiDeleteMock.mockRejectedValue(new ErrorApi(estado, codigo, 'texto del servidor'))
    await renderizar()
    const confirmacion = await pedirBaja()

    await userEvent.click(confirmacion.getByRole('button', { name: 'Confirmar eliminación' }))

    expect(await screen.findByRole('alertdialog')).toHaveTextContent(texto)
    expect(screen.queryByText('Retiro eliminado.')).not.toBeInTheDocument()
  })

  it('un 404 en la baja vuelve a pedir el listado porque la fila quedó vieja', async () => {
    mockearGrilla(paginaFixture([retiroFixture()]))
    apiDeleteMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'x'))
    await renderizar()
    const confirmacion = await pedirBaja()
    const antes = consultasDeLaGrilla().length

    await userEvent.click(confirmacion.getByRole('button', { name: 'Confirmar eliminación' }))

    await waitFor(() => expect(consultasDeLaGrilla()).toHaveLength(antes + 1))
  })

  it('un fallo de red en la baja muestra un mensaje genérico', async () => {
    mockearGrilla(paginaFixture([retiroFixture()]))
    apiDeleteMock.mockRejectedValue(new Error('sin red'))
    await renderizar()
    const confirmacion = await pedirBaja()

    await userEvent.click(confirmacion.getByRole('button', { name: 'Confirmar eliminación' }))

    expect(await screen.findByRole('alertdialog')).toHaveTextContent('No se pudo eliminar el retiro.')
  })
})

function rechazosDeBaja(): [string, number, string][] {
  return [
    ['retiro_de_cierre_no_editable', 409, 'El retiro de cierre de turno lo genera el sistema al cerrar'],
    ['periodo_conciliado', 409, 'La fecha de este retiro ya está conciliada en tesorería'],
    ['no_encontrado', 404, 'El retiro ya no existe'],
  ]
}
