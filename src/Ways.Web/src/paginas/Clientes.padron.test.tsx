import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Clientes } from './Clientes'
import type { ClienteListado, PaginaDe } from '../api/tipos'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown])),
    put: vi.fn(),
    delete: vi.fn(),
  },
  ErrorApi: class ErrorApiMock extends Error {},
}))

function clienteFixture(sobrescribir: Partial<ClienteListado> = {}): ClienteListado {
  return {
    id: 7,
    numero: 7,
    nombre: 'Luis',
    apellido: 'Pérez',
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
    creditoIlimitado: false,
    saldo: 0,
    activo: true,
    idEmpresa: null,
    esConsumidorFinal: false,
    ...sobrescribir,
  }
}

function paginaDeLaRuta(ruta: string, items: ClienteListado[], total: number): PaginaDe<ClienteListado> {
  const parametros = new URLSearchParams(ruta.split('?')[1] ?? '')

  return {
    items,
    total,
    pagina: Number(parametros.get('pagina') ?? 1),
    tamanio: Number(parametros.get('tamanio') ?? 25),
  }
}

type Pendiente = { ruta: string; resolver: (pagina: PaginaDe<ClienteListado>) => void; rechazar: (e: Error) => void }

/** Listado que responde en el acto con `total` filas de a una; el resto de las lecturas de la pantalla, fijas. */
function responder(total = 1, items: ClienteListado[] = [clienteFixture()]) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta.startsWith('/clientes')) return Promise.resolve(paginaDeLaRuta(ruta, items, total))
    if (ruta === '/catalogos-fiscales/condiciones-fiscales') {
      return Promise.resolve([{ id: 1, codigo: 'CF', nombre: 'Consumidor final', codigoAfip: 5, activo: true }])
    }
    if (ruta === '/listas-precio') return Promise.resolve([{ id: 1, nombre: 'Minorista', esDefault: true }])

    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

/** Listado cuyas respuestas se resuelven a mano, por pedido, para ordenar las llegadas. */
function responderADemanda(inicial: ClienteListado[]) {
  const pendientes: Pendiente[] = []
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/clientes') return Promise.resolve(paginaDeLaRuta(ruta, inicial, inicial.length))
    if (ruta.startsWith('/clientes')) {
      return new Promise<PaginaDe<ClienteListado>>((resolver, rechazar) => pendientes.push({ ruta, resolver, rechazar }))
    }
    if (ruta === '/catalogos-fiscales/condiciones-fiscales') {
      return Promise.resolve([{ id: 1, codigo: 'CF', nombre: 'Consumidor final', codigoAfip: 5, activo: true }])
    }
    if (ruta === '/listas-precio') return Promise.resolve([{ id: 1, nombre: 'Minorista', esDefault: true }])

    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })

  return pendientes
}

function rutasDeClientes() {
  return apiGetMock.mock.calls.map(([ruta]) => String(ruta)).filter((r) => r.startsWith('/clientes'))
}

