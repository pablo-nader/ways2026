import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Usuarios } from './Usuarios'
import { ETIQUETA_OPCION_PLATAFORMA, ETIQUETA_PLATAFORMA, ETIQUETA_SIN_DUENIO } from '../api/organizacion'
import { ROL } from '../api/tipos'
import type { PaginaDe, RolListado, UsuarioAutenticado, UsuarioListado } from '../api/tipos'

// stage-20-organizacion-relaciones-y-bajas, slice 2 (tareas 2.12 y 2.13).

const apiGetMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: vi.fn(),
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

const ROLES: RolListado[] = [{ id: ROL.Vendedor, nombre: 'Vendedor', descripcion: null }]

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
const cuentaDePlataforma = usuarioFixture({
  id: 52,
  usuario: 'staff',
  mail: 'staff@ways.test',
  idTenant: null,
  nombreTenant: null,
})

function montar(items: UsuarioListado[] = [cuentaDeTenant, cuentaDeOtroTenant, cuentaDePlataforma]) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/roles') return Promise.resolve(ROLES)
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

  /** Spec S5: un admin de tenant no puede enumerar el nombre de otro tenant — las opciones salen
   * de las filas que ya recibió, y el servidor solo le manda las suyas. */
  it('un dataset de un solo tenant ofrece exactamente una opción de tenant', async () => {
    usuarioActual = autenticadoFixture({ id: 4, usuario: 'admin', rolId: ROL.Admin, rol: 'Admin', idTenant: 2 })
    montar([cuentaDeTenant])
    await waitFor(() => expect(screen.getByText('vendedor.sur')).toBeInTheDocument())

    const etiquetas = within(screen.getByLabelText('Tenant'))
      .getAllByRole('option')
      .map((o) => o.textContent)
    expect(etiquetas).toEqual(['Todos', 'Comercio Sur'])
    expect(screen.queryByText('Almacén Este')).not.toBeInTheDocument()
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
