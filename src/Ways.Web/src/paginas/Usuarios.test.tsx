import { act, cleanup, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { Usuarios } from './Usuarios'
import { ErrorApi } from '../api/cliente'
import { ETIQUETA_OPCION_PLATAFORMA, ETIQUETA_PLATAFORMA, ETIQUETA_SIN_DUENIO } from '../api/organizacion'
import { ROL } from '../api/tipos'
import type { PaginaDe, RolListado, TenantListado, UsuarioAutenticado, UsuarioListado } from '../api/tipos'

// stage-20-organizacion-relaciones-y-bajas, slice 2 (tareas 2.12 y 2.13).

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()
const apiPutMock = vi.fn()
const apiDeleteMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown])),
    put: (...args: unknown[]) => apiPutMock(...(args as [string, unknown])),
    delete: (...args: unknown[]) => apiDeleteMock(...(args as [string])),
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

function autenticadoFixture(sobrescribir: Partial<UsuarioAutenticado> = {}): UsuarioAutenticado {
  return {
    id: 1,
    usuario: 'root',
    mail: 'root@ways.test',
    rolId: ROL.Root,
    rol: 'Root',
    ultimaConexion: null,
    idTenant: null,
    ...sobrescribir,
  }
}

let usuarioActual: UsuarioAutenticado | null = autenticadoFixture()

vi.mock('../auth/useAuth', () => ({
  useAuth: () => ({ usuario: usuarioActual, cargando: false, iniciarSesion: vi.fn(), cerrarSesion: vi.fn() }),
}))

const ROLES: RolListado[] = [
  { id: ROL.Vendedor, nombre: 'Vendedor', descripcion: null },
  { id: ROL.Admin, nombre: 'Admin', descripcion: null },
  { id: ROL.Root, nombre: 'Root', descripcion: null },
]

function usuarioFixture(sobrescribir: Partial<UsuarioListado> = {}): UsuarioListado {
  return {
    id: 50,
    usuario: 'vendedor.sur',
    mail: 'vendedor.sur@ways.test',
    rolId: ROL.Vendedor,
    rol: 'Vendedor',
    estado: 'Activo',
    ultimaConexion: null,
    createdAt: '2026-03-01T10:00:00-03:00',
    idTenant: 2,
    nombreTenant: 'Comercio Sur',
    ...sobrescribir,
  }
}

const cuentaDeTenant = usuarioFixture()
const cuentaDeOtroTenant = usuarioFixture({
  id: 51,
  usuario: 'vendedor.este',
  mail: 'vendedor.este@ways.test',
  idTenant: 3,
  nombreTenant: 'Almacén Este',
})
/** `idTenant: null` obliga a `rolId: Root`: `PoliticaDeRoles.ValidarConsistenciaDeRolYAlcance`
 * rechaza cualquier otro rol sin tenant, así que un fixture con Vendedor y sin tenant sería una
 * entidad que el servidor no puede haber emitido. */
const cuentaDePlataforma = usuarioFixture({
  id: 52,
  usuario: 'staff',
  mail: 'staff@ways.test',
  rolId: ROL.Root,
  rol: 'Root',
  idTenant: null,
  nombreTenant: null,
})

/**
 * Fixture del universo de tenants pedido a `GET /plataforma/tenants` (tarea 2.17, gap de tamaño
 * de página). Por defecto se deriva de las filas montadas — igual que el viejo
 * `opcionesDeTenant(filas)` — así los tests que no ejercitan el gap no se enteran del cambio de
 * fuente; los que sí lo ejercitan pasan una lista explícita con un tenant AUSENTE de las filas.
 */
function tenantFixture(id: number, nombre: string): TenantListado {
  return {
    id,
    nombre,
    estado: 'Activo',
    createdAt: '2026-01-01T10:00:00-03:00',
    cantidadEmpresas: 0,
    cantidadPuntosVenta: 0,
    cantidadUsuarios: 0,
  }
}

function tenantsDesdeFilas(items: UsuarioListado[]): TenantListado[] {
  const porId = new Map<number, string>()
  for (const item of items) {
    if (item.idTenant !== null && !porId.has(item.idTenant)) {
      porId.set(item.idTenant, item.nombreTenant ?? `Tenant ${item.idTenant}`)
    }
  }

  return [...porId.entries()].map(([id, nombre]) => tenantFixture(id, nombre))
}

function montar(
  items: UsuarioListado[] = [cuentaDeTenant, cuentaDeOtroTenant, cuentaDePlataforma],
  tenantsDePlataforma: TenantListado[] = tenantsDesdeFilas(items),
) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/roles') return Promise.resolve(ROLES)
    if (ruta === '/plataforma/tenants') return Promise.resolve(tenantsDePlataforma)
    if (ruta.startsWith('/usuarios')) {
      return Promise.resolve<PaginaDe<UsuarioListado>>({
        items,
        total: items.length,
        pagina: 1,
        tamanio: 20,
      })
    }

    return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
  })

  return render(<Usuarios />)
}

/** Columnas: ID · Usuario · Mail · Tenant · Rol · Estado · Última conexión · Acciones. */
const COLUMNA_TENANT = 3

function usuariosVisibles() {
  return screen
    .getAllByRole('row')
    .slice(1)
    .map((f) => within(f).getAllByRole('cell')[1]?.textContent ?? '')
}