function renderClientes() {
  return render(<Clientes />, { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
})

describe('Clientes — orden en el servidor', () => {
  it('un clic en un encabezado pide el listado ordenado desde la página 1; el tercero vuelve al orden de siempre', async () => {
    const usuario = userEvent.setup()
    responder(60)
    renderClientes()
    await screen.findByText('Luis Pérez')
    await usuario.click(screen.getByRole('button', { name: 'Siguiente' }))
    await waitFor(() => expect(rutasDeClientes().at(-1)).toBe('/clientes?pagina=2'))

    await usuario.click(screen.getByRole('button', { name: 'Nombre' }))

    await waitFor(() => expect(rutasDeClientes().at(-1)).toBe('/clientes?orden=nombre&direccion=ascendente'))
    expect(screen.getByRole('columnheader', { name: 'Nombre' })).toHaveAttribute('aria-sort', 'ascending')

    await usuario.click(screen.getByRole('button', { name: 'Nombre' }))
    await waitFor(() => expect(rutasDeClientes().at(-1)).toBe('/clientes?orden=nombre&direccion=descendente'))
    expect(screen.getByRole('columnheader', { name: 'Nombre' })).toHaveAttribute('aria-sort', 'descending')

    await usuario.click(screen.getByRole('button', { name: 'Nombre' }))
    await waitFor(() => expect(rutasDeClientes().at(-1)).toBe('/clientes'))
    expect(screen.getByRole('columnheader', { name: 'Nombre' })).toHaveAttribute('aria-sort', 'none')
  })

  it.each([
    ['N°', 'numero'],
    ['Nombre', 'nombre'],
    ['Documento', 'documento'],
    ['Teléfono', 'telefono'],
    ['Email', 'email'],
    ['Estado', 'estado'],
  ])('el encabezado %s ordena por la clave %s del servidor', async (titulo, clave) => {
    const usuario = userEvent.setup()
    responder()
    renderClientes()
    await screen.findByText('Luis Pérez')

    await usuario.click(screen.getByRole('button', { name: titulo }))

    await waitFor(() => expect(rutasDeClientes().at(-1)).toBe(`/clientes?orden=${clave}&direccion=ascendente`))
  })

  /** El servidor ordena Estado por su etiqueta (`OrdenDeClientes`: Activo antes que Inactivo en ascendente): si la pantalla
   * cambiara los textos, el orden que dice mostrar dejaría de ser el que ve. */
  it('las etiquetas de estado son Activo e Inactivo, en el orden alfabético que el servidor usa', async () => {
    responder(2, [clienteFixture(), clienteFixture({ id: 8, activo: false })])
    renderClientes()
    await screen.findAllByText('Luis Pérez')

    const etiquetas = within(screen.getByLabelText('Filtrar por estado'))
      .getAllByRole('option')
      .map((o) => o.textContent)
      .filter((t) => t !== 'Todos')

    expect(etiquetas).toEqual(['Activo', 'Inactivo'])
    expect(etiquetas).toEqual([...etiquetas].sort(new Intl.Collator('es').compare))
    expect(screen.getAllByText(/^(Activo|Inactivo)$/, { selector: '.badge' }).map((b) => b.textContent)).toEqual([
      'Activo',
      'Inactivo',
    ])
  })

  it('el filtro de número no pasa de nueve dígitos y no manda un valor que el servidor rechazaría', async () => {
    const usuario = userEvent.setup()
    responder()
    renderClientes()
    await screen.findByText('Luis Pérez')

    await usuario.type(screen.getByLabelText('Filtrar por N°'), '1234567890')

    await waitFor(() => expect(rutasDeClientes().at(-1)).toBe('/clientes?numero=123456789'))
    expect(screen.getByLabelText('Filtrar por N°')).toHaveValue('123456789')
  })

  it('Acciones no ordena: no es una columna de datos', async () => {
    responder()
    renderClientes()
    await screen.findByText('Luis Pérez')

    expect(screen.queryByRole('button', { name: 'Acciones' })).toBeNull()
  })

  it('una consulta que falla deja la tabla sin filas bajo el encabezado que pidió, y los filtros montados', async () => {
    const usuario = userEvent.setup()
    responder()
    renderClientes()
    await screen.findByText('Luis Pérez')
    const filtro = screen.getByLabelText('Filtrar por nombre')
    apiGetMock.mockImplementation((ruta: string) =>
      ruta.startsWith('/clientes') ? Promise.reject(new Error('caído')) : Promise.resolve([]),
    )

    await usuario.click(screen.getByRole('button', { name: 'Nombre' }))

    expect(await screen.findByText('No se pudieron cargar los clientes.')).toBeInTheDocument()
    expect(screen.queryByText('Luis Pérez')).toBeNull()
    expect(screen.queryByRole('button', { name: 'Siguiente' })).toBeNull()
    expect(screen.getByRole('columnheader', { name: 'Nombre' })).toHaveAttribute('aria-sort', 'ascending')
    expect(screen.getByLabelText('Filtrar por nombre')).toBe(filtro)
  })
})

describe('Clientes — filtros por columna en el servidor', () => {
  it('un texto espera la demora y se pide una sola vez por ráfaga', async () => {
    const usuario = userEvent.setup()
    responder()
    renderClientes()
    await screen.findByText('Luis Pérez')
    const antes = rutasDeClientes().length

    await usuario.type(screen.getByLabelText('Filtrar por nombre'), 'ana')

    expect(rutasDeClientes()).toHaveLength(antes)
    await waitFor(() => expect(rutasDeClientes().at(-1)).toBe('/clientes?nombre=ana'))
    expect(rutasDeClientes()).toHaveLength(antes + 1)
  })

  it('un selector se aplica en el acto, sin pasar por la demora', async () => {
    const usuario = userEvent.setup()
    responder()
    renderClientes()
    await screen.findByText('Luis Pérez')

    await usuario.selectOptions(screen.getByLabelText('Filtrar por estado'), 'Activo')

    expect(rutasDeClientes().at(-1)).toBe('/clientes?activo=true')
  })

  it('el filtro de número solo acepta dígitos', async () => {
    const usuario = userEvent.setup()
    responder()
    renderClientes()
    await screen.findByText('Luis Pérez')

    await usuario.type(screen.getByLabelText('Filtrar por N°'), '1a2')

    expect(screen.getByLabelText('Filtrar por N°')).toHaveValue('12')
    await waitFor(() => expect(rutasDeClientes().at(-1)).toBe('/clientes?numero=12'))
  })

  it.each([
    ['nombre', 'ana', 'nombre=ana'],
    ['número de documento', '30', 'documento=30'],
    ['teléfono', '11', 'telefono=11'],
    ['email', '@x', 'email=%40x'],
  ])('el filtro de %s manda su parámetro', async (columna, texto, esperado) => {
    const usuario = userEvent.setup()
    responder()
    renderClientes()
    await screen.findByText('Luis Pérez')

    await usuario.type(screen.getByLabelText(`Filtrar por ${columna}`), texto)

    await waitFor(() => expect(rutasDeClientes().at(-1)).toBe(`/clientes?${esperado}`))
  })

  it('cambiar un filtro vuelve a la página 1 y conserva el orden', async () => {
    const usuario = userEvent.setup()
    responder(60)
    renderClientes()
    await screen.findByText('Luis Pérez')
    await usuario.click(screen.getByRole('button', { name: 'Email' }))
    await waitFor(() => expect(rutasDeClientes().at(-1)).toBe('/clientes?orden=email&direccion=ascendente'))
    await usuario.click(screen.getByRole('button', { name: 'Siguiente' }))
    await waitFor(() => expect(rutasDeClientes().at(-1)).toBe('/clientes?orden=email&direccion=ascendente&pagina=2'))

    await usuario.selectOptions(screen.getByLabelText('Filtrar por estado'), 'Inactivo')

    expect(rutasDeClientes().at(-1)).toBe('/clientes?activo=false&orden=email&direccion=ascendente')
  })

  it('el filtro conserva el foco y lo escrito cuando llega el listado', async () => {
    const usuario = userEvent.setup()
    responder()
    renderClientes()
    await screen.findByText('Luis Pérez')
    const filtro = screen.getByLabelText('Filtrar por nombre')

    await usuario.type(filtro, 'lu')
    await waitFor(() => expect(rutasDeClientes().at(-1)).toBe('/clientes?nombre=lu'))
    await screen.findByText('Luis Pérez')

    expect(screen.getByLabelText('Filtrar por nombre')).toBe(filtro)
    expect(filtro).toHaveFocus()
    expect(filtro).toHaveValue('lu')
  })

  it('Limpiar vacía los filtros, conserva el orden y vuelve a pedir', async () => {
    const usuario = userEvent.setup()
    responder()
    renderClientes()
    await screen.findByText('Luis Pérez')
    await usuario.click(screen.getByRole('button', { name: 'Nombre' }))
    await usuario.selectOptions(screen.getByLabelText('Filtrar por estado'), 'Activo')
    await usuario.type(screen.getByPlaceholderText(/Buscar por nombre/), 'luis')
    await usuario.click(screen.getByRole('button', { name: 'Buscar' }))
    await waitFor(() =>
      expect(rutasDeClientes().at(-1)).toBe('/clientes?busqueda=luis&activo=true&orden=nombre&direccion=ascendente'),
    )

    await usuario.click(screen.getByRole('button', { name: 'Limpiar filtros' }))

    await waitFor(() => expect(rutasDeClientes().at(-1)).toBe('/clientes?orden=nombre&direccion=ascendente'))
    expect(screen.getByLabelText('Filtrar por estado')).toHaveValue('')
    expect(screen.getByPlaceholderText(/Buscar por nombre/)).toHaveValue('')
    expect(screen.getByRole('button', { name: 'Limpiar filtros' })).toBeDisabled()
  })

  it('una respuesta tardía de una consulta anterior no pisa a la vigente', async () => {
    const usuario = userEvent.setup()
    const pendientes = responderADemanda([clienteFixture()])
    renderClientes()
    await screen.findByText('Luis Pérez')

    await usuario.selectOptions(screen.getByLabelText('Filtrar por estado'), 'Activo')
    await usuario.selectOptions(screen.getByLabelText('Filtrar por estado'), 'Inactivo')
    expect(pendientes.map((p) => p.ruta)).toEqual(['/clientes?activo=true', '/clientes?activo=false'])

    await act(async () =>
      pendientes[1].resolver(paginaDeLaRuta(pendientes[1].ruta, [clienteFixture({ id: 9, nombre: 'Segundo' })], 1)),
    )
    await act(async () =>
      pendientes[0].resolver(paginaDeLaRuta(pendientes[0].ruta, [clienteFixture({ id: 8, nombre: 'Primero' })], 1)),
    )

    expect(screen.getByText('Segundo Pérez')).toBeInTheDocument()
    expect(screen.queryByText('Primero Pérez')).toBeNull()
  })

  it('una respuesta tardía no apaga el estado de carga de la consulta vigente, que sigue en vuelo', async () => {
    const usuario = userEvent.setup()
    const pendientes = responderADemanda([clienteFixture()])
    renderClientes()
    await screen.findByText('Luis Pérez')
    await usuario.selectOptions(screen.getByLabelText('Filtrar por estado'), 'Activo')
    await usuario.selectOptions(screen.getByLabelText('Filtrar por estado'), 'Inactivo')
    const cuerpo = screen.getAllByRole('rowgroup')[1]
    expect(cuerpo).toHaveStyle({ opacity: '0.6' })

    await act(async () =>
      pendientes[0].resolver(paginaDeLaRuta(pendientes[0].ruta, [clienteFixture({ id: 8, nombre: 'Primero' })], 1)),
    )

    expect(cuerpo).toHaveStyle({ opacity: '0.6' })
    expect(screen.queryByText('Primero Pérez')).toBeNull()

    await act(async () =>
      pendientes[1].resolver(paginaDeLaRuta(pendientes[1].ruta, [clienteFixture({ id: 9, nombre: 'Segundo' })], 1)),
    )

    expect(cuerpo).not.toHaveStyle({ opacity: '0.6' })
    expect(screen.getByText('Segundo Pérez')).toBeInTheDocument()
  })

  it('una lectura que falla después de otra más nueva no deja su error', async () => {
    const usuario = userEvent.setup()
    const pendientes = responderADemanda([clienteFixture()])
    renderClientes()
    await screen.findByText('Luis Pérez')
    await usuario.selectOptions(screen.getByLabelText('Filtrar por estado'), 'Activo')
    await usuario.selectOptions(screen.getByLabelText('Filtrar por estado'), 'Inactivo')

    await act(async () =>
      pendientes[1].resolver(paginaDeLaRuta(pendientes[1].ruta, [clienteFixture({ id: 9, nombre: 'Segundo' })], 1)),
    )
    await act(async () => pendientes[0].rechazar(new Error('tarde')))

    expect(screen.queryByText('No se pudieron cargar los clientes.')).toBeNull()
    expect(screen.getByText('Segundo Pérez')).toBeInTheDocument()
  })
})

describe('Clientes — paginación en el servidor', () => {
  it('muestra la página, el total y mueve la consulta con Anterior y Siguiente', async () => {
    const usuario = userEvent.setup()
    responder(60)
    renderClientes()
    await screen.findByText('Luis Pérez')

    expect(screen.getByText('Página 1 de 3 — 60 clientes')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Anterior' })).toBeDisabled()

    await usuario.click(screen.getByRole('button', { name: 'Siguiente' }))
    await waitFor(() => expect(rutasDeClientes().at(-1)).toBe('/clientes?pagina=2'))
    expect(await screen.findByText('Página 2 de 3 — 60 clientes')).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Siguiente' }))
    expect(await screen.findByText('Página 3 de 3 — 60 clientes')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeDisabled()

    await usuario.click(screen.getByRole('button', { name: 'Anterior' }))
    await waitFor(() => expect(rutasDeClientes().at(-1)).toBe('/clientes?pagina=2'))
  })

  it('el tamaño de página viaja y vuelve a la página 1', async () => {
    const usuario = userEvent.setup()
    responder(60)
    renderClientes()
    await screen.findByText('Luis Pérez')
    await usuario.click(screen.getByRole('button', { name: 'Siguiente' }))
    await waitFor(() => expect(rutasDeClientes().at(-1)).toBe('/clientes?pagina=2'))

    await usuario.selectOptions(screen.getByLabelText('Filas por página'), '50')

    await waitFor(() => expect(rutasDeClientes().at(-1)).toBe('/clientes?tamanio=50'))
    expect(await screen.findByText('Página 1 de 2 — 60 clientes')).toBeInTheDocument()
  })

  it('con una página fuera de rango pide la última válida en vez de mostrar una página vacía', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/clientes') {
        return Promise.resolve({ items: [], total: 50, pagina: 3, tamanio: 25 } satisfies PaginaDe<ClienteListado>)
      }
      if (ruta.startsWith('/clientes')) return Promise.resolve(paginaDeLaRuta(ruta, [clienteFixture()], 50))

      return Promise.resolve([])
    })

    renderClientes()

    expect(await screen.findByText('Página 2 de 2 — 50 clientes')).toBeInTheDocument()
    expect(rutasDeClientes()).toEqual(['/clientes', '/clientes?pagina=2'])
    expect(screen.queryByText('Página 3 de 2 — 50 clientes')).toBeNull()
  })
})

