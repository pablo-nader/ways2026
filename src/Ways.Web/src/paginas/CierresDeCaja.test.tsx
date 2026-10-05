import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { CierresDeCaja } from './CierresDeCaja'
import type { PaginaDeTurnos, PuntoVentaListado, ResumenDeCierrePorRetiro, TurnoListado } from '../api/tipos'
import type { EstadoDePuntoVenta } from '../puntoVenta/PuntoVentaContext'

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

let estadoDePuntoVenta: EstadoDePuntoVenta
vi.mock('../puntoVenta/usePuntoVenta', () => ({
  usePuntoVenta: () => estadoDePuntoVenta,
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

function turnoFixture(sobrescribir: Partial<TurnoListado> = {}): TurnoListado {
  return {
    id: 501,
    idPuntoVenta: 7,
    fechaApertura: '2026-09-16T12:00:00Z',
    fechaCierre: '2026-09-16T20:00:00Z',
    estado: 'Cerrado',
    ...sobrescribir,
  }
}

function paginaFixture(items: TurnoListado[], sobrescribir: Partial<PaginaDeTurnos> = {}): PaginaDeTurnos {
  return { items, total: items.length, pagina: 1, tamanio: 10, ...sobrescribir }
}

function resumenFixture(sobrescribir: Partial<ResumenDeCierrePorRetiro> = {}): ResumenDeCierrePorRetiro {
  return {
    idTurnoCaja: 501,
    puntoVenta: { id: 7, numero: 7, nombre: 'Local Centro' },
    fechaApertura: '2026-09-16T12:00:00Z',
    fechaCierre: '2026-09-16T20:00:00Z',
    vendedor: 'jperez',
    empleadoCierre: 'jperez',
    fondoInicial: 500,
    ventasPorMedio: [],
    totalVentas: 0,
    retiros: [],
    totalRetiros: 0,
    ventasEnEfectivoNetas: 0,
    gastosEnEfectivo: 0,
    refuerzos: 0,
    diferencia: 0,
    ...sobrescribir,
  }
}

const RUTA_LISTA = '/caja/turnos?idPuntoVenta=7&estado=Cerrado&pagina=1&tamanio=10'
const BOTON = 'Reimprimir ticket de cierre'

beforeEach(() => {
  apiGetMock.mockReset()
  const pv = puntoVentaFixture()
  estadoDePuntoVenta = { puntosVenta: [pv], puntoVenta: pv, elegir: () => undefined, recargar: () => Promise.resolve() }
})

describe('CierresDeCaja — listado del punto de venta', () => {
  it('pide al servidor solo los turnos cerrados del PV actual y los muestra en el orden recibido, uno por fila', async () => {
    apiGetMock.mockResolvedValue(
      paginaFixture([
        turnoFixture({ id: 502, fechaApertura: '2026-09-17T12:00:00Z', fechaCierre: '2026-09-17T20:00:00Z' }),
        turnoFixture({ id: 501 }),
      ]),
    )
    render(<CierresDeCaja alReimprimir={vi.fn()} />)

    const fila502 = await screen.findByRole('row', { name: /#502/ })
    const fila501 = screen.getByRole('row', { name: /#501/ })

    expect(apiGetMock).toHaveBeenCalledWith(RUTA_LISTA)
    expect(within(fila502).getByRole('button', { name: BOTON })).toBeInTheDocument()
    expect(within(fila501).getByRole('button', { name: BOTON })).toBeInTheDocument()
    const filas = screen.getAllByRole('row').map((f) => f.textContent)
    expect(filas.findIndex((t) => t?.includes('#502'))).toBeLessThan(filas.findIndex((t) => t?.includes('#501')))
    expect(screen.getByText(/Cierres de caja — Local Centro/)).toBeInTheDocument()
  })

  it('sin cierres muestra el estado vacío', async () => {
    apiGetMock.mockResolvedValue(paginaFixture([]))
    render(<CierresDeCaja alReimprimir={vi.fn()} />)

    expect(await screen.findByText('Este punto de venta todavía no tiene cierres de caja.')).toBeInTheDocument()
  })

  it('sin punto de venta no pide nada y lo dice', () => {
    estadoDePuntoVenta = { ...estadoDePuntoVenta, puntoVenta: null }
    render(<CierresDeCaja alReimprimir={vi.fn()} />)

    expect(screen.getByText('No hay un punto de venta seleccionado.')).toBeInTheDocument()
    expect(apiGetMock).not.toHaveBeenCalled()
  })

  it('si falla la carga muestra el error de la lista', async () => {
    apiGetMock.mockRejectedValue(new Error('boom'))
    render(<CierresDeCaja alReimprimir={vi.fn()} />)

    expect(await screen.findByText('No se pudieron cargar los cierres de caja.')).toBeInTheDocument()
  })

  it('Siguiente pide la página 2 del mismo PV', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === RUTA_LISTA) return Promise.resolve(paginaFixture([turnoFixture({ id: 501 })], { total: 15 }))
      if (ruta === '/caja/turnos?idPuntoVenta=7&estado=Cerrado&pagina=2&tamanio=10') {
        return Promise.resolve(paginaFixture([turnoFixture({ id: 490 })], { total: 15, pagina: 2 }))
      }
      return Promise.reject(new Error(ruta))
    })
    render(<CierresDeCaja alReimprimir={vi.fn()} />)
    await screen.findByRole('row', { name: /#501/ })

    await userEvent.click(screen.getByRole('button', { name: 'Siguiente' }))

    expect(await screen.findByRole('row', { name: /#490/ })).toBeInTheDocument()
    expect(screen.queryByRole('row', { name: /#501/ })).not.toBeInTheDocument()
    expect(screen.getByText(/Página 2 de 2/)).toBeInTheDocument()
  })

  it('una respuesta tardía del PV anterior no pisa la lista del PV actual', async () => {
    let resolverPv7: (p: PaginaDeTurnos) => void = () => {}
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === RUTA_LISTA) return new Promise<PaginaDeTurnos>((resolve) => (resolverPv7 = resolve))
      if (ruta === '/caja/turnos?idPuntoVenta=8&estado=Cerrado&pagina=1&tamanio=10') {
        return Promise.resolve(paginaFixture([turnoFixture({ id: 800, idPuntoVenta: 8 })]))
      }
      return Promise.reject(new Error(ruta))
    })
    const { rerender } = render(<CierresDeCaja alReimprimir={vi.fn()} />)

    const pv8 = puntoVentaFixture({ id: 8, nombre: 'Local Norte' })
    estadoDePuntoVenta = { ...estadoDePuntoVenta, puntoVenta: pv8, puntosVenta: [pv8] }
    rerender(<CierresDeCaja alReimprimir={vi.fn()} />)
    await screen.findByRole('row', { name: /#800/ })

    await act(async () => {
      resolverPv7(paginaFixture([turnoFixture({ id: 501 })]))
    })

    expect(screen.getByRole('row', { name: /#800/ })).toBeInTheDocument()
    expect(screen.queryByRole('row', { name: /#501/ })).not.toBeInTheDocument()
  })
})

describe('CierresDeCaja — reimpresión', () => {
  it('pide el resumen de cierre del turno elegido y se lo entrega a alReimprimir', async () => {
    const resumen = resumenFixture({ idTurnoCaja: 501 })
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === RUTA_LISTA) return Promise.resolve(paginaFixture([turnoFixture({ id: 502 }), turnoFixture({ id: 501 })]))
      if (ruta === '/caja/turnos/501/resumen-de-cierre') return Promise.resolve(resumen)
      return Promise.reject(new Error(ruta))
    })
    const alReimprimir = vi.fn()
    render(<CierresDeCaja alReimprimir={alReimprimir} />)

    await userEvent.click(within(await screen.findByRole('row', { name: /#501/ })).getByRole('button', { name: BOTON }))

    await waitFor(() => expect(alReimprimir).toHaveBeenCalledTimes(1))
    expect(alReimprimir).toHaveBeenCalledWith(resumen)
    expect(apiGetMock).not.toHaveBeenCalledWith('/caja/turnos/502/resumen-de-cierre')
  })

  it('doble clic sincrónico: una sola request y un solo trabajo; mientras corre quedan inertes todos los botones', async () => {
    let resolver: (r: ResumenDeCierrePorRetiro) => void = () => {}
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === RUTA_LISTA) return Promise.resolve(paginaFixture([turnoFixture({ id: 502 }), turnoFixture({ id: 501 })]))
      if (ruta === '/caja/turnos/501/resumen-de-cierre') return new Promise<ResumenDeCierrePorRetiro>((resolve) => (resolver = resolve))
      return Promise.reject(new Error(ruta))
    })
    const alReimprimir = vi.fn()
    render(<CierresDeCaja alReimprimir={alReimprimir} />)

    const boton = within(await screen.findByRole('row', { name: /#501/ })).getByRole('button', { name: BOTON })
    act(() => {
      boton.click()
      boton.click()
    })

    expect(apiGetMock.mock.calls.filter(([ruta]) => ruta === '/caja/turnos/501/resumen-de-cierre')).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'Reimprimiendo…' })).toBeDisabled()
    expect(within(screen.getByRole('row', { name: /#502/ })).getByRole('button', { name: BOTON })).toBeDisabled()

    await act(async () => {
      resolver(resumenFixture())
    })
    expect(alReimprimir).toHaveBeenCalledTimes(1)
    expect(screen.getAllByRole('button', { name: BOTON })).toHaveLength(2)
    expect(screen.getAllByRole('button', { name: BOTON })[0]).toBeEnabled()
  })

  it('si falla el resumen muestra el error, no imprime y deja reintentar', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === RUTA_LISTA) return Promise.resolve(paginaFixture([turnoFixture({ id: 501 })]))
      return Promise.reject(new Error('boom'))
    })
    const alReimprimir = vi.fn()
    render(<CierresDeCaja alReimprimir={alReimprimir} />)

    await userEvent.click(await screen.findByRole('button', { name: BOTON }))

    expect(await screen.findByText('No se pudo obtener el ticket de cierre.')).toBeInTheDocument()
    expect(alReimprimir).not.toHaveBeenCalled()
    expect(screen.getByRole('button', { name: BOTON })).toBeEnabled()
  })

  it('el error de un intento anterior se borra apenas empieza el siguiente', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === RUTA_LISTA) return Promise.resolve(paginaFixture([turnoFixture({ id: 501 })]))
      return Promise.reject(new Error('boom'))
    })
    render(<CierresDeCaja alReimprimir={vi.fn()} />)
    await userEvent.click(await screen.findByRole('button', { name: BOTON }))
    await screen.findByText('No se pudo obtener el ticket de cierre.')

    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/501/resumen-de-cierre') return new Promise(() => {})
      return Promise.reject(new Error(ruta))
    })
    await userEvent.click(screen.getByRole('button', { name: BOTON }))

    expect(screen.queryByText('No se pudo obtener el ticket de cierre.')).not.toBeInTheDocument()
  })

  it('cambiar de página borra el error de la reimpresión anterior', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === RUTA_LISTA) return Promise.resolve(paginaFixture([turnoFixture({ id: 501 })], { total: 15 }))
      if (ruta === '/caja/turnos?idPuntoVenta=7&estado=Cerrado&pagina=2&tamanio=10') return new Promise(() => {})
      return Promise.reject(new Error('boom'))
    })
    render(<CierresDeCaja alReimprimir={vi.fn()} />)
    await userEvent.click(await screen.findByRole('button', { name: BOTON }))
    await screen.findByText('No se pudo obtener el ticket de cierre.')

    await userEvent.click(screen.getByRole('button', { name: 'Siguiente' }))

    expect(screen.queryByText('No se pudo obtener el ticket de cierre.')).not.toBeInTheDocument()
  })
})