function celdaDeTenant(nombreDeUsuario: string) {
  const fila = screen.getByRole('row', { name: new RegExp(nombreDeUsuario) })

  return within(fila).getAllByRole('cell')[COLUMNA_TENANT]
}

describe('Usuarios (stage-20, slice 2 — columna de tenant y filtro)', () => {
  beforeEach(() => {
    apiGetMock.mockReset()
    usuarioActual = autenticadoFixture()
  })

  it('rinde el nombre del tenant para una cuenta de tenant', async () => {
    montar()
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())

    expect(celdaDeTenant('vendedor.sur')).toHaveTextContent('Comercio Sur')
  })

  /**
   * Cláusula bajo prueba: la etiqueta "Plataforma" la pone LA WEB (design D14). El servidor manda
   * `nombreTenant = null` y el discriminador es `idTenant === null`.
   */
  it('rinde el literal "Plataforma" para una cuenta sin tenant, nunca una celda vacía', async () => {
    montar()
    await waitFor(() => expect(screen.getByText('staff')).toBeInTheDocument())

    const celda = celdaDeTenant('staff')
    expect(celda).toHaveTextContent(ETIQUETA_PLATAFORMA)
    expect(celda.textContent?.trim()).not.toBe('')
  })

  /**
   * Cláusula bajo prueba: la MISMA (Reconciliación 9), por su lado adverso. Las dos filas llegan
   * con `nombreTenant === null`; solo `idTenant` las distingue. Una pantalla que se apoyara en el
   * nombre rendiría "Plataforma" en las dos y presentaría un huérfano como personal de
   * plataforma. Las dos cuentas conviven en el mismo dataset a propósito.
   */
  it('un huérfano y el personal de plataforma comparten nombre nulo y se rinden DISTINTO', async () => {
    const huerfano = usuarioFixture({ id: 53, usuario: 'huerfano', idTenant: 7, nombreTenant: null })
    montar([huerfano, cuentaDePlataforma])
    await waitFor(() => expect(screen.getByText('huerfano')).toBeInTheDocument())

    expect(celdaDeTenant('huerfano')).toHaveTextContent(ETIQUETA_SIN_DUENIO)
    expect(celdaDeTenant('huerfano')).not.toHaveTextContent(ETIQUETA_PLATAFORMA)
    expect(celdaDeTenant('staff')).toHaveTextContent(ETIQUETA_PLATAFORMA)
  })

  it('el filtro por tenant angosta las filas sin pedir nada más a la API', async () => {
    const usuario = userEvent.setup()
    montar()
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())

    const llamadasAlCargar = apiGetMock.mock.calls.length
    await usuario.selectOptions(screen.getByLabelText('Tenant'), '3')

    expect(usuariosVisibles()).toEqual(['vendedor.este'])
    expect(apiGetMock.mock.calls).toHaveLength(llamadasAlCargar)

    await usuario.selectOptions(screen.getByLabelText('Tenant'), '')
    expect(usuariosVisibles()).toEqual(['vendedor.sur', 'vendedor.este', 'staff'])
  })

  it('el filtro ofrece la opción de plataforma y aísla al personal sin tenant', async () => {
    const usuario = userEvent.setup()
    montar()
    await waitFor(() => expect(screen.getByText('staff')).toBeInTheDocument())

    await usuario.selectOptions(screen.getByLabelText('Tenant'), ETIQUETA_OPCION_PLATAFORMA)

    expect(usuariosVisibles()).toEqual(['staff'])
  })

  /**
   * Cláusula bajo prueba: el filtro NO confunde a un tenant llamado literalmente "Plataforma" con
   * el personal de plataforma. La columna rinde el mismo texto en las dos filas — `nombre` es
   * texto libre — así que el único separador es la clave del `<option>`, que para la cuenta sin
   * tenant no es ningún `String(idTenant)`.
   */
  it('un tenant llamado "Plataforma" no se confunde con el personal de plataforma', async () => {
    const usuario = userEvent.setup()
    const cuentaDeTenantHomonimo = usuarioFixture({
      id: 54,
      usuario: 'cuenta.homonima',
      idTenant: 9,
      nombreTenant: 'Plataforma',
    })
    montar([cuentaDeTenantHomonimo, cuentaDePlataforma])
    await waitFor(() => expect(screen.getByText('cuenta.homonima')).toBeInTheDocument())

    expect(celdaDeTenant('cuenta.homonima')).toHaveTextContent(ETIQUETA_PLATAFORMA)
    expect(celdaDeTenant('staff')).toHaveTextContent(ETIQUETA_PLATAFORMA)

    const etiquetas = within(screen.getByLabelText('Tenant'))
      .getAllByRole('option')
      .map((o) => o.textContent)
    expect(new Set(etiquetas).size).toBe(etiquetas.length)

    await usuario.selectOptions(screen.getByLabelText('Tenant'), ETIQUETA_OPCION_PLATAFORMA)
    expect(usuariosVisibles()).toEqual(['staff'])

    await usuario.selectOptions(screen.getByLabelText('Tenant'), '9')
    expect(usuariosVisibles()).toEqual(['cuenta.homonima'])
  })

  /**
   * Cláusula bajo prueba (ronda 2, R2-7): `esPlataforma &&` gatea el FILTRO por tenant y la COLUMNA
   * de tenant, con el mismo criterio que ya rige en `Empresas.tsx` y `PuntosVenta.tsx`. Para un
   * admin de tenant TODAS las filas son de su propio tenant —el servidor solo le manda esas—, así
   * que la columna repite el mismo nombre en cada fila y el filtro ofrece una sola opción que no
   * angosta nada: los dos son controles muertos.
   */
  it('un admin de tenant no ve el filtro ni la columna de tenant; un actor de plataforma sí', async () => {
    usuarioActual = autenticadoFixture({ id: 4, usuario: 'admin', rolId: ROL.Admin, rol: 'Admin', idTenant: 2 })
    montar([cuentaDeTenant])
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())

    expect(screen.queryByLabelText('Tenant')).not.toBeInTheDocument()
    expect(screen.queryByRole('columnheader', { name: 'Tenant' })).not.toBeInTheDocument()
    expect(screen.queryByText('Comercio Sur')).not.toBeInTheDocument()

    cleanup()
    apiGetMock.mockReset()
    usuarioActual = autenticadoFixture()
    montar([cuentaDeTenant])
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())

    expect(screen.getByLabelText('Tenant')).toBeInTheDocument()
    expect(screen.getByRole('columnheader', { name: 'Tenant' })).toBeInTheDocument()
    expect(celdaDeTenant('vendedor.sur')).toHaveTextContent('Comercio Sur')
  })

  /**
   * Cláusula bajo prueba: la guarda de generación de `cargar` (`react-async-state` regla 2). La
   * búsqueda es el ÚNICO camino de estas cuatro pantallas que puede tener dos LECTURAS en vuelo
   * — las escrituras bloquean todo lo que podría supersederlas (regla 9) — así que acá la guarda
   * es carga viva, no defensa en profundidad.
   *
   * El confundidor a esquivar es el de la regla 7: un `waitFor` que aserte "sigue estando lo
   * nuevo" sale verde en su primer tick, ANTES de que el microtask viejo aterrice, y no prueba
   * nada. Por eso la promesa vieja se resuelve DENTRO de `act` y la aserción va sincrónica
   * después del flush.
   */
  it('una respuesta de búsqueda vieja que aterriza tarde no pisa a la nueva', async () => {
    const usuario = userEvent.setup()
    let resolverVieja!: (pagina: PaginaDe<UsuarioListado>) => void
    const vieja = new Promise<PaginaDe<UsuarioListado>>((resolver) => {
      resolverVieja = resolver
    })

    function pagina(items: UsuarioListado[]): PaginaDe<UsuarioListado> {
      return { items, total: items.length, pagina: 1, tamanio: 20 }
    }

    let busquedas = 0
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/roles') return Promise.resolve(ROLES)
      if (ruta.startsWith('/usuarios')) {
        busquedas += 1
        if (busquedas === 1) return Promise.resolve(pagina([cuentaDeTenant, cuentaDeOtroTenant]))
        if (busquedas === 2) return vieja

        return Promise.resolve(pagina([cuentaDePlataforma]))
      }

      return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
    })

    render(<Usuarios />)
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())

    await usuario.type(screen.getByPlaceholderText('Buscar usuario o mail…'), 'sur')
    await usuario.click(screen.getByRole('button', { name: 'Buscar' }))

    await usuario.clear(screen.getByPlaceholderText('Buscar usuario o mail…'))
    await usuario.type(screen.getByPlaceholderText('Buscar usuario o mail…'), 'staff')
    await usuario.click(screen.getByRole('button', { name: 'Buscar' }))
    await waitFor(() => expect(screen.getByText('staff')).toBeInTheDocument())

    await act(async () => {
      resolverVieja(pagina([cuentaDeTenant, cuentaDeOtroTenant]))
      await vieja
    })

    expect(usuariosVisibles()).toEqual(['staff'])
    expect(screen.queryByText('vendedor.sur')).not.toBeInTheDocument()
  })

  /** Tarea 2.13: `idTenant` no se presenta como identidad del dueño en ninguna celda. */
  it('no presenta idTenant como identidad del dueño en ninguna celda', async () => {
    montar()
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())

    for (const fila of screen.getAllByRole('row').slice(1)) {
      expect(within(fila).getAllByRole('cell')[COLUMNA_TENANT]).not.toHaveTextContent(/^\d+$/)
    }

    expect(within(screen.getByLabelText('Tenant')).getByRole('option', { name: 'Comercio Sur' })).toHaveValue('2')
  })
})

