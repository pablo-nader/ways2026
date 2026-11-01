import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, useLocation } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ReporteDeVentasPorArticulo } from './ReporteDeVentasPorArticulo'
import { rangoDeLaSemanaActual } from '../api/reportes'
import type {
  AreaListado,
  CategoriaListado,
  EmpresaListado,
  GrupoListado,
  ComprobanteEmitido,
  PuntoVentaListado,
  TicketsDeArticulo,
  VentasPorArticulo,
} from '../api/tipos'

const apiGetMock = vi.fn()
const apiDescargarMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: vi.fn(),
    put: vi.fn(),
    delete: vi.fn(),
    descargar: (...args: unknown[]) => apiDescargarMock(...(args as [string])),
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

const empresaUno = { id: 1, idTenant: 1, razonSocial: 'Empresa Uno SA', nombreFantasia: null } as EmpresaListado
const empresaDos = { id: 2, idTenant: 1, razonSocial: 'Empresa Dos SA', nombreFantasia: null } as EmpresaListado
const pvUno = { id: 11, idEmpresa: 1, nombre: 'Sucursal Centro' } as PuntoVentaListado
const pvDos = { id: 22, idEmpresa: 2, nombre: 'Sucursal Norte' } as PuntoVentaListado
const areaAlmacen: AreaListado = { id: 1, nombre: 'Almacén', activo: true, idEmpresa: null, orden: 1 }
const grupoCigarrillos: GrupoListado = { id: 30, nombre: 'Cigarrillos', activo: true, idEmpresa: null, margen: null }
const categoriaTabaco: CategoriaListado = { id: 10, nombre: 'Tabaco', activo: true, idEmpresa: null, orden: 1, idCategoriaPadre: null }

function respuestaFixture(sobrescribir: Partial<VentasPorArticulo> = {}): VentasPorArticulo {
  return {
    desde: '2026-10-12',
    hasta: '2026-10-18',
    zonaHoraria: 'America/Argentina/Buenos_Aires',
    cantidadTotal: 13.5,
    total: 12500,
    articulos: [
      {
        idArticulo: 1,
        codigo: 'MARL-20',
        descripcion: 'Marlboro box 20',
        area: 'Almacén',
        grupo: 'Cigarrillos',
        categoria: 'Tabaco',
        cantidad: 10,
        total: 10000,
      },
      {
        idArticulo: 2,
        codigo: null,
        descripcion: 'Fiambre suelto',
        area: null,
        grupo: null,
        categoria: null,
        cantidad: 3.5,
        total: 2500,
      },
    ],
    ...sobrescribir,
  }
}

function ticketsFixture(idArticulo: number): TicketsDeArticulo {
  return {
    desde: '2026-10-12',
    hasta: '2026-10-18',
    zonaHoraria: 'America/Argentina/Buenos_Aires',
    idArticulo,
    descripcion: idArticulo === 1 ? 'Marlboro box 20' : 'Fiambre suelto',
    cantidadTotal: 4,
    total: 4000,
    tickets: [
      {
        idComprobante: 345,
        fecha: '2026-10-15T01:30:00+00:00',
        puntoVenta: 'Sucursal Centro',
        comprobante: 'Ticket 0001-00000345',
        cliente: null,
        cantidad: 4,
        total: 4000,
      },
    ],
  }
}

function comprobanteFixture(): ComprobanteEmitido {
  return {
    id: 345,
    numero: 345,
    numeroVisible: '0001-00000345',
    estado: 'Emitido',
    fecha: '2026-10-15T01:30:00+00:00',
    idPuntoVenta: 11,
    idCliente: 1,
    idComprobanteAsociado: null,
    subtotal: 4000,
    descuentoTotal: 0,
    descuentoManualTotal: 0,
    recargoManualTotal: 0,
    total: 4000,
    direccionEntrega: null,
    observaciones: null,
    items: [
      {
        orden: 1,
        idArticulo: 1,
        descripcion: 'Marlboro box 20',
        cantidad: 4,
        precioUnitario: 1000,
        descuento: 0,
        total: 4000,
        codigoLote: null,
      },
    ],
    pagos: [{ idMedioPago: 5, importe: 4000, referencia: null, vuelto: 0 }],
    idPresupuestoOrigen: null,
  } as unknown as ComprobanteEmitido
}

