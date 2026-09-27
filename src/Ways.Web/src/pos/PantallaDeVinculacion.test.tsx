import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { PantallaDeVinculacion } from './PantallaDeVinculacion'
import { ErrorApi } from '../api/cliente'
import { establecerTokenDeSesionBearer, tokenDeSesionBearerActual } from '../api/entornoTauri'
import { ROL } from '../api/tipos'
import type { PuntoVentaListado, UsuarioAutenticado } from '../api/tipos'

const apiPostMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: vi.fn(),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown?])),
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

const listarPuntosVentaMock = vi.fn()
vi.mock('../api/organizacion', () => ({
  clienteDeOrganizacion: { listarPuntosVenta: (...args: unknown[]) => listarPuntosVentaMock(...args) },
}))

const vincularMock = vi.fn()
vi.mock('../api/dispositivos', () => ({
  clienteDeDispositivos: { vincular: (...args: unknown[]) => vincularMock(...args) },
}))

// Solo se reemplaza el guardado de la credencial: `corriendoEnTauri`, el token en memoria y la
// persistencia de sesión son los reales, así que "nunca persiste" se observa en el IPC.
const guardarCredencialDeDispositivoMock = vi.fn()
vi.mock('../api/entornoTauri', async (importarOriginal) => ({
  ...(await importarOriginal<typeof import('../api/entornoTauri')>()),
  guardarCredencialDeDispositivo: (...args: unknown[]) => guardarCredencialDeDispositivoMock(...args),
}))

type GlobalConTauri = typeof globalThis & { __TAURI__?: { core: { invoke: ReturnType<typeof vi.fn> } } }
const invokeMock = vi.fn()

function instalarPuenteTauri() {
  invokeMock.mockResolvedValue(undefined)
  ;(globalThis as GlobalConTauri).__TAURI__ = { core: { invoke: invokeMock } }
}

function usuarioFixture(sobrescribir: Partial<UsuarioAutenticado> = {}): UsuarioAutenticado {
  return {
    id: 1,
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
    id: 7,
    idTenant: 1,
    idEmpresa: 3,
    nombre: 'Local Centro',
    domicilio: null,
    horario: null,
    whatsapp: null,
    instagram: null,
    facebook: null,
    web: null,
    nombreTenant: 'Tenant Demo',
    razonSocialEmpresa: 'Empresa Demo',
    modo: 'Web',
    ...sobrescribir,
  }
}

afterEach(() => {
  delete (globalThis as GlobalConTauri).__TAURI__
  invokeMock.mockReset()
  establecerTokenDeSesionBearer(null)
})

beforeEach(() => {
  apiPostMock.mockReset()
  listarPuntosVentaMock.mockReset()
  vincularMock.mockReset()
  guardarCredencialDeDispositivoMock.mockReset()
  guardarCredencialDeDispositivoMock.mockResolvedValue(undefined)
})

async function iniciarSesionComo(usuario: UsuarioAutenticado) {
  apiPostMock.mockImplementation((ruta: string) => (ruta === '/auth/login' ? Promise.resolve(usuario) : Promise.resolve(undefined)))
  await userEvent.type(screen.getByPlaceholderText('Correo electrónico'), usuario.mail)
  await userEvent.type(screen.getByPlaceholderText('Contraseña'), 'secreta123')
  await userEvent.click(screen.getByRole('button', { name: 'Continuar' }))
}