// stage-20-organizacion-relaciones-y-bajas, tarea 2.17 (bug reportado por el dueño a mitad del
// slice): un actor de plataforma no podía crear un Admin porque el alta no ofrecía tenant y el
// servidor la rechazaba con 400 `tenant_requerido`.

const SELECTOR_DE_ALTA = 'Tenant de la cuenta'

describe('Usuarios (stage-20, tarea 2.17 — selector de tenant en el alta)', () => {
  beforeEach(() => {
    apiGetMock.mockReset()
    apiPostMock.mockReset()
    apiPostMock.mockResolvedValue(undefined)
    usuarioActual = autenticadoFixture()
  })

  async function abrirAlta(usuario: ReturnType<typeof userEvent.setup>) {
    await usuario.click(screen.getByRole('button', { name: 'Nuevo' }))
  }

  async function completarDatosBasicos(usuario: ReturnType<typeof userEvent.setup>) {
    await usuario.type(screen.getByLabelText('Usuario'), 'nuevo.admin')
    await usuario.type(screen.getByLabelText('Mail'), 'nuevo.admin@ways.test')
    await usuario.type(screen.getByLabelText('Contraseña'), 'unaClaveLarga')
  }

  function cuerpoDelAlta() {
    const llamada = apiPostMock.mock.calls.find(([ruta]) => ruta === '/usuarios')
    expect(llamada, 'no se emitió el POST /usuarios').toBeDefined()

    return llamada![1] as Record<string, unknown>
  }

  /**
   * Cláusula bajo prueba: `esNuevo && ofreceTenant` en `FormularioUsuario` — el selector existe
   * para un actor de plataforma, y sus opciones salen de `tenantsAsignables`, que es
   * `opcionesDeTenant(filas)` MENOS el token de plataforma (que no es un id y ningún rol que no
   * sea root puede llevar).
   */
  it('ofrece el selector de tenant a un actor de plataforma, sin la opción de plataforma', async () => {
    const usuario = userEvent.setup()
    montar()
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())
    await abrirAlta(usuario)

    const selector = screen.getByLabelText(SELECTOR_DE_ALTA)
    const etiquetas = within(selector)
      .getAllByRole('option')
      .map((o) => o.textContent)

    expect(etiquetas).toEqual(['Elegí un tenant', 'Almacén Este', 'Comercio Sur'])
    expect(etiquetas).not.toContain(ETIQUETA_OPCION_PLATAFORMA)
  })

  /**
   * Cláusula bajo prueba: la MISMA, por su lado adverso (spec S5, anti-oráculo). Un admin de
   * tenant no enumera tenants: crea dentro del suyo y el servidor se lo impone
   * (`ServicioDeUsuarios.CrearAsync`: `Actor.EsDePlataforma ? datos.IdTenant : Actor.IdTenant`).
   */
  it('un admin de tenant no ve el selector de tenant en el alta', async () => {
    const usuario = userEvent.setup()
    usuarioActual = autenticadoFixture({ id: 4, usuario: 'admin', rolId: ROL.Admin, rol: 'Admin', idTenant: 2 })
    montar([cuentaDeTenant])
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())
    await abrirAlta(usuario)

    expect(screen.getByLabelText('Usuario')).toBeInTheDocument()
    expect(screen.queryByLabelText(SELECTOR_DE_ALTA)).not.toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba: el `onChange` del rol LIMPIA `idTenant` en el estado.
   *
   * A diferencia de M4, acá NO hay un fallback derivado que enmascare la falta de limpieza: el
   * `value` del `<select>` se deriva del MISMO `valor.idTenant` que viaja en el POST, así que un
   * estado sucio se ve en las dos observaciones. Verificado corriendo la mutación (M15): quitar la
   * limpieza deja el `<select>` en `2` estando deshabilitado. El cuerpo del POST se asserta igual
   * porque es lo único que el servidor ve — con rol root y tenant, contesta 403.
   */
  it('el rol Root deshabilita el selector y limpia el tenant ya elegido', async () => {
    const usuario = userEvent.setup()
    montar()
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())
    await abrirAlta(usuario)

    await usuario.selectOptions(screen.getByLabelText(SELECTOR_DE_ALTA), '2')
    expect(screen.getByLabelText(SELECTOR_DE_ALTA)).toHaveValue('2')

    await usuario.selectOptions(screen.getByLabelText('Rol'), String(ROL.Root))

    expect(screen.getByLabelText(SELECTOR_DE_ALTA)).toBeDisabled()
    expect(screen.getByLabelText(SELECTOR_DE_ALTA)).toHaveValue('')

    await completarDatosBasicos(usuario)
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalled())
    expect(cuerpoDelAlta()).toMatchObject({ rolId: ROL.Root, idTenant: null })
  })

  /**
   * Cláusula bajo prueba: `idTenant: datos.idTenant` en el constructor del payload de alta — el
   * campo que faltaba y que producía el 400 `tenant_requerido` que reportó el dueño.
   */
  it('el alta de un rol de tenant manda el idTenant elegido', async () => {
    const usuario = userEvent.setup()
    montar()
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())
    await abrirAlta(usuario)

    await usuario.selectOptions(screen.getByLabelText('Rol'), String(ROL.Admin))
    await usuario.selectOptions(screen.getByLabelText(SELECTOR_DE_ALTA), '3')
    await completarDatosBasicos(usuario)
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalled())
    expect(cuerpoDelAlta()).toMatchObject({
      usuario: 'nuevo.admin',
      mail: 'nuevo.admin@ways.test',
      rolId: ROL.Admin,
      idTenant: 3,
    })
  })

  /**
   * Cláusula bajo prueba: un admin de tenant manda `idTenant: null` — el `FORMULARIO_VACIO` no
   * arrastra tenant y no hay selector que lo llene. El servidor le impone el suyo.
   */
  it('el alta de un admin de tenant manda idTenant null', async () => {
    const usuario = userEvent.setup()
    usuarioActual = autenticadoFixture({ id: 4, usuario: 'admin', rolId: ROL.Admin, rol: 'Admin', idTenant: 2 })
    montar([cuentaDeTenant])
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())
    await abrirAlta(usuario)

    await completarDatosBasicos(usuario)
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalled())
    expect(cuerpoDelAlta()).toMatchObject({ rolId: ROL.Vendedor, idTenant: null })
  })

  /**
   * Cláusula bajo prueba: `disabled={guardando || tenantsCargando || esRolDePlataforma}` —
   * `react-async-state` regla 5. Para un actor de plataforma `tenantsCargando` es
   * `tenantsDePlataformaCargando` — el propio `GET /plataforma/tenants` en vuelo, no la carga de
   * la página de usuarios — así que el `<select>` queda inerte hasta que SU fuente de opciones
   * llega, no la de la tabla.
   */
  it('el selector queda inerte mientras la lista de tenants está cargando', async () => {
    const usuario = userEvent.setup()
    let resolverTenants!: (tenants: TenantListado[]) => void
    const tenantsPendientes = new Promise<TenantListado[]>((resolver) => {
      resolverTenants = resolver
    })

    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/roles') return Promise.resolve(ROLES)
      if (ruta === '/plataforma/tenants') return tenantsPendientes
      if (ruta.startsWith('/usuarios')) {
        return Promise.resolve<PaginaDe<UsuarioListado>>({
          items: [cuentaDeTenant],
          total: 1,
          pagina: 1,
          tamanio: 20,
        })
      }

      return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
    })

    render(<Usuarios />)
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())
    await abrirAlta(usuario)

    expect(screen.getByLabelText(SELECTOR_DE_ALTA)).toBeDisabled()

    await act(async () => {
      resolverTenants([tenantFixture(2, 'Comercio Sur')])
      await tenantsPendientes
    })

    expect(screen.getByLabelText(SELECTOR_DE_ALTA)).toBeEnabled()
  })

  /**
   * Cláusula bajo prueba (gap de tamaño de página, cerrado a continuación de la tarea 2.17): el
   * selector del alta pide el universo COMPLETO de tenants vía `listarTenants()`, no
   * `opcionesDeTenant(filas)`. Antes de este cierre un tenant sin ningún usuario en la página
   * cargada (tamaño 25) era imposible de asignar; acá el tenant fixture "Tenant Fantasma" no
   * tiene NINGUNA fila entre los usuarios montados y aun así debe aparecer, ordenado junto a los
   * demás.
   */
  it('el universo de tenants del selector incluye uno sin usuarios en la página actual', async () => {
    const usuario = userEvent.setup()
    montar(
      [cuentaDeTenant, cuentaDeOtroTenant, cuentaDePlataforma],
      [tenantFixture(2, 'Comercio Sur'), tenantFixture(3, 'Almacén Este'), tenantFixture(99, 'Tenant Fantasma')],
    )
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())
    await abrirAlta(usuario)

    const selector = screen.getByLabelText(SELECTOR_DE_ALTA)
    const etiquetas = within(selector)
      .getAllByRole('option')
      .map((o) => o.textContent)

    expect(etiquetas).toEqual(['Elegí un tenant', 'Almacén Este', 'Comercio Sur', 'Tenant Fantasma'])
    expect(apiGetMock).toHaveBeenCalledWith('/plataforma/tenants')
  })

  /**
   * Cláusula bajo prueba, anti-oráculo de la anterior (spec S5): un admin de tenant JAMÁS pide el
   * universo de tenants, ni siquiera de fondo — `GET /plataforma/tenants` es `SoloPlataforma` y
   * un admin de tenant no debe poder enumerar tenants ajenos. Verificado corriendo la mutación
   * (M19): sacar la guarda `esPlataforma` del efecto de fetch hace que este test falle porque el
   * mock SÍ ve la llamada.
   */
  it('un admin de tenant nunca pide el universo de tenants (listarTenants)', async () => {
    usuarioActual = autenticadoFixture({ id: 4, usuario: 'admin', rolId: ROL.Admin, rol: 'Admin', idTenant: 2 })
    montar([cuentaDeTenant])
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())

    expect(apiGetMock).not.toHaveBeenCalledWith('/plataforma/tenants')
  })
})