function mockearRutasBase(sobrescribir?: (ruta: string) => Promise<unknown> | undefined) {
  apiGetMock.mockImplementation((ruta: string) => {
    const propia = sobrescribir?.(ruta)
    if (propia) return propia
    if (ruta === '/empresas') return Promise.resolve([empresaUno, empresaDos])
    if (ruta === '/puntos-venta') return Promise.resolve([pvUno, pvDos])
    if (ruta === '/catalogos/areas') return Promise.resolve([areaAlmacen])
    if (ruta === '/catalogos/grupos') return Promise.resolve([grupoCigarrillos])
    if (ruta === '/catalogos/categorias') return Promise.resolve([categoriaTabaco])
    if (ruta.startsWith('/articulos?')) {
      return Promise.resolve({
        items: [{ id: 77, codigoInterno: 'ACE-1', nombre: 'Aceite girasol' }],
        total: 1,
        pagina: 1,
        tamanio: 25,
      })
    }
    if (ruta.startsWith('/reportes/ventas/por-articulo?')) return Promise.resolve(respuestaFixture())
    const tickets = /^\/reportes\/ventas\/por-articulo\/(\d+)\/tickets\?/.exec(ruta)
    if (tickets) return Promise.resolve(ticketsFixture(Number(tickets[1])))
    if (ruta === '/ventas/345') return Promise.resolve(comprobanteFixture())
    if (ruta.startsWith('/catalogos/medios-pago')) return Promise.resolve([{ id: 5, nombre: 'Efectivo' }])
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

function llamadasDeReporte(): string[] {
  return apiGetMock.mock.calls.map((c) => c[0] as string).filter((r) => r.startsWith('/reportes/ventas/por-articulo?'))
}

function ultimaLlamadaDeReporte(): URLSearchParams {
  const llamadas = llamadasDeReporte()
  return new URLSearchParams(llamadas[llamadas.length - 1].split('?')[1])
}

let ubicacionActual = ''

function SondaDeUbicacion() {
  ubicacionActual = useLocation().search
  return null
}

function renderReporte(entrada = '/') {
  return render(<ReporteDeVentasPorArticulo />, {
    wrapper: ({ children }) => (
      <MemoryRouter initialEntries={[entrada]}>
        <SondaDeUbicacion />
        {children}
      </MemoryRouter>
    ),
  })
}

function llamadasDeTickets(): string[] {
  return apiGetMock.mock.calls.map((c) => c[0] as string).filter((r) => r.includes('/tickets?'))
}

/** Espera a que los selectores de catálogo tengan sus opciones: el `<select>` existe antes que los datos. */
async function esperarCatalogos() {
  await screen.findByRole('option', { name: 'Almacén' })
  await screen.findByRole('option', { name: 'Cigarrillos' })
  await screen.findByRole('option', { name: 'Tabaco' })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiDescargarMock.mockReset()
  apiDescargarMock.mockResolvedValue(undefined)
})

describe('ReporteDeVentasPorArticulo', () => {
  it('pide la semana en curso de la primera empresa y renderiza filas y fila de totales', async () => {
    mockearRutasBase()
    renderReporte()

    const filaUno = await screen.findByRole('row', { name: /MARL-20/ })
    expect(within(filaUno).getByText('Marlboro box 20')).toBeInTheDocument()
    expect(within(filaUno).getByText('Almacén')).toBeInTheDocument()
    expect(within(filaUno).getByText('Cigarrillos')).toBeInTheDocument()
    expect(within(filaUno).getByText('Tabaco')).toBeInTheDocument()
    expect(within(filaUno).getByText('10')).toBeInTheDocument()
    expect(within(filaUno).getByText('$ 10.000,00')).toBeInTheDocument()

    const filaDos = screen.getByRole('row', { name: /Fiambre suelto/ })
    expect(within(filaDos).getByText('3,500')).toBeInTheDocument()
    expect(within(filaDos).getByText('$ 2.500,00')).toBeInTheDocument()

    const totales = screen.getByRole('row', { name: /^Total/ })
    expect(within(totales).getByText('13,500')).toBeInTheDocument()
    expect(within(totales).getByText('$ 12.500,00')).toBeInTheDocument()

    const semana = rangoDeLaSemanaActual()
    const parametros = ultimaLlamadaDeReporte()
    expect(parametros.get('idEmpresa')).toBe('1')
    expect(parametros.get('desde')).toBe(semana.desde)
    expect(parametros.get('hasta')).toBe(semana.hasta)
    for (const omitido of ['idPuntoVenta', 'idArea', 'idGrupo', 'idCategoria', 'idArticulo']) {
      expect(parametros.has(omitido)).toBe(false)
    }
  })

  it('muestra "Sin asignar" en código y clasificaciones nulos', async () => {
    mockearRutasBase()
    renderReporte()

    const filaDos = await screen.findByRole('row', { name: /Fiambre suelto/ })
    expect(within(filaDos).getAllByText('Sin asignar')).toHaveLength(4)
  })

  it('ordena por el total como número y deja la fila de totales al final', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/ventas/por-articulo?')) {
        return Promise.resolve(
          respuestaFixture({
            articulos: [
              { idArticulo: 1, codigo: 'A-1', descripcion: 'Alfa', area: null, grupo: null, categoria: null, cantidad: 1, total: 1500 },
              { idArticulo: 2, codigo: 'Z-1', descripcion: 'Zeta', area: null, grupo: null, categoria: null, cantidad: 2, total: 900 },
            ],
          }),
        )
      }
      return undefined
    })
    const usuario = userEvent.setup()
    renderReporte()

    await screen.findByRole('row', { name: /Alfa/ })
    const cuerpo = () => screen.getAllByRole('row').slice(1).map((f) => f.textContent ?? '')

    await usuario.click(screen.getByRole('button', { name: /Total/ }))
    expect(screen.getByRole('columnheader', { name: /Total/ })).toHaveAttribute('aria-sort', 'ascending')
    const ordenadas = cuerpo()
    expect(ordenadas[0]).toContain('Zeta')
    expect(ordenadas[1]).toContain('Alfa')
    expect(ordenadas[2]).toMatch(/^Total/)

    await usuario.click(screen.getByRole('button', { name: /Total/ }))
    expect(cuerpo()[0]).toContain('Alfa')
  })

  it('un resultado vacío muestra el estado vacío y no dibuja totales', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/ventas/por-articulo?')) {
        return Promise.resolve(respuestaFixture({ articulos: [], cantidadTotal: 0, total: 0 }))
      }
      return undefined
    })
    renderReporte()

    expect(await screen.findByText('No hay ventas de artículos que coincidan con los filtros.')).toBeInTheDocument()
    expect(screen.queryByRole('row', { name: /^Total/ })).not.toBeInTheDocument()
  })

  it('manda área, grupo y categoría elegidos en la consulta', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderReporte()

    await screen.findByText('MARL-20')
    await esperarCatalogos()
    await usuario.selectOptions(screen.getByLabelText('Área'), 'Almacén')
    await usuario.selectOptions(screen.getByLabelText('Grupo'), 'Cigarrillos')
    await usuario.selectOptions(screen.getByLabelText('Categoría'), 'Tabaco')

    await waitFor(() => {
      const parametros = ultimaLlamadaDeReporte()
      expect(parametros.get('idArea')).toBe('1')
      expect(parametros.get('idGrupo')).toBe('30')
      expect(parametros.get('idCategoria')).toBe('10')
    })
  })

  it('cambiar de empresa limpia el punto de venta y solo ofrece los de la empresa elegida', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderReporte()

    await screen.findByText('MARL-20')
    await screen.findByRole('option', { name: 'Sucursal Centro' })
    expect(screen.queryByRole('option', { name: 'Sucursal Norte' })).not.toBeInTheDocument()
    await usuario.selectOptions(screen.getByLabelText('Punto de venta'), 'Sucursal Centro')
    await waitFor(() => expect(ultimaLlamadaDeReporte().get('idPuntoVenta')).toBe('11'))

    await usuario.selectOptions(screen.getByLabelText('Empresa'), 'Empresa Dos SA')

    await screen.findByRole('option', { name: 'Sucursal Norte' })
    await waitFor(() => {
      const parametros = ultimaLlamadaDeReporte()
      expect(parametros.get('idEmpresa')).toBe('2')
      expect(parametros.has('idPuntoVenta')).toBe(false)
    })
  })

  it('filtra por un artículo buscado y permite quitarlo', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderReporte()

    await screen.findByText('MARL-20')
    await usuario.click(screen.getByRole('combobox', { name: 'Artículo' }))
    await usuario.keyboard('aceite')
    await usuario.click(await screen.findByRole('option', { name: 'ACE-1 — Aceite girasol' }))

    await waitFor(() => expect(ultimaLlamadaDeReporte().get('idArticulo')).toBe('77'))

    await usuario.click(screen.getByRole('button', { name: 'Quitar' }))

    await waitFor(() => expect(ultimaLlamadaDeReporte().has('idArticulo')).toBe(false))
  })

  it('muestra el mensaje del servidor cuando el reporte responde 400', async () => {
    const { ErrorApi } = await import('../api/cliente')
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/ventas/por-articulo?')) {
        return Promise.reject(new ErrorApi(400, 'rango_invalido', 'El rango de fechas es inválido.'))
      }
      return undefined
    })
    renderReporte()

    expect(await screen.findByText('El rango de fechas es inválido.')).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
  })

  it('una respuesta vieja no pisa la de un filtro posterior', async () => {
    let resolverPrimera: (v: VentasPorArticulo) => void = () => {}
    let consultas = 0
    mockearRutasBase((ruta) => {
      if (!ruta.startsWith('/reportes/ventas/por-articulo?')) return undefined
      consultas += 1
      if (consultas === 1) return new Promise((resolver) => (resolverPrimera = resolver))
      return Promise.resolve(respuestaFixture({ articulos: [{ ...respuestaFixture().articulos[0], descripcion: 'Respuesta nueva' }] }))
    })
    const usuario = userEvent.setup()
    renderReporte()

    await waitFor(() => expect(consultas).toBe(1))
    await esperarCatalogos()
    await usuario.selectOptions(screen.getByLabelText('Área'), 'Almacén')
    await screen.findByText('Respuesta nueva')

    await act(async () => {
      resolverPrimera(respuestaFixture({ articulos: [{ ...respuestaFixture().articulos[0], descripcion: 'Respuesta vieja' }] }))
    })

    expect(screen.queryByText('Respuesta vieja')).not.toBeInTheDocument()
    expect(screen.getByText('Respuesta nueva')).toBeInTheDocument()
  })

  it('mientras carga un filtro nuevo marca la tabla vieja como ocupada y avisa que actualiza', async () => {
    let resolverSegunda: (v: VentasPorArticulo) => void = () => {}
    let consultas = 0
    mockearRutasBase((ruta) => {
      if (!ruta.startsWith('/reportes/ventas/por-articulo?')) return undefined
      consultas += 1
      if (consultas === 1) return Promise.resolve(respuestaFixture())
      return new Promise((resolver) => (resolverSegunda = resolver))
    })
    const usuario = userEvent.setup()
    renderReporte()

    await screen.findByText('MARL-20')
    await esperarCatalogos()
    await usuario.selectOptions(screen.getByLabelText('Área'), 'Almacén')

    expect(await screen.findByText('Actualizando…')).toHaveAttribute('role', 'status')
    expect(screen.getByRole('table').closest('[aria-busy]')).toHaveAttribute('aria-busy', 'true')

    await act(async () => {
      resolverSegunda(respuestaFixture({ articulos: [{ ...respuestaFixture().articulos[0], descripcion: 'Filtrado' }] }))
    })

    expect(await screen.findByText('Filtrado')).toBeInTheDocument()
    expect(screen.queryByText('Actualizando…')).not.toBeInTheDocument()
    expect(screen.getByRole('table').closest('[aria-busy]')).toHaveAttribute('aria-busy', 'false')
  })

  it('un rango inválido limpia el error de la consulta anterior', async () => {
    const { ErrorApi } = await import('../api/cliente')
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/ventas/por-articulo?')) {
        return Promise.reject(new ErrorApi(500, 'error', 'Falló el servidor.'))
      }
      return undefined
    })
    const usuario = userEvent.setup()
    renderReporte()

    await screen.findByText('Falló el servidor.')
    await usuario.clear(screen.getByLabelText('Desde'))

    expect(await screen.findByText(/Elegí un rango de fechas válido/)).toBeInTheDocument()
    expect(screen.queryByText('Falló el servidor.')).not.toBeInTheDocument()
  })

  it('busca artículos incluyendo los dados de baja', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderReporte()

    await screen.findByText('MARL-20')
    await usuario.click(screen.getByRole('combobox', { name: 'Artículo' }))
    await usuario.keyboard('aceite')
    await screen.findByRole('option', { name: 'ACE-1 — Aceite girasol' })

    const busqueda = apiGetMock.mock.calls.map((c) => c[0] as string).find((r) => r.startsWith('/articulos?'))!
    expect(new URLSearchParams(busqueda.split('?')[1]).get('incluirEliminados')).toBe('true')
  })

  it('el botón de descarga apunta al export con los mismos filtros y formato=xlsx una sola vez', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderReporte()

    await screen.findByText('MARL-20')
    await esperarCatalogos()
    await usuario.selectOptions(screen.getByLabelText('Grupo'), 'Cigarrillos')
    await waitFor(() => expect(ultimaLlamadaDeReporte().get('idGrupo')).toBe('30'))
    await usuario.click(screen.getByRole('button', { name: 'Descargar' }))

    await waitFor(() => expect(apiDescargarMock).toHaveBeenCalledTimes(1))
    const ruta = apiDescargarMock.mock.calls[0][0] as string
    const [camino, query] = ruta.split('?')
    expect(camino).toBe('/reportes/ventas/por-articulo/export')
    const parametros = new URLSearchParams(query)
    expect(parametros.getAll('formato')).toEqual(['xlsx'])
    expect(parametros.get('idEmpresa')).toBe('1')
    expect(parametros.get('idGrupo')).toBe('30')
    expect(parametros.get('desde')).toBe(rangoDeLaSemanaActual().desde)
  })
})

