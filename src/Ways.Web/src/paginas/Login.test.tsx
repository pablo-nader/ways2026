import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router'
import type { InitialEntry } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Login } from './Login'
import { ErrorApi } from '../api/cliente'
import { ROL } from '../api/tipos'
import type { UsuarioAutenticado } from '../api/tipos'
import { AuthProvider } from '../auth/AuthContext'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown])),
    put: vi.fn(),
    delete: vi.fn(),
    descargar: vi.fn(),
  },
  alPerderLaSesion: () => () => {},
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

function usuarioFixture(sobrescribir: Partial<UsuarioAutenticado> = {}): UsuarioAutenticado {
  return {
    id: 4,
    usuario: 'vendedor',
    mail: 'vendedor@ways.test',
    rolId: ROL.Vendedor,
    rol: 'Vendedor',
    ultimaConexion: null,
    idTenant: 1,
    ...sobrescribir,
  }
}

function Pantalla({ nombre }: { nombre: string }) {
  const { pathname, search } = useLocation()
  return (
    <main>
      {nombre} en {pathname}
      {search}
    </main>
  )
}

/** Login detrás del `AuthProvider` real: `iniciarSesion` publica el usuario por contexto y es ese
 * re-render el que dispara la navegación de salida, igual que en la aplicación. */
function renderLogin(entrada: InitialEntry = '/login') {
  return render(
    <MemoryRouter initialEntries={[entrada]}>
      <AuthProvider>
        <Routes>
          <Route path="/login" element={<Login />} />
          <Route path="/pos" element={<Pantalla nombre="Ventas" />} />
          <Route path="/caja/historico" element={<Pantalla nombre="Histórico de cajas" />} />
          <Route path="/" element={<Pantalla nombre="Inicio" />} />
        </Routes>
      </AuthProvider>
    </MemoryRouter>,
  )
}

async function ingresar(mail: string) {
  const usuario = userEvent.setup()
  await usuario.type(await screen.findByPlaceholderText('Correo electrónico'), mail)
  await usuario.type(screen.getByPlaceholderText('Contraseña'), 'secreto')
  await usuario.click(screen.getByRole('button', { name: 'Continuar' }))
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiGetMock.mockRejectedValue(new ErrorApi(401, 'no_autenticado', 'Tu sesión expiró.'))
})

describe('Login — destino después de ingresar', () => {
  it('quien opera el POS entra directo a vender (paridad legacy A1)', async () => {
    apiPostMock.mockResolvedValue(usuarioFixture())
    renderLogin()

    await ingresar('vendedor@ways.test')

    expect(await screen.findByText('Ventas en /pos')).toBeInTheDocument()
    expect(apiPostMock).toHaveBeenCalledWith('/auth/login', { mail: 'vendedor@ways.test', password: 'secreto' })
  })

  it('Root no opera el POS: entra al inicio', async () => {
    apiPostMock.mockResolvedValue(usuarioFixture({ id: 1, usuario: 'root', mail: 'root@ways.test', rolId: ROL.Root, rol: 'Root', idTenant: null }))
    renderLogin()

    await ingresar('root@ways.test')

    expect(await screen.findByText('Inicio en /')).toBeInTheDocument()
  })

  it('la ruta de origen guardada por RutaProtegida gana sobre el destino por rol, con su query', async () => {
    apiPostMock.mockResolvedValue(usuarioFixture())
    renderLogin({ pathname: '/login', state: { desde: { pathname: '/caja/historico', search: '?desde=2026-08-01' } } })

    await ingresar('vendedor@ways.test')

    expect(await screen.findByText('Histórico de cajas en /caja/historico?desde=2026-08-01')).toBeInTheDocument()
  })

  it('con credenciales rechazadas muestra el mensaje de la API, limpia la contraseña y se queda en /login', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(401, 'credenciales_invalidas', 'Usuario o contraseña incorrectos.'))
    renderLogin()

    await ingresar('vendedor@ways.test')

    expect(await screen.findByText('Usuario o contraseña incorrectos.')).toBeInTheDocument()
    expect(screen.getByPlaceholderText('Contraseña')).toHaveValue('')
    expect(screen.getByPlaceholderText('Correo electrónico')).toHaveValue('vendedor@ways.test')
    expect(screen.getByRole('button', { name: 'Continuar' })).toBeEnabled()
  })
})