describe('PantallaDeVinculacion', () => {
  it('un usuario no-admin ve el error y la sesión se cierra, sin pasar al paso de elegir PV', async () => {
    const alVinculado = vi.fn()
    render(<PantallaDeVinculacion alVinculado={alVinculado} />)

    await iniciarSesionComo(usuarioFixture({ rolId: ROL.Vendedor, rol: 'Vendedor' }))

    expect(await screen.findByText('Se necesita un usuario administrador para vincular el dispositivo.')).toBeInTheDocument()
    expect(screen.queryByLabelText('Punto de venta')).not.toBeInTheDocument()
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/auth/logout'))
  })

  it('un Admin avanza al paso de elegir PV + nombre del equipo', async () => {
    listarPuntosVentaMock.mockResolvedValue([puntoVentaFixture(), puntoVentaFixture({ id: 8, nombre: 'Sucursal Norte' })])
    render(<PantallaDeVinculacion alVinculado={vi.fn()} />)

    await iniciarSesionComo(usuarioFixture())

    expect(await screen.findByLabelText('Punto de venta')).toBeInTheDocument()
    expect(screen.getByRole('option', { name: 'Local Centro' })).toBeInTheDocument()
    expect(screen.getByRole('option', { name: 'Sucursal Norte' })).toBeInTheDocument()
  })

  it('vincular envía idPuntoVenta + nombre, cierra la sesión de admin y avisa con el dispositivo', async () => {
    listarPuntosVentaMock.mockResolvedValue([puntoVentaFixture()])
    const dispositivo = {
      id: 1,
      nombre: 'Caja 1',
      idPuntoVenta: 7,
      puntoVenta: { numero: 1, nombre: 'Local Centro' },
      empresa: { nombre: 'Empresa Demo' },
    }
    vincularMock.mockResolvedValue({ datos: dispositivo, secreto: 'un-secreto-de-prueba' })
    const alVinculado = vi.fn()
    render(<PantallaDeVinculacion alVinculado={alVinculado} />)

    await iniciarSesionComo(usuarioFixture())
    await screen.findByLabelText('Punto de venta')

    await userEvent.selectOptions(screen.getByLabelText('Punto de venta'), 'Local Centro')
    await userEvent.type(screen.getByLabelText('Nombre del equipo'), 'Caja 1')
    await userEvent.click(screen.getByRole('button', { name: 'Vincular' }))

    await waitFor(() => expect(vincularMock).toHaveBeenCalledWith({ idPuntoVenta: 7, nombre: 'Caja 1' }))
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/auth/logout'))
    await waitFor(() => expect(alVinculado).toHaveBeenCalledWith(dispositivo))
  })

  it('doble click en "Continuar" dispara exactamente un POST de login (react-async-state regla 9)', async () => {
    let resolverLogin: (usuario: UsuarioAutenticado) => void = () => {}
    const pendiente = new Promise<UsuarioAutenticado>((resolve) => {
      resolverLogin = resolve
    })
    apiPostMock.mockImplementation((ruta: string) => (ruta === '/auth/login' ? pendiente : Promise.resolve(undefined)))
    listarPuntosVentaMock.mockResolvedValue([puntoVentaFixture()])
    render(<PantallaDeVinculacion alVinculado={vi.fn()} />)

    await userEvent.type(screen.getByPlaceholderText('Correo electrónico'), 'admin@ways.test')
    await userEvent.type(screen.getByPlaceholderText('Contraseña'), 'secreta123')
    const boton = screen.getByRole('button', { name: 'Continuar' })
    fireEvent.click(boton)
    fireEvent.click(boton)

    resolverLogin(usuarioFixture())
    await waitFor(() => expect(apiPostMock.mock.calls.filter((c) => c[0] === '/auth/login')).toHaveLength(1))
  })

  it('si vincular falla, muestra el error del servidor y no persiste ni cierra sesión', async () => {
    listarPuntosVentaMock.mockResolvedValue([puntoVentaFixture()])
    vincularMock.mockRejectedValue(new ErrorApi(409, 'punto_de_venta_ya_vinculado', 'El punto de venta ya tiene un dispositivo vinculado.'))
    const alVinculado = vi.fn()
    render(<PantallaDeVinculacion alVinculado={alVinculado} />)

    await iniciarSesionComo(usuarioFixture())
    await screen.findByLabelText('Punto de venta')
    await userEvent.selectOptions(screen.getByLabelText('Punto de venta'), 'Local Centro')
    await userEvent.type(screen.getByLabelText('Nombre del equipo'), 'Caja 1')
    await userEvent.click(screen.getByRole('button', { name: 'Vincular' }))

    expect(await screen.findByText('El punto de venta ya tiene un dispositivo vinculado.')).toBeInTheDocument()
    expect(alVinculado).not.toHaveBeenCalled()
    expect(guardarCredencialDeDispositivoMock).not.toHaveBeenCalled()
    expect(apiPostMock).not.toHaveBeenCalledWith('/auth/logout')
  })

  /** judgment-day ronda 1 (hallazgo WARNING/SUGGESTION, ambos jueces): si el POST de vinculación
   * ya comprometió al servidor (dispositivo creado, secreto emitido UNA sola vez) y solo falla el
   * IPC local, el mensaje NO puede ser el genérico "no se pudo vincular" — eso es falso (el
   * vínculo existe) y empujaría a un reintento que crea un segundo dispositivo huérfano. Tampoco
   * cierra la sesión de admin ni avisa `alVinculado`: la pantalla se queda como está para que el
   * admin lea el mensaje real (revocar y volver a vincular). */
  it('si vincular funciona pero falla el guardado local, avisa que el dispositivo YA quedó vinculado y no cierra la sesión ni avanza', async () => {
    listarPuntosVentaMock.mockResolvedValue([puntoVentaFixture()])
    const dispositivo = {
      id: 1,
      nombre: 'Caja 1',
      idPuntoVenta: 7,
      puntoVenta: { numero: 1, nombre: 'Local Centro' },
      empresa: { nombre: 'Empresa Demo' },
    }
    vincularMock.mockResolvedValue({ datos: dispositivo, secreto: 'un-secreto-de-prueba' })
    guardarCredencialDeDispositivoMock.mockRejectedValue(new Error('IPC falló'))
    const alVinculado = vi.fn()
    render(<PantallaDeVinculacion alVinculado={alVinculado} />)

    await iniciarSesionComo(usuarioFixture())
    await screen.findByLabelText('Punto de venta')
    await userEvent.selectOptions(screen.getByLabelText('Punto de venta'), 'Local Centro')
    await userEvent.type(screen.getByLabelText('Nombre del equipo'), 'Caja 1')
    await userEvent.click(screen.getByRole('button', { name: 'Vincular' }))

    expect(await screen.findByText(/El dispositivo quedó vinculado, pero no se pudo guardar la credencial/)).toBeInTheDocument()
    expect(screen.getByText(/Revocalo desde Dispositivos y volvé a vincularlo/)).toBeInTheDocument()
    expect(alVinculado).not.toHaveBeenCalled()
    expect(apiPostMock).not.toHaveBeenCalledWith('/auth/logout')

    // judgment-day ronda 2 (residual #5, juez A): el mensaje le dice al admin que revoque y
    // vuelva a vincular en vez de reintentar a ciegas — este bloque prueba que la pantalla
    // realmente lo hace imposible, no solo que lo diga. Ningún control de este paso (select,
    // input, botón) puede sobrevivir al estado terminal.
    expect(screen.queryByLabelText('Punto de venta')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Nombre del equipo')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Vincular' })).not.toBeInTheDocument()
  })
})

