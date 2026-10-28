import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Usuarios } from './Usuarios'
import { ROL } from '../api/tipos'
import type { PaginaDe, RolListado, TenantListado, UsuarioAutenticado, UsuarioListado } from '../api/tipos'

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
  ErrorApi: class ErrorApiMock extends Error {},
}))

function autenticado(sobrescribir: Partial<UsuarioAutenticado> = {}): UsuarioAutenticado {
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

let usuarioActual: UsuarioAutenticado = autenticado()

vi.mock('../auth/useAuth', () => ({
  useAuth: () => ({ usuario: usuarioActual, cargando: false, iniciarSesion: vi.fn(), cerrarSesion: vi.fn() }),
}))

const ROLES: RolListado[] = [
  { id: ROL.Vendedor, nombre: 'Vendedor', descripcion: null },
  { id: ROL.Admin, nombre: 'Admin', descripcion: null },
  { id: ROL.Root, nombre: 'Root', descripcion: null },
]

const TENANTS: TenantListado[] = [
  {
    id: 2,
    nombre: 'Comercio Sur',
    estado: 'Activo',
    createdAt: '2026-01-01T10:00:00-03:00',
    cantidadEmpresas: 0,
    cantidadPuntosVenta: 0,
    cantidadUsuarios: 0,
  },
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

function paginaDeLaRuta(ruta: string, items: UsuarioListado[], total: number): PaginaDe<UsuarioListado> {
  const parametros = new URLSearchParams(ruta.split('?')[1] ?? '')

  return {
    items,
    total,
    pagina: Number(parametros.get('pagina') ?? 1),
    tamanio: Number(parametros.get('tamanio') ?? 25),
  }
}

type Pendiente = {
  ruta: string
  resolver: (pagina: PaginaDe<UsuarioListado>) => void
  rechazar: (e: Error) => void
}

function esElListado(ruta: string) {
  return ruta.startsWith('/usuarios')
}

/** Las lecturas de referencia de la pantalla (roles y tenants), fijas. */
function referencia(ruta: string) {
  if (ruta === '/roles') return Promise.resolve(ROLES)
  if (ruta === '/plataforma/tenants') return Promise.resolve(TENANTS)

  return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
}

/** Listado que responde en el acto con `total` filas. */
function responder(total = 1, items: UsuarioListado[] = [usuarioFixture()]) {
  apiGetMock.mockImplementation((ruta: string) =>
    esElListado(ruta) ? Promise.resolve(paginaDeLaRuta(ruta, items, total)) : referencia(ruta),
  )
}

/** Listado cuyas respuestas se resuelven a mano: la carga inicial (`/usuarios` pelado) responde en el acto. */
function responderADemanda(inicial: UsuarioListado[]) {
  const pendientes: Pendiente[] = []
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/usuarios') return Promise.resolve(paginaDeLaRuta(ruta, inicial, inicial.length))
    if (esElListado(ruta)) {
      return new Promise<PaginaDe<UsuarioListado>>((resolver, rechazar) => pendientes.push({ ruta, resolver, rechazar }))
    }

    return referencia(ruta)
  })

  return pendientes
}

function rutasDelListado() {
  return apiGetMock.mock.calls.map(([ruta]) => String(ruta)).filter(esElListado)
}

function renderUsuarios() {
  return render(<Usuarios />)
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiPutMock.mockReset()
  apiDeleteMock.mockReset()
  usuarioActual = autenticado()
})

