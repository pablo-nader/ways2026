import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
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
    montar()
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())

    await usuario.type(screen.getByPlaceholderText('Buscar usuario o mail…'), 'juan')
    await usuario.click(
      within(screen.getByRole('row', { name: /vendedor\.sur/ })).getByRole('button', { name: 'Baja' }),
    )
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

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

// stage-20-organizacion-relaciones-y-bajas, slice 5 (tareas 5.6, 5.7 y 5.8), más las entradas
// arrastradas de la slice 2 (puntos 2 y 4). El patrón de baja es el MISMO que el de `Tenants.tsx`
// y se replica en la misma PR (`react-async-state` regla 10).

function botonDeBajaDe(nombreDeUsuario: string) {
  return within(screen.getByRole('row', { name: new RegExp(nombreDeUsuario) })).getByRole('button', {
    name: 'Baja',
  })
}

describe('Usuarios (stage-20, slice 5 — baja lógica tras la puerta de confirmación)', () => {
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

  /**
   * Cláusula bajo prueba: el `{baja && <ConfirmacionDeBaja …>}` que reemplazó al `confirm()`
   * nativo. El diálogo del navegador no se podía dejar inerte mientras el DELETE estaba en vuelo,
   * y era la única de las cuatro pantallas que no compartía la puerta (regla 10).
   */
  it('el botón de baja no llama a la API hasta que se confirma', async () => {
    const usuario = userEvent.setup()
    montar()
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())

    await usuario.click(botonDeBajaDe('vendedor\\.sur'))
    expect(apiDeleteMock).not.toHaveBeenCalled()
    expect(screen.getByRole('alertdialog', { name: 'Confirmar baja' })).toHaveTextContent(
      '¿Dar de baja al usuario "vendedor.sur"?',
    )

    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))
    await waitFor(() => expect(apiDeleteMock).toHaveBeenCalledWith('/usuarios/50'))
    await waitFor(() => expect(screen.getByText('Usuario "vendedor.sur" dado de baja.')).toBeInTheDocument())
  })

  it('cancelar cierra la puerta y no llama nunca a la API', async () => {
    const usuario = userEvent.setup()
    montar()
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())

    await usuario.click(botonDeBajaDe('vendedor\\.sur'))
    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(apiDeleteMock).not.toHaveBeenCalled()
  })

  /** Cláusula bajo prueba: la ventana inerte completa — ver el test gemelo de `Tenants.test.tsx`. */
  it('durante el DELETE y su refresco no queda ninguna acción alcanzable', async () => {
    const usuario = userEvent.setup()
    let resolverDelete!: () => void
    let resolverRefresco!: (pagina: PaginaDe<UsuarioListado>) => void

    let cargas = 0
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/roles') return Promise.resolve(ROLES)
      if (ruta === '/plataforma/tenants') return Promise.resolve(tenantsDesdeFilas([cuentaDeTenant]))
      if (!ruta.startsWith('/usuarios')) return Promise.reject(new Error(`ruta inesperada: ${ruta}`))

      cargas += 1
      if (cargas === 1) {
        return Promise.resolve<PaginaDe<UsuarioListado>>({
          items: [cuentaDeTenant],
          total: 1,
          pagina: 1,
          tamanio: 20,
        })
      }

      return new Promise<PaginaDe<UsuarioListado>>((resolver) => {
        resolverRefresco = resolver
      })
    })
    apiDeleteMock.mockImplementation(
      () =>
        new Promise<void>((resolver) => {
          resolverDelete = resolver
        }),
    )

    render(<Usuarios />)
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())

    await usuario.click(botonDeBajaDe('vendedor\\.sur'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    expect(screen.getByRole('button', { name: 'Dando de baja…' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Nuevo' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Buscar' })).toBeDisabled()
    expect(screen.getByPlaceholderText('Buscar usuario o mail…')).toBeDisabled()
    for (const boton of [
      ...screen.getAllByRole('button', { name: 'Editar' }),
      ...screen.getAllByRole('button', { name: 'Baja' }),
    ]) {
      expect(boton).toBeDisabled()
    }

    await act(async () => {
      resolverDelete()
      await Promise.resolve()
    })

    await waitFor(() => expect(screen.getByText('Usuario "vendedor.sur" dado de baja.')).toBeInTheDocument())

    await act(async () => {
      resolverRefresco({ items: [], total: 0, pagina: 1, tamanio: 20 })
      await Promise.resolve()
    })

    await waitFor(() => expect(screen.getByRole('button', { name: 'Nuevo' })).toBeEnabled())
    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
  })

  /** Cláusula bajo prueba: `ocupadoRef`, la guarda de re-entrancia del mismo tick (regla 9). */
  it('un segundo click sobre la confirmación en vuelo se descarta', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockImplementation(() => new Promise<void>(() => {}))
    montar([cuentaDeTenant])
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())

    await usuario.click(botonDeBajaDe('vendedor\\.sur'))
    const confirmar = screen.getByRole('button', { name: 'Confirmar baja' })
    await act(async () => {
      confirmar.click()
      confirmar.click()
      await Promise.resolve()
    })

    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
  })

  it('un refresco fallido después de la baja no la reporta como fallida', async () => {
    const usuario = userEvent.setup()
    let cargas = 0
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/roles') return Promise.resolve(ROLES)
      if (ruta === '/plataforma/tenants') return Promise.resolve(tenantsDesdeFilas([cuentaDeTenant]))
      if (!ruta.startsWith('/usuarios')) return Promise.reject(new Error(`ruta inesperada: ${ruta}`))

      cargas += 1
      if (cargas === 1) {
        return Promise.resolve<PaginaDe<UsuarioListado>>({
          items: [cuentaDeTenant],
          total: 1,
          pagina: 1,
          tamanio: 20,
        })
      }

      return Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó.'))
    })

    render(<Usuarios />)
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())

    await usuario.click(botonDeBajaDe('vendedor\\.sur'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await waitFor(() =>
      expect(
        screen.getByText(
          'Usuario "vendedor.sur" dado de baja. Se eliminó, pero no se pudo actualizar la vista. Recargá la pantalla.',
        ),
      ).toBeInTheDocument(),
    )
  })

  /**
   * Cláusula bajo prueba: `copiaDeFalloDeBaja(e, 'el usuario')` en vez del `e.message` pelado que
   * usaba el viejo `accion()`. `usuario_en_uso` es el cuarto código del set y tiene su propia guía.
   */
  it('un 409 usuario_en_uso rinde su guía propia sin tragarse el mensaje del servidor', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockRejectedValue(
      new ErrorApi(409, 'usuario_en_uso', 'No se puede dar de baja el usuario porque hay 7 ventas a su nombre.'),
    )
    montar([cuentaDeTenant])
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())

    await usuario.click(botonDeBajaDe('vendedor\\.sur'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await waitFor(() => expect(screen.getByText(/7 ventas a su nombre/)).toBeInTheDocument())
    expect(
      screen.getByText(/Reasigná o dá de baja esas operaciones antes de eliminar la cuenta\./),
    ).toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba: que los rechazos PREEXISTENTES de `PoliticaDeRoles` sigan llegando con
   * su mensaje. No están en el mapa de códigos, así que caen por el fallback — que rinde el
   * mensaje del servidor y no inventa ninguna guía.
   */
  it('un rechazo de PoliticaDeRoles se rinde con su propio mensaje, sin guía inventada', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockRejectedValue(
      new ErrorApi(403, 'operacion_no_permitida', 'No podés dar de baja tu propia cuenta.'),
    )
    montar([cuentaDeTenant])
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())

    await usuario.click(botonDeBajaDe('vendedor\\.sur'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await waitFor(() =>
      expect(screen.getByText('No podés dar de baja tu propia cuenta.')).toBeInTheDocument(),
    )
    expect(screen.queryByText(/Reasigná o dá de baja esas operaciones/)).not.toBeInTheDocument()
  })

  /** Anti-oráculo (BO-R12) en la capa de UI: un 404 nunca insinúa uso ni alcance. */
  it('un 404 rinde la copia neutra de inexistencia, nunca una pista de uso', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'No existe el usuario 50.'))
    montar([cuentaDeTenant])
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())

    await usuario.click(botonDeBajaDe('vendedor\\.sur'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await waitFor(() =>
      expect(
        screen.getByText('No se pudo dar de baja el usuario. Ya no existe o no está a tu alcance. Actualizá el listado.'),
      ).toBeInTheDocument(),
    )
    expect(screen.queryByText(/en uso|a su nombre/)).not.toBeInTheDocument()
  })
})

