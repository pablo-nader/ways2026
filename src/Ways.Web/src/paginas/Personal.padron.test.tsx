import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Personal } from './Personal'
import { ROL } from '../api/tipos'
import type { PaginaDe, PersonalListado } from '../api/tipos'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()
const apiDeleteMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown])),
    put: vi.fn(),
    delete: (...args: unknown[]) => apiDeleteMock(...(args as [string])),
  },
  ErrorApi: class ErrorApiMock extends Error {},
}))

vi.mock('../auth/useAuth', () => ({
  useAuth: () => ({
    usuario: { id: 1, usuario: 'yo', mail: 'yo@ways.test', rolId: ROL.Admin, rol: 'Admin', ultimaConexion: null, idTenant: 1 },
    cargando: false,
    iniciarSesion: vi.fn(),
    cerrarSesion: vi.fn(),
  }),
}))

function personalFixture(sobrescribir: Partial<PersonalListado> = {}): PersonalListado {
  return {
    id: 1,
    nombre: 'Ana Gómez',
    documento: null,
    telefono: null,
    observaciones: null,
    valorHora: 3500,
    saldo: 12000,
    idEmpresa: null,
    idUsuario: null,
    nombreUsuario: null,
    idCliente: null,
    nombreCliente: null,
    horarioHabitual: null,
    ...sobrescribir,
  }
}

function paginaDeLaRuta(ruta: string, items: PersonalListado[], total: number): PaginaDe<PersonalListado> {
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
  resolver: (pagina: PaginaDe<PersonalListado>) => void
  rechazar: (e: Error) => void
}

const REFERENCIAS_VACIAS: PaginaDe<never> = { items: [], total: 0, pagina: 1, tamanio: 200 }

/** El listado del padrón es `/personal…`; las referencias de vínculo van a `/usuarios` y `/clientes`. */
function esElListado(ruta: string) {
  return ruta.startsWith('/personal')
}

/** Listado que responde en el acto con `total` filas; las referencias, vacías. */
function responder(total = 1, items: PersonalListado[] = [personalFixture()]) {
  apiGetMock.mockImplementation((ruta: string) =>
    esElListado(ruta) ? Promise.resolve(paginaDeLaRuta(ruta, items, total)) : Promise.resolve(REFERENCIAS_VACIAS),
  )
}

/** Listado cuyas respuestas se resuelven a mano, por pedido: el primero (la carga inicial) responde en el acto. */
function responderADemanda(inicial: PersonalListado[]) {
  const pendientes: Pendiente[] = []
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/personal') return Promise.resolve(paginaDeLaRuta(ruta, inicial, inicial.length))
    if (esElListado(ruta)) {
      return new Promise<PaginaDe<PersonalListado>>((resolver, rechazar) => pendientes.push({ ruta, resolver, rechazar }))
    }

    return Promise.resolve(REFERENCIAS_VACIAS)
  })

  return pendientes
}

function rutasDelListado() {
  return apiGetMock.mock.calls.map(([ruta]) => String(ruta)).filter(esElListado)
}