describe('Usuarios — orden en el servidor', () => {
  it('un clic en un encabezado pide el listado ordenado desde la página 1; el tercero vuelve al orden de siempre', async () => {
    const usuario = userEvent.setup()
    responder(60)
    renderUsuarios()
    await screen.findByText('vendedor.sur')
    await usuario.click(screen.getByRole('button', { name: 'Siguiente' }))
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/usuarios?pagina=2'))

    await usuario.click(screen.getByRole('button', { name: 'Mail' }))

    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/usuarios?orden=mail&direccion=ascendente'))
    expect(screen.getByRole('columnheader', { name: 'Mail' })).toHaveAttribute('aria-sort', 'ascending')

    await usuario.click(screen.getByRole('button', { name: 'Mail' }))
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/usuarios?orden=mail&direccion=descendente'))
    expect(screen.getByRole('columnheader', { name: 'Mail' })).toHaveAttribute('aria-sort', 'descending')

    await usuario.click(screen.getByRole('button', { name: 'Mail' }))
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/usuarios'))
    expect(screen.getByRole('columnheader', { name: 'Mail' })).toHaveAttribute('aria-sort', 'none')
  })

  it.each([
    ['ID', 'id'],
    ['Usuario', 'usuario'],
    ['Mail', 'mail'],
    ['Tenant', 'tenant'],
    ['Rol', 'rol'],
    ['Estado', 'estado'],
    ['Última conexión', 'ultimaConexion'],
  ])('para el actor de plataforma el encabezado %s ordena por la clave %s del servidor', async (titulo, clave) => {
    const usuario = userEvent.setup()
    responder()
    renderUsuarios()
    await screen.findByText('vendedor.sur')

    await usuario.click(screen.getByRole('button', { name: titulo }))

    await waitFor(() => expect(rutasDelListado().at(-1)).toBe(`/usuarios?orden=${clave}&direccion=ascendente`))
  })

  it('Acciones no ordena: no es una columna de datos', async () => {
    responder()
    renderUsuarios()
    await screen.findByText('vendedor.sur')

    expect(screen.queryByRole('button', { name: 'Acciones' })).toBeNull()
  })

  it('una consulta que falla deja la tabla sin filas bajo el encabezado que pidió, y los filtros montados', async () => {
    const usuario = userEvent.setup()
    responder()
    renderUsuarios()
    await screen.findByText('vendedor.sur')
    const filtro = screen.getByLabelText('Filtrar por usuario')
    apiGetMock.mockImplementation((ruta: string) =>
      esElListado(ruta) ? Promise.reject(new Error('caído')) : referencia(ruta),
    )

    await usuario.click(screen.getByRole('button', { name: 'Mail' }))

    expect(await screen.findByText('No se pudieron cargar los usuarios.')).toBeInTheDocument()
    expect(screen.queryByText('vendedor.sur')).toBeNull()
    expect(screen.queryByRole('button', { name: 'Siguiente' })).toBeNull()
    expect(screen.getByRole('columnheader', { name: 'Mail' })).toHaveAttribute('aria-sort', 'ascending')
    expect(screen.getByLabelText('Filtrar por usuario')).toBe(filtro)
  })
})

describe('Usuarios — el tenant es solo del actor de plataforma', () => {
  it('un actor de tenant no tiene la columna ni el filtro de tenant, y ninguna consulta suya los pide', async () => {
    const usuario = userEvent.setup()
    usuarioActual = autenticado({ id: 4, usuario: 'admin', rolId: ROL.Admin, rol: 'Admin', idTenant: 2 })
    responder()
    renderUsuarios()
    await screen.findByText('vendedor.sur')

    expect(screen.queryByRole('columnheader', { name: 'Tenant' })).toBeNull()
    expect(screen.queryByLabelText('Filtrar por tenant')).toBeNull()

    for (const titulo of ['ID', 'Usuario', 'Mail', 'Rol', 'Estado', 'Última conexión']) {
      await usuario.click(screen.getByRole('button', { name: titulo }))
      await waitFor(() => expect(rutasDelListado().at(-1)).toContain('orden='))
    }
    await usuario.type(screen.getByLabelText('Filtrar por usuario'), 'ana')
    await waitFor(() => expect(rutasDelListado().at(-1)).toContain('usuario=ana'))

    expect(rutasDelListado().some((r) => r.includes('tenant'))).toBe(false)
    expect(apiGetMock.mock.calls.some(([ruta]) => ruta === '/plataforma/tenants')).toBe(false)
  })

  it('el actor de plataforma filtra por tenant con las opciones del universo y la de plataforma', async () => {
    const usuario = userEvent.setup()
    responder()
    renderUsuarios()
    await screen.findByText('vendedor.sur')
    const filtro = screen.getByLabelText('Filtrar por tenant')
    await within(filtro).findByRole('option', { name: 'Comercio Sur' })

    await usuario.selectOptions(filtro, 'Comercio Sur')
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/usuarios?tenant=2'))

    await usuario.selectOptions(filtro, 'Plataforma (sin tenant)')
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/usuarios?tenant=sin-tenant'))
  })
})

