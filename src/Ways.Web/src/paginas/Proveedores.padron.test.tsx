import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Proveedores } from './Proveedores'
import { Toaster } from '../toasts/Toaster'
import type { PaginaDe, ProveedorListado } from '../api/tipos'

const apiGetMock = vi.fn()
const apiDeleteMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: vi.fn(),
    put: vi.fn(),
    delete: (...args: unknown[]) => apiDeleteMock(...(args as [string])),
  },
  ErrorApi: class ErrorApiMock extends Error {},
}))

function proveedorFixture(sobrescribir: Partial<ProveedorListado> = {}): ProveedorListado {
  return {
    id: 1,
    razonSocial: 'Proveedor Uno SA',
    nombreFantasia: null,
    cuit: null,
    idCondicionFiscal: 1,
    domicilio: null,
    telefono: null,
    email: null,
    vendedor: null,
    celularVendedor: null,
    supervisor: null,
    celularSupervisor: null,
    margen: null,
    observaciones: null,
    activo: true,
    idEmpresa: null,
    percibeIibb: false,
    percibeIva: false,
    preciosIncluyenIva: false,
    ...sobrescribir,
  }
}

function paginaDeLaRuta(ruta: string, items: ProveedorListado[], total: number): PaginaDe<ProveedorListado> {
  const parametros = new URLSearchParams(ruta.split('?')[1] ?? '')

  return {
    items,
    total,
    pagina: Number(parametros.get('pagina') ?? 1),
    tamanio: Number(parametros.get('tamanio') ?? 25),
  }
}

type Pendiente = { ruta: string; resolver: (pagina: PaginaDe<ProveedorListado>) => void; rechazar: (e: Error) => void }

function lecturasBase(ruta: string) {
  if (ruta === '/catalogos-fiscales/condiciones-fiscales') return Promise.resolve([])

  return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
}

function responder(total = 1, items: ProveedorListado[] = [proveedorFixture()]) {
  apiGetMock.mockImplementation((ruta: string) =>
    ruta.startsWith('/proveedores') ? Promise.resolve(paginaDeLaRuta(ruta, items, total)) : lecturasBase(ruta),
  )
}

/** Listado cuyas respuestas, salvo la primera, se resuelven a mano por pedido para ordenar las llegadas. */
function responderADemanda(inicial: ProveedorListado[]) {
  const pendientes: Pendiente[] = []
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/proveedores') return Promise.resolve(paginaDeLaRuta(ruta, inicial, inicial.length))
    if (ruta.startsWith('/proveedores')) {
      return new Promise<PaginaDe<ProveedorListado>>((resolver, rechazar) => pendientes.push({ ruta, resolver, rechazar }))
    }

    return lecturasBase(ruta)
  })

  return pendientes
}

function rutasDelListado() {
  return apiGetMock.mock.calls.map(([ruta]) => String(ruta)).filter((r) => r.startsWith('/proveedores'))
}