function renderPersonal() {
  return render(<Personal />, { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiDeleteMock.mockReset()
})

describe('Personal — orden en el servidor', () => {
  it('un clic en un encabezado pide el listado ordenado desde la página 1; el tercero vuelve al orden de siempre', async () => {
    const usuario = userEvent.setup()
    responder(60)
    renderPersonal()
    await screen.findByText('Ana Gómez')
    await usuario.click(screen.getByRole('button', { name: 'Siguiente' }))
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/personal?pagina=2'))

    await usuario.click(screen.getByRole('button', { name: 'Nombre' }))

    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/personal?orden=nombre&direccion=ascendente'))
    expect(screen.getByRole('columnheader', { name: 'Nombre' })).toHaveAttribute('aria-sort', 'ascending')

    await usuario.click(screen.getByRole('button', { name: 'Nombre' }))
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/personal?orden=nombre&direccion=descendente'))
    expect(screen.getByRole('columnheader', { name: 'Nombre' })).toHaveAttribute('aria-sort', 'descending')

    await usuario.click(screen.getByRole('button', { name: 'Nombre' }))
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/personal'))
    expect(screen.getByRole('columnheader', { name: 'Nombre' })).toHaveAttribute('aria-sort', 'none')
  })

  it.each([
    ['Nombre', 'nombre'],
    ['Valor hora', 'valorHora'],
    ['Saldo', 'saldo'],
    ['Usuario', 'usuario'],
    ['Cliente', 'cliente'],
  ])('el encabezado %s ordena por la clave %s del servidor', async (titulo, clave) => {
    const usuario = userEvent.setup()
    responder()
    renderPersonal()
    await screen.findByText('Ana Gómez')

    await usuario.click(screen.getByRole('button', { name: titulo }))

    await waitFor(() => expect(rutasDelListado().at(-1)).toBe(`/personal?orden=${clave}&direccion=ascendente`))
  })

  it('Acciones no ordena: no es una columna de datos', async () => {
    responder()
    renderPersonal()
    await screen.findByText('Ana Gómez')

    expect(screen.queryByRole('button', { name: 'Acciones' })).toBeNull()
  })

  it('una consulta que falla deja la tabla sin filas bajo el encabezado que pidió, y los filtros montados', async () => {
    const usuario = userEvent.setup()
    responder()
    renderPersonal()
    await screen.findByText('Ana Gómez')
    const filtro = screen.getByLabelText('Filtrar por nombre')
    apiGetMock.mockImplementation((ruta: string) =>
      esElListado(ruta) ? Promise.reject(new Error('caído')) : Promise.resolve(REFERENCIAS_VACIAS),
    )

    await usuario.click(screen.getByRole('button', { name: 'Nombre' }))

    expect(await screen.findByText('No se pudo cargar el personal.')).toBeInTheDocument()
    expect(screen.queryByText('Ana Gómez')).toBeNull()
    expect(screen.queryByRole('button', { name: 'Siguiente' })).toBeNull()
    expect(screen.getByRole('columnheader', { name: 'Nombre' })).toHaveAttribute('aria-sort', 'ascending')
    expect(screen.getByLabelText('Filtrar por nombre')).toBe(filtro)
  })
})

describe('Personal — filtros por columna en el servidor', () => {
  it('un texto espera la demora y se pide una sola vez por ráfaga', async () => {
    const usuario = userEvent.setup()
    responder()
    renderPersonal()
    await screen.findByText('Ana Gómez')
    const antes = rutasDelListado().length

    await usuario.type(screen.getByLabelText('Filtrar por nombre'), 'ana')

    expect(rutasDelListado()).toHaveLength(antes)
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/personal?nombre=ana'))
    expect(rutasDelListado()).toHaveLength(antes + 1)
  })

  it.each([
    ['nombre', 'ana', 'nombre=ana'],
    ['usuario', 'lu', 'usuario=lu'],
    ['cliente', 'paz', 'cliente=paz'],
  ])('el filtro de %s manda su parámetro', async (columna, texto, esperado) => {
    const usuario = userEvent.setup()
    responder()
    renderPersonal()
    await screen.findByText('Ana Gómez')

    await usuario.type(screen.getByLabelText(`Filtrar por ${columna}`), texto)

    await waitFor(() => expect(rutasDelListado().at(-1)).toBe(`/personal?${esperado}`))
  })

  it('los rangos de valor hora y de saldo mandan desde y hasta, y vaciar un campo lo saca de la consulta', async () => {
    const usuario = userEvent.setup()
    responder()
    renderPersonal()
    await screen.findByText('Ana Gómez')

    await usuario.type(screen.getByLabelText('Valor hora desde'), '100')
    await usuario.type(screen.getByLabelText('Valor hora hasta'), '250,5')
    await usuario.type(screen.getByLabelText('Saldo desde'), '10')
    await usuario.type(screen.getByLabelText('Saldo hasta'), '99')

    await waitFor(() =>
      expect(rutasDelListado().at(-1)).toBe(
        '/personal?valorHoraDesde=100&valorHoraHasta=250.5&saldoDesde=10&saldoHasta=99',
      ),
    )

    await usuario.clear(screen.getByLabelText('Valor hora desde'))
    await usuario.clear(screen.getByLabelText('Saldo hasta'))

    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/personal?valorHoraHasta=250.5&saldoDesde=10'))
  })

  it('el saldo desde admite un negativo (adelantos) y lo manda con su signo; el valor hora no', async () => {
    const usuario = userEvent.setup()
    responder()
    renderPersonal()
    await screen.findByText('Ana Gómez')

    await usuario.type(screen.getByLabelText('Saldo desde'), '-500')
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/personal?saldoDesde=-500'))

    await usuario.type(screen.getByLabelText('Valor hora desde'), '-7')
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/personal?valorHoraDesde=7&saldoDesde=-500'))
  })

  it('cambiar un filtro vuelve a la página 1 y conserva el orden', async () => {
    const usuario = userEvent.setup()
    responder(60)
    renderPersonal()
    await screen.findByText('Ana Gómez')
    await usuario.click(screen.getByRole('button', { name: 'Saldo' }))
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/personal?orden=saldo&direccion=ascendente'))
    await usuario.click(screen.getByRole('button', { name: 'Siguiente' }))
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/personal?orden=saldo&direccion=ascendente&pagina=2'))

    await usuario.type(screen.getByLabelText('Filtrar por usuario'), 'lu')

    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/personal?usuario=lu&orden=saldo&direccion=ascendente'))
  })

  it('el filtro conserva el foco y lo escrito cuando llega el listado', async () => {
    const usuario = userEvent.setup()
    responder()
    renderPersonal()
    await screen.findByText('Ana Gómez')
    const filtro = screen.getByLabelText('Filtrar por nombre')

    await usuario.type(filtro, 'an')
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/personal?nombre=an'))
    await screen.findByText('Ana Gómez')

    expect(screen.getByLabelText('Filtrar por nombre')).toBe(filtro)
    expect(filtro).toHaveFocus()
    expect(filtro).toHaveValue('an')
  })

  it('el filtro conserva el foco mientras el listado nuevo todavía no llegó', async () => {
    const usuario = userEvent.setup()
    const pendientes = responderADemanda([personalFixture()])
    renderPersonal()
    await screen.findByText('Ana Gómez')
    const filtro = screen.getByLabelText('Filtrar por nombre')

    await usuario.type(filtro, 'an')
    await waitFor(() => expect(pendientes).toHaveLength(1))

    expect(screen.getByLabelText('Filtrar por nombre')).toBe(filtro)
    expect(filtro).toHaveFocus()
    expect(screen.getByText('Ana Gómez')).toBeInTheDocument()

    await act(async () => pendientes[0].resolver(paginaDeLaRuta(pendientes[0].ruta, [personalFixture()], 1)))
    expect(filtro).toHaveFocus()
  })

  it('Limpiar vacía los filtros, conserva el orden y vuelve a pedir', async () => {
    const usuario = userEvent.setup()
    responder()
    renderPersonal()
    await screen.findByText('Ana Gómez')
    await usuario.click(screen.getByRole('button', { name: 'Nombre' }))
    await usuario.type(screen.getByLabelText('Filtrar por cliente'), 'paz')
    await usuario.type(screen.getByLabelText('Buscar personal'), 'ana')
    await usuario.click(screen.getByRole('button', { name: 'Buscar' }))
    await waitFor(() =>
      expect(rutasDelListado().at(-1)).toBe('/personal?busqueda=ana&cliente=paz&orden=nombre&direccion=ascendente'),
    )

    await usuario.click(screen.getByRole('button', { name: 'Limpiar filtros' }))

    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/personal?orden=nombre&direccion=ascendente'))
    expect(screen.getByLabelText('Filtrar por cliente')).toHaveValue('')
    expect(screen.getByLabelText('Buscar personal')).toHaveValue('')
    expect(screen.getByRole('button', { name: 'Limpiar filtros' })).toBeDisabled()
  })

  it('una respuesta tardía de una consulta anterior no pisa a la vigente', async () => {
    const usuario = userEvent.setup()
    const pendientes = responderADemanda([personalFixture()])
    renderPersonal()
    await screen.findByText('Ana Gómez')

    await usuario.click(screen.getByRole('button', { name: 'Nombre' }))
    await usuario.click(screen.getByRole('button', { name: 'Nombre' }))
    expect(pendientes.map((p) => p.ruta)).toEqual([
      '/personal?orden=nombre&direccion=ascendente',
      '/personal?orden=nombre&direccion=descendente',
    ])

    await act(async () =>
      pendientes[1].resolver(paginaDeLaRuta(pendientes[1].ruta, [personalFixture({ id: 9, nombre: 'Segundo' })], 1)),
    )
    await act(async () =>
      pendientes[0].resolver(paginaDeLaRuta(pendientes[0].ruta, [personalFixture({ id: 8, nombre: 'Primero' })], 1)),
    )

    expect(screen.getByText('Segundo')).toBeInTheDocument()
    expect(screen.queryByText('Primero')).toBeNull()
  })

  it('una respuesta tardía no apaga el estado de carga de la consulta vigente, que sigue en vuelo', async () => {
    const usuario = userEvent.setup()
    const pendientes = responderADemanda([personalFixture()])
    renderPersonal()
    await screen.findByText('Ana Gómez')
    await usuario.click(screen.getByRole('button', { name: 'Nombre' }))
    await usuario.click(screen.getByRole('button', { name: 'Nombre' }))
    const cuerpo = screen.getAllByRole('rowgroup')[1]
    expect(cuerpo).toHaveStyle({ opacity: '0.6' })

    await act(async () =>
      pendientes[0].resolver(paginaDeLaRuta(pendientes[0].ruta, [personalFixture({ id: 8, nombre: 'Primero' })], 1)),
    )

    expect(cuerpo).toHaveStyle({ opacity: '0.6' })
    expect(screen.queryByText('Primero')).toBeNull()

    await act(async () =>
      pendientes[1].resolver(paginaDeLaRuta(pendientes[1].ruta, [personalFixture({ id: 9, nombre: 'Segundo' })], 1)),
    )

    expect(cuerpo).not.toHaveStyle({ opacity: '0.6' })
  })

  it('una lectura que falla después de otra más nueva no deja su error', async () => {
    const usuario = userEvent.setup()
    const pendientes = responderADemanda([personalFixture()])
    renderPersonal()
    await screen.findByText('Ana Gómez')
    await usuario.click(screen.getByRole('button', { name: 'Nombre' }))
    await usuario.click(screen.getByRole('button', { name: 'Nombre' }))

    await act(async () =>
      pendientes[1].resolver(paginaDeLaRuta(pendientes[1].ruta, [personalFixture({ id: 9, nombre: 'Segundo' })], 1)),
    )
    await act(async () => pendientes[0].rechazar(new Error('tarde')))

    expect(screen.queryByText('No se pudo cargar el personal.')).toBeNull()
    expect(screen.getByText('Segundo')).toBeInTheDocument()
  })
})

describe('Personal — paginación en el servidor', () => {
  it('muestra la página, el total y mueve la consulta con Anterior y Siguiente', async () => {
    const usuario = userEvent.setup()
    responder(60)
    renderPersonal()
    await screen.findByText('Ana Gómez')

    expect(screen.getByText('Página 1 de 3 — 60 personas')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Anterior' })).toBeDisabled()

    await usuario.click(screen.getByRole('button', { name: 'Siguiente' }))
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/personal?pagina=2'))
    expect(await screen.findByText('Página 2 de 3 — 60 personas')).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Siguiente' }))
    expect(await screen.findByText('Página 3 de 3 — 60 personas')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeDisabled()

    await usuario.click(screen.getByRole('button', { name: 'Anterior' }))
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/personal?pagina=2'))
  })

  it('ya no avisa "Se muestran N de M": el pager reemplaza al aviso de resultado cortado', async () => {
    responder(250)
    renderPersonal()
    await screen.findByText('Ana Gómez')

    expect(screen.queryByText(/Refiná la búsqueda/)).toBeNull()
    expect(screen.getByText('Página 1 de 10 — 250 personas')).toBeInTheDocument()
  })

  it('el tamaño de página viaja y vuelve a la página 1', async () => {
    const usuario = userEvent.setup()
    responder(60)
    renderPersonal()
    await screen.findByText('Ana Gómez')
    await usuario.click(screen.getByRole('button', { name: 'Siguiente' }))
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/personal?pagina=2'))

    await usuario.selectOptions(screen.getByLabelText('Filas por página'), '50')

    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/personal?tamanio=50'))
    expect(await screen.findByText('Página 1 de 2 — 60 personas')).toBeInTheDocument()
  })

  it('con una página fuera de rango pide la última válida en vez de mostrar una página vacía', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/personal') {
        return Promise.resolve({ items: [], total: 50, pagina: 3, tamanio: 25 } satisfies PaginaDe<PersonalListado>)
      }
      if (esElListado(ruta)) return Promise.resolve(paginaDeLaRuta(ruta, [personalFixture()], 50))

      return Promise.resolve(REFERENCIAS_VACIAS)
    })

    renderPersonal()

    expect(await screen.findByText('Página 2 de 2 — 50 personas')).toBeInTheDocument()
    expect(rutasDelListado()).toEqual(['/personal', '/personal?pagina=2'])
    expect(screen.queryByText('Página 3 de 2 — 50 personas')).toBeNull()
  })
})