describe('Usuarios — filtros por columna en el servidor', () => {
  it('un texto espera la demora y se pide una sola vez por ráfaga', async () => {
    const usuario = userEvent.setup()
    responder()
    renderUsuarios()
    await screen.findByText('vendedor.sur')
    const antes = rutasDelListado().length

    await usuario.type(screen.getByLabelText('Filtrar por usuario'), 'ana')

    expect(rutasDelListado()).toHaveLength(antes)
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/usuarios?usuario=ana'))
    expect(rutasDelListado()).toHaveLength(antes + 1)
  })

  it.each([
    ['usuario', 'ana', 'usuario=ana'],
    ['mail', '@x', 'mail=%40x'],
    ['ID', '12', 'id=12'],
  ])('el filtro de %s manda su parámetro', async (columna, texto, esperado) => {
    const usuario = userEvent.setup()
    responder()
    renderUsuarios()
    await screen.findByText('vendedor.sur')

    await usuario.type(screen.getByLabelText(`Filtrar por ${columna}`), texto)

    await waitFor(() => expect(rutasDelListado().at(-1)).toBe(`/usuarios?${esperado}`))
  })

  it('el filtro de ID solo acepta dígitos y no pasa de nueve', async () => {
    const usuario = userEvent.setup()
    responder()
    renderUsuarios()
    await screen.findByText('vendedor.sur')

    await usuario.type(screen.getByLabelText('Filtrar por ID'), '1a2345678901')

    expect(screen.getByLabelText('Filtrar por ID')).toHaveValue('123456789')
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/usuarios?id=123456789'))
  })

  it('los selectores de rol, estado y conexión se aplican en el acto con el parámetro del servidor', async () => {
    const usuario = userEvent.setup()
    responder()
    renderUsuarios()
    await screen.findByText('vendedor.sur')
    await within(screen.getByLabelText('Filtrar por rol')).findByRole('option', { name: 'Admin' })

    await usuario.selectOptions(screen.getByLabelText('Filtrar por rol'), 'Admin')
    expect(rutasDelListado().at(-1)).toBe(`/usuarios?rolId=${ROL.Admin}`)

    await usuario.selectOptions(screen.getByLabelText('Filtrar por estado'), 'Bloqueado')
    expect(rutasDelListado().at(-1)).toBe(`/usuarios?rolId=${ROL.Admin}&estado=Bloqueado`)

    await usuario.selectOptions(screen.getByLabelText('Filtrar por conexión'), 'Sin conexión')
    expect(rutasDelListado().at(-1)).toBe(`/usuarios?rolId=${ROL.Admin}&estado=Bloqueado&conectado=false`)

    await usuario.selectOptions(screen.getByLabelText('Filtrar por conexión'), 'Con conexión')
    expect(rutasDelListado().at(-1)).toBe(`/usuarios?rolId=${ROL.Admin}&estado=Bloqueado&conectado=true`)
  })

  it('el selector de estado ofrece los tres estados de la cuenta', async () => {
    responder()
    renderUsuarios()
    await screen.findByText('vendedor.sur')

    const opciones = within(screen.getByLabelText('Filtrar por estado'))
      .getAllByRole('option')
      .map((o) => o.textContent)

    expect(opciones).toEqual(['Todos', 'Activo', 'Inactivo', 'Bloqueado'])
  })

  it('cambiar un filtro vuelve a la página 1 y conserva el orden', async () => {
    const usuario = userEvent.setup()
    responder(60)
    renderUsuarios()
    await screen.findByText('vendedor.sur')
    await usuario.click(screen.getByRole('button', { name: 'Mail' }))
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/usuarios?orden=mail&direccion=ascendente'))
    await usuario.click(screen.getByRole('button', { name: 'Siguiente' }))
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/usuarios?orden=mail&direccion=ascendente&pagina=2'))

    await usuario.selectOptions(screen.getByLabelText('Filtrar por estado'), 'Inactivo')

    expect(rutasDelListado().at(-1)).toBe('/usuarios?estado=Inactivo&orden=mail&direccion=ascendente')
  })

  it('el filtro conserva el foco y lo escrito cuando llega el listado', async () => {
    const usuario = userEvent.setup()
    responder()
    renderUsuarios()
    await screen.findByText('vendedor.sur')
    const filtro = screen.getByLabelText('Filtrar por usuario')

    await usuario.type(filtro, 've')
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/usuarios?usuario=ve'))
    await screen.findByText('vendedor.sur')

    expect(screen.getByLabelText('Filtrar por usuario')).toBe(filtro)
    expect(filtro).toHaveFocus()
    expect(filtro).toHaveValue('ve')
  })

  it('el filtro conserva el foco mientras el listado nuevo todavía no llegó', async () => {
    const usuario = userEvent.setup()
    const pendientes = responderADemanda([usuarioFixture()])
    renderUsuarios()
    await screen.findByText('vendedor.sur')
    const filtro = screen.getByLabelText('Filtrar por usuario')

    await usuario.type(filtro, 've')
    await waitFor(() => expect(pendientes).toHaveLength(1))

    expect(screen.getByLabelText('Filtrar por usuario')).toBe(filtro)
    expect(filtro).toHaveFocus()
    expect(screen.getByText('vendedor.sur')).toBeInTheDocument()

    await act(async () => pendientes[0].resolver(paginaDeLaRuta(pendientes[0].ruta, [usuarioFixture()], 1)))
    expect(filtro).toHaveFocus()
  })

  it('Limpiar vacía los filtros, conserva el orden y vuelve a pedir', async () => {
    const usuario = userEvent.setup()
    responder()
    renderUsuarios()
    await screen.findByText('vendedor.sur')
    await usuario.click(screen.getByRole('button', { name: 'Usuario' }))
    await usuario.selectOptions(screen.getByLabelText('Filtrar por estado'), 'Activo')
    await usuario.type(screen.getByPlaceholderText('Buscar usuario o mail…'), 'sur')
    await usuario.click(screen.getByRole('button', { name: 'Buscar' }))
    await waitFor(() =>
      expect(rutasDelListado().at(-1)).toBe('/usuarios?busqueda=sur&estado=Activo&orden=usuario&direccion=ascendente'),
    )

    await usuario.click(screen.getByRole('button', { name: 'Limpiar filtros' }))

    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/usuarios?orden=usuario&direccion=ascendente'))
    expect(screen.getByLabelText('Filtrar por estado')).toHaveValue('')
    expect(screen.getByPlaceholderText('Buscar usuario o mail…')).toHaveValue('')
    expect(screen.getByRole('button', { name: 'Limpiar filtros' })).toBeDisabled()
  })

  it('una respuesta tardía de una consulta anterior no pisa a la vigente', async () => {
    const usuario = userEvent.setup()
    const pendientes = responderADemanda([usuarioFixture()])
    renderUsuarios()
    await screen.findByText('vendedor.sur')

    await usuario.selectOptions(screen.getByLabelText('Filtrar por estado'), 'Activo')
    await usuario.selectOptions(screen.getByLabelText('Filtrar por estado'), 'Inactivo')
    expect(pendientes.map((p) => p.ruta)).toEqual(['/usuarios?estado=Activo', '/usuarios?estado=Inactivo'])

    await act(async () =>
      pendientes[1].resolver(paginaDeLaRuta(pendientes[1].ruta, [usuarioFixture({ id: 9, usuario: 'segundo' })], 1)),
    )
    await act(async () =>
      pendientes[0].resolver(paginaDeLaRuta(pendientes[0].ruta, [usuarioFixture({ id: 8, usuario: 'primero' })], 1)),
    )

    expect(screen.getByText('segundo')).toBeInTheDocument()
    expect(screen.queryByText('primero')).toBeNull()
  })

  it('una respuesta tardía no apaga el estado de carga de la consulta vigente, que sigue en vuelo', async () => {
    const usuario = userEvent.setup()
    const pendientes = responderADemanda([usuarioFixture()])
    renderUsuarios()
    await screen.findByText('vendedor.sur')
    await usuario.selectOptions(screen.getByLabelText('Filtrar por estado'), 'Activo')
    await usuario.selectOptions(screen.getByLabelText('Filtrar por estado'), 'Inactivo')
    const cuerpo = screen.getAllByRole('rowgroup')[1]
    expect(cuerpo).toHaveStyle({ opacity: '0.6' })

    await act(async () =>
      pendientes[0].resolver(paginaDeLaRuta(pendientes[0].ruta, [usuarioFixture({ id: 8, usuario: 'primero' })], 1)),
    )

    expect(cuerpo).toHaveStyle({ opacity: '0.6' })
    expect(screen.queryByText('primero')).toBeNull()

    await act(async () =>
      pendientes[1].resolver(paginaDeLaRuta(pendientes[1].ruta, [usuarioFixture({ id: 9, usuario: 'segundo' })], 1)),
    )

    expect(cuerpo).not.toHaveStyle({ opacity: '0.6' })
    expect(screen.getByText('segundo')).toBeInTheDocument()
  })

  it('una lectura que falla después de otra más nueva no deja su error', async () => {
    const usuario = userEvent.setup()
    const pendientes = responderADemanda([usuarioFixture()])
    renderUsuarios()
    await screen.findByText('vendedor.sur')
    await usuario.selectOptions(screen.getByLabelText('Filtrar por estado'), 'Activo')
    await usuario.selectOptions(screen.getByLabelText('Filtrar por estado'), 'Inactivo')

    await act(async () =>
      pendientes[1].resolver(paginaDeLaRuta(pendientes[1].ruta, [usuarioFixture({ id: 9, usuario: 'segundo' })], 1)),
    )
    await act(async () => pendientes[0].rechazar(new Error('tarde')))

    expect(screen.queryByText('No se pudieron cargar los usuarios.')).toBeNull()
    expect(screen.getByText('segundo')).toBeInTheDocument()
  })
})

