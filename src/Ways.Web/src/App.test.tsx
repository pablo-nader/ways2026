import { render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { App } from './App'
import { ErrorApi } from './api/cliente'

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
