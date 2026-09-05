import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { AuthProvider } from './AuthContext'
import { useAuth } from './useAuth'
import { ErrorApi } from '../api/cliente'
import { ROL } from '../api/tipos'
import type { UsuarioAutenticado } from '../api/tipos'
import { CLAVE_PUNTO_VENTA_DE_SESION } from '../puntoVenta/almacenDePuntoVenta'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown])),
    put: vi.fn(),
    delete: vi.fn(),
  },
  alPerderLaSesion: () => () => undefined,
  ErrorApi: class ErrorApiMock extends Error {
    estado: number
    codigo: string
    constructor(estado: number, codigo: string, mensaje: string) {
      super(mensaje)
      this.estado = estado
      this.codigo = codigo
    }
    get esNoAutenticado() {
      return this.estado === 401
    }
  },
}))

const ana: UsuarioAutenticado = {
  id: 9,
  usuario: 'ana',
  mail: 'ana@ways.test',
  rolId: ROL.Vendedor,
  rol: 'Vendedor',
  ultimaConexion: null,
  idTenant: 2,
}

function Sonda() {
  const { usuario, cargando, iniciarSesion, cerrarSesion } = useAuth()

  if (cargando) return <p>cargando</p>

  return (
    <div>
      <p>{`Usuario: ${usuario?.usuario ?? 'nadie'}`}</p>
      <button type="button" onClick={() => void iniciarSesion(ana.mail, 'secreto').catch(() => undefined)}>
        Ingresar
      </button>
      <button type="button" onClick={() => void cerrarSesion().catch(() => undefined)}>
        Salir
      </button>
    </div>
  )
}

function montar() {
  return render(
    <AuthProvider>
      <Sonda />
    </AuthProvider>,
  )
}

function guardarPuntoVentaAjeno() {
  localStorage.setItem(CLAVE_PUNTO_VENTA_DE_SESION, JSON.stringify({ idUsuario: 9, idPuntoVenta: 100 }))
}

beforeEach(() => {
  localStorage.clear()
  apiGetMock.mockReset()
  apiPostMock.mockReset()
})

describe('AuthProvider — ciclo de vida del punto de venta de sesión', () => {
  it('iniciar sesión olvida el punto de venta guardado y publica al usuario', async () => {
    apiGetMock.mockRejectedValue(new ErrorApi(401, 'no_autenticado', 'Tu sesión expiró.'))
    apiPostMock.mockResolvedValue(ana)
    guardarPuntoVentaAjeno()
    montar()
    await screen.findByText('Usuario: nadie')

    await userEvent.click(screen.getByRole('button', { name: 'Ingresar' }))

    expect(await screen.findByText('Usuario: ana')).toBeInTheDocument()
    expect(localStorage.getItem(CLAVE_PUNTO_VENTA_DE_SESION)).toBeNull()
  })

  it('un inicio de sesión rechazado no toca lo guardado', async () => {
    apiGetMock.mockRejectedValue(new ErrorApi(401, 'no_autenticado', 'Tu sesión expiró.'))
    apiPostMock.mockRejectedValue(new ErrorApi(401, 'credenciales_invalidas', 'Credenciales inválidas.'))
    guardarPuntoVentaAjeno()
    montar()
    await screen.findByText('Usuario: nadie')

    await userEvent.click(screen.getByRole('button', { name: 'Ingresar' }))

    expect(screen.getByText('Usuario: nadie')).toBeInTheDocument()
    expect(localStorage.getItem(CLAVE_PUNTO_VENTA_DE_SESION)).not.toBeNull()
  })

  it('cerrar sesión olvida el punto de venta guardado', async () => {
    apiGetMock.mockResolvedValue(ana)
    apiPostMock.mockResolvedValue(undefined)
    montar()
    await screen.findByText('Usuario: ana')
    guardarPuntoVentaAjeno()

    await userEvent.click(screen.getByRole('button', { name: 'Salir' }))

    expect(await screen.findByText('Usuario: nadie')).toBeInTheDocument()
    expect(localStorage.getItem(CLAVE_PUNTO_VENTA_DE_SESION)).toBeNull()
  })

  it('cerrar sesión lo olvida aunque el logout falle', async () => {
    apiGetMock.mockResolvedValue(ana)
    apiPostMock.mockRejectedValue(new ErrorApi(500, 'error', 'Falló el logout'))
    montar()
    await screen.findByText('Usuario: ana')
    guardarPuntoVentaAjeno()

    await userEvent.click(screen.getByRole('button', { name: 'Salir' }))

    expect(await screen.findByText('Usuario: nadie')).toBeInTheDocument()
    expect(localStorage.getItem(CLAVE_PUNTO_VENTA_DE_SESION)).toBeNull()
  })
})
