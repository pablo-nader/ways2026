import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { AppPos } from './AppPos'
import { ErrorApi } from '../api/cliente'
import { ROL } from '../api/tipos'
import type { ClienteListado, MedioPagoListado, PaginaDe, ParametroResuelto, PuntoVentaListado, UsuarioAutenticado } from '../api/tipos'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()
const leerCredencialDeDispositivoMock = vi.fn()

// `dispositivos.ts` (no mockeado en este archivo, corre real) también importa de aca
// (`corriendoEnTauri`/`establecerTokenDeSesionBearer`) — el mock tiene que cubrir TODO lo que el
// módulo real exporta, o esos imports quedan `undefined` en cualquier módulo que lo importe
// (Vitest mockea por path resuelto, no por import individual).
vi.mock('../api/entornoTauri', () => ({
  corriendoEnTauri: () => false,
  establecerTokenDeSesionBearer: () => {},
  guardarCredencialDeDispositivo: () => Promise.resolve(),
  leerCredencialDeDispositivo: (...args: unknown[]) => leerCredencialDeDispositivoMock(...args),
  tokenDeSesionBearerActual: () => null,
  urlBaseApi: () => '',
  inicializarUrlServidor: () => Promise.resolve(),
}))

/** Espejo mínimo del observador real de `../api/cliente`: `dispararPerdidaDeSesion` simula lo que
 * `exigirRespuestaOk` hace en producción ante CUALQUIER 401 (`AuthContext` se suscribe igual, acá
 * se prueba que `AppPos` también reacciona). */
let observadores = new Set<() => void>()
function dispararPerdidaDeSesion() {
  observadores.forEach((o) => o())
}

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown?])),
    put: vi.fn(),
    delete: vi.fn(),
  },
  alPerderLaSesion: (o: () => void) => {
    observadores.add(o)
    return () => observadores.delete(o)
  },
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
  // stage-pos-venta-offline-web: este árbol monta `Pos.tsx` (vía `ShellPos`, ruta `/vender`), que
  // ahora hace `instanceof ErrorDeRed` (`useSincronizacionOffline`) — sin este mock el símbolo
  // importado queda `undefined` bajo este `vi.mock` y ese `instanceof` tira `TypeError`.
  ErrorDeRed: class ErrorDeRedMock extends Error {
    causa: unknown
    constructor(causa: unknown) {
      super('No se pudo contactar al servidor. Revisá tu conexión.')
      this.causa = causa
    }
  },
}))

const DISPOSITIVO = {
  id: 1,
  nombre: 'Caja 1',
  idPuntoVenta: 7,
  puntoVenta: { numero: 1, nombre: 'Local Centro' },
  empresa: { nombre: 'Almacén Demo' },
}