// stage-20-organizacion-relaciones-y-bajas, slice 2 — correcciones de la ronda 1 de judgment-day.

describe('Usuarios (slice 2, ronda 1 — universo de tenants, filtro y escrituras)', () => {
  beforeEach(() => {
    apiGetMock.mockReset()
    apiPostMock.mockReset()
    apiPutMock.mockReset()
    apiDeleteMock.mockReset()
    apiPostMock.mockResolvedValue(undefined)
    apiPutMock.mockResolvedValue(undefined)
    apiDeleteMock.mockResolvedValue(undefined)
    usuarioActual = autenticadoFixture()
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  function paginaDe(items: UsuarioListado[]): PaginaDe<UsuarioListado> {
    return { items, total: items.length, pagina: 1, tamanio: 20 }
  }

  function rutasDeUsuarios() {
    return apiGetMock.mock.calls.map(([ruta]) => ruta as string).filter((r) => r.startsWith('/usuarios'))
  }

  /**
   * Cláusula bajo prueba: el `setError(ERROR_TENANTS)` del `.catch` del universo de tenants
   * (`react-async-state` regla 7). Sin él, un actor de plataforma se quedaba con un `<select>`
   * `required` habilitado y VACÍO: la validación HTML se negaba a mandar el formulario sin decir
   * por qué, y las dependencias del efecto (`[esPlataforma]`) no lo reintentaban nunca. El
   * reintento se cuelga de abrir "Nuevo", que es el único momento en que el universo hace falta.
   */
  it('un fallo del universo de tenants se rinde en pantalla y abrir "Nuevo" lo reintenta', async () => {
    const usuario = userEvent.setup()
    let intentos = 0
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/roles') return Promise.resolve(ROLES)
      if (ruta === '/plataforma/tenants') {
        intentos += 1

        return intentos === 1
          ? Promise.reject(new ErrorApi(503, 'no_disponible', 'Se cayó el servicio.'))
          : Promise.resolve([tenantFixture(2, 'Comercio Sur')])
      }
      if (ruta.startsWith('/usuarios')) return Promise.resolve(paginaDe([cuentaDeTenant]))

      return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
    })

    render(<Usuarios />)
    await waitFor(() => expect(screen.getByText(/No se pudo cargar la lista de tenants/)).toBeInTheDocument())
    expect(intentos).toBe(1)

    await usuario.click(screen.getByRole('button', { name: 'Nuevo' }))

    await waitFor(() => expect(intentos).toBe(2))
    await waitFor(() =>
      expect(
        within(screen.getByLabelText(SELECTOR_DE_ALTA)).getByRole('option', { name: 'Comercio Sur' }),
      ).toBeInTheDocument(),
    )
    expect(screen.queryByText(/No se pudo cargar la lista de tenants/)).not.toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba: `disabled={guardando || sinTenantAsignable}` en Guardar. Un control
   * `disabled` queda EXENTO de la validación de restricciones de HTML, así que el `required` del
   * `<select>` de tenant no frena nada mientras el universo carga o falló: el POST saldría con
   * `idTenant: null` y un rol que no es root, y el servidor contestaría 400 `tenant_requerido`.
   */
  it('Guardar queda inerte para un actor de plataforma mientras el universo de tenants no está', async () => {
    const usuario = userEvent.setup()
    let resolverTenants!: (tenants: TenantListado[]) => void
    const pendientes = new Promise<TenantListado[]>((resolver) => {
      resolverTenants = resolver
    })

    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/roles') return Promise.resolve(ROLES)
      if (ruta === '/plataforma/tenants') return pendientes
      if (ruta.startsWith('/usuarios')) return Promise.resolve(paginaDe([cuentaDeTenant]))

      return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
    })

    render(<Usuarios />)
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())
    await usuario.click(screen.getByRole('button', { name: 'Nuevo' }))

    expect(screen.getByRole('button', { name: 'Guardar' })).toBeDisabled()

    await act(async () => {
      resolverTenants([tenantFixture(2, 'Comercio Sur')])
      await pendientes
    })

    expect(screen.getByRole('button', { name: 'Guardar' })).toBeEnabled()
  })

  /** La otra mitad de la misma cláusula: si el universo FALLÓ, Guardar sigue inerte — no hay
   * tenant que mandar y el 400 del servidor sería la única señal. */
  it('Guardar queda inerte cuando el universo de tenants falló', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/roles') return Promise.resolve(ROLES)
      if (ruta === '/plataforma/tenants') {
        return Promise.reject(new ErrorApi(503, 'no_disponible', 'Se cayó el servicio.'))
      }
      if (ruta.startsWith('/usuarios')) return Promise.resolve(paginaDe([cuentaDeTenant]))

      return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
    })

    render(<Usuarios />)
    await waitFor(() => expect(screen.getByText(/No se pudo cargar la lista de tenants/)).toBeInTheDocument())
    await usuario.click(screen.getByRole('button', { name: 'Nuevo' }))

    await waitFor(() => expect(screen.getByRole('button', { name: 'Guardar' })).toBeDisabled())
  })

  /**
   * Cláusula bajo prueba: la marca de estado de `opcionesDeTenantAsignable`, vista desde la
   * pantalla. El servidor no mira el estado del tenant destino al crear, así que un tenant
   * suspendido se sigue ofreciendo — pero un usuario creado adentro no va a poder entrar, y sin la
   * marca eso no se ve en ningún lado.
   *
   * `Kiosco Viejo (baja)` NO es una fila que el listado pueda devolver hoy: un tenant en `Baja` está
   * borrado lógicamente y `GET /plataforma/tenants` lo filtra. Está acá para ejercitar la rama, que
   * se conserva como defensa en profundidad, no para presentarla como algo que el operador ve.
   */
  it('el selector del alta marca a los tenants que no están activos y no los esconde', async () => {
    const usuario = userEvent.setup()
    montar(
      [cuentaDeTenant],
      [
        tenantFixture(2, 'Comercio Sur'),
        { ...tenantFixture(3, 'Almacén Este'), estado: 'Suspendido' },
        { ...tenantFixture(4, 'Kiosco Viejo'), estado: 'Baja' },
      ],
    )
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())
    await usuario.click(screen.getByRole('button', { name: 'Nuevo' }))

    const etiquetas = within(screen.getByLabelText(SELECTOR_DE_ALTA))
      .getAllByRole('option')
      .map((o) => o.textContent)

    expect(etiquetas).toEqual([
      'Elegí un tenant',
      'Almacén Este (suspendido)',
      'Comercio Sur',
      'Kiosco Viejo (baja)',
    ])
  })

  /**
   * Cláusula bajo prueba: el `setFiltroTenant((prev) => seleccionVigente(...))` de `cargar` — la
   * reconciliación ESCRITA en el estado. Derivarla solo al pintar (`tenantVigente`) no alcanza: la
   * selección inválida sigue viva en `filtroTenant` y se reaplica sola en cuanto la opción
   * reaparece. El confundidor es justamente ese fallback derivado, así que la observación
   * discriminante no es "el filtro se ve en Todos mientras la opción no está" sino "las filas
   * vuelven ENTERAS cuando la opción reaparece".
   */
  it('un filtro invalidado por una búsqueda no resucita cuando las filas vuelven', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/roles') return Promise.resolve(ROLES)
      if (ruta === '/plataforma/tenants') return Promise.resolve(tenantsDesdeFilas([cuentaDeTenant]))
      if (ruta.startsWith('/usuarios?')) return Promise.resolve(paginaDe([cuentaDeTenant]))
      if (ruta.startsWith('/usuarios')) {
        return Promise.resolve(paginaDe([cuentaDeTenant, cuentaDeOtroTenant, cuentaDePlataforma]))
      }

      return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
    })

    render(<Usuarios />)
    await waitFor(() => expect(screen.getByText('vendedor.este')).toBeInTheDocument())

    await usuario.selectOptions(screen.getByLabelText('Tenant'), '3')
    expect(usuariosVisibles()).toEqual(['vendedor.este'])

    await usuario.type(screen.getByPlaceholderText('Buscar usuario o mail…'), 'sur')
    await usuario.click(screen.getByRole('button', { name: 'Buscar' }))
    await waitFor(() => expect(usuariosVisibles()).toEqual(['vendedor.sur']))

    await usuario.clear(screen.getByPlaceholderText('Buscar usuario o mail…'))
    await usuario.click(screen.getByRole('button', { name: 'Buscar' }))

    await waitFor(() => expect(screen.getByText('vendedor.este')).toBeInTheDocument())
    expect(screen.getByLabelText('Tenant')).toHaveValue('')
    expect(usuariosVisibles()).toEqual(['vendedor.sur', 'vendedor.este', 'staff'])
  })

  /**
   * Cláusula bajo prueba: `cargar(token, busquedaAplicada, true)` en `refrescarTrasEscribir` — el
   * término REALMENTE aplicado, no el borrador del input. Con `busqueda` el refresco post-baja
   * angostaba la tabla con un texto que el operador tipeó y nunca buscó.
   */
  it('el refresco post-escritura usa el término buscado, no el borrador tipeado', async () => {
    const usuario = userEvent.setup()
    vi.stubGlobal('confirm', () => true)
    montar()
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())

    await usuario.type(screen.getByPlaceholderText('Buscar usuario o mail…'), 'juan')
    await usuario.click(
      within(screen.getByRole('row', { name: /vendedor\.sur/ })).getByRole('button', { name: 'Baja' }),
    )

    await waitFor(() => expect(apiDeleteMock).toHaveBeenCalledWith('/usuarios/50'))
    await waitFor(() => expect(screen.getByText(/dado de baja/)).toBeInTheDocument())

    expect(rutasDeUsuarios().at(-1)).toBe('/usuarios')
    expect(usuariosVisibles()).toEqual(['vendedor.sur', 'vendedor.este', 'staff'])
  })

  /**
   * Cláusula bajo prueba: el `try` propio del POST de contraseña. Compartir el `try` con el PUT
   * hacía que un fallo del cambio de contraseña reportara "No se pudo guardar." aunque el perfil
   * YA estaba commiteado — `react-async-state` regla 6, una escritura commiteada nunca se reporta
   * como fallida.
   */
  it('un fallo del cambio de contraseña no reporta el PUT ya commiteado como fallido', async () => {
    const usuario = userEvent.setup()
    montar([cuentaDeTenant])
    apiPostMock.mockRejectedValue(new ErrorApi(400, 'password_debil', 'La contraseña es muy corta.'))
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())

    const llamadasAlCargar = rutasDeUsuarios().length
    await usuario.click(screen.getByRole('button', { name: 'Editar' }))
    await usuario.type(screen.getByLabelText('Contraseña'), 'corta')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(screen.getByText(/no se pudo cambiar la contraseña/)).toBeInTheDocument())
    expect(apiPutMock).toHaveBeenCalledWith('/usuarios/50', expect.anything())
    expect(screen.getByText(/actualizado/)).toBeInTheDocument()
    expect(screen.queryByText('No se pudo guardar.')).not.toBeInTheDocument()
    expect(rutasDeUsuarios().length).toBeGreaterThan(llamadasAlCargar)
  })
})