function renderProveedores() {
  return render(<><Proveedores /><Toaster /></>, { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiDeleteMock.mockReset()
})

describe('Proveedores — orden en el servidor', () => {
  it('un clic en un encabezado pide el listado ordenado desde la página 1; el tercero vuelve al orden de siempre', async () => {
    const usuario = userEvent.setup()
    responder(60)
    renderProveedores()
    await screen.findByText('Proveedor Uno SA')
    await usuario.click(screen.getByRole('button', { name: 'Siguiente' }))
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/proveedores?pagina=2'))

    await usuario.click(screen.getByRole('button', { name: 'Margen' }))

    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/proveedores?orden=margen&direccion=ascendente'))
    expect(screen.getByRole('columnheader', { name: 'Margen' })).toHaveAttribute('aria-sort', 'ascending')

    await usuario.click(screen.getByRole('button', { name: 'Margen' }))
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/proveedores?orden=margen&direccion=descendente'))
    expect(screen.getByRole('columnheader', { name: 'Margen' })).toHaveAttribute('aria-sort', 'descending')

    await usuario.click(screen.getByRole('button', { name: 'Margen' }))
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/proveedores'))
    expect(screen.getByRole('columnheader', { name: 'Margen' })).toHaveAttribute('aria-sort', 'none')
  })

  it.each([
    ['Razón social', 'razonSocial'],
    ['CUIT', 'cuit'],
    ['Teléfono', 'telefono'],
    ['Email', 'email'],
    ['Margen', 'margen'],
    ['Estado', 'estado'],
  ])('el encabezado %s ordena por la clave %s del servidor', async (titulo, clave) => {
    const usuario = userEvent.setup()
    responder()
    renderProveedores()
    await screen.findByText('Proveedor Uno SA')

    await usuario.click(screen.getByRole('button', { name: titulo }))

    await waitFor(() => expect(rutasDelListado().at(-1)).toBe(`/proveedores?orden=${clave}&direccion=ascendente`))
  })

  /** El servidor ordena Estado por su etiqueta (`OrdenDeProveedores`: Activo antes que Inactivo en ascendente): si la
   * pantalla cambiara los textos, el orden que dice mostrar dejaría de ser el que ve. */
  it('las etiquetas de estado son Activo e Inactivo, en el orden alfabético que el servidor usa', async () => {
    responder(2, [proveedorFixture(), proveedorFixture({ id: 2, razonSocial: 'Proveedor Dos SA', activo: false })])
    renderProveedores()
    await screen.findByText('Proveedor Dos SA')

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

  it('una consulta que falla deja la tabla sin filas bajo el encabezado que pidió, y los filtros montados', async () => {
    const usuario = userEvent.setup()
    responder()
    renderProveedores()
    await screen.findByText('Proveedor Uno SA')
    const filtro = screen.getByLabelText('Filtrar por razón social')
    apiGetMock.mockImplementation((ruta: string) =>
      ruta.startsWith('/proveedores') ? Promise.reject(new Error('caído')) : Promise.resolve([]),
    )

    await usuario.click(screen.getByRole('button', { name: 'Razón social' }))

    expect(await screen.findByText('No se pudieron cargar los proveedores.')).toBeInTheDocument()
    expect(screen.queryByText('Proveedor Uno SA')).toBeNull()
    expect(screen.queryByRole('button', { name: 'Siguiente' })).toBeNull()
    expect(screen.getByRole('columnheader', { name: 'Razón social' })).toHaveAttribute('aria-sort', 'ascending')
    expect(screen.getByLabelText('Filtrar por razón social')).toBe(filtro)
  })
})

describe('Proveedores — filtros por columna en el servidor', () => {
  it('un texto espera la demora y se pide una sola vez por ráfaga', async () => {
    const usuario = userEvent.setup()
    responder()
    renderProveedores()
    await screen.findByText('Proveedor Uno SA')
    const antes = rutasDelListado().length

    await usuario.type(screen.getByLabelText('Filtrar por razón social'), 'uno')

    expect(rutasDelListado()).toHaveLength(antes)
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/proveedores?razonSocial=uno'))
    expect(rutasDelListado()).toHaveLength(antes + 1)
  })

  it('un selector se aplica en el acto, sin pasar por la demora', async () => {
    const usuario = userEvent.setup()
    responder()
    renderProveedores()
    await screen.findByText('Proveedor Uno SA')

    await usuario.selectOptions(screen.getByLabelText('Filtrar por estado'), 'Inactivo')

    expect(rutasDelListado().at(-1)).toBe('/proveedores?activo=false')
  })

  it.each([
    ['razón social', 'uno', 'razonSocial=uno'],
    ['CUIT', '30-7', 'cuitContiene=30-7'],
    ['teléfono', '11', 'telefono=11'],
    ['email', '@x', 'email=%40x'],
  ])('el filtro de %s manda su parámetro', async (columna, texto, esperado) => {
    const usuario = userEvent.setup()
    responder()
    renderProveedores()
    await screen.findByText('Proveedor Uno SA')

    await usuario.type(screen.getByLabelText(`Filtrar por ${columna}`), texto)

    await waitFor(() => expect(rutasDelListado().at(-1)).toBe(`/proveedores?${esperado}`))
  })

  it('el rango de margen manda desde y hasta, y vaciar un campo lo saca de la consulta', async () => {
    const usuario = userEvent.setup()
    responder()
    renderProveedores()
    await screen.findByText('Proveedor Uno SA')

    await usuario.type(screen.getByLabelText('Margen desde'), '10')
    await usuario.type(screen.getByLabelText('Margen hasta'), '12,5')

    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/proveedores?margenDesde=10&margenHasta=12.5'))

    await usuario.clear(screen.getByLabelText('Margen desde'))

    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/proveedores?margenHasta=12.5'))
  })

  it('cambiar un filtro vuelve a la página 1 y conserva el orden', async () => {
    const usuario = userEvent.setup()
    responder(60)
    renderProveedores()
    await screen.findByText('Proveedor Uno SA')
    await usuario.click(screen.getByRole('button', { name: 'Email' }))
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/proveedores?orden=email&direccion=ascendente'))
    await usuario.click(screen.getByRole('button', { name: 'Siguiente' }))
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/proveedores?orden=email&direccion=ascendente&pagina=2'))

    await usuario.selectOptions(screen.getByLabelText('Filtrar por estado'), 'Activo')

    expect(rutasDelListado().at(-1)).toBe('/proveedores?activo=true&orden=email&direccion=ascendente')
  })

  it('el filtro conserva el foco y lo escrito cuando llega el listado', async () => {
    const usuario = userEvent.setup()
    responder()
    renderProveedores()
    await screen.findByText('Proveedor Uno SA')
    const filtro = screen.getByLabelText('Filtrar por razón social')

    await usuario.type(filtro, 'un')
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/proveedores?razonSocial=un'))
    await screen.findByText('Proveedor Uno SA')

    expect(screen.getByLabelText('Filtrar por razón social')).toBe(filtro)
    expect(filtro).toHaveFocus()
    expect(filtro).toHaveValue('un')
  })

  it('Limpiar vacía los filtros, incluida la búsqueda general, conserva el orden y vuelve a pedir', async () => {
    const usuario = userEvent.setup()
    responder()
    renderProveedores()
    await screen.findByText('Proveedor Uno SA')
    await usuario.click(screen.getByRole('button', { name: 'Razón social' }))
    await usuario.selectOptions(screen.getByLabelText('Filtrar por estado'), 'Activo')
    await usuario.type(screen.getByPlaceholderText(/Buscar por razón social/), 'uno')
    await usuario.click(screen.getByRole('button', { name: 'Buscar' }))
    await waitFor(() =>
      expect(rutasDelListado().at(-1)).toBe('/proveedores?busqueda=uno&activo=true&orden=razonSocial&direccion=ascendente'),
    )

    await usuario.click(screen.getByRole('button', { name: 'Limpiar filtros' }))

    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/proveedores?orden=razonSocial&direccion=ascendente'))
    expect(screen.getByLabelText('Filtrar por estado')).toHaveValue('')
    expect(screen.getByPlaceholderText(/Buscar por razón social/)).toHaveValue('')
  })

  it('una respuesta tardía de una consulta anterior no pisa a la vigente ni apaga su estado de carga', async () => {
    const usuario = userEvent.setup()
    const pendientes = responderADemanda([proveedorFixture()])
    renderProveedores()
    await screen.findByText('Proveedor Uno SA')
    await usuario.selectOptions(screen.getByLabelText('Filtrar por estado'), 'Activo')
    await usuario.selectOptions(screen.getByLabelText('Filtrar por estado'), 'Inactivo')
    expect(pendientes.map((p) => p.ruta)).toEqual(['/proveedores?activo=true', '/proveedores?activo=false'])
    const cuerpo = screen.getAllByRole('rowgroup')[1]

    await act(async () =>
      pendientes[0].resolver(paginaDeLaRuta(pendientes[0].ruta, [proveedorFixture({ id: 8, razonSocial: 'Primero' })], 1)),
    )

    expect(cuerpo).toHaveStyle({ opacity: '0.6' })
    expect(screen.queryByText('Primero')).toBeNull()

    await act(async () =>
      pendientes[1].resolver(paginaDeLaRuta(pendientes[1].ruta, [proveedorFixture({ id: 9, razonSocial: 'Segundo' })], 1)),
    )

    expect(screen.getByText('Segundo')).toBeInTheDocument()
    expect(cuerpo).not.toHaveStyle({ opacity: '0.6' })
  })

  it('una lectura que falla después de otra más nueva no deja su error', async () => {
    const usuario = userEvent.setup()
    const pendientes = responderADemanda([proveedorFixture()])
    renderProveedores()
    await screen.findByText('Proveedor Uno SA')
    await usuario.selectOptions(screen.getByLabelText('Filtrar por estado'), 'Activo')
    await usuario.selectOptions(screen.getByLabelText('Filtrar por estado'), 'Inactivo')

    await act(async () =>
      pendientes[1].resolver(paginaDeLaRuta(pendientes[1].ruta, [proveedorFixture({ id: 9, razonSocial: 'Segundo' })], 1)),
    )
    await act(async () => pendientes[0].rechazar(new Error('tarde')))

    expect(screen.queryByText('No se pudieron cargar los proveedores.')).toBeNull()
    expect(screen.getByText('Segundo')).toBeInTheDocument()
  })
})

describe('Proveedores — paginación en el servidor', () => {
  it('muestra la página, el total y mueve la consulta con Anterior y Siguiente', async () => {
    const usuario = userEvent.setup()
    responder(60)
    renderProveedores()
    await screen.findByText('Proveedor Uno SA')

    expect(screen.getByText('Página 1 de 3 — 60 proveedores')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Anterior' })).toBeDisabled()

    await usuario.click(screen.getByRole('button', { name: 'Siguiente' }))
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/proveedores?pagina=2'))
    expect(await screen.findByText('Página 2 de 3 — 60 proveedores')).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Siguiente' }))
    expect(await screen.findByText('Página 3 de 3 — 60 proveedores')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeDisabled()

    await usuario.click(screen.getByRole('button', { name: 'Anterior' }))
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/proveedores?pagina=2'))
  })

  it('el tamaño de página viaja y vuelve a la página 1', async () => {
    const usuario = userEvent.setup()
    responder(60)
    renderProveedores()
    await screen.findByText('Proveedor Uno SA')
    await usuario.click(screen.getByRole('button', { name: 'Siguiente' }))
    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/proveedores?pagina=2'))

    await usuario.selectOptions(screen.getByLabelText('Filas por página'), '50')

    await waitFor(() => expect(rutasDelListado().at(-1)).toBe('/proveedores?tamanio=50'))
    expect(await screen.findByText('Página 1 de 2 — 60 proveedores')).toBeInTheDocument()
  })

  it('con una página fuera de rango pide la última válida en vez de mostrar una página vacía', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/proveedores') {
        return Promise.resolve({ items: [], total: 50, pagina: 3, tamanio: 25 } satisfies PaginaDe<ProveedorListado>)
      }
      if (ruta.startsWith('/proveedores')) return Promise.resolve(paginaDeLaRuta(ruta, [proveedorFixture()], 50))

      return Promise.resolve([])
    })

    renderProveedores()

    expect(await screen.findByText('Página 2 de 2 — 50 proveedores')).toBeInTheDocument()
    expect(rutasDelListado()).toEqual(['/proveedores', '/proveedores?pagina=2'])
  })

  it('tras una baja que vacía la última página recarga la última válida', async () => {
    const usuario = userEvent.setup()
    const otro = proveedorFixture({ id: 2, razonSocial: 'Proveedor Dos SA' })
    let total = 26
    apiDeleteMock.mockImplementation(() => {
      total = 25

      return Promise.resolve()
    })
    apiGetMock.mockImplementation((ruta: string) => {
      if (!ruta.startsWith('/proveedores')) return Promise.resolve([])
      const pagina = Number(new URLSearchParams(ruta.split('?')[1] ?? '').get('pagina') ?? 1)

      return Promise.resolve({
        items: pagina === 1 ? [otro] : [proveedorFixture()],
        total,
        pagina,
        tamanio: 25,
      } satisfies PaginaDe<ProveedorListado>)
    })
    renderProveedores()
    await screen.findByText('Proveedor Dos SA')
    await usuario.click(screen.getByRole('button', { name: 'Siguiente' }))
    await screen.findByText('Proveedor Uno SA')

    await usuario.click(within(screen.getByRole('row', { name: /Proveedor Uno SA/ })).getByRole('button', { name: 'Eliminar' }))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    expect(await screen.findByText('Página 1 de 1 — 25 proveedores')).toBeInTheDocument()
    expect(rutasDelListado().at(-1)).toBe('/proveedores')
  })
})

describe('Proveedores — los controles del padrón durante una escritura o una confirmación', () => {
  it('con la confirmación de baja abierta los filtros, el orden y el pager quedan inertes', async () => {
    const usuario = userEvent.setup()
    responder(60)
    renderProveedores()
    await screen.findByText('Proveedor Uno SA')

    await usuario.click(screen.getByRole('button', { name: 'Eliminar' }))
    await screen.findByRole('button', { name: 'Confirmar baja' })

    expect(screen.getByLabelText('Filtrar por razón social')).toBeDisabled()
    expect(screen.getByLabelText('Margen desde')).toBeDisabled()
    expect(screen.getByLabelText('Filtrar por estado')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeDisabled()
  })

  it('un filtro tipeado antes de abrir la confirmación espera a que termine la baja y no invalida su refresco', async () => {
    const usuario = userEvent.setup()
    let terminarBaja: () => void = () => {}
    apiDeleteMock.mockReturnValue(new Promise<void>((r) => (terminarBaja = r)))
    responder()
    renderProveedores()
    await screen.findByText('Proveedor Uno SA')
    const antes = rutasDelListado().length

    await usuario.type(screen.getByLabelText('Filtrar por razón social'), 'zz')
    await usuario.click(screen.getByRole('button', { name: 'Eliminar' }))
    await screen.findByRole('button', { name: 'Confirmar baja' })
    await new Promise((r) => setTimeout(r, 450))
    expect(rutasDelListado()).toHaveLength(antes)

    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))
    await new Promise((r) => setTimeout(r, 450))
    expect(rutasDelListado()).toHaveLength(antes)

    await act(async () => terminarBaja())

    expect(await screen.findByRole('status')).toHaveTextContent('Proveedor "Proveedor Uno SA" dado de baja.')
    await waitFor(() => expect(rutasDelListado().slice(antes)).toEqual(['/proveedores', '/proveedores?razonSocial=zz']))
  })
})