describe('ReporteDeVentasPorArticulo - tickets por artículo', () => {
  it('el botón de la fila abre los tickets con el recorte aplicado del reporte y sin los demás filtros', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderReporte()

    await screen.findByText('MARL-20')
    await esperarCatalogos()
    await usuario.selectOptions(screen.getByLabelText('Punto de venta'), 'Sucursal Centro')
    await usuario.selectOptions(screen.getByLabelText('Grupo'), 'Cigarrillos')
    await waitFor(() => {
      expect(ultimaLlamadaDeReporte().get('idPuntoVenta')).toBe('11')
      expect(ultimaLlamadaDeReporte().get('idGrupo')).toBe('30')
    })

    await usuario.click(await screen.findByRole('button', { name: 'Ver tickets de Marlboro box 20' }))

    await screen.findByRole('row', { name: /Ticket 0001-00000345/ })
    const semana = rangoDeLaSemanaActual()
    expect(llamadasDeTickets()).toEqual([
      `/reportes/ventas/por-articulo/1/tickets?idEmpresa=1&idPuntoVenta=11&desde=${semana.desde}&hasta=${semana.hasta}`,
    ])
    expect(ubicacionActual).toBe('?articulo=1')
  })

  it('cada fila tiene su botón con el nombre de su artículo', async () => {
    mockearRutasBase()
    renderReporte()

    expect(await screen.findByRole('button', { name: 'Ver tickets de Marlboro box 20' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Ver tickets de Fiambre suelto' })).toBeInTheDocument()
  })

  it('abrir un ticket lo refleja en la URL, y cerrarlo vuelve a la lista con el foco en su botón', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderReporte()

    await usuario.click(await screen.findByRole('button', { name: 'Ver tickets de Marlboro box 20' }))
    const fila = await screen.findByRole('row', { name: /Ticket 0001-00000345/ })
    const botonDelTicket = within(fila).getByRole('button', { name: /Ver el detalle del ticket/ })
    await usuario.click(botonDelTicket)

    const detalle = await screen.findByRole('dialog', { name: 'Detalle de la venta 0001-00000345' })
    expect(await within(detalle).findByText('Efectivo')).toBeInTheDocument()
    expect(ubicacionActual).toBe('?articulo=1&ticket=345')

    await usuario.click(within(detalle).getByRole('button', { name: 'Cerrar' }))

    await waitFor(() => expect(screen.queryByRole('dialog', { name: /Detalle de la venta/ })).not.toBeInTheDocument())
    expect(ubicacionActual).toBe('?articulo=1')
    expect(screen.getByRole('dialog', { name: /Tickets de Marlboro box 20/ })).toBeInTheDocument()
    expect(botonDelTicket).toHaveFocus()
  })

  it('cerrar la lista quita los parámetros y devuelve el foco al botón de la fila', async () => {
    mockearRutasBase()
    const usuario = userEvent.setup()
    renderReporte()

    const boton = await screen.findByRole('button', { name: 'Ver tickets de Marlboro box 20' })
    await usuario.click(boton)
    const dialogo = await screen.findByRole('dialog', { name: /Tickets de Marlboro box 20/ })
    await screen.findByRole('row', { name: /Ticket 0001-00000345/ })
    await usuario.click(within(dialogo).getByRole('button', { name: 'Cerrar' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(ubicacionActual).toBe('')
    expect(boton).toHaveFocus()
  })

  it('un enlace con artículo y ticket reabre los dos modales', async () => {
    mockearRutasBase()
    renderReporte('/?articulo=1&ticket=345')

    expect(await screen.findByRole('dialog', { name: 'Detalle de la venta 0001-00000345' })).toBeInTheDocument()
    expect(await screen.findByRole('dialog', { name: /Tickets de Marlboro box 20/ })).toBeInTheDocument()
  })

  it('un parámetro inválido o un ticket sin artículo no abre nada', async () => {
    mockearRutasBase()
    renderReporte('/?articulo=abc&ticket=345')

    await screen.findByText('MARL-20')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(llamadasDeTickets()).toEqual([])
  })
})