// stage-20-organizacion-relaciones-y-bajas, slice 2 — correcciones de la ronda 2 de judgment-day.

describe('Usuarios (slice 2, ronda 2 — slots de aviso separados y reintento del universo)', () => {
  beforeEach(() => {
    apiGetMock.mockReset()
    apiPostMock.mockReset()
    apiPutMock.mockReset()
    apiDeleteMock.mockReset()
    apiPostMock.mockResolvedValue(undefined)
    apiPutMock.mockResolvedValue(undefined)
    apiDeleteMock.mockResolvedValue(undefined)
    usuarioActual = autenticadoFixture()
  })

  function paginaDe(items: UsuarioListado[]): PaginaDe<UsuarioListado> {
    return { items, total: items.length, pagina: 1, tamanio: 20 }
  }

  /**
   * Cláusula bajo prueba (R2-1): el fallo del universo de tenants tiene BANNER PROPIO, derivado de
   * `tenantsDePlataformaFallo`, y NO viaja por el slot compartido `error`.
   *
   * El confundidor es el orden de llegada: si el 503 de `/plataforma/tenants` aterriza DESPUÉS del
   * 200 de `/usuarios`, el `setError('')` del camino feliz ya corrió y el banner sobrevive aunque
   * el fallo esté ruteado por `error`. Por eso las dos promesas se controlan a mano y el orden se
   * invierte a propósito: tenants RECHAZA primero, los usuarios resuelven DESPUÉS. Con el fallo en
   * `error`, ese `setError('')` posterior lo borra y la pantalla no dice nada.
   */
  it('el fallo del universo de tenants sobrevive a una carga de usuarios que resuelve después', async () => {
    let rechazarTenants!: (e: unknown) => void
    let resolverUsuarios!: (p: PaginaDe<UsuarioListado>) => void
    const tenantsPendientes = new Promise<TenantListado[]>((_, rechazar) => {
      rechazarTenants = rechazar
    })
    const usuariosPendientes = new Promise<PaginaDe<UsuarioListado>>((resolver) => {
      resolverUsuarios = resolver
    })

    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/roles') return Promise.resolve(ROLES)
      if (ruta === '/plataforma/tenants') return tenantsPendientes
      if (ruta.startsWith('/usuarios')) return usuariosPendientes

      return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
    })

    render(<Usuarios />)

    await act(async () => {
      rechazarTenants(new ErrorApi(503, 'no_disponible', 'Se cayó el servicio.'))
      await tenantsPendientes.catch(() => undefined)
    })
    expect(screen.getByText(/No se pudo cargar la lista de tenants/)).toBeInTheDocument()

    await act(async () => {
      resolverUsuarios(paginaDe([cuentaDeTenant]))
      await usuariosPendientes
    })

    expect(screen.getByText('vendedor.sur')).toBeInTheDocument()
    expect(screen.getByText(/No se pudo cargar la lista de tenants/)).toBeInTheDocument()
  })

  /**
   * La otra mitad de la misma cláusula: el banner del universo no tapa un fallo REAL del listado de
   * usuarios. Los dos se rinden a la vez, cada uno en su slot.
   */
  it('el fallo del universo y el del listado de usuarios se rinden los dos, no uno sobre el otro', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/roles') return Promise.resolve(ROLES)
      if (ruta === '/plataforma/tenants') {
        return Promise.reject(new ErrorApi(503, 'no_disponible', 'Se cayó el servicio.'))
      }
      if (ruta.startsWith('/usuarios')) {
        return Promise.reject(new ErrorApi(500, 'error', 'Se cayó el listado de usuarios.'))
      }

      return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
    })

    render(<Usuarios />)

    await waitFor(() => expect(screen.getByText('Se cayó el listado de usuarios.')).toBeInTheDocument())
    expect(screen.getByText(/No se pudo cargar la lista de tenants/)).toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba (R2-2): el fallo del POST de contraseña va al slot ROJO y la confirmación
   * del PUT ya commiteado se queda en el verde. Los dos se ven, y la aserción es sobre la VARIANTE
   * del alert, no solo sobre el texto: anunciar un fallo dentro de `alert-success` es exactamente
   * el defecto, y el texto solo no lo distingue.
   */
  it('el fallo de la contraseña va en rojo y la confirmación del perfil se queda en verde', async () => {
    const usuario = userEvent.setup()
    montar([cuentaDeTenant])
    apiPostMock.mockRejectedValue(new ErrorApi(400, 'password_debil', 'La contraseña es muy corta.'))
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())

    await usuario.click(screen.getByRole('button', { name: 'Editar' }))
    await usuario.type(screen.getByLabelText('Contraseña'), 'corta')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(screen.getByText(/no se pudo cambiar la contraseña/)).toBeInTheDocument())

    const fallo = screen.getByText(/no se pudo cambiar la contraseña/)
    expect(fallo).toHaveClass('alert-danger')
    expect(fallo).not.toHaveClass('alert-success')

    const confirmacion = screen.getByText('Usuario "vendedor.sur" actualizado.')
    expect(confirmacion).toHaveClass('alert-success')
    expect(confirmacion).not.toHaveTextContent(/contraseña/)

    // El perfil commiteó: el formulario se cierra igual.
    expect(screen.queryByLabelText('Mail')).not.toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba (R2-4): `!tenantsDePlataformaCargando` en el reintento colgado de "Nuevo".
   * Sin ella, cada click mientras el reintento está EN VUELO dispara otro `GET /plataforma/tenants`.
   */
  it('dos clicks seguidos en "Nuevo" disparan un solo reintento del universo de tenants', async () => {
    const usuario = userEvent.setup()
    let intentos = 0
    let resolverReintento!: (tenants: TenantListado[]) => void
    const reintento = new Promise<TenantListado[]>((resolver) => {
      resolverReintento = resolver
    })

    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/roles') return Promise.resolve(ROLES)
      if (ruta === '/plataforma/tenants') {
        intentos += 1

        return intentos === 1
          ? Promise.reject(new ErrorApi(503, 'no_disponible', 'Se cayó el servicio.'))
          : reintento
      }
      if (ruta.startsWith('/usuarios')) return Promise.resolve(paginaDe([cuentaDeTenant]))

      return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
    })

    render(<Usuarios />)
    await waitFor(() => expect(screen.getByText(/No se pudo cargar la lista de tenants/)).toBeInTheDocument())
    expect(intentos).toBe(1)

    await usuario.click(screen.getByRole('button', { name: 'Nuevo' }))
    await usuario.click(screen.getByRole('button', { name: 'Nuevo' }))

    expect(intentos).toBe(2)

    await act(async () => {
      resolverReintento([tenantFixture(2, 'Comercio Sur')])
      await reintento
    })

    expect(screen.queryByText(/No se pudo cargar la lista de tenants/)).not.toBeInTheDocument()
    expect(intentos).toBe(2)
  })
})