describe('Clientes — los controles del padrón durante una escritura', () => {
  async function abrirAltaConPostPendiente(usuario: ReturnType<typeof userEvent.setup>) {
    let resolver: (valor: ClienteListado) => void = () => {}
    apiPostMock.mockReturnValue(new Promise<ClienteListado>((r) => (resolver = r)))
    responder()
    renderClientes()
    await screen.findByText('Luis Pérez')
    await usuario.click(screen.getByRole('button', { name: 'Agregar' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Nuevo cliente' })
    await usuario.type(within(dialogo).getByLabelText('Nombre'), 'Ana')
    const condicion = within(dialogo).getByLabelText('Condición fiscal')
    await within(condicion).findByRole('option', { name: 'Consumidor final' })
    await usuario.selectOptions(condicion, '1')
    await waitFor(() => expect(within(dialogo).getByLabelText('Lista de precios')).toHaveValue('1'))

    return { dialogo, resolver: (cliente: ClienteListado) => act(async () => resolver(cliente)) }
  }

  it('los filtros, el orden y el pager quedan inertes mientras guarda', async () => {
    const usuario = userEvent.setup()
    const { dialogo, resolver } = await abrirAltaConPostPendiente(usuario)

    await usuario.click(within(dialogo).getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(screen.getByLabelText('Filtrar por nombre')).toBeDisabled())
    expect(screen.getByLabelText('Filtrar por estado')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Buscar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Limpiar filtros' })).toBeDisabled()

    await resolver(clienteFixture({ id: 8 }))
    await waitFor(() => expect(screen.getByLabelText('Filtrar por nombre')).toBeEnabled())
  })

  it('un filtro tipeado antes de guardar espera a que termine la escritura y no invalida su refresco', async () => {
    const usuario = userEvent.setup()
    const { dialogo, resolver } = await abrirAltaConPostPendiente(usuario)
    const antes = rutasDeClientes().length

    await usuario.type(screen.getByLabelText('Filtrar por nombre'), 'zz')
    await usuario.click(within(dialogo).getByRole('button', { name: 'Guardar' }))
    await new Promise((r) => setTimeout(r, 450))

    expect(rutasDeClientes()).toHaveLength(antes)

    await resolver(clienteFixture({ id: 8 }))

    await waitFor(() => expect(rutasDeClientes().at(-1)).toBe('/clientes?nombre=zz'))
    expect(await screen.findByText('Cliente "Ana" creado.')).toBeInTheDocument()
  })
})