describe('Usuarios — paginación en el servidor', () => {
  it('muestra la página, el total y mueve la consulta con Anterior y Siguiente', async () => {
    const usuario = userEvent.setup()
    responder(60)
    renderUsuarios()
    await screen.findByText('vendedor.sur')

    expect(screen.getByText('Página 1 de 3 — 60 usuario(s)')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Anterior' })).toBeDisabled()

    await usuario.click(screen.getByRole('button', { name: 'Siguiente' }))
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/usuarios?pagina=2'))
    expect(await screen.findByText('Página 2 de 3 — 60 usuario(s)')).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Siguiente' }))
    expect(await screen.findByText('Página 3 de 3 — 60 usuario(s)')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeDisabled()

    await usuario.click(screen.getByRole('button', { name: 'Anterior' }))
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/usuarios?pagina=2'))
  })

  it('el tamaño de página viaja y vuelve a la página 1', async () => {
    const usuario = userEvent.setup()
    responder(60)
    renderUsuarios()
    await screen.findByText('vendedor.sur')
    await usuario.click(screen.getByRole('button', { name: 'Siguiente' }))
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/usuarios?pagina=2'))

    await usuario.selectOptions(screen.getByLabelText('Filas por página'), '50')

    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/usuarios?tamanio=50'))
    expect(await screen.findByText('Página 1 de 2 — 60 usuario(s)')).toBeInTheDocument()
  })

  it('con una página fuera de rango pide la última válida en vez de mostrar una página vacía', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/usuarios') {
        return Promise.resolve({ items: [], total: 50, pagina: 3, tamanio: 25 } satisfies PaginaDe<UsuarioListado>)
      }
      if (esElListado(ruta)) return Promise.resolve(paginaDeLaRuta(ruta, [usuarioFixture()], 50))

      return referencia(ruta)
    })

    renderUsuarios()

    expect(await screen.findByText('Página 2 de 2 — 50 usuario(s)')).toBeInTheDocument()
    expect(rutasDelListado()).toEqual(['/usuarios', '/usuarios?pagina=2'])
    expect(screen.queryByText('Página 3 de 2 — 50 usuario(s)')).toBeNull()
  })
})

