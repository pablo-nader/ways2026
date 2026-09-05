import { act, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Layout } from './Layout'
import { ROL } from '../api/tipos'
import type { PuntoVentaListado, UsuarioAutenticado } from '../api/tipos'
import type { EstadoDePuntoVenta } from '../puntoVenta/PuntoVentaContext'

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

function puntoVentaFixture(sobrescribir: Partial<PuntoVentaListado> = {}): PuntoVentaListado {
  return {
    id: 100,
    idTenant: 1,
    idEmpresa: 10,
    nombre: 'Centro',
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
const norte = puntoVentaFixture({ id: 101, nombre: 'Norte' })

function estadoConPuntosVenta(
  puntosVenta: PuntoVentaListado[],
  puntoVenta: PuntoVentaListado | null,
): EstadoDePuntoVenta {
  return { puntosVenta, puntoVenta, elegir: vi.fn(), recargar: vi.fn(() => Promise.resolve()) }
}

let usuarioActual: UsuarioAutenticado | null = usuarioFixture()
let estadoDePuntoVenta = estadoConPuntosVenta([centro, norte], centro)
const cerrarSesionMock = vi.fn(async () => {})

vi.mock('../auth/useAuth', () => ({
  useAuth: () => ({ usuario: usuarioActual, cargando: false, iniciarSesion: vi.fn(), cerrarSesion: cerrarSesionMock }),
}))

vi.mock('../puntoVenta/usePuntoVenta', () => ({ usePuntoVenta: () => estadoDePuntoVenta }))

type EstadoDeRuta = { desde?: { pathname: string; search: string } }

function Contenido() {
  const { pathname, state } = useLocation()
  const desde = (state as EstadoDeRuta | null)?.desde

  return (
    <main>
      Contenido de {pathname}
      {desde && ` desde ${desde.pathname}${desde.search}`}
    </main>
  )
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

/** La franja de color que antecede a la barra. */
function franja() {
  const elemento = document.querySelector('nav.ways-nav')
  if (!elemento) throw new Error('no hay franja de color')
  return elemento
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
  estadoDePuntoVenta = estadoConPuntosVenta([centro, norte], centro)
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

  it('Vender anuncia la ruta actual con aria-current, nunca con la clase active del botón', () => {
    renderLayout('/pos')

    const vender = barra().getByRole('link', { name: 'Vender' })
    expect(vender).toHaveAttribute('aria-current', 'page')
    expect(vender).not.toHaveClass('active')
  })

  it('Caja anuncia la ruta actual con aria-current y con la clase active del nav-link', () => {
    renderLayout('/caja')

    const caja = barra().getByRole('link', { name: 'Caja' })
    expect(caja).toHaveAttribute('aria-current', 'page')
    expect(caja).toHaveClass('nav-link', 'active')
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

  // Cláusula bajo prueba: el `onClick` de los links de primer nivel. Acá `pathname` no cambia,
  // así que el efecto que cierra la barra al navegar no corre.
  // Evidencia de mutación: sin el `onClick` el alternador queda con aria-expanded="true".
  it('tocar el link de la ruta ya activa cierra el menú móvil abierto', async () => {
    const usuario = userEvent.setup()
    renderLayout('/caja')
    const alternador = screen.getByRole('button', { name: 'Abrir menú' })

    await usuario.click(alternador)
    expect(alternador).toHaveAttribute('aria-expanded', 'true')

    await usuario.click(screen.getByRole('link', { name: 'Caja' }))

    expect(screen.getByText('Contenido de /caja')).toBeInTheDocument()
    expect(alternador).toHaveAttribute('aria-expanded', 'false')
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

describe('Layout — punto de venta en la cabecera', () => {
  it('con más de un punto de venta el nombre es un link a /punto-de-venta que lleva la ubicación actual', async () => {
    const usuario = userEvent.setup()
    renderLayout('/caja?turno=3')

    const insignia = screen.getByRole('link', { name: 'Punto de venta Centro, cambiar' })
    expect(insignia).toHaveAttribute('href', '/punto-de-venta')
    expect(insignia).toHaveTextContent('Centro')

    await usuario.click(insignia)

    expect(screen.getByText('Contenido de /punto-de-venta desde /caja?turno=3')).toBeInTheDocument()
  })

  it('en /punto-de-venta el nombre deja de ser link y marca la página actual', () => {
    renderLayout('/punto-de-venta')

    expect(screen.queryByRole('link', { name: /Punto de venta/ })).not.toBeInTheDocument()
    expect(screen.getByText('Punto de venta: Centro')).toHaveAttribute('aria-current', 'page')
  })

  it('con un solo punto de venta muestra el nombre como texto, sin link para cambiarlo', () => {
    estadoDePuntoVenta = estadoConPuntosVenta([centro], centro)
    renderLayout()

    expect(screen.queryByRole('link', { name: /Punto de venta/ })).not.toBeInTheDocument()
    expect(screen.getByText('Punto de venta: Centro')).not.toHaveAttribute('aria-current')
  })

  it('sin punto de venta activo avisa a quien opera el POS', () => {
    usuarioActual = usuarioFixture({ rolId: ROL.Vendedor, rol: 'Vendedor' })
    estadoDePuntoVenta = estadoConPuntosVenta([], null)
    renderLayout()

    expect(screen.getByText('Sin punto de venta')).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: /Punto de venta/ })).not.toBeInTheDocument()
  })

  it('Root no ve nada del punto de venta', () => {
    usuarioActual = usuarioFixture({ rolId: ROL.Root, rol: 'Root', idTenant: null })
    estadoDePuntoVenta = estadoConPuntosVenta([], null)
    renderLayout()

    expect(screen.queryByText('Sin punto de venta')).not.toBeInTheDocument()
    expect(screen.queryByText(/Punto de venta/)).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: /Punto de venta/ })).not.toBeInTheDocument()
  })

  it.each([
    { puntoVenta: centro, color: 'color_1' },
    { puntoVenta: norte, color: 'color_2' },
  ])('con $puntoVenta.nombre activo la franja lleva la clase $color', ({ puntoVenta, color }) => {
    estadoDePuntoVenta = estadoConPuntosVenta([centro, norte], puntoVenta)
    renderLayout()

    expect(franja().className).toBe(`ways-nav ${color}`)
  })
})