const dispositivoFixture = {
  id: 1,
  nombre: 'Caja 1',
  idPuntoVenta: 7,
  puntoVenta: { numero: 1, nombre: 'Local Centro' },
  empresa: { nombre: 'Empresa Demo' },
}

/** Bajo Tauri el login admin responde con el bearer (`solicitarBearer: true`). */
async function iniciarSesionBajoTauriComo(usuario: UsuarioAutenticado, token = 'token-admin') {
  apiPostMock.mockImplementation((ruta: string) =>
    ruta === '/auth/login' ? Promise.resolve({ usuario, token, expiraEl: '2026-09-27T12:15:00Z' }) : Promise.resolve(undefined),
  )
  await userEvent.type(screen.getByPlaceholderText('Correo electrónico'), usuario.mail)
  await userEvent.type(screen.getByPlaceholderText('Contraseña'), 'secreta123')
  await userEvent.click(screen.getByRole('button', { name: 'Continuar' }))
}

async function completarVinculacion() {
  await screen.findByLabelText('Punto de venta')
  await userEvent.selectOptions(screen.getByLabelText('Punto de venta'), 'Local Centro')
  await userEvent.type(screen.getByLabelText('Nombre del equipo'), 'Caja 1')
  await userEvent.click(screen.getByRole('button', { name: 'Vincular' }))
}

function nuncaPersistioSesion() {
  expect(invokeMock.mock.calls.filter(([comando]) => comando === 'guardar_sesion_de_cajero')).toEqual([])
}