describe('Personal — los controles del padrón durante una escritura', () => {
  async function abrirAltaConPostPendiente(usuario: ReturnType<typeof userEvent.setup>) {
    let resolver: (valor: PersonalListado) => void = () => {}
    apiPostMock.mockReturnValue(new Promise<PersonalListado>((r) => (resolver = r)))
    responder()
    renderPersonal()
    await screen.findByText('Ana Gómez')
    await usuario.click(screen.getByRole('button', { name: 'Agregar' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Nuevo personal' })
    await usuario.type(within(dialogo).getByLabelText('Nombre'), 'Carla')
    await usuario.type(within(dialogo).getByLabelText('Valor hora'), '100')

    return { dialogo, resolver: (nuevo: PersonalListado) => act(async () => resolver(nuevo)) }
  }

  it('los filtros, el orden y el pager quedan inertes mientras guarda', async () => {
    const usuario = userEvent.setup()
    const { dialogo, resolver } = await abrirAltaConPostPendiente(usuario)

    await usuario.click(within(dialogo).getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(screen.getByLabelText('Filtrar por nombre')).toBeDisabled())
    expect(screen.getByLabelText('Saldo desde')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Buscar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Limpiar filtros' })).toBeDisabled()

    await resolver(personalFixture({ id: 8 }))
    await waitFor(() => expect(screen.getByLabelText('Filtrar por nombre')).toBeEnabled())
  })

  it('un filtro tipeado antes de guardar espera a que termine la escritura y no invalida su refresco', async () => {
    const usuario = userEvent.setup()
    const { dialogo, resolver } = await abrirAltaConPostPendiente(usuario)
    const antes = rutasDelListado().length

    await usuario.type(screen.getByLabelText('Filtrar por nombre'), 'zz')
    await usuario.click(within(dialogo).getByRole('button', { name: 'Guardar' }))
    await new Promise((r) => setTimeout(r, 450))

    expect(rutasDelListado()).toHaveLength(antes)

    await resolver(personalFixture({ id: 8 }))

    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/personal?nombre=zz'))
    expect(await screen.findByText('Personal "Carla" creado.')).toBeInTheDocument()
  })

  it('con la confirmación de baja abierta los filtros y el pager quedan inertes, y un filtro tipeado antes espera', async () => {
    const usuario = userEvent.setup()
    responder(60)
    renderPersonal()
    await screen.findByText('Ana Gómez')
    const antes = rutasDelListado().length

    await usuario.type(screen.getByLabelText('Filtrar por nombre'), 'zz')
    await usuario.click(screen.getByRole('button', { name: 'Eliminar Ana Gómez' }))
    await screen.findByRole('alertdialog')
    await new Promise((r) => setTimeout(r, 450))

    expect(screen.getByLabelText('Filtrar por nombre')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeDisabled()
    expect(rutasDelListado()).toHaveLength(antes)

    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/personal?nombre=zz'))
  })
})

describe('Personal — una baja fallida con una lectura en vuelo', () => {
  it('repone la lectura que su token invalidó: la tabla no queda atenuada para siempre', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockRejectedValue(new Error('rechazada'))
    const pendientes = responderADemanda([personalFixture()])
    renderPersonal()
    await screen.findByText('Ana Gómez')

    await usuario.click(screen.getByRole('button', { name: 'Saldo' }))
    const cuerpo = screen.getAllByRole('rowgroup')[1]
    expect(cuerpo).toHaveStyle({ opacity: '0.6' })
    await usuario.click(screen.getByRole('button', { name: 'Eliminar Ana Gómez' }))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await waitFor(() => expect(pendientes).toHaveLength(2))
    expect(pendientes[1].ruta).toBe(pendientes[0].ruta)
    await act(async () => pendientes[1].resolver(paginaDeLaRuta(pendientes[1].ruta, [personalFixture()], 1)))

    expect(cuerpo).not.toHaveStyle({ opacity: '0.6' })
    expect(screen.getByText(/No se pudo dar de baja a "Ana Gómez"/)).toBeInTheDocument()
  })
})