// Entradas arrastradas de la slice 2 que esta slice cierra (puntos 2 y 4 del bloque BINDING).

describe('Usuarios (slice 5 — cierre de las entradas arrastradas de la slice 2)', () => {
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

  /**
   * Cláusula bajo prueba (entrada 2): el slot PROPIO de `ERROR_ALTA_SIN_TENANTS`. En el slot
   * compartido `error` lo borraba el `setError('')` de la siguiente carga con el universo todavía
   * caído — el rechazo desaparecía y el alta seguía siendo imposible sin decir por qué.
   *
   * El disparo va por `fireEvent.submit` y NO por un click, y eso se dice en vez de disfrazarse:
   * el botón "Guardar" está `disabled` en esta ventana (`sinTenantAsignable`), así que por click
   * la rama es inalcanzable — es el superviviente M35 que la slice 2 registró como tal. El evento
   * de submit es exactamente lo que produciría la carrera del mismo tick contra la que existe el
   * re-chequeo, y lo que este test prueba es el SLOT: que una carga posterior de la tabla no se
   * lo lleve puesto.
   */
  it('el rechazo del alta sin universo de tenants sobrevive a una carga posterior', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/roles') return Promise.resolve(ROLES)
      if (ruta === '/plataforma/tenants') return Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó.'))
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

    await usuario.click(screen.getByRole('button', { name: 'Nuevo' }))
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeDisabled()

    await act(async () => {
      fireEvent.submit(screen.getByLabelText('Usuario').closest('form') as HTMLFormElement)
      await Promise.resolve()
    })

    await waitFor(() =>
      expect(screen.getByText('No se puede crear el usuario: todavía falta la lista de tenants.')).toBeInTheDocument(),
    )
    expect(apiPostMock).not.toHaveBeenCalled()

    // Una carga posterior de la TABLA no puede llevárselo puesto: son dos slots con dueños
    // distintos, y `cargar` termina con un `setError('')` que en el slot compartido lo borraba.
    await usuario.click(screen.getByRole('button', { name: 'Buscar' }))

    await waitFor(() =>
      expect(screen.getByText('No se puede crear el usuario: todavía falta la lista de tenants.')).toBeInTheDocument(),
    )
  })

  /**
   * Cláusula bajo prueba (entrada 4): los `setErrorPassword('')` de Cancelar y de Buscar. El fallo
   * del cambio de contraseña quedaba prendido sobre una pantalla que el operador ya cerró o
   * reemplazó por otra búsqueda.
   */
  it('el fallo del cambio de contraseña se apaga al cancelar el formulario', async () => {
    const usuario = userEvent.setup()
    montar([cuentaDeTenant])
    apiPostMock.mockRejectedValue(new ErrorApi(400, 'password_debil', 'La contraseña es muy corta.'))
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())

    await usuario.click(screen.getByRole('button', { name: 'Editar' }))
    await usuario.type(screen.getByLabelText('Contraseña'), 'corta')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))
    await waitFor(() => expect(screen.getByText(/no se pudo cambiar la contraseña/)).toBeInTheDocument())

    await usuario.click(screen.getByRole('button', { name: 'Editar' }))
    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByText(/no se pudo cambiar la contraseña/)).not.toBeInTheDocument()
  })

  it('el fallo del cambio de contraseña se apaga al buscar de nuevo', async () => {
    const usuario = userEvent.setup()
    montar([cuentaDeTenant])
    apiPostMock.mockRejectedValue(new ErrorApi(400, 'password_debil', 'La contraseña es muy corta.'))
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())

    await usuario.click(screen.getByRole('button', { name: 'Editar' }))
    await usuario.type(screen.getByLabelText('Contraseña'), 'corta')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))
    await waitFor(() => expect(screen.getByText(/no se pudo cambiar la contraseña/)).toBeInTheDocument())

    await usuario.click(screen.getByRole('button', { name: 'Buscar' }))

    await waitFor(() => expect(screen.queryByText(/no se pudo cambiar la contraseña/)).not.toBeInTheDocument())
  })

  /**
   * Cláusula bajo prueba (entrada 4): el `esPlataforma &&` que gatea el banner del universo de
   * tenants, en paridad con TODOS sus elementos hermanos. Para un admin de tenant ese `GET` ni se
   * dispara, así que la bandera no puede prender: el gate es paridad estructural, y el test
   * asserta la otra mitad —que para un actor de plataforma sí se rinde— para que borrar el gate no
   * sea gratis.
   */
  it('el banner del universo de tenants es de plataforma, como todo lo que depende de él', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/roles') return Promise.resolve(ROLES)
      if (ruta === '/plataforma/tenants') return Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó.'))
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
    await waitFor(() => expect(screen.getByText(/No se pudo cargar la lista de tenants/)).toBeInTheDocument())

    cleanup()
    usuarioActual = autenticadoFixture({ id: 4, usuario: 'admin', rolId: ROL.Admin, rol: 'Admin', idTenant: 2 })
    render(<Usuarios />)
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())

    expect(screen.queryByText(/No se pudo cargar la lista de tenants/)).not.toBeInTheDocument()
  })
})