describe('PantallaDeVinculacion bajo Tauri (bearer de admin en memoria)', () => {
  it('fuera de Tauri el login admin no pide bearer ni instala token', async () => {
    listarPuntosVentaMock.mockResolvedValue([puntoVentaFixture()])
    render(<PantallaDeVinculacion alVinculado={vi.fn()} />)

    await iniciarSesionComo(usuarioFixture())
    await screen.findByLabelText('Punto de venta')

    expect(apiPostMock).toHaveBeenCalledWith('/auth/login', { mail: 'admin@ways.test', password: 'secreta123' })
    expect(tokenDeSesionBearerActual()).toBeNull()
  })

  it('pide solicitarBearer, instala el token en memoria antes de listar los PV y nunca lo persiste', async () => {
    instalarPuenteTauri()
    let tokenAlListar: string | null = 'sin-llamar'
    listarPuntosVentaMock.mockImplementation(() => {
      tokenAlListar = tokenDeSesionBearerActual()
      return Promise.resolve([puntoVentaFixture()])
    })
    render(<PantallaDeVinculacion alVinculado={vi.fn()} />)

    await iniciarSesionBajoTauriComo(usuarioFixture())
    await screen.findByLabelText('Punto de venta')

    expect(apiPostMock).toHaveBeenCalledWith('/auth/login', { mail: 'admin@ways.test', password: 'secreta123', solicitarBearer: true })
    expect(tokenAlListar).toBe('token-admin')
    expect(tokenDeSesionBearerActual()).toBe('token-admin')
    nuncaPersistioSesion()
  })

  it('un no-admin ve el error, el token nunca queda instalado y no se llama a /auth/logout', async () => {
    instalarPuenteTauri()
    render(<PantallaDeVinculacion alVinculado={vi.fn()} />)

    await iniciarSesionBajoTauriComo(usuarioFixture({ rolId: ROL.Vendedor, rol: 'Vendedor' }))

    expect(await screen.findByText('Se necesita un usuario administrador para vincular el dispositivo.')).toBeInTheDocument()
    expect(tokenDeSesionBearerActual()).toBeNull()
    expect(apiPostMock).not.toHaveBeenCalledWith('/auth/logout')
    expect(listarPuntosVentaMock).not.toHaveBeenCalled()
    nuncaPersistioSesion()
  })

  it('al vincular, suelta el token ANTES de avisar alVinculado y no llama a /auth/logout', async () => {
    instalarPuenteTauri()
    listarPuntosVentaMock.mockResolvedValue([puntoVentaFixture()])
    vincularMock.mockResolvedValue({ datos: dispositivoFixture, secreto: 'un-secreto-de-prueba' })
    let tokenAlAvisar: string | null = 'sin-llamar'
    const alVinculado = vi.fn(() => {
      tokenAlAvisar = tokenDeSesionBearerActual()
    })
    render(<PantallaDeVinculacion alVinculado={alVinculado} />)

    await iniciarSesionBajoTauriComo(usuarioFixture())
    await completarVinculacion()

    await waitFor(() => expect(alVinculado).toHaveBeenCalledWith(dispositivoFixture))
    expect(tokenAlAvisar).toBeNull()
    expect(tokenDeSesionBearerActual()).toBeNull()
    expect(apiPostMock).not.toHaveBeenCalledWith('/auth/logout')
    nuncaPersistioSesion()
  })

  it('si falla el listado de PV después del login, suelta el token', async () => {
    instalarPuenteTauri()
    listarPuntosVentaMock.mockRejectedValue(new ErrorApi(500, 'error', 'Falló el listado.'))
    render(<PantallaDeVinculacion alVinculado={vi.fn()} />)

    await iniciarSesionBajoTauriComo(usuarioFixture())

    expect(await screen.findByText('Falló el listado.')).toBeInTheDocument()
    expect(tokenDeSesionBearerActual()).toBeNull()
  })

  it('si el guardado local de la credencial falla (flujo terminal), suelta el token', async () => {
    instalarPuenteTauri()
    listarPuntosVentaMock.mockResolvedValue([puntoVentaFixture()])
    vincularMock.mockResolvedValue({ datos: dispositivoFixture, secreto: 'un-secreto-de-prueba' })
    guardarCredencialDeDispositivoMock.mockRejectedValue(new Error('IPC falló'))
    render(<PantallaDeVinculacion alVinculado={vi.fn()} />)

    await iniciarSesionBajoTauriComo(usuarioFixture())
    await completarVinculacion()

    expect(await screen.findByText(/El dispositivo quedó vinculado, pero no se pudo guardar la credencial/)).toBeInTheDocument()
    expect(tokenDeSesionBearerActual()).toBeNull()
  })

  it('un 401 al vincular (bearer vencido) vuelve al login y suelta el token', async () => {
    instalarPuenteTauri()
    listarPuntosVentaMock.mockResolvedValue([puntoVentaFixture()])
    vincularMock.mockRejectedValue(new ErrorApi(401, 'no_autenticado', 'Tu sesión expiró.'))
    render(<PantallaDeVinculacion alVinculado={vi.fn()} />)

    await iniciarSesionBajoTauriComo(usuarioFixture())
    await completarVinculacion()

    expect(await screen.findByText('La sesión de administrador venció. Volvé a ingresar.')).toBeInTheDocument()
    expect(screen.queryByLabelText('Punto de venta')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Continuar' })).toBeEnabled()
    expect(tokenDeSesionBearerActual()).toBeNull()
  })

  it('otro error al vincular deja el token para reintentar sin volver a loguear', async () => {
    instalarPuenteTauri()
    listarPuntosVentaMock.mockResolvedValue([puntoVentaFixture()])
    vincularMock.mockRejectedValue(new ErrorApi(409, 'punto_de_venta_ya_vinculado', 'El punto de venta ya tiene un dispositivo vinculado.'))
    render(<PantallaDeVinculacion alVinculado={vi.fn()} />)

    await iniciarSesionBajoTauriComo(usuarioFixture())
    await completarVinculacion()

    expect(await screen.findByText('El punto de venta ya tiene un dispositivo vinculado.')).toBeInTheDocument()
    expect(screen.getByLabelText('Punto de venta')).toBeInTheDocument()
    expect(tokenDeSesionBearerActual()).toBe('token-admin')
  })

  it('si se desmonta con el login en vuelo, el token que llega tarde nunca se instala', async () => {
    instalarPuenteTauri()
    let resolverLogin: (valor: unknown) => void = () => {}
    apiPostMock.mockImplementation(
      () =>
        new Promise((resolve) => {
          resolverLogin = resolve
        }),
    )
    const vista = render(<PantallaDeVinculacion alVinculado={vi.fn()} />)
    await userEvent.type(screen.getByPlaceholderText('Correo electrónico'), 'admin@ways.test')
    await userEvent.type(screen.getByPlaceholderText('Contraseña'), 'secreta123')
    await userEvent.click(screen.getByRole('button', { name: 'Continuar' }))

    vista.unmount()
    await act(async () => {
      resolverLogin({ usuario: usuarioFixture(), token: 'token-tardio', expiraEl: '2026-09-27T12:15:00Z' })
    })

    expect(tokenDeSesionBearerActual()).toBeNull()
    expect(listarPuntosVentaMock).not.toHaveBeenCalled()
  })

  it('al desmontar suelta su token, pero no uno que otro flujo instaló después', async () => {
    instalarPuenteTauri()
    listarPuntosVentaMock.mockResolvedValue([puntoVentaFixture()])
    const primera = render(<PantallaDeVinculacion alVinculado={vi.fn()} />)
    await iniciarSesionBajoTauriComo(usuarioFixture())
    await screen.findByLabelText('Punto de venta')
    expect(tokenDeSesionBearerActual()).toBe('token-admin')

    primera.unmount()
    expect(tokenDeSesionBearerActual()).toBeNull()

    const segunda = render(<PantallaDeVinculacion alVinculado={vi.fn()} />)
    await iniciarSesionBajoTauriComo(usuarioFixture(), 'token-admin-2')
    await screen.findByLabelText('Punto de venta')
    establecerTokenDeSesionBearer('token-de-otro-flujo')

    segunda.unmount()
    expect(tokenDeSesionBearerActual()).toBe('token-de-otro-flujo')
  })
})
