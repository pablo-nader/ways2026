import { act, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Layout } from './Layout'
import { ROL } from '../api/tipos'
import type { UsuarioAutenticado } from '../api/tipos'

function usuarioFixture(sobrescribir: Partial<UsuarioAutenticado> = {}): UsuarioAutenticado {
  return {
    id: 9,
    usuario: 'admin',
    mail: 'admin@ways.test',
    rolId: ROL.Admin,
    rol: 'Admin',
    ultimaConexion: null,
    idTenant: 1,
    ...sobrescribir,
  }
}

let usuarioActual: UsuarioAutenticado | null = usuarioFixture()
const cerrarSesionMock = vi.fn(async () => {})

vi.mock('../auth/useAuth', () => ({
  useAuth: () => ({ usuario: usuarioActual, cargando: false, iniciarSesion: vi.fn(), cerrarSesion: cerrarSesionMock }),
}))

function Contenido() {
  const { pathname } = useLocation()
  return <main>Contenido de {pathname}</main>
}

function renderLayout(ruta = '/') {
  return render(
    <MemoryRouter initialEntries={[ruta]}>
      <Routes>
        <Route path="/login" element={<div>Pantalla de login</div>} />
        <Route element={<Layout />}>
          <Route path="*" element={<Contenido />} />
        </Route>
      </Routes>
    </MemoryRouter>,
  )
}

/** La lista de primer nivel de la barra, ubicada por el elemento que el alternador móvil controla. */
function barra() {
  const alternador = screen.getByRole('button', { name: 'Abrir menú' })
  const colapso = document.getElementById(alternador.getAttribute('aria-controls') ?? '')
  if (!colapso) throw new Error('el alternador móvil no controla ningún elemento')
  return within(colapso)
}

const nombres = (elementos: HTMLElement[]) => elementos.map((elemento) => elemento.textContent)

/** Entradas de primer nivel alcanzables (los ítems de un grupo cerrado quedan afuera por `hidden`)
 * marcadas como activas, sea por `aria-current` en un link o por la clase del botón del grupo. */
function entradasActivas() {
  const lista = barra()
  return [...lista.queryAllByRole('link'), ...lista.queryAllByRole('button')].filter(
    (elemento) => elemento.hasAttribute('aria-current') || elemento.classList.contains('active'),
  )
}

beforeEach(() => {
  usuarioActual = usuarioFixture()
  cerrarSesionMock.mockClear()
})