// stage-20-organizacion-relaciones-y-bajas, slice 5, judgment-day ronda 1 (C1, C2 y C4).

/** Un `keydown` despachado DIRECTAMENTE sobre el buscador, que con la puerta abierta está
 * deshabilitado. Ningún operador puede llegar acá —un input inerte no recibe teclas del navegador—
 * y por eso mismo es la palanca POR DEBAJO del confound (`mutation-proof-tests` regla 3): es la
 * única forma de acuñar una generación en la ventana en que la puerta está abierta y ver qué hace
 * el token de la escritura. Con el bloqueo revertido, esta MISMA tecla es alcanzable a mano. */
function enterEnElBuscador() {
  fireEvent.keyDown(screen.getByPlaceholderText('Buscar usuario o mail…'), { key: 'Enter' })
}

describe('Usuarios (slice 5, ronda 1 — la puerta es modal y el token se acuña al confirmar)', () => {
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

  /**
   * Cláusula bajo prueba: `bloqueado = ocupado || baja !== null` en TODOS los `disabled` de la
   * pantalla. Con `ocupado` solo, la puerta abierta dejaba vivos Guardar, Buscar, el buscador,
   * Nuevo, Editar y Baja: cualquiera de ellos acuñaba una generación nueva y el DELETE que salía
   * después ya no aplicaba nada (`react-async-state` regla 9 — bloquear la ventana, no reconciliar
   * tokens).
   */
  it('con la puerta abierta no queda ninguna otra acción alcanzable', async () => {
    const usuario = userEvent.setup()
    montar([cuentaDeTenant])
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())

    await usuario.click(screen.getByRole('button', { name: 'Editar' }))
    await usuario.click(botonDeBajaDe('vendedor\\.sur'))

    const puerta = screen.getByRole('alertdialog', { name: 'Confirmar baja' })
    expect(screen.getByPlaceholderText('Buscar usuario o mail…')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Buscar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Nuevo' })).toBeDisabled()
    expect(screen.getByLabelText('Tenant')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Editar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Baja' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeDisabled()
    // El botón del formulario dice "Guardar", no "Guardando…": no hay ninguna escritura en vuelo,
    // solo una puerta abierta.
    expect(screen.queryByRole('button', { name: 'Guardando…' })).not.toBeInTheDocument()
    expect(within(puerta).getByRole('button', { name: 'Confirmar baja' })).toBeEnabled()
    expect(within(puerta).getByRole('button', { name: 'Cancelar' })).toBeEnabled()

    await usuario.click(within(puerta).getByRole('button', { name: 'Confirmar baja' }))

    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument())
    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
    expect(apiDeleteMock).toHaveBeenCalledWith('/usuarios/50')
  })

  /**
   * Cláusula bajo prueba: `const token = ++generacion.current` como PRIMERA sentencia síncrona de
   * `confirmarBaja`, y la ausencia del chequeo de generación posterior a la red. Con el token
   * acuñado al ABRIR la puerta, una búsqueda en el medio lo dejaba viejo: el DELETE salía igual, el
   * 204 volvía y el `if (generacion.current !== token) return` se lo tragaba — la fila seguía
   * listada, la puerta seguía abierta y cada click repetía un DELETE silencioso.
   */
  it('una generación acuñada entre abrir y confirmar no se traga el 204', async () => {
    const usuario = userEvent.setup()
    montar([cuentaDeTenant])
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())

    await usuario.click(botonDeBajaDe('vendedor\\.sur'))

    await act(async () => {
      enterEnElBuscador()
    })
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())

    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await waitFor(() =>
      expect(screen.getByText('Usuario "vendedor.sur" dado de baja.')).toBeInTheDocument(),
    )
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
  })

  /**
   * Cláusula bajo prueba: que `cancelarBaja` NO haga `++generacion.current`. Cancelar no supersede
   * nada —solo cierra la puerta—, y el incremento descartaba una lectura en vuelo dejando sin
   * ejecutar el `finally` gateado de `cargar`: la pantalla se quedaba en "Cargando…" para siempre,
   * sin tabla, sin error y sin nada que apretar.
   */
  it('cancelar no clava la pantalla cuando hay una búsqueda en vuelo', async () => {
    const usuario = userEvent.setup()
    let resolverBusqueda!: (pagina: PaginaDe<UsuarioListado>) => void
    let cargas = 0
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/roles') return Promise.resolve(ROLES)
      if (ruta === '/plataforma/tenants') return Promise.resolve(tenantsDesdeFilas([cuentaDeTenant]))
      if (!ruta.startsWith('/usuarios')) return Promise.reject(new Error(`ruta inesperada: ${ruta}`))

      cargas += 1
      if (cargas === 1) {
        return Promise.resolve<PaginaDe<UsuarioListado>>({
          items: [cuentaDeTenant],
          total: 1,
          pagina: 1,
          tamanio: 20,
        })
      }

      return new Promise<PaginaDe<UsuarioListado>>((resolver) => {
        resolverBusqueda = resolver
      })
    })

    render(<Usuarios />)
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())

    await usuario.click(botonDeBajaDe('vendedor\\.sur'))
    await act(async () => {
      enterEnElBuscador()
    })
    expect(screen.getByText('Cargando…')).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()

    await act(async () => {
      resolverBusqueda({ items: [cuentaDeTenant], total: 1, pagina: 1, tamanio: 20 })
      await Promise.resolve()
    })

    await waitFor(() => expect(screen.queryByText('Cargando…')).not.toBeInTheDocument())
    expect(screen.getByText('vendedor.sur')).toBeInTheDocument()
    expect(apiDeleteMock).not.toHaveBeenCalled()
  })

  /**
   * Cláusula bajo prueba: el `setError('')` de `cancelarBaja`. Tras un 409 la puerta queda abierta
   * con el motivo en rojo al lado; cancelarla sin limpiarlo dejaba el banner huérfano, hablando de
   * una baja que ya nadie está por hacer.
   */
  it('cancelar después de un rechazo se lleva el motivo con la puerta', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockRejectedValue(
      new ErrorApi(409, 'usuario_en_uso', 'No se puede dar de baja el usuario porque hay 7 ventas a su nombre.'),
    )
    montar([cuentaDeTenant])
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())

    await usuario.click(botonDeBajaDe('vendedor\\.sur'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))
    await waitFor(() => expect(screen.getByText(/7 ventas a su nombre/)).toBeInTheDocument())

    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.queryByText(/7 ventas a su nombre/)).not.toBeInTheDocument()
  })
})
