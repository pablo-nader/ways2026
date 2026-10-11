import { StrictMode } from 'react'
import type { ReactElement } from 'react'
import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router'
import type { InitialEntry } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Login } from './Login'
import { ErrorApi } from '../api/cliente'
import { ROL } from '../api/tipos'
import type { SitioPublico, UsuarioAutenticado } from '../api/tipos'
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

const sitioDeLaPlataforma: SitioPublico = { esPlataforma: true, nombreComercio: null, admiteUsuario: false }
const sitioDeUnComercio: SitioPublico = {
  esPlataforma: false,
  nombreComercio: 'Autoservicio Sur',
  admiteUsuario: true,
}

/** Toda lectura rechaza (sin sesión) salvo la del sitio, que responde lo que se le pase. */
function responderSitio(sitio: () => Promise<SitioPublico>) {
  apiGetMock.mockImplementation((ruta: string) =>
    ruta === '/publico/sitio' ? sitio() : Promise.reject(new ErrorApi(401, 'no_autenticado', 'Tu sesión expiró.')),
  )
}

function cuerpoEnviado() {
  return apiPostMock.mock.calls[0][1] as Record<string, unknown>
}

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
function renderLogin(entrada: InitialEntry = '/login', envolver = (hijo: ReactElement) => hijo) {
  return render(
    envolver(
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
    ),
  )
}

/** El campo existe desde el primer render, inerte, hasta que llega el sitio: se espera al DATO (el
 * campo habilitado), no al elemento. */
async function esperarCampo(marcador: string) {
  const campo = await screen.findByPlaceholderText(marcador)
  await waitFor(() => expect(campo).toBeEnabled())

  return campo
}

async function ingresar(identificador: string, marcador = 'Correo electrónico') {
  const usuario = userEvent.setup()
  await usuario.type(await esperarCampo(marcador), identificador)
  await usuario.type(screen.getByPlaceholderText('Contraseña'), 'secreto')
  await usuario.click(screen.getByRole('button', { name: 'Continuar' }))
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  responderSitio(() => Promise.resolve(sitioDeLaPlataforma))
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

  it('muestra el link a la descarga de la app de escritorio', async () => {
    renderLogin()

    expect(
      await screen.findByRole('link', { name: 'Descargar la app de escritorio' }),
    ).toHaveAttribute('href', '/descargar')
  })
})

