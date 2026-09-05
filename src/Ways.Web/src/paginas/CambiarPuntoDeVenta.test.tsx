import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation, useNavigate } from 'react-router'
import type { InitialEntry } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { CambiarPuntoDeVenta } from './CambiarPuntoDeVenta'
import type { PuntoVentaListado } from '../api/tipos'
import type { EstadoDePuntoVenta } from '../puntoVenta/PuntoVentaContext'

function puntoVentaFixture(sobrescribir: Partial<PuntoVentaListado> = {}): PuntoVentaListado {
  return {
    id: 100,
    idTenant: 2,
    idEmpresa: 20,
    nombre: 'Local Centro',
    domicilio: null,
    horario: null,
    whatsapp: null,
    instagram: null,
    facebook: null,
    web: null,
    nombreTenant: null,
    razonSocialEmpresa: null,
    ...sobrescribir,
  }
}

const centro = puntoVentaFixture()
const norte = puntoVentaFixture({ id: 101, nombre: 'Local Norte' })

function estadoConPuntosVenta(
  puntosVenta: PuntoVentaListado[],
  puntoVenta: PuntoVentaListado | null,
): EstadoDePuntoVenta {
  return { puntosVenta, puntoVenta, elegir: vi.fn(), recargar: vi.fn(() => Promise.resolve()) }
}

let estadoDePuntoVenta = estadoConPuntosVenta([centro, norte], centro)

vi.mock('../puntoVenta/usePuntoVenta', () => ({ usePuntoVenta: () => estadoDePuntoVenta }))

/** Cualquier otra ruta: muestra dónde se llegó y permite volver atrás en el historial. */
function Destino() {
  const { pathname, search } = useLocation()
  const navegar = useNavigate()

  return (
    <main>
      <p>
        Llegaste a {pathname}
        {search}
      </p>
      <button type="button" onClick={() => navegar(-1)}>
        Volver
      </button>
    </main>
  )
}

function renderPagina(...entradas: InitialEntry[]) {
  return render(
    <MemoryRouter initialEntries={entradas}>
      <Routes>
        <Route path="/punto-de-venta" element={<CambiarPuntoDeVenta />} />
        <Route path="*" element={<Destino />} />
      </Routes>
    </MemoryRouter>,
  )
}

const selectorDesdeCaja: InitialEntry = {
  pathname: '/punto-de-venta',
  state: { desde: { pathname: '/caja', search: '?turno=3' } },
}

beforeEach(() => {
  estadoDePuntoVenta = estadoConPuntosVenta([centro, norte], centro)
})

describe('CambiarPuntoDeVenta', () => {
  it('elegir otro punto de venta lo activa y vuelve a la pantalla de origen con su query', async () => {
    renderPagina(selectorDesdeCaja)

    await userEvent.click(screen.getByRole('button', { name: 'Local Norte' }))

    expect(estadoDePuntoVenta.elegir).toHaveBeenCalledTimes(1)
    expect(estadoDePuntoVenta.elegir).toHaveBeenCalledWith(101)
    expect(screen.getByText('Llegaste a /caja?turno=3')).toBeInTheDocument()
  })

  it('la vuelta reemplaza al selector en el historial: volver atrás no regresa a él', async () => {
    renderPagina('/caja', selectorDesdeCaja)

    await userEvent.click(screen.getByRole('button', { name: 'Local Norte' }))
    await userEvent.click(screen.getByRole('button', { name: 'Volver' }))

    expect(screen.getByText('Llegaste a /caja')).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Elegí el punto de venta' })).not.toBeInTheDocument()
  })

  // Cláusula bajo prueba: `desde.pathname !== RUTA_PROPIA` al calcular el destino.
  // Evidencia de mutación: sin la cláusula el destino es el propio selector, la pantalla se queda
  // en él y "Llegaste a /" nunca aparece.
  it('si el origen guardado es el propio selector, vuelve al inicio', async () => {
    renderPagina({
      pathname: '/punto-de-venta',
      state: { desde: { pathname: '/punto-de-venta', search: '' } },
    })

    await userEvent.click(screen.getByRole('button', { name: 'Local Norte' }))

    expect(screen.getByText('Llegaste a /')).toBeInTheDocument()
  })

  it('sin origen guardado vuelve al inicio', async () => {
    renderPagina('/punto-de-venta')

    await userEvent.click(screen.getByRole('button', { name: 'Local Norte' }))

    expect(estadoDePuntoVenta.elegir).toHaveBeenCalledWith(101)
    expect(screen.getByText('Llegaste a /')).toBeInTheDocument()
  })

  it('marca el punto de venta activo como actual', () => {
    renderPagina('/punto-de-venta')

    expect(screen.getByRole('button', { name: 'Local Centro (actual)' })).toHaveAttribute('aria-current', 'true')
    expect(screen.getByRole('button', { name: 'Local Norte' })).not.toHaveAttribute('aria-current')
  })

  // Cláusula bajo prueba: `puntosVenta.length > 1` decide si se ofrece el selector.
  // Evidencia de mutación: con `>= 1` el único punto de venta aparece como botón y el aviso no.
  it('con un solo punto de venta muestra su nombre y avisa que no hay otro, sin botones', () => {
    estadoDePuntoVenta = estadoConPuntosVenta([centro], centro)
    renderPagina('/punto-de-venta')

    expect(screen.getByText('Local Centro')).toBeInTheDocument()
    expect(screen.getByText('Este es el único punto de venta disponible.')).toBeInTheDocument()
    expect(screen.queryAllByRole('button')).toEqual([])
    expect(estadoDePuntoVenta.elegir).not.toHaveBeenCalled()
  })

  it('sin puntos de venta lo avisa', () => {
    estadoDePuntoVenta = estadoConPuntosVenta([], null)
    renderPagina('/punto-de-venta')

    expect(screen.getByText('Sin puntos de venta disponibles')).toBeInTheDocument()
    expect(screen.queryAllByRole('button')).toEqual([])
  })
})