function usuarioFixture(sobrescribir: Partial<UsuarioAutenticado> = {}): UsuarioAutenticado {
  return {
    id: 4,
    usuario: 'jperez',
    mail: 'jperez@ways.test',
    rolId: ROL.Vendedor,
    rol: 'Vendedor',
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

const consumidorFinal: ClienteListado = {
  id: 1,
  numero: 1,
  nombre: 'Consumidor Final',
  apellido: null,
  razonSocial: null,
  tipoDocumento: null,
  numeroDocumento: null,
  idCondicionFiscal: 1,
  nacimiento: null,
  domicilio: null,
  telefono: null,
  celular: null,
  email: null,
  observaciones: null,
  idListaPrecio: 1,
  limiteCredito: 0,
  creditoIlimitado: true,
  saldo: 0,
  activo: true,
  idEmpresa: null,
  esConsumidorFinal: true,
}

const medioEfectivo: MedioPagoListado = {
  id: 1,
  nombre: 'Efectivo',
  activo: true,
  idEmpresa: null,
  orden: 1,
  comportamiento: 'Efectivo',
  admiteVuelto: true,
  requiereReferencia: false,
  recargoPorcentaje: null,
}

const SIN_SESION = () => Promise.reject(new ErrorApi(401, 'no_autenticado', 'Tu sesión expiró.'))

/** Rutas comunes (dispositivo, catálogo de `Pos.tsx`, PV) — `/auth/me` por defecto es "sin
 * sesión" (401), que es el camino que la mayoría de los tests de este archivo ejercita; los
 * tests de restart-con-sesión lo sobrescriben. */
function mockearRutasComunes(sobrescribir?: (ruta: string) => Promise<unknown> | undefined) {
  apiGetMock.mockImplementation((ruta: string) => {
    // `sobrescribir` va PRIMERO: varios tests de este archivo necesitan pisar `/auth/me` (el
    // camino por defecto de abajo es "sin sesión") o `/puntos-venta` (PV inexistente).
    const propia = sobrescribir?.(ruta)
    if (propia) return propia

    if (ruta === '/auth/me') return SIN_SESION()
    if (ruta === '/clientes') {
      const pagina: PaginaDe<ClienteListado> = { items: [consumidorFinal], total: 1, pagina: 1, tamanio: 25 }
      return Promise.resolve(pagina)
    }
    if (ruta === '/catalogos/medios-pago') return Promise.resolve<MedioPagoListado[]>([medioEfectivo])
    if (ruta.startsWith('/parametros/tolerancia_pago')) return Promise.resolve<ParametroResuelto>({ clave: 'tolerancia_pago', valor: '10' })
    if (ruta === '/puntos-venta') return Promise.resolve<PuntoVentaListado[]>([puntoVentaFixture()])
    // stage-pos-turno-y-foco: turno ABIERTO por defecto — `Pos.tsx` lo consulta apenas monta.
    if (ruta.startsWith('/caja/turnos/abierto')) return Promise.resolve({ id: 501, estado: 'Abierto' })
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiPostMock.mockResolvedValue(undefined)
  observadores = new Set()
  leerCredencialDeDispositivoMock.mockReset()
  leerCredencialDeDispositivoMock.mockResolvedValue(null)
})

describe('AppPos — máquina de estados del POS de escritorio (stage-desktop-pos)', () => {
  it('dispositivo no vinculado (404 dispositivo_no_vinculado) muestra la pantalla de vinculación', async () => {
    apiGetMock.mockImplementation((ruta: string) =>
      ruta === '/dispositivos/actual'
        ? Promise.reject(new ErrorApi(404, 'dispositivo_no_vinculado', 'El dispositivo no está vinculado.'))
        : Promise.reject(new Error(`ruta no mockeada: ${ruta}`)),
    )
    render(<AppPos />)

    expect(await screen.findByText('Vincular este equipo')).toBeInTheDocument()
  })

  it('un error de red (no un 404 explícito) SIN credencial local muestra el error genérico', async () => {
    // stage-desktop-pos, slice 3: sin respuesta del servidor Y sin credencial local guardada
    // (`leerCredencialDeDispositivoMock` resuelve null por defecto, ver beforeEach) no hay
    // ninguna evidencia de nada — mensaje genérico, nunca "vincular" (esa pantalla exige un 404
    // EXPLÍCITO del servidor, ver el test de más abajo).
    apiGetMock.mockImplementation((ruta: string) =>
      ruta === '/dispositivos/actual' ? Promise.reject(new Error('fetch falló')) : Promise.reject(new Error(`ruta no mockeada: ${ruta}`)),
    )
    render(<AppPos />)

    expect(await screen.findByText('No se pudo determinar el dispositivo.')).toBeInTheDocument()
    expect(screen.queryByText('Vincular este equipo')).not.toBeInTheDocument()
    expect(leerCredencialDeDispositivoMock).toHaveBeenCalled()
  })

  it('un error de red (no un 404 explícito) CON credencial local guardada distingue "sin red pero vinculado"', async () => {
    // Punto F de la slice 3: la credencial local SOLO se consulta cuando al servidor no se le
    // pudo ni preguntar (este caso) — nunca cuando respondió con un error real, y nunca puede
    // pisar un 404 `dispositivo_no_vinculado` explícito (ver el test de más abajo, sin este
    // mock). La base sigue siendo la única autoridad: esto es un mensaje MÁS específico, no un
    // salto directo a "con sesión".
    leerCredencialDeDispositivoMock.mockResolvedValue('secreto-guardado')
    apiGetMock.mockImplementation((ruta: string) =>
      ruta === '/dispositivos/actual' ? Promise.reject(new Error('fetch falló')) : Promise.reject(new Error(`ruta no mockeada: ${ruta}`)),
    )
    render(<AppPos />)

    expect(await screen.findByText(/este equipo ya está vinculado/)).toBeInTheDocument()
    expect(screen.queryByText('No se pudo determinar el dispositivo.')).not.toBeInTheDocument()
    expect(screen.queryByText('Vincular este equipo')).not.toBeInTheDocument()
  })

  it('un 404 dispositivo_no_vinculado EXPLÍCITO manda a vincular aunque haya una credencial local guardada, sin consultarla', async () => {
    // Esto NO es un error de red: el servidor respondió con un 404 explícito
    // (`ErrorApi(404, 'dispositivo_no_vinculado', ...)`), la misma rama que el test de más abajo
    // sin credencial. El 404 explícito siempre gana, aunque haya una credencial local — la base
    // es la única fuente de verdad (ver doc-comment de `AppPos`, paso 1).
    leerCredencialDeDispositivoMock.mockResolvedValue('secreto-guardado')
    apiGetMock.mockImplementation((ruta: string) =>
      ruta === '/dispositivos/actual'
        ? Promise.reject(new ErrorApi(404, 'dispositivo_no_vinculado', 'El dispositivo no está vinculado.'))
        : Promise.reject(new Error(`ruta no mockeada: ${ruta}`)),
    )
    render(<AppPos />)

    expect(await screen.findByText('Vincular este equipo')).toBeInTheDocument()
    expect(leerCredencialDeDispositivoMock).not.toHaveBeenCalled()
  })

  it('un error del servidor RECHAZABLE (no un fallo de red) nunca consulta la credencial local, aunque haya una', async () => {
    // El servidor SÍ respondió (con un error, pero respondió) — "no se pudo ni preguntar" no
    // aplica, así que la credencial local no entra en juego aunque exista.
    leerCredencialDeDispositivoMock.mockResolvedValue('secreto-guardado')
    apiGetMock.mockImplementation((ruta: string) =>
      ruta === '/dispositivos/actual'
        ? Promise.reject(new ErrorApi(500, 'error_interno', 'Error interno del servidor.'))
        : Promise.reject(new Error(`ruta no mockeada: ${ruta}`)),
    )
    render(<AppPos />)

    expect(await screen.findByText('Error interno del servidor.')).toBeInTheDocument()
    expect(leerCredencialDeDispositivoMock).not.toHaveBeenCalled()
  })

  it('un 404 dispositivo_no_vinculado EXPLÍCITO manda a vincular (la base es la fuente de verdad)', async () => {
    apiGetMock.mockImplementation((ruta: string) =>
      ruta === '/dispositivos/actual'
        ? Promise.reject(new ErrorApi(404, 'dispositivo_no_vinculado', 'El dispositivo no está vinculado.'))
        : Promise.reject(new Error(`ruta no mockeada: ${ruta}`)),
    )
    render(<AppPos />)

    expect(await screen.findByText('Vincular este equipo')).toBeInTheDocument()
  })

  it('dispositivo vinculado sin sesión (GET /auth/me 401) muestra el login del dispositivo con empresa/PV/nombre', async () => {
    mockearRutasComunes((ruta) => (ruta === '/dispositivos/actual' ? Promise.resolve(DISPOSITIVO) : undefined))
    render(<AppPos />)

    expect(await screen.findByText('Almacén Demo')).toBeInTheDocument()
    expect(screen.getByText(/PV 1 — Local Centro · Caja 1/)).toBeInTheDocument()
  })

  it('el login del dispositivo llama a login-dispositivo con el usuario tipeado', async () => {
    mockearRutasComunes((ruta) => (ruta === '/dispositivos/actual' ? Promise.resolve(DISPOSITIVO) : undefined))
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/auth/login-dispositivo' ? Promise.resolve(usuarioFixture()) : Promise.reject(new Error(`ruta no mockeada: ${ruta}`)),
    )
    render(<AppPos />)

    await screen.findByPlaceholderText('Usuario')
    await userEvent.type(screen.getByPlaceholderText('Usuario'), 'jperez')
    await userEvent.type(screen.getByPlaceholderText('Contraseña'), 'secreta123')
    await userEvent.click(screen.getByRole('button', { name: 'Ingresar' }))

    // solicitarBearer: false porque corriendoEnTauri() está mockeado en false en este archivo
    // (jsdom, sin window.__TAURI__) — el contrato del navegador normal no cambia.
    await waitFor(() =>
      expect(apiPostMock).toHaveBeenCalledWith('/auth/login-dispositivo', {
        usuario: 'jperez',
        password: 'secreta123',
        solicitarBearer: false,
      }),
    )
  })

  it('con sesión llega al shell: header con empresa/PV/cajero y "Vender" ya montado (Pos con el PV fijo)', async () => {
    mockearRutasComunes((ruta) => (ruta === '/dispositivos/actual' ? Promise.resolve(DISPOSITIVO) : undefined))
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/auth/login-dispositivo' ? Promise.resolve(usuarioFixture()) : Promise.reject(new Error(`ruta no mockeada: ${ruta}`)),
    )
    render(<AppPos />)

    await screen.findByPlaceholderText('Usuario')
    await userEvent.type(screen.getByPlaceholderText('Usuario'), 'jperez')
    await userEvent.type(screen.getByPlaceholderText('Contraseña'), 'secreta123')
    await userEvent.click(screen.getByRole('button', { name: 'Ingresar' }))

    expect(await screen.findByRole('link', { name: 'Vender' })).toBeInTheDocument()
    expect(screen.getByText('Almacén Demo')).toBeInTheDocument()
    expect(screen.getByText(/PV 1 — Local Centro · jperez/)).toBeInTheDocument()
    // Prueba que el PV fijo llegó de verdad a `Pos.tsx` vía `usePuntoVenta()`: el efecto de
    // parámetros de pago consulta el `idEmpresa`/`idPuntoVenta` del PV RESUELTO
    // (`puntoVentaFixture()`, id 7 / idEmpresa 3), nunca uno sintético armado desde el dispositivo.
    await waitFor(() => expect(apiGetMock).toHaveBeenCalledWith('/parametros/tolerancia_pago?idEmpresa=3&idPuntoVenta=7'))
  })

  it('un dispositivo revocado entre la carga y el login (dispositivo_no_vinculado) vuelve a la pantalla de vinculación', async () => {
    mockearRutasComunes((ruta) => (ruta === '/dispositivos/actual' ? Promise.resolve(DISPOSITIVO) : undefined))
    apiPostMock.mockImplementation((ruta: string) =>
      ruta === '/auth/login-dispositivo'
        ? Promise.reject(new ErrorApi(404, 'dispositivo_no_vinculado', 'El dispositivo no está vinculado.'))
        : Promise.reject(new Error(`ruta no mockeada: ${ruta}`)),
    )
    render(<AppPos />)

    await screen.findByPlaceholderText('Usuario')
    await userEvent.type(screen.getByPlaceholderText('Usuario'), 'jperez')
    await userEvent.type(screen.getByPlaceholderText('Contraseña'), 'secreta123')
    await userEvent.click(screen.getByRole('button', { name: 'Ingresar' }))

    expect(await screen.findByText('Vincular este equipo')).toBeInTheDocument()
  })
})

describe('AppPos — la sesión del cajero no vence hasta que cierra sesión (restart / F5)', () => {
  it('restart con una cookie de sesión todavía válida (GET /auth/me 200) entra directo al shell, sin pasar por el login', async () => {
    mockearRutasComunes((ruta) => {
      if (ruta === '/dispositivos/actual') return Promise.resolve(DISPOSITIVO)
      if (ruta === '/auth/me') return Promise.resolve(usuarioFixture())
      return undefined
    })
    render(<AppPos />)

    expect(await screen.findByRole('link', { name: 'Vender' })).toBeInTheDocument()
    expect(screen.queryByPlaceholderText('Usuario')).not.toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalledWith('/auth/login-dispositivo', expect.anything())
  })

  it('restart con la sesión vencida (GET /auth/me 401) muestra el login del dispositivo', async () => {
    mockearRutasComunes((ruta) => (ruta === '/dispositivos/actual' ? Promise.resolve(DISPOSITIVO) : undefined))
    render(<AppPos />)

    expect(await screen.findByPlaceholderText('Usuario')).toBeInTheDocument()
  })

  it('con sesión de un usuario que no puede operar el POS (ej. Root), cierra esa sesión y muestra el login', async () => {
    mockearRutasComunes((ruta) => {
      if (ruta === '/dispositivos/actual') return Promise.resolve(DISPOSITIVO)
      if (ruta === '/auth/me') return Promise.resolve(usuarioFixture({ rolId: ROL.Root, rol: 'Root', idTenant: null }))
      return undefined
    })
    render(<AppPos />)

    expect(await screen.findByPlaceholderText('Usuario')).toBeInTheDocument()
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/auth/logout'))
  })

  it('con sesión pero el PV del dispositivo ya no existe, cierra esa sesión y muestra el login', async () => {
    mockearRutasComunes((ruta) => {
      if (ruta === '/dispositivos/actual') return Promise.resolve(DISPOSITIVO)
      if (ruta === '/auth/me') return Promise.resolve(usuarioFixture())
      if (ruta === '/puntos-venta') return Promise.resolve<PuntoVentaListado[]>([puntoVentaFixture({ id: 999 })])
      return undefined
    })
    render(<AppPos />)

    expect(await screen.findByPlaceholderText('Usuario')).toBeInTheDocument()
    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/auth/logout'))
  })

  it('un 401 en cualquier llamada mientras se está en con-sesion vuelve al login (dispositivo sigue vinculado)', async () => {
    mockearRutasComunes((ruta) => {
      if (ruta === '/dispositivos/actual') return Promise.resolve(DISPOSITIVO)
      if (ruta === '/auth/me') return Promise.resolve(usuarioFixture())
      return undefined
    })
    render(<AppPos />)
    await screen.findByRole('link', { name: 'Vender' })

    // La sesión se revoca del lado del servidor: la próxima vez que `AppPos` resuelva todo de
    // nuevo, `/auth/me` ya no la reconoce.
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/dispositivos/actual') return Promise.resolve(DISPOSITIVO)
      if (ruta === '/auth/me') return SIN_SESION()
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    dispararPerdidaDeSesion()

    expect(await screen.findByPlaceholderText('Usuario')).toBeInTheDocument()
  })

  it('un 401 en con-sesion con el dispositivo también revocado (404) vuelve a la pantalla de vinculación', async () => {
    mockearRutasComunes((ruta) => {
      if (ruta === '/dispositivos/actual') return Promise.resolve(DISPOSITIVO)
      if (ruta === '/auth/me') return Promise.resolve(usuarioFixture())
      return undefined
    })
    render(<AppPos />)
    await screen.findByRole('link', { name: 'Vender' })

    apiGetMock.mockImplementation((ruta: string) =>
      ruta === '/dispositivos/actual'
        ? Promise.reject(new ErrorApi(404, 'dispositivo_no_vinculado', 'El dispositivo no está vinculado.'))
        : Promise.reject(new Error(`ruta no mockeada: ${ruta}`)),
    )
    dispararPerdidaDeSesion()

    expect(await screen.findByText('Vincular este equipo')).toBeInTheDocument()
  })

  it('un 401 mientras se está en la pantalla de login (todavía no hay sesión) no hace nada raro', async () => {
    mockearRutasComunes((ruta) => (ruta === '/dispositivos/actual' ? Promise.resolve(DISPOSITIVO) : undefined))
    render(<AppPos />)
    await screen.findByPlaceholderText('Usuario')

    dispararPerdidaDeSesion()

    expect(await screen.findByPlaceholderText('Usuario')).toBeInTheDocument()
  })
})
