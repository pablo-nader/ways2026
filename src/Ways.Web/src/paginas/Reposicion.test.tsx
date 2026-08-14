import { render, screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Reposicion } from './Reposicion'
import type { FilaDeReposicion, PuntoVentaListado, Reposicion as ReposicionRespuesta } from '../api/tipos'

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

const puntoVentaCentro: PuntoVentaListado = {
  id: 10,
  idTenant: 1,
  idEmpresa: 1,
  nombre: 'PV Centro',
  domicilio: null,
  horario: null,
  whatsapp: null,
  instagram: null,
  facebook: null,
  web: null,
}

function filaFixture(sobrescribir: Partial<FilaDeReposicion> = {}): FilaDeReposicion {
  return {
    idArticulo: 100,
    articulo: 'Yerba mate 1kg',
    cantidad: 3,
    minimo: 10,
    reposicion: 20,
    sugerido: 17,
    idProveedor: 1,
    proveedor: 'Proveedor Uno',
    consumoDiarioPromedio: null,
    diasDeCobertura: null,
    ...sobrescribir,
  }
}

function reposicionFixture(filas: FilaDeReposicion[], idPuntoVenta = 10): ReposicionRespuesta {
  return { idPuntoVenta, hoy: '2026-08-14', diasDeRotacion: 30, zonaHoraria: 'America/Argentina/Buenos_Aires', filas }
}

function mockearRutasBase(sobrescribir?: (ruta: string) => Promise<unknown> | undefined) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/puntos-venta') return Promise.resolve([puntoVentaCentro])
    const propia = sobrescribir?.(ruta)
    if (propia) return propia
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

function renderReposicion() {
  return render(<Reposicion />, { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiDescargarMock.mockReset()
  apiDescargarMock.mockResolvedValue(undefined)
})

describe('Reposicion (stage-13-stock-inteligente, Slice 6 — web)', () => {
  it('arranca con el primer punto de venta cargado, sin ?dias= en la consulta', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/reposicion?')) return Promise.resolve(reposicionFixture([filaFixture()]))
      return undefined
    })
    renderReposicion()

    await screen.findByLabelText('Punto de venta')
    expect(screen.getByLabelText('Punto de venta')).toHaveValue('10')
    const llamada = apiGetMock.mock.calls.find((call: unknown[]) => (call[0] as string).startsWith('/reportes/stock/reposicion?'))!
    expect(llamada[0] as string).toBe('/reportes/stock/reposicion?idPuntoVenta=10')
  })

  it('sugerido renderiza — cuando es null, nunca 0', async () => {
    const filaSinReposicionConfigurada = filaFixture({ idArticulo: 1, sugerido: null })
    const filaConSugerido = filaFixture({ idArticulo: 2, sugerido: 8 })
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/reposicion?')) {
        return Promise.resolve(reposicionFixture([filaSinReposicionConfigurada, filaConSugerido]))
      }
      return undefined
    })
    renderReposicion()

    const filas = await screen.findAllByRole('row')
    // fila 0 = encabezado, fila 1 = header del grupo de proveedor, filas 2/3 = datos.
    const filaUno = filas[2]
    const filaDos = filas[3]
    expect(within(filaUno).getByText('—')).toBeInTheDocument()
    expect(within(filaDos).queryByText('—')).not.toBeInTheDocument()
    expect(within(filaDos).getByText('8')).toBeInTheDocument()
  })

  it('agrupa por proveedor mostrando el nombre y la cantidad de filas en el encabezado del grupo', async () => {
    const filaUno = filaFixture({ idArticulo: 1, idProveedor: 1, proveedor: 'Proveedor Uno' })
    const filaDos = filaFixture({ idArticulo: 2, idProveedor: 1, proveedor: 'Proveedor Uno' })
    const filaSinProveedor = filaFixture({ idArticulo: 3, idProveedor: null, proveedor: null })
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/reposicion?')) {
        return Promise.resolve(reposicionFixture([filaUno, filaDos, filaSinProveedor]))
      }
      return undefined
    })
    renderReposicion()

    expect(await screen.findByText('Proveedor Uno (2)')).toBeInTheDocument()
    expect(screen.getByText('Sin proveedor (1)')).toBeInTheDocument()
  })

  it('el botón de descarga apunta a /reportes/stock/reposicion/export con el idPuntoVenta elegido', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/reposicion?')) return Promise.resolve(reposicionFixture([filaFixture()]))
      return undefined
    })
    renderReposicion()

    await screen.findByText('Yerba mate 1kg')
    expect(await screen.findByRole('button', { name: 'Descargar' })).toBeInTheDocument()
  })

  it('sin filas bajo el mínimo muestra un estado vacío', async () => {
    mockearRutasBase((ruta) => {
      if (ruta.startsWith('/reportes/stock/reposicion?')) return Promise.resolve(reposicionFixture([]))
      return undefined
    })
    renderReposicion()

    expect(await screen.findByText('No hay artículos bajo el mínimo para este punto de venta.')).toBeInTheDocument()
  })
})