describe('Login — qué se pide según el sitio', () => {
  it('mientras llega el sitio el formulario se ve, pero inerte', async () => {
    let resolver: (sitio: SitioPublico) => void = () => {}
    responderSitio(() => new Promise<SitioPublico>((r) => (resolver = r)))
    renderLogin()

    const campo = await screen.findByPlaceholderText('Correo electrónico')
    expect(campo).toBeDisabled()
    expect(screen.getByPlaceholderText('Contraseña')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Continuar' })).toBeDisabled()

    await act(async () => resolver(sitioDeLaPlataforma))

    await waitFor(() => expect(campo).toBeEnabled())
    expect(screen.getByRole('button', { name: 'Continuar' })).toBeEnabled()
  })

  it('bajo la plataforma pide solo el correo y envía { mail }', async () => {
    apiPostMock.mockResolvedValue(usuarioFixture())
    renderLogin()

    const campo = await esperarCampo('Correo electrónico')
    expect(campo).toHaveAttribute('type', 'email')
    expect(screen.queryByPlaceholderText('Usuario o mail')).not.toBeInTheDocument()
    expect(screen.queryByRole('heading', { level: 2 })).not.toBeInTheDocument()

    await ingresar('vendedor@ways.test')

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    expect(Object.keys(cuerpoEnviado()).sort()).toEqual(['mail', 'password'])
  })

  it('bajo el dominio de un comercio muestra su nombre y el campo "Usuario o mail"', async () => {
    responderSitio(() => Promise.resolve(sitioDeUnComercio))
    renderLogin()

    const campo = await esperarCampo('Usuario o mail')

    expect(campo).toHaveAttribute('type', 'text')
    expect(screen.queryByPlaceholderText('Correo electrónico')).not.toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 2, name: 'Autoservicio Sur' })).toBeInTheDocument()
  })

  it('bajo un comercio, un valor sin @ viaja como { usuario }', async () => {
    responderSitio(() => Promise.resolve(sitioDeUnComercio))
    apiPostMock.mockResolvedValue(usuarioFixture())
    renderLogin()

    await ingresar('jperez', 'Usuario o mail')

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    expect(apiPostMock.mock.calls[0][0]).toBe('/auth/login')
    expect(Object.keys(cuerpoEnviado()).sort()).toEqual(['password', 'usuario'])
    expect(cuerpoEnviado()).toMatchObject({ usuario: 'jperez', password: 'secreto' })
  })

  it('bajo un comercio, un valor con @ viaja como { mail }', async () => {
    responderSitio(() => Promise.resolve(sitioDeUnComercio))
    apiPostMock.mockResolvedValue(usuarioFixture())
    renderLogin()

    await ingresar('vendedor@ways.test', 'Usuario o mail')

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    expect(Object.keys(cuerpoEnviado()).sort()).toEqual(['mail', 'password'])
    expect(cuerpoEnviado()).toMatchObject({ mail: 'vendedor@ways.test', password: 'secreto' })
  })

  it('si no se puede leer el sitio, ofrece el ingreso por correo y lo avisa', async () => {
    responderSitio(() => Promise.reject(new ErrorApi(500, 'error_interno', 'Falló.')))
    apiPostMock.mockResolvedValue(usuarioFixture())
    renderLogin()

    await esperarCampo('Correo electrónico')
    expect(screen.getByText(/No se pudo identificar el sitio/)).toBeInTheDocument()

    await ingresar('vendedor@ways.test')

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    expect(Object.keys(cuerpoEnviado()).sort()).toEqual(['mail', 'password'])
  })

  /**
   * StrictMode monta el efecto dos veces: la primera lectura queda vieja. Si resuelve DESPUÉS de la
   * segunda no puede pisarla: gana la última iniciada, no la última resuelta.
   */
  it('una respuesta vieja del sitio no pisa a la lectura más nueva', async () => {
    const respuestas: Array<(sitio: SitioPublico) => void> = []
    responderSitio(() => new Promise<SitioPublico>((r) => respuestas.push(r)))
    renderLogin('/login', (hijo) => <StrictMode>{hijo}</StrictMode>)

    await waitFor(() => expect(respuestas).toHaveLength(2))
    const [lecturaVieja, lecturaNueva] = respuestas

    await act(async () => lecturaNueva(sitioDeLaPlataforma))
    await esperarCampo('Correo electrónico')

    await act(async () => lecturaVieja(sitioDeUnComercio))

    expect(screen.getByPlaceholderText('Correo electrónico')).toBeEnabled()
    expect(screen.queryByPlaceholderText('Usuario o mail')).not.toBeInTheDocument()
    expect(screen.queryByRole('heading', { level: 2 })).not.toBeInTheDocument()
  })

  it('usuario_requiere_dominio se explica en español y no repite el texto del servidor', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(400, 'usuario_requiere_dominio', 'texto crudo del servidor'))
    renderLogin()

    await ingresar('jperez')

    expect(await screen.findByText(/hay que entrar por el dominio del comercio/)).toBeInTheDocument()
    expect(screen.queryByText('texto crudo del servidor')).not.toBeInTheDocument()
  })

  it('dos envíos del mismo tick salen como una sola request', async () => {
    apiPostMock.mockReturnValue(new Promise(() => {}))
    renderLogin()
    const usuario = userEvent.setup()
    await usuario.type(await esperarCampo('Correo electrónico'), 'vendedor@ways.test')
    await usuario.type(screen.getByPlaceholderText('Contraseña'), 'secreto')
    const boton = screen.getByRole('button', { name: 'Continuar' })

    act(() => {
      boton.click()
      boton.click()
    })

    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })
})
