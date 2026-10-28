import { render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { App } from './App'
import { ErrorApi } from './api/cliente'
import { ROL } from './api/tipos'

const apiGetMock = vi.fn()

vi.mock('./api/cliente', async (importarOriginal) => {
  const original = await importarOriginal<typeof import('./api/cliente')>()
  return { ...original, api: { ...original.api, get: (...args: unknown[]) => apiGetMock(...(args as [string])) } }
})

/** Sin sesión: toda lectura (`/auth/me`, `/publico/sitio`) responde 401. */
function sinSesion() {
  apiGetMock.mockImplementation(() => Promise.reject(new ErrorApi(401, 'no_autenticado', 'Tu sesión expiró.')))
}

describe('App bajo /admin', () => {
  beforeEach(() => {
    apiGetMock.mockReset()
    sinSesion()
  })

  it('resuelve las rutas públicas con el prefijo /admin', () => {
    window.history.pushState({}, '', '/admin/descargar')

    render(<App />)

    expect(screen.getByRole('link', { name: 'Descargar para Windows' })).toBeInTheDocument()
  })

  it('sin sesión manda al login conservando el prefijo /admin', async () => {
    window.history.pushState({}, '', '/admin/compras')

    render(<App />)

    await waitFor(() => expect(window.location.pathname).toBe('/admin/login'))
    expect(await screen.findByRole('button', { name: 'Continuar' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Descargar la app de escritorio' })).toHaveAttribute(
      'href',
      '/admin/descargar',
    )
  })
})

const PUNTO_DE_VENTA = {
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
  nombreTenant: 'Tenant Demo',
  razonSocialEmpresa: 'Empresa Demo',
  modo: 'Web',
}

describe('App — ruta /combos', () => {
  beforeEach(() => {
    apiGetMock.mockReset()
  })

  function conSesion(rolId: number) {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/auth/me') {
        return Promise.resolve({ id: 1, usuario: 'u', mail: 'u@ejemplo.com', rolId, rol: 'Rol', ultimaConexion: null, idTenant: 1 })
      }
      if (ruta === '/puntos-venta') return Promise.resolve([PUNTO_DE_VENTA])
      if (ruta === '/combos' || ruta === '/familias' || ruta === '/empresas') return Promise.resolve([])
      return Promise.reject(new ErrorApi(404, 'no_encontrado', 'No existe.'))
    })
  }

  it('un Admin entra a la pantalla de combos', async () => {
    conSesion(ROL.Admin)
    window.history.pushState({}, '', '/admin/combos')

    render(<App />)

    expect(await screen.findByText('No hay combos cargados.')).toBeInTheDocument()
    expect(window.location.pathname).toBe('/admin/combos')
  })

  it.each([
    ['Supervisor', ROL.Supervisor],
    ['Vendedor', ROL.Vendedor],
  ])('un %s no entra: lo manda al inicio', async (_nombre, rolId) => {
    conSesion(rolId)
    window.history.pushState({}, '', '/admin/combos')

    render(<App />)

    await waitFor(() => expect(window.location.pathname).not.toBe('/admin/combos'))
    expect(screen.queryByText('No hay combos cargados.')).not.toBeInTheDocument()
  })
})

describe('App — ruta vieja /gastos', () => {
  beforeEach(() => {
    apiGetMock.mockReset()
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/auth/me') {
        return Promise.resolve({ id: 1, usuario: 'u', mail: 'u@ejemplo.com', rolId: ROL.Admin, rol: 'Rol', ultimaConexion: null, idTenant: 1 })
      }
      if (ruta === '/puntos-venta') return Promise.resolve([PUNTO_DE_VENTA])
      return Promise.reject(new ErrorApi(404, 'no_encontrado', 'No existe.'))
    })
  })

  it('redirige a /pagos para que los marcadores viejos sigan funcionando', async () => {
    window.history.pushState({}, '', '/admin/gastos')

    render(<App />)

    await waitFor(() => expect(window.location.pathname).toBe('/admin/pagos'))
  })
})