describe('Usuarios — los controles del padrón durante una escritura', () => {
  async function abrirAltaConPostPendiente(usuario: ReturnType<typeof userEvent.setup>) {
    usuarioActual = autenticado({ id: 4, usuario: 'admin', rolId: ROL.Admin, rol: 'Admin', idTenant: 2 })
    let resolver: (valor?: unknown) => void = () => {}
    apiPostMock.mockReturnValue(new Promise((r) => (resolver = r)))
    responder()
    renderUsuarios()
    await screen.findByText('vendedor.sur')
    await usuario.click(screen.getByRole('button', { name: 'Agregar' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Nuevo usuario' })
    await usuario.type(within(dialogo).getByLabelText('Usuario'), 'nuevo.admin')
    await usuario.type(within(dialogo).getByLabelText('Mail'), 'nuevo.admin@ways.test')
    await usuario.type(within(dialogo).getByLabelText('Contraseña'), 'unaClaveLarga')

    return { dialogo, resolver: () => act(async () => resolver()) }
  }

  it('los filtros, el orden y el pager quedan inertes mientras guarda', async () => {
    const usuario = userEvent.setup()
    const { dialogo, resolver } = await abrirAltaConPostPendiente(usuario)

    await usuario.click(within(dialogo).getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(screen.getByLabelText('Filtrar por usuario')).toBeDisabled())
    expect(screen.getByLabelText('Filtrar por estado')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Buscar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Limpiar filtros' })).toBeDisabled()

    await resolver()
    await waitFor(() => expect(screen.getByLabelText('Filtrar por usuario')).toBeEnabled())
  })

  it('un filtro tipeado antes de guardar espera a que termine la escritura y no invalida su refresco', async () => {
    const usuario = userEvent.setup()
    const { dialogo, resolver } = await abrirAltaConPostPendiente(usuario)
    const antes = rutasDelListado().length

    await usuario.type(screen.getByLabelText('Filtrar por usuario'), 'zz')
    await usuario.click(within(dialogo).getByRole('button', { name: 'Guardar' }))
    await new Promise((r) => setTimeout(r, 450))

    expect(rutasDelListado()).toHaveLength(antes)

    await resolver()

    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/usuarios?usuario=zz'))
    expect(await screen.findByText('Usuario "nuevo.admin" creado.')).toBeInTheDocument()
  })

  it('con la confirmación de baja abierta los filtros y el pager quedan inertes, y un filtro tipeado antes espera', async () => {
    const usuario = userEvent.setup()
    usuarioActual = autenticado({ id: 4, usuario: 'admin', rolId: ROL.Admin, rol: 'Admin', idTenant: 2 })
    responder(60)
    renderUsuarios()
    await screen.findByText('vendedor.sur')
    const antes = rutasDelListado().length

    await usuario.type(screen.getByLabelText('Filtrar por usuario'), 'zz')
    await usuario.click(screen.getByRole('button', { name: 'Eliminar' }))
    await screen.findByRole('alertdialog')
    await new Promise((r) => setTimeout(r, 450))

    expect(screen.getByLabelText('Filtrar por usuario')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeDisabled()
    expect(rutasDelListado()).toHaveLength(antes)

    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/usuarios?usuario=zz'))
  })
})

describe('Usuarios — una escritura fallida con una lectura en vuelo', () => {
  async function conLecturaEnVuelo(usuario: ReturnType<typeof userEvent.setup>, filas: UsuarioListado[]) {
    usuarioActual = autenticado({ id: 4, usuario: 'admin', rolId: ROL.Admin, rol: 'Admin', idTenant: 2 })
    const pendientes = responderADemanda(filas)
    renderUsuarios()
    await screen.findByText(filas[0].usuario)
    await usuario.click(screen.getByRole('button', { name: 'Mail' }))
    const cuerpo = screen.getAllByRole('rowgroup')[1]
    expect(cuerpo).toHaveStyle({ opacity: '0.6' })

    return { pendientes, cuerpo }
  }

  it('una baja rechazada repone la lectura que su token invalidó', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockRejectedValue(new Error('rechazada'))
    const { pendientes, cuerpo } = await conLecturaEnVuelo(usuario, [usuarioFixture()])

    await usuario.click(screen.getByRole('button', { name: 'Eliminar' }))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await waitFor(() => expect(pendientes).toHaveLength(2))
    await act(async () => pendientes[1].resolver(paginaDeLaRuta(pendientes[1].ruta, [usuarioFixture()], 1)))

    expect(cuerpo).not.toHaveStyle({ opacity: '0.6' })
  })

  it('una acción rechazada (Desbloquear) repone la lectura que su token invalidó', async () => {
    const usuario = userEvent.setup()
    apiPostMock.mockRejectedValue(new Error('rechazada'))
    const bloqueado = usuarioFixture({ estado: 'Bloqueado' })
    const { pendientes, cuerpo } = await conLecturaEnVuelo(usuario, [bloqueado])

    await usuario.click(screen.getByRole('button', { name: 'Desbloquear' }))

    await waitFor(() => expect(pendientes).toHaveLength(2))
    await act(async () => pendientes[1].resolver(paginaDeLaRuta(pendientes[1].ruta, [bloqueado], 1)))

    expect(cuerpo).not.toHaveStyle({ opacity: '0.6' })
  })
})

describe('Usuarios — el aviso del cambio de contraseña fallido', () => {
  it('se apaga cuando cambia la consulta (un filtro, no solo Buscar)', async () => {
    const usuario = userEvent.setup()
    usuarioActual = autenticado({ id: 4, usuario: 'admin', rolId: ROL.Admin, rol: 'Admin', idTenant: 2 })
    apiPutMock.mockResolvedValue(undefined)
    apiPostMock.mockRejectedValue(new Error('clave débil'))
    responder()
    renderUsuarios()
    await screen.findByText('vendedor.sur')

    await usuario.click(screen.getByRole('button', { name: 'Editar' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Editando usuario 50' })
    await usuario.type(within(dialogo).getByLabelText('Contraseña'), 'unaClaveLarga')
    await usuario.click(within(dialogo).getByRole('button', { name: 'Guardar' }))
    expect(await screen.findByText(/no se pudo cambiar la contraseña/)).toBeInTheDocument()

    await usuario.selectOptions(screen.getByLabelText('Filtrar por estado'), 'Activo')

    await waitFor(() => expect(screen.queryByText(/no se pudo cambiar la contraseña/)).toBeNull())
  })
})