describe('Layout — barra de navegación agrupada', () => {
  it('Admin: Vender es un botón-link a /pos y las entradas de primer nivel son las del menú por rol', () => {
    renderLayout()

    const vender = barra().getByRole('link', { name: 'Vender' })
    expect(vender).toHaveAttribute('href', '/pos')
    expect(vender).toHaveClass('btn', 'btn-success')
    expect(nombres(barra().getAllByRole('link'))).toEqual(['Vender', 'Caja'])
    expect(nombres(barra().getAllByRole('button'))).toEqual(['Ventas', 'Compras', 'Reportes', 'Administración'])
  })

  it('no hay ninguna entrada "Inicio": el logo es el link al inicio', () => {
    renderLayout('/pos')

    expect(screen.queryByRole('link', { name: 'Inicio' })).not.toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Ways, ir al inicio' })).toHaveAttribute('href', '/')
  })

  it('Vendedor: sin Reportes ni Administración', () => {
    usuarioActual = usuarioFixture({ rolId: ROL.Vendedor, rol: 'Vendedor' })
    renderLayout()

    expect(screen.queryByRole('button', { name: 'Administración' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Reportes' })).not.toBeInTheDocument()
    expect(barra().getByRole('link', { name: 'Vender' })).toBeInTheDocument()
  })

  it('Root: solo el desplegable Administración, sin Vender', () => {
    usuarioActual = usuarioFixture({ rolId: ROL.Root, rol: 'Root', idTenant: null })
    renderLayout()

    expect(barra().queryAllByRole('link')).toEqual([])
    expect(nombres(barra().getAllByRole('button'))).toEqual(['Administración'])
  })

  it.each([
    ['/caja/historico', ['Reportes']],
    ['/caja/tesoreria', ['Reportes']],
    ['/pos', ['Vender']],
    ['/caja/turnos/7/z', ['Caja']],
    ['/catalogos/marcas', ['Administración']],
    ['/', []],
  ])('en %s las entradas activas de la barra son %j', (ruta, esperadas) => {
    renderLayout(ruta)

    expect(nombres(entradasActivas())).toEqual(esperadas)
  })

  it('en /caja/historico la entrada activa es el botón del grupo Reportes, no un link', () => {
    renderLayout('/caja/historico')

    const [activa] = entradasActivas()
    expect(activa).toBe(screen.getByRole('button', { name: 'Reportes' }))
    expect(screen.queryByRole('link', { name: 'Caja' })).not.toHaveAttribute('aria-current')
  })

  it('abrir Administración muestra las secciones del Admin con sus encabezados', async () => {
    const usuario = userEvent.setup()
    renderLayout()

    await usuario.click(screen.getByRole('button', { name: 'Administración' }))

    expect(nombres(screen.getAllByRole('heading', { level: 6 }))).toEqual([
      'Catálogo',
      'Terceros',
      'Stock',
      'Configuración',
      'Organización',
    ])
    expect(screen.getByRole('link', { name: 'Artículos' })).toHaveAttribute('href', '/articulos')
  })

  it('hay un solo grupo abierto a la vez', async () => {
    const usuario = userEvent.setup()
    renderLayout()

    await usuario.click(screen.getByRole('button', { name: 'Ventas' }))
    expect(screen.getByRole('link', { name: 'Presupuestos' })).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Reportes' }))

    expect(screen.getByRole('button', { name: 'Ventas' })).toHaveAttribute('aria-expanded', 'false')
    expect(screen.queryByRole('link', { name: 'Presupuestos' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Reportes' })).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByRole('link', { name: 'Tablero' })).toBeInTheDocument()
  })

  it('elegir un ítem del desplegable navega y cierra el grupo', async () => {
    const usuario = userEvent.setup()
    renderLayout()

    await usuario.click(screen.getByRole('button', { name: 'Reportes' }))
    await usuario.click(screen.getByRole('link', { name: 'Tablero' }))

    expect(screen.getByText('Contenido de /tablero')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Reportes' })).toHaveAttribute('aria-expanded', 'false')
    expect(screen.queryByRole('link', { name: 'Tablero' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Reportes' })).toHaveClass('active')
  })

  it('el alternador móvil abre y cierra el menú que controla', async () => {
    const usuario = userEvent.setup()
    renderLayout()
    const alternador = screen.getByRole('button', { name: 'Abrir menú' })
    const colapso = document.getElementById(alternador.getAttribute('aria-controls') ?? '')

    expect(alternador).toHaveAttribute('aria-expanded', 'false')
    expect(colapso).not.toHaveClass('show')

    await usuario.click(alternador)
    expect(alternador).toHaveAttribute('aria-expanded', 'true')
    expect(colapso).toHaveClass('show')

    await usuario.click(alternador)
    expect(alternador).toHaveAttribute('aria-expanded', 'false')
    expect(colapso).not.toHaveClass('show')
  })

  it('navegar desde el menú móvil abierto lo cierra', async () => {
    const usuario = userEvent.setup()
    renderLayout()

    await usuario.click(screen.getByRole('button', { name: 'Abrir menú' }))
    await usuario.click(screen.getByRole('link', { name: 'Caja' }))

    expect(screen.getByText('Contenido de /caja')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Abrir menú' })).toHaveAttribute('aria-expanded', 'false')
  })

  it('Salir cierra la sesión y lleva a /login', async () => {
    const usuario = userEvent.setup()
    renderLayout('/pos')

    await usuario.click(screen.getByRole('button', { name: 'Salir' }))

    expect(cerrarSesionMock).toHaveBeenCalledTimes(1)
    expect(await screen.findByText('Pantalla de login')).toBeInTheDocument()
  })

  // Cláusula bajo prueba: la guarda de reentrancia por ref de `salir` (react-async-state, regla 11).
  // Evidencia de mutación: sin el `if (saliendoRef.current) return` este caso falla con dos llamadas.
  it('dos clics en Salir en el mismo tick disparan un solo cierre de sesión', async () => {
    renderLayout('/pos')
    const salir = screen.getByRole('button', { name: 'Salir' })

    await act(async () => {
      salir.click()
      salir.click()
    })

    expect(cerrarSesionMock).toHaveBeenCalledTimes(1)
    expect(screen.getByText('Pantalla de login')).toBeInTheDocument()
  })
})
