import { render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Tenants } from './Tenants'
import type { TenantListado } from '../api/tipos'

// stage-20-organizacion-relaciones-y-bajas, slice 2 (tareas 2.9 y 2.13).

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
      this.estado = estado
      this.codigo = codigo
    }
  },
}))

/**
 * Los tres contadores son pairwise-distintos ENTRE SÍ y distintos del id y de los contadores del
 * otro tenant (`mutation-proof-tests` regla 12b): con valores iguales, intercambiar dos columnas
 * de la tabla no cambiaría nada de lo que el test ve.
 */
const tenantUno: TenantListado = {
  id: 1,
  nombre: 'Comercio Sur',
  estado: 'Activo',
  createdAt: '2026-01-15T10:00:00-03:00',
  cantidadEmpresas: 2,
  cantidadPuntosVenta: 3,
  cantidadUsuarios: 4,
}

const tenantDos: TenantListado = {
  id: 2,
  nombre: 'Almacén Este',
  estado: 'Suspendido',
  createdAt: '2026-02-20T10:00:00-03:00',
  cantidadEmpresas: 5,
  cantidadPuntosVenta: 6,
  cantidadUsuarios: 7,
}

function montar(items: TenantListado[] = [tenantUno, tenantDos]) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/plataforma/tenants') return Promise.resolve(items)

    return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
  })

  return render(
    <MemoryRouter>
      <Tenants />
    </MemoryRouter>,
  )
}

function celdas(nombre: string) {
  const fila = screen.getByRole('row', { name: new RegExp(nombre) })

  return within(fila).getAllByRole('cell')
}

describe('Tenants (stage-20, slice 2 — contadores de hijos)', () => {
  beforeEach(() => {
    apiGetMock.mockReset()
  })

  it('rinde los tres contadores de cada tenant en su propia columna', async () => {
    montar()
    await waitFor(() => expect(screen.getByText('Comercio Sur')).toBeInTheDocument())

    // Columnas: ID · Nombre · Estado · Empresas · Puntos de venta · Usuarios · Creado · Acciones
    const uno = celdas('Comercio Sur')
    expect(uno[3]).toHaveTextContent('2')
    expect(uno[4]).toHaveTextContent('3')
    expect(uno[5]).toHaveTextContent('4')

    const dos = celdas('Almacén Este')
    expect(dos[3]).toHaveTextContent('5')
    expect(dos[4]).toHaveTextContent('6')
    expect(dos[5]).toHaveTextContent('7')
  })

  it('encabeza las tres columnas con su nombre, en orden', () => {
    montar()

    return waitFor(() => {
      const encabezados = screen.getAllByRole('columnheader').map((h) => h.textContent)
      expect(encabezados).toEqual([
        'ID',
        'Nombre',
        'Estado',
        'Empresas',
        'Puntos de venta',
        'Usuarios',
        'Creado',
        'Acciones',
      ])
    })
  })

  it('un tenant sin hijos rinde ceros, no celdas vacías', async () => {
    montar([{ ...tenantUno, cantidadEmpresas: 0, cantidadPuntosVenta: 0, cantidadUsuarios: 0 }])
    await waitFor(() => expect(screen.getByText('Comercio Sur')).toBeInTheDocument())

    const fila = celdas('Comercio Sur')
    expect(fila[3]).toHaveTextContent('0')
    expect(fila[4]).toHaveTextContent('0')
    expect(fila[5]).toHaveTextContent('0')
  })

  /** Tarea 2.13: ninguna celda presenta un id crudo como identidad de un dueño. En esta pantalla
   * el único id que se rinde es el del PROPIO tenant (columna ID, su identidad, no la de un
   * dueño) y ninguna columna nueva lo repite. */
  it('no presenta ids de dueño: el único id de la fila es el del propio tenant', async () => {
    montar([tenantUno])
    await waitFor(() => expect(screen.getByText('Comercio Sur')).toBeInTheDocument())

    const fila = celdas('Comercio Sur')
    expect(fila[0]).toHaveTextContent('0001')
    expect(fila.filter((c) => c.textContent === '1')).toHaveLength(0)
  })
})
