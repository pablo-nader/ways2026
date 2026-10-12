import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Combos } from './Combos'
import { ErrorApi } from '../api/cliente'
import type { ArticuloListado, ComboListado, EmpresaListado, FamiliaListado, PaginaDe } from '../api/tipos'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()
const apiPutMock = vi.fn()
const apiDeleteMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown?])),
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

function diferida<T>() {
  let resolver!: (valor: T) => void
  let rechazar!: (motivo: unknown) => void
  const promesa = new Promise<T>((resolve, reject) => {
    resolver = resolve
    rechazar = reject
  })
  return { promesa, resolver, rechazar }
}

function articuloFixture(id: number, nombre: string, sobrescribir: Partial<ArticuloListado> = {}): ArticuloListado {
  return { id, codigoInterno: `A${id}`, nombre, unidadVenta: 'Unidad', activo: true, ...sobrescribir } as ArticuloListado
}

const pancho = articuloFixture(1, 'Pancho')
const gaseosa = articuloFixture(2, 'Gaseosa 500')
const queso = articuloFixture(3, 'Queso por kilo', { unidadVenta: 'Peso' })
const viejo = articuloFixture(4, 'Pancho viejo', { activo: false })

const lineaCoca: FamiliaListado = { id: 7, nombre: 'Línea Coca 500', activo: true, cantidadArticulos: 3 }
const empresa = { id: 3, razonSocial: 'Alsina SA', nombreFantasia: null } as EmpresaListado

function comboFixture(sobrescribir: Partial<ComboListado> = {}): ComboListado {
  return {
    id: 4,
    codigo: 12,
    nombre: 'Pancho y Coca',
    precio: 5000,
    idEmpresa: null,
    activo: true,
    componentes: [
      { idArticulo: 1, idFamilia: null, nombre: 'Pancho', cantidad: 1, disponible: true },
      { idArticulo: null, idFamilia: 7, nombre: 'Línea Coca 500', cantidad: 2, disponible: true },
    ],
    ...sobrescribir,
  }
}

type Escenario = {
  /** Una función permite devolver respuestas distintas en cada lectura. */
  combos?: ComboListado[] | (() => Promise<ComboListado[]>)
  familias?: FamiliaListado[] | null
  empresas?: EmpresaListado[] | null
  /** Resultados de la búsqueda de artículos por término. */
  articulos?: Record<string, ArticuloListado[]>
  /** `total` que el servidor informa por término; por defecto, la cantidad de ítems (sin más páginas). */
  totales?: Record<string, number>
}

function mockearApiGet({
  combos = [],
  familias = [lineaCoca],
  empresas = [empresa],
  articulos = {},
  totales = {},
}: Escenario = {}) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/combos') return typeof combos === 'function' ? combos() : Promise.resolve(combos)
    if (ruta === '/familias') return familias === null ? Promise.reject(new Error('sin red')) : Promise.resolve(familias)
    if (ruta === '/empresas') return empresas === null ? Promise.reject(new Error('sin red')) : Promise.resolve(empresas)
    if (ruta.startsWith('/articulos?')) {
      const termino = new URLSearchParams(ruta.split('?')[1]).get('busqueda') ?? ''
      const items = articulos[termino] ?? []
      return Promise.resolve<PaginaDe<ArticuloListado>>({ items, total: totales[termino] ?? items.length, pagina: 1, tamanio: 20 })
    }
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

const ARTICULOS = { pancho: [pancho, queso, viejo, gaseosa], gaseosa: [gaseosa] }

let usuario: ReturnType<typeof userEvent.setup>

beforeEach(() => {
  usuario = userEvent.setup({ delay: null })
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiPutMock.mockReset()
  apiDeleteMock.mockReset()
  mockearApiGet({ articulos: ARTICULOS })
})

function fila(n: number) {
  return within(screen.getByRole('group', { name: `Componente ${n}` }))
}

async function elegirArticulo(n: number, termino: string, id: number) {
  await usuario.type(fila(n).getByLabelText(`Buscar artículo del componente ${n}`), termino)
  await usuario.click(fila(n).getByRole('button', { name: `Buscar artículos del componente ${n}` }))
  const lista = await fila(n).findByRole('listbox', { name: `Resultados del componente ${n}` })
  await usuario.click(within(lista).getByRole('option', { name: new RegExp(`^A${id} — `) }))
  await fila(n).findByText('Elegido:', { exact: false })
}

async function elegirFamilia(n: number, id: number) {
  await usuario.selectOptions(fila(n).getByLabelText(`Tipo del componente ${n}`), 'Familia')
  await fila(n).findByRole('option', { name: 'Línea Coca 500' })
  await usuario.selectOptions(fila(n).getByLabelText(`Familia del componente ${n}`), String(id))
}

async function cambiarCantidad(n: number, cantidad: string) {
  const campo = fila(n).getByLabelText(`Cantidad del componente ${n}`)
  await usuario.clear(campo)
  if (cantidad !== '') await usuario.type(campo, cantidad)
}

async function abrirNuevo() {
  render(<Combos />)
  await usuario.click(await screen.findByRole('button', { name: 'Agregar' }))
  await screen.findByRole('dialog')
}

/** Alta válida: nombre, precio, un artículo y una familia (1 + 2 unidades). */
async function llenarAltaValida() {
  await usuario.type(screen.getByLabelText('Nombre'), 'Pancho y Coca')
  await usuario.type(screen.getByLabelText('Precio del combo ($)'), '5000')
  await elegirArticulo(1, 'pancho', 1)
  await elegirFamilia(2, 7)
  await cambiarCantidad(2, '2')
}

function guardar() {
  return within(screen.getByRole('dialog')).getByRole('button', { name: 'Guardar' })
}

describe('Combos — listado', () => {
  it('muestra código C<n>, nombre, precio, componentes y estado, también de los inactivos, y explica el código', async () => {
    mockearApiGet({
      combos: [
        comboFixture(),
        comboFixture({ id: 5, codigo: 13, nombre: 'Combo viejo', precio: 1234.5, activo: false }),
      ],
    })

    render(<Combos />)

    const filaActiva = (await screen.findByText('Pancho y Coca')).closest('tr')!
    expect(within(filaActiva).getByText('C12')).toBeInTheDocument()
    expect(within(filaActiva).getByText('$ 5.000,00')).toBeInTheDocument()
    expect(within(filaActiva).getByText('1 × Pancho + 2 × Línea Coca 500 (familia)')).toBeInTheDocument()
    expect(within(filaActiva).getByText('Activo')).toBeInTheDocument()
    const filaInactiva = screen.getByText('Combo viejo').closest('tr')!
    expect(within(filaInactiva).getByText('C13')).toBeInTheDocument()
    expect(within(filaInactiva).getByText('$ 1.234,50')).toBeInTheDocument()
    expect(within(filaInactiva).getByText('Inactivo')).toBeInTheDocument()
    expect(screen.getByText(/tipeando su código con la C adelante/)).toBeInTheDocument()
  })

  it('marca en la fila el combo con un componente no disponible', async () => {
    mockearApiGet({
      combos: [
        comboFixture({
          componentes: [
            { idArticulo: 5, idFamilia: null, nombre: null, cantidad: 1, disponible: false },
            { idArticulo: 1, idFamilia: null, nombre: 'Pancho', cantidad: 1, disponible: true },
          ],
        }),
        comboFixture({ id: 6, codigo: 14, nombre: 'Combo sano' }),
      ],
    })

    render(<Combos />)

    const marcada = (await screen.findByText('Pancho y Coca')).closest('tr')!
    expect(within(marcada).getByText('1 × Artículo #5 (no disponible) + 1 × Pancho')).toBeInTheDocument()
    expect(within(marcada).getByText(/no se puede completar hasta corregirlo/)).toBeInTheDocument()
    expect(within(screen.getByText('Combo sano').closest('tr')!).queryByText(/no se puede completar/)).not.toBeInTheDocument()
  })

  it('sin combos lo dice', async () => {
    render(<Combos />)

    expect(await screen.findByText('No hay combos cargados.')).toBeInTheDocument()
  })

  it('si la lectura falla muestra el mensaje del servidor y no la fila de vacío', async () => {
    mockearApiGet({ combos: () => Promise.reject(new ErrorApi(500, 'error_interno', 'Falló el servidor.')) })

    render(<Combos />)

    expect(await screen.findByText('Falló el servidor.')).toBeInTheDocument()
    expect(screen.queryByText('No hay combos cargados.')).not.toBeInTheDocument()
  })

  it('si la lectura falla sin respuesta usa el texto genérico', async () => {
    mockearApiGet({ combos: () => Promise.reject(new Error('sin red')) })

    render(<Combos />)

    expect(await screen.findByText('No se pudieron cargar los combos.')).toBeInTheDocument()
  })

  it('si no cargan las familias o las empresas avisa y la pantalla sigue andando', async () => {
    mockearApiGet({ combos: [comboFixture()], familias: null, empresas: null })

    render(<Combos />)

    expect(await screen.findByText('Pancho y Coca')).toBeInTheDocument()
    expect(await screen.findByText(/No se pudieron cargar las familias/)).toBeInTheDocument()
    expect(screen.getByText(/No se pudieron cargar las empresas/)).toBeInTheDocument()
  })

  it('una respuesta tardía de la lectura inicial no pisa el listado que llegó después de guardar', async () => {
    const inicial = diferida<ComboListado[]>()
    const creado = comboFixture({ id: 9, nombre: 'Pancho y Coca' })
    let lecturas = 0
    mockearApiGet({
      articulos: ARTICULOS,
      combos: () => {
        lecturas += 1
        return lecturas === 1 ? inicial.promesa : Promise.resolve([creado])
      },
    })
    apiPostMock.mockResolvedValue(creado)
    render(<Combos />)
    await usuario.click(await screen.findByRole('button', { name: 'Agregar' }))
    await llenarAltaValida()

    await usuario.click(guardar())
    await screen.findByText('Combo "Pancho y Coca" creado.')
    await screen.findByText('C12')

    await act(async () => inicial.resolver([]))

    expect(screen.getByText('C12')).toBeInTheDocument()
    expect(screen.queryByText('No hay combos cargados.')).not.toBeInTheDocument()
  })
})

describe('Combos — alta', () => {
  it('manda el cuerpo exacto con un artículo y una familia, sin código, y refresca el listado', async () => {
    apiPostMock.mockResolvedValue(comboFixture())
    await abrirNuevo()
    await llenarAltaValida()

    await usuario.click(guardar())

    await screen.findByText('Combo "Pancho y Coca" creado.')
    expect(apiPostMock).toHaveBeenCalledTimes(1)
    const [ruta, cuerpo] = apiPostMock.mock.calls[0] as [string, Record<string, unknown>]
    expect(ruta).toBe('/combos')
    expect(cuerpo).toStrictEqual({
      nombre: 'Pancho y Coca',
      precio: 5000,
      idEmpresa: null,
      activo: true,
      componentes: [
        { idArticulo: 1, cantidad: 1 },
        { idFamilia: 7, cantidad: 2 },
      ],
    })
    expect(Object.keys(cuerpo)).not.toContain('codigo')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    await waitFor(() => expect(apiGetMock.mock.calls.filter(([ruta]) => ruta === '/combos')).toHaveLength(2))
  })

  it('con código y empresa elegidos los manda; el código se explica como opcional en el alta', async () => {
    apiPostMock.mockResolvedValue(comboFixture())
    await abrirNuevo()
    expect(screen.getByText('Es lo que se tipea en la caja. Se asigna solo si lo dejás vacío.')).toBeInTheDocument()
    await llenarAltaValida()
    await usuario.type(screen.getByLabelText('Código'), '77')
    await screen.findByRole('option', { name: 'Alsina SA' })
    await usuario.selectOptions(screen.getByLabelText('Empresa'), '3')

    await usuario.click(guardar())

    await screen.findByText('Combo "Pancho y Coca" creado.')
    const cuerpo = apiPostMock.mock.calls[0][1] as Record<string, unknown>
    expect(cuerpo.codigo).toBe(77)
    expect(cuerpo.idEmpresa).toBe(3)
  })

  it('el activo desmarcado viaja como false', async () => {
    apiPostMock.mockResolvedValue(comboFixture())
    await abrirNuevo()
    await llenarAltaValida()
    await usuario.click(screen.getByLabelText('Activo'))

    await usuario.click(guardar())

    await screen.findByText('Combo "Pancho y Coca" creado.')
    expect((apiPostMock.mock.calls[0][1] as { activo: boolean }).activo).toBe(false)
  })

  it('no ofrece artículos pesables ni inactivos y lo avisa; sin ninguno elegible lo dice', async () => {
    await abrirNuevo()

    await usuario.type(fila(1).getByLabelText('Buscar artículo del componente 1'), 'pancho')
    await usuario.click(fila(1).getByRole('button', { name: 'Buscar artículos del componente 1' }))

    const lista = await fila(1).findByRole('listbox')
    expect(within(lista).getAllByRole('option').map((o) => o.textContent)).toEqual(['A1 — Pancho', 'A2 — Gaseosa 500'])
    expect(fila(1).getByText(/Se ocultaron 2 resultados de los mostrados/)).toBeInTheDocument()
    expect(fila(1).queryByText(/Se muestran los primeros/)).not.toBeInTheDocument()
  })

  it('con más resultados que los recibidos avisa que son los primeros y aclara que lo oculto es de lo mostrado', async () => {
    mockearApiGet({ articulos: { mixto: [pancho, queso] }, totales: { mixto: 60 } })
    await abrirNuevo()

    await usuario.type(fila(1).getByLabelText('Buscar artículo del componente 1'), 'mixto')
    await usuario.click(fila(1).getByRole('button', { name: 'Buscar artículos del componente 1' }))

    const lista = await fila(1).findByRole('listbox')
    expect(within(lista).getAllByRole('option').map((o) => o.textContent)).toEqual(['A1 — Pancho'])
    expect(fila(1).getByText('Se muestran los primeros 2 de 60 resultados. Refiná la búsqueda.')).toBeInTheDocument()
    expect(fila(1).getByText(/Se ocultó 1 resultado de los mostrados/)).toBeInTheDocument()
  })

  it('si la primera página es toda de pesables y hay más, no afirma que no hay artículos por unidad', async () => {
    mockearApiGet({ articulos: { queso: [queso, articuloFixture(8, 'Fiambre', { unidadVenta: 'Peso' })] }, totales: { queso: 60 } })
    await abrirNuevo()

    await usuario.type(fila(1).getByLabelText('Buscar artículo del componente 1'), 'queso')
    await usuario.click(fila(1).getByRole('button', { name: 'Buscar artículos del componente 1' }))

    expect(await fila(1).findByText('Se muestran los primeros 2 de 60 resultados. Refiná la búsqueda.')).toBeInTheDocument()
    expect(fila(1).getByText('Ninguno de los resultados mostrados puede ser componente.')).toBeInTheDocument()
    expect(fila(1).queryByText('No hay artículos por unidad que coincidan.')).not.toBeInTheDocument()
    expect(fila(1).queryByRole('listbox')).not.toBeInTheDocument()
  })

  it('una búsqueda que solo trae pesables dice que no hay artículos por unidad', async () => {
    mockearApiGet({ articulos: { queso: [queso] } })
    await abrirNuevo()

    await usuario.type(fila(1).getByLabelText('Buscar artículo del componente 1'), 'queso')
    await usuario.click(fila(1).getByRole('button', { name: 'Buscar artículos del componente 1' }))

    expect(await fila(1).findByText('No hay artículos por unidad que coincidan.')).toBeInTheDocument()
    expect(fila(1).queryByRole('listbox')).not.toBeInTheDocument()
  })

  it('un fallo de la búsqueda se muestra en la fila', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta.startsWith('/articulos?')) return Promise.reject(new ErrorApi(500, 'error_interno', 'Búsqueda caída.'))
      return Promise.resolve([])
    })
    await abrirNuevo()

    await usuario.type(fila(1).getByLabelText('Buscar artículo del componente 1'), 'x')
    await usuario.click(fila(1).getByRole('button', { name: 'Buscar artículos del componente 1' }))

    expect(await fila(1).findByText('Búsqueda caída.')).toBeInTheDocument()
  })

  it('agrega y quita componentes, y el resumen cuenta lo que se va armando', async () => {
    await abrirNuevo()
    await usuario.type(screen.getByLabelText('Precio del combo ($)'), '5000')
    await elegirArticulo(1, 'pancho', 1)
    await elegirFamilia(2, 7)

    expect(await screen.findByText('1 × Pancho + 1 × Línea Coca 500 (familia) por $ 5.000,00')).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Agregar componente' }))
    expect(screen.getByRole('group', { name: 'Componente 3' })).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Quitar componente 1' }))
    expect(screen.queryByRole('group', { name: 'Componente 3' })).not.toBeInTheDocument()
    expect(screen.getByText('1 × Línea Coca 500 (familia) por $ 5.000,00')).toBeInTheDocument()
  })

  it('cambiar el tipo de una fila descarta lo elegido', async () => {
    await abrirNuevo()
    await elegirArticulo(1, 'pancho', 1)

    await usuario.selectOptions(fila(1).getByLabelText('Tipo del componente 1'), 'Familia')
    await usuario.selectOptions(fila(1).getByLabelText('Tipo del componente 1'), 'Articulo')

    expect(fila(1).queryByText('Elegido:', { exact: false })).not.toBeInTheDocument()
  })

  it('el botón de agregar componente se deshabilita en el tope de 50', async () => {
    await abrirNuevo()
    const agregar = screen.getByRole('button', { name: 'Agregar componente' })

    for (let i = 0; i < 48; i += 1) fireEvent.click(agregar)

    expect(screen.getAllByRole('group', { name: /^Componente \d+$/ })).toHaveLength(50)
    expect(agregar).toBeDisabled()
  })

  it('al abrir el foco queda en el nombre y cancelar lo devuelve al botón que abrió el modal', async () => {
    render(<Combos />)
    const abrir = await screen.findByRole('button', { name: 'Agregar' })

    await usuario.click(abrir)
    await waitFor(() => expect(screen.getByLabelText('Nombre')).toHaveFocus())
    await usuario.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(abrir).toHaveFocus()
  })
})

describe('Combos — validación del cliente', () => {
  it('no muestra errores antes de intentar guardar', async () => {
    await abrirNuevo()

    expect(screen.queryByText('Ingresá un nombre para el combo.')).not.toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('guardar vacío muestra cada error junto a su campo, avisa arriba y no llama al servidor', async () => {
    await abrirNuevo()

    await usuario.click(guardar())

    expect(await screen.findByText('Ingresá un nombre para el combo.')).toBeInTheDocument()
    expect(screen.getByText('Ingresá un precio mayor a cero.')).toBeInTheDocument()
    expect(fila(1).getByText('Elegí un artículo.')).toBeInTheDocument()
    expect(fila(2).getByText('Elegí un artículo.')).toBeInTheDocument()
    expect(within(screen.getByRole('dialog')).getByRole('alert')).toHaveTextContent('Revisá los campos marcados.')
    expect(apiPostMock).not.toHaveBeenCalled()
    expect(screen.getByLabelText('Nombre')).toHaveAttribute('aria-invalid', 'true')
  })

  it('los errores se recalculan al corregir y el aviso de arriba se va con el primer cambio', async () => {
    await abrirNuevo()
    await usuario.click(guardar())
    await screen.findByText('Ingresá un nombre para el combo.')

    await usuario.type(screen.getByLabelText('Nombre'), 'Combo')

    expect(screen.queryByText('Ingresá un nombre para el combo.')).not.toBeInTheDocument()
    expect(screen.queryByText('Revisá los campos marcados.')).not.toBeInTheDocument()
    expect(screen.getByText('Ingresá un precio mayor a cero.')).toBeInTheDocument()
  })

  it('un código inválido se marca en el campo', async () => {
    await abrirNuevo()
    await llenarAltaValida()
    await usuario.type(screen.getByLabelText('Código'), '0')

    await usuario.click(guardar())

    expect(await screen.findByText('El código tiene que ser un entero entre 1 y 999999.')).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('un artículo repetido se marca en la segunda fila', async () => {
    await abrirNuevo()
    await llenarAltaValida()
    await usuario.selectOptions(fila(2).getByLabelText('Tipo del componente 2'), 'Articulo')
    await elegirArticulo(2, 'pancho', 1)

    await usuario.click(guardar())

    expect(await fila(2).findByText('Este artículo ya está en el combo.')).toBeInTheDocument()
    expect(fila(1).queryByText('Este artículo ya está en el combo.')).not.toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('una cantidad inválida se marca en su fila', async () => {
    await abrirNuevo()
    await llenarAltaValida()
    await cambiarCantidad(1, '')

    await usuario.click(guardar())

    expect(await fila(1).findByText('Ingresá la cantidad.')).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('con una sola unidad en total marca el conjunto', async () => {
    await abrirNuevo()
    await usuario.type(screen.getByLabelText('Nombre'), 'Solo')
    await usuario.type(screen.getByLabelText('Precio del combo ($)'), '100')
    await elegirArticulo(1, 'pancho', 1)
    await usuario.click(screen.getByRole('button', { name: 'Quitar componente 2' }))

    await usuario.click(guardar())

    expect(
      await within(screen.getByRole('group', { name: 'Componentes' })).findByText(
        'El combo tiene que sumar al menos 2 unidades entre todos sus componentes.',
      ),
    ).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('el campo de precio no deja escribir un tercer decimal', async () => {
    await abrirNuevo()
    const precio = screen.getByLabelText('Precio del combo ($)')

    await usuario.type(precio, '10,555')

    expect(precio).toHaveValue('10,55')
  })
})

describe('Combos — rechazos del servidor', () => {
  const casos: [string, number, string, string][] = [
    ['combo_precio_invalido', 400, 'cb-precio-error', 'El precio tiene que ser mayor a cero'],
    ['nombre_requerido', 400, 'cb-nombre-error', 'Ingresá un nombre para el combo.'],
    ['nombre_muy_largo', 400, 'cb-nombre-error', 'El nombre no puede superar los 150 caracteres.'],
    ['combo_codigo_invalido', 400, 'cb-codigo-error', 'El código tiene que ser un entero entre 1 y 999999.'],
    ['combo_codigo_duplicado', 409, 'cb-codigo-error', 'Ya hay un combo con ese código. Elegí otro.'],
    ['combo_codigo_agotado', 409, 'cb-codigo-error', 'El último código posible ya está en uso'],
  ]

  it.each(casos)('%s se muestra junto a su campo y el formulario conserva lo escrito', async (codigo, estado, idDelError, texto) => {
    apiPostMock.mockRejectedValue(new ErrorApi(estado, codigo, 'mensaje crudo'))
    await abrirNuevo()
    await llenarAltaValida()

    await usuario.click(guardar())

    await waitFor(() => expect(document.getElementById(idDelError)).toHaveTextContent(texto))
    expect(within(screen.getByRole('dialog')).getByRole('alert')).toHaveTextContent('Revisá el mensaje junto al campo marcado')
    expect(screen.getByLabelText('Nombre')).toHaveValue('Pancho y Coca')
    expect(guardar()).toBeEnabled()
  })

  it.each([
    ['combo_componente_invalido', 400, 'Cada componente tiene que ser un artículo o una familia.'],
    ['combo_cantidad_invalida', 400, 'La cantidad tiene que ser un entero entre 1 y 999999.'],
    ['combo_componente_duplicado', 409, 'Un artículo o una familia puede figurar una sola vez en el combo.'],
    ['combo_unidades_insuficientes', 400, 'El combo tiene que sumar al menos 2 unidades'],
    ['combo_componentes_excedidos', 400, 'Un combo admite hasta 50 componentes.'],
    ['combo_componente_no_unitario', 400, 'se vende por peso'],
  ])('%s se muestra en la sección de componentes', async (codigo, estado, texto) => {
    apiPostMock.mockRejectedValue(new ErrorApi(estado, codigo, 'mensaje crudo'))
    await abrirNuevo()
    await llenarAltaValida()

    await usuario.click(guardar())

    expect(await within(screen.getByRole('group', { name: 'Componentes' })).findByText(texto, { exact: false })).toBeInTheDocument()
    expect(screen.queryByText('mensaje crudo')).not.toBeInTheDocument()
  })

  it('el error del servidor se va con el siguiente cambio del formulario', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'combo_codigo_duplicado', 'x'))
    await abrirNuevo()
    await llenarAltaValida()
    await usuario.click(guardar())
    await waitFor(() => expect(document.getElementById('cb-codigo-error')).toBeInTheDocument())

    await usuario.type(screen.getByLabelText('Código'), '5')

    expect(document.getElementById('cb-codigo-error')).not.toBeInTheDocument()
  })

  it('referencia_invalida y activo_requerido van al aviso del formulario', async () => {
    apiPostMock.mockRejectedValueOnce(new ErrorApi(400, 'referencia_invalida', 'No existe la empresa 3.'))
    await abrirNuevo()
    await llenarAltaValida()

    await usuario.click(guardar())

    expect(await within(screen.getByRole('dialog')).findByRole('alert')).toHaveTextContent(/^No existe la empresa 3\. Quitá o reemplazá/)

    apiPostMock.mockRejectedValueOnce(new ErrorApi(400, 'activo_requerido', 'x'))
    await usuario.click(guardar())

    await waitFor(() =>
      expect(within(screen.getByRole('dialog')).getByRole('alert')).toHaveTextContent('Falta indicar si el combo está activo.'),
    )
  })

  it('un código desconocido muestra el mensaje del servidor en el aviso del formulario', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(400, 'algo_nuevo', 'Dijo algo raro.'))
    await abrirNuevo()
    await llenarAltaValida()

    await usuario.click(guardar())

    expect(await within(screen.getByRole('dialog')).findByRole('alert')).toHaveTextContent('Dijo algo raro.')
  })

  it('un 500 avisa que no se sabe si se guardó y deja reintentar', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(500, 'error_interno', 'boom'))
    await abrirNuevo()
    await llenarAltaValida()

    await usuario.click(guardar())

    expect(await within(screen.getByRole('dialog')).findByRole('alert')).toHaveTextContent('No se pudo confirmar el resultado')
    expect(guardar()).toBeEnabled()
  })
})

describe('Combos — edición', () => {
  it('abre con los valores del combo, explica que el código vacío conserva el actual y manda el PUT completo con activo', async () => {
    mockearApiGet({ combos: [comboFixture()], articulos: ARTICULOS })
    apiPutMock.mockResolvedValue(comboFixture())
    render(<Combos />)
    await usuario.click(await screen.findByRole('button', { name: 'Editar Pancho y Coca' }))
    await screen.findByRole('dialog', { name: 'Editar combo' })

    expect(screen.getByLabelText('Nombre')).toHaveValue('Pancho y Coca')
    expect(screen.getByLabelText('Código')).toHaveValue('12')
    expect(screen.getByText('Es lo que se tipea en la caja. Si lo dejás vacío se conserva el actual.')).toBeInTheDocument()
    expect(fila(1).getByText('Pancho')).toBeInTheDocument()
    await screen.findByRole('option', { name: 'Línea Coca 500' })
    expect(fila(2).getByLabelText('Familia del componente 2')).toHaveValue('7')

    await elegirArticulo(1, 'gaseosa', 2)
    await cambiarCantidad(2, '3')
    await usuario.click(screen.getByLabelText('Activo'))
    await usuario.click(guardar())

    await screen.findByText('Combo "Pancho y Coca" actualizado.')
    expect(apiPutMock).toHaveBeenCalledWith('/combos/4', {
      codigo: 12,
      nombre: 'Pancho y Coca',
      precio: 5000,
      idEmpresa: null,
      activo: false,
      componentes: [
        { idArticulo: 2, cantidad: 1 },
        { idFamilia: 7, cantidad: 3 },
      ],
    })
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('un combo de otra empresa que no está en el selector conserva su empresa', async () => {
    mockearApiGet({ combos: [comboFixture({ idEmpresa: 9 })], empresas: [empresa] })
    apiPutMock.mockResolvedValue(comboFixture())
    render(<Combos />)
    await usuario.click(await screen.findByRole('button', { name: 'Editar Pancho y Coca' }))
    await screen.findByRole('option', { name: 'Alsina SA' })

    expect(screen.getByLabelText('Empresa')).toHaveValue('9')
    expect(screen.getByRole('option', { name: 'Empresa #9 (no disponible)' })).toBeInTheDocument()

    await usuario.click(guardar())

    await screen.findByText('Combo "Pancho y Coca" actualizado.')
    expect((apiPutMock.mock.calls[0][1] as { idEmpresa: number }).idEmpresa).toBe(9)
  })

  it('un componente no disponible se muestra marcado y no deja guardar hasta quitarlo', async () => {
    mockearApiGet({
      combos: [
        comboFixture({
          componentes: [
            { idArticulo: 5, idFamilia: null, nombre: null, cantidad: 1, disponible: false },
            { idArticulo: 1, idFamilia: null, nombre: 'Pancho', cantidad: 2, disponible: true },
          ],
        }),
      ],
    })
    apiPutMock.mockResolvedValue(comboFixture())
    render(<Combos />)
    await usuario.click(await screen.findByRole('button', { name: 'Editar Pancho y Coca' }))
    await screen.findByRole('dialog')

    expect(fila(1).getByText('Este componente ya no está disponible: quitalo o reemplazalo para poder guardar.')).toBeInTheDocument()
    expect(fila(1).getByText('Elegido:', { exact: false })).toHaveTextContent('Artículo #5')

    await usuario.click(guardar())

    await waitFor(() => expect(within(screen.getByRole('dialog')).getByRole('alert')).toHaveTextContent('Revisá los campos marcados.'))
    expect(apiPutMock).not.toHaveBeenCalled()

    await usuario.click(screen.getByRole('button', { name: 'Quitar componente 1' }))
    await usuario.click(guardar())

    await screen.findByText('Combo "Pancho y Coca" actualizado.')
    expect((apiPutMock.mock.calls[0][1] as { componentes: unknown[] }).componentes).toStrictEqual([{ idArticulo: 1, cantidad: 2 }])
  })

  it('reemplazar el componente no disponible por otro artículo permite guardar', async () => {
    mockearApiGet({
      articulos: ARTICULOS,
      combos: [
        comboFixture({
          componentes: [
            { idArticulo: 5, idFamilia: null, nombre: null, cantidad: 1, disponible: false },
            { idArticulo: 1, idFamilia: null, nombre: 'Pancho', cantidad: 1, disponible: true },
          ],
        }),
      ],
    })
    apiPutMock.mockResolvedValue(comboFixture())
    render(<Combos />)
    await usuario.click(await screen.findByRole('button', { name: 'Editar Pancho y Coca' }))
    await screen.findByRole('dialog')

    await elegirArticulo(1, 'gaseosa', 2)

    expect(fila(1).queryByText(/ya no está disponible/)).not.toBeInTheDocument()
    await usuario.click(guardar())
    await screen.findByText('Combo "Pancho y Coca" actualizado.')
    expect((apiPutMock.mock.calls[0][1] as { componentes: unknown[] }).componentes).toStrictEqual([
      { idArticulo: 2, cantidad: 1 },
      { idArticulo: 1, cantidad: 1 },
    ])
  })

  it('un 404 al guardar cierra el formulario, lo avisa y vuelve a leer el listado', async () => {
    mockearApiGet({ combos: [comboFixture()] })
    apiPutMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'No existe el combo 4.'))
    render(<Combos />)
    await usuario.click(await screen.findByRole('button', { name: 'Editar Pancho y Coca' }))
    await screen.findByRole('dialog')

    await usuario.click(guardar())

    expect(await screen.findByText('El combo ya no existe. Se actualizó el listado.')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    await waitFor(() => expect(apiGetMock.mock.calls.filter(([ruta]) => ruta === '/combos')).toHaveLength(2))
  })
})

describe('Combos — escritura en vuelo', () => {
  it('mientras el POST está en vuelo la ventana queda inerte y un doble clic manda una sola escritura', async () => {
    const respuesta = diferida<ComboListado>()
    apiPostMock.mockReturnValue(respuesta.promesa)
    mockearApiGet({ combos: [comboFixture()], articulos: ARTICULOS })
    render(<Combos />)
    await screen.findByText('Pancho y Coca')
    await usuario.click(screen.getByRole('button', { name: 'Agregar' }))
    await llenarAltaValida()
    const boton = guardar()

    await act(async () => {
      boton.click()
      boton.click()
    })

    expect(apiPostMock).toHaveBeenCalledTimes(1)
    expect(screen.getByLabelText('Nombre')).toBeDisabled()
    expect(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Agregar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Editar Pancho y Coca' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Dar de baja Pancho y Coca' })).toBeDisabled()

    await act(async () => respuesta.resolver(comboFixture()))
    await screen.findByText('Combo "Pancho y Coca" creado.')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Agregar' })).toBeEnabled())
  })

  it('si el guardado salió bien pero el refresco falla lo dice sin reportar el guardado como fallido', async () => {
    let lecturas = 0
    mockearApiGet({
      articulos: ARTICULOS,
      combos: () => {
        lecturas += 1
        return lecturas === 1 ? Promise.resolve([]) : Promise.reject(new Error('sin red'))
      },
    })
    apiPostMock.mockResolvedValue(comboFixture())
    await abrirNuevo()
    await llenarAltaValida()

    await usuario.click(guardar())

    expect(await screen.findByText('Combo "Pancho y Coca" creado.')).toBeInTheDocument()
    expect(await screen.findByText('No se pudo actualizar el listado. Volvé a entrar para ver el cambio.')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })
})

describe('Combos — baja', () => {
  async function pedirBaja() {
    mockearApiGet({ combos: [comboFixture()] })
    render(<Combos />)
    const disparador = await screen.findByRole('button', { name: 'Dar de baja Pancho y Coca' })
    await usuario.click(disparador)
    await screen.findByRole('alertdialog')
    return disparador
  }

  it('pide confirmación nombrando el combo y cancelar no borra nada', async () => {
    const disparador = await pedirBaja()

    expect(screen.getByRole('alertdialog')).toHaveTextContent('¿Dar de baja el combo "Pancho y Coca"?')
    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(apiDeleteMock).not.toHaveBeenCalled()
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(disparador).toHaveFocus()
  })

  it('con la puerta abierta el resto de la pantalla queda inerte', async () => {
    await pedirBaja()

    expect(screen.getByRole('button', { name: 'Agregar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Editar Pancho y Coca' })).toBeDisabled()
  })

  it('confirmar da de baja, avisa y refresca el listado', async () => {
    apiDeleteMock.mockResolvedValue(undefined)
    await pedirBaja()
    mockearApiGet({ combos: [] })

    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    expect(await screen.findByText('Combo "Pancho y Coca" dado de baja.')).toBeInTheDocument()
    expect(apiDeleteMock).toHaveBeenCalledWith('/combos/4')
    expect(await screen.findByText('No hay combos cargados.')).toBeInTheDocument()
  })

  it('un doble clic en confirmar manda un solo DELETE', async () => {
    const respuesta = diferida<undefined>()
    apiDeleteMock.mockReturnValue(respuesta.promesa)
    await pedirBaja()
    const confirmar = screen.getByRole('button', { name: 'Confirmar baja' })

    await act(async () => {
      confirmar.click()
      confirmar.click()
    })

    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
    await act(async () => respuesta.resolver(undefined))
    await screen.findByText('Combo "Pancho y Coca" dado de baja.')
  })

  it('un 404 en la baja lo avisa, cierra la puerta y vuelve a leer el listado', async () => {
    apiDeleteMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'No existe.'))
    await pedirBaja()

    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    expect(await screen.findByText('El combo ya no existe. Se actualizó el listado.')).toBeInTheDocument()
    expect(screen.queryByText(/otra persona/)).not.toBeInTheDocument()
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    await waitFor(() => expect(apiGetMock.mock.calls.filter(([ruta]) => ruta === '/combos')).toHaveLength(2))
  })

  it('otro fallo muestra el mensaje y deja la puerta abierta para reintentar', async () => {
    apiDeleteMock.mockRejectedValue(new ErrorApi(409, 'otro', 'No se puede.'))
    await pedirBaja()

    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    expect(await screen.findByText('No se puede.')).toBeInTheDocument()
    expect(screen.getByRole('alertdialog')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Confirmar baja' })).toBeEnabled()
  })
})

function pagina(items: ArticuloListado[]): PaginaDe<ArticuloListado> {
  return { items, total: items.length, pagina: 1, tamanio: 25 }
}

type BusquedaPendiente = ReturnType<typeof diferida<PaginaDe<ArticuloListado>>>

/** Cada término de búsqueda queda pendiente hasta que el test lo resuelve, para controlar el orden de las respuestas. */
function mockearBusquedasDiferidas() {
  const pendientes: Record<string, BusquedaPendiente> = {}
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/combos') return Promise.resolve([])
    if (ruta === '/familias') return Promise.resolve([lineaCoca])
    if (ruta === '/empresas') return Promise.resolve([empresa])
    if (ruta.startsWith('/articulos?')) {
      const termino = new URLSearchParams(ruta.split('?')[1]).get('busqueda') ?? ''
      pendientes[termino] ??= diferida<PaginaDe<ArticuloListado>>()
      return pendientes[termino].promesa
    }
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
  return pendientes
}

function opcionesDe(n: number) {
  return within(fila(n).getByRole('listbox')).getAllByRole('option')
}

async function opcionDe(n: number, nombre: string) {
  return within(await fila(n).findByRole('listbox')).getByRole('option', { name: nombre })
}

function campoDeBusqueda(n: number) {
  return fila(n).getByRole('combobox', { name: `Buscar artículo del componente ${n}` })
}

async function buscarConBoton(n: number, termino: string) {
  await usuario.type(campoDeBusqueda(n), termino)
  await usuario.click(fila(n).getByRole('button', { name: `Buscar artículos del componente ${n}` }))
}

describe('Combos — buscador de artículo con teclado', () => {
  async function abrirConResultados() {
    await abrirNuevo()
    await buscarConBoton(1, 'pancho')
    await fila(1).findByRole('listbox', { name: 'Resultados del componente 1' })
    await waitFor(() => expect(campoDeBusqueda(1)).toHaveFocus())
  }

  it('las flechas mueven la opción resaltada sin elegir, y el campo la anuncia con aria-activedescendant', async () => {
    await abrirConResultados()
    const opciones = opcionesDe(1)
    expect(opciones[0]).toHaveAttribute('aria-selected', 'true')
    expect(campoDeBusqueda(1)).toHaveAttribute('aria-activedescendant', opciones[0].id)

    await usuario.keyboard('{ArrowDown}')

    expect(opciones[1]).toHaveAttribute('aria-selected', 'true')
    expect(campoDeBusqueda(1)).toHaveAttribute('aria-activedescendant', opciones[1].id)
    expect(fila(1).queryByText('Elegido:', { exact: false })).not.toBeInTheDocument()

    await usuario.keyboard('{ArrowDown}')
    expect(opciones[0]).toHaveAttribute('aria-selected', 'true')
    await usuario.keyboard('{ArrowUp}')
    expect(opciones[1]).toHaveAttribute('aria-selected', 'true')
    expect(fila(1).queryByText('Elegido:', { exact: false })).not.toBeInTheDocument()
  })

  it('Enter elige la opción resaltada y no la primera', async () => {
    await abrirConResultados()

    await usuario.keyboard('{ArrowDown}{Enter}')

    expect(await fila(1).findByText('Elegido:', { exact: false })).toHaveTextContent('Gaseosa 500')
    expect(fila(1).queryByRole('listbox')).not.toBeInTheDocument()
  })

  it('Enter con el campo editado busca de nuevo en vez de elegir', async () => {
    await abrirConResultados()

    await usuario.type(campoDeBusqueda(1), 'x{Enter}')

    expect(fila(1).queryByText('Elegido:', { exact: false })).not.toBeInTheDocument()
    await waitFor(() => expect(apiGetMock.mock.calls.filter(([ruta]) => String(ruta).startsWith('/articulos?'))).toHaveLength(2))
  })

  it('un clic en una opción la elige', async () => {
    await abrirConResultados()

    await usuario.click(within(fila(1).getByRole('listbox')).getByRole('option', { name: 'A2 — Gaseosa 500' }))

    expect(await fila(1).findByText('Elegido:', { exact: false })).toHaveTextContent('Gaseosa 500')
  })

  it('Escape cierra la lista sin elegir y sin cerrar el modal', async () => {
    await abrirConResultados()

    await usuario.keyboard('{Escape}')

    expect(fila(1).queryByRole('listbox')).not.toBeInTheDocument()
    expect(campoDeBusqueda(1)).toHaveAttribute('aria-expanded', 'false')
    expect(fila(1).queryByText('Elegido:', { exact: false })).not.toBeInTheDocument()
    expect(screen.getByRole('dialog')).toBeInTheDocument()

    await usuario.keyboard('{ArrowDown}')
    expect(opcionesDe(1)).toHaveLength(2)
  })

  it('salir del campo cierra la lista sin elegir', async () => {
    await abrirConResultados()

    await usuario.click(screen.getByLabelText('Nombre'))

    expect(fila(1).queryByRole('listbox')).not.toBeInTheDocument()
    expect(fila(1).queryByText('Elegido:', { exact: false })).not.toBeInTheDocument()
  })
})

describe('Combos — buscador de artículo asincrónico', () => {
  // Cláusula que prueba: la generación de `buscar` descarta la respuesta de una búsqueda superada.
  it('dos búsquedas que se superponen y responden al revés muestran solo la más nueva', async () => {
    const pendientes = mockearBusquedasDiferidas()
    await abrirNuevo()
    await buscarConBoton(1, 'uno')
    await waitFor(() => expect(pendientes.uno).toBeDefined())
    await usuario.clear(campoDeBusqueda(1))
    await usuario.type(campoDeBusqueda(1), 'dos{Enter}')
    await waitFor(() => expect(pendientes.dos).toBeDefined())

    await act(async () => pendientes.dos.resolver(pagina([gaseosa])))
    await act(async () => pendientes.uno.resolver(pagina([pancho])))

    expect(opcionesDe(1).map((o) => o.textContent)).toEqual(['A2 — Gaseosa 500'])
  })

  // Cláusula que prueba: elegir invalida la búsqueda en vuelo.
  it('una respuesta que llega después de elegir un artículo no reabre la lista ni pisa lo elegido', async () => {
    const pendientes = mockearBusquedasDiferidas()
    await abrirNuevo()
    await buscarConBoton(1, 'uno')
    await waitFor(() => expect(pendientes.uno).toBeDefined())
    await act(async () => pendientes.uno.resolver(pagina([pancho, gaseosa])))
    await fila(1).findByRole('listbox')
    await usuario.type(campoDeBusqueda(1), 'x{Enter}')
    await waitFor(() => expect(pendientes.unox).toBeDefined())
    await usuario.keyboard('{ArrowDown}')
    await usuario.click(await opcionDe(1, 'A2 — Gaseosa 500'))
    await fila(1).findByText('Elegido:', { exact: false })

    await act(async () => pendientes.unox.resolver(pagina([pancho])))

    expect(fila(1).queryByRole('listbox')).not.toBeInTheDocument()
    expect(fila(1).getByText('Elegido:', { exact: false })).toHaveTextContent('Gaseosa 500')
    expect(fila(1).queryByText(/Se muestran los primeros|No hay artículos por unidad/)).not.toBeInTheDocument()
  })

  it('una respuesta que llega después de cerrar el modal no rompe nada ni aparece al reabrirlo', async () => {
    const errores = vi.spyOn(console, 'error').mockImplementation(() => {})
    const pendientes = mockearBusquedasDiferidas()
    render(<Combos />)
    await usuario.click(await screen.findByRole('button', { name: 'Agregar' }))
    await screen.findByRole('dialog')
    await buscarConBoton(1, 'uno')
    await waitFor(() => expect(pendientes.uno).toBeDefined())
    await usuario.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancelar' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    await act(async () => pendientes.uno.resolver(pagina([pancho])))

    await usuario.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByRole('dialog')
    expect(fila(1).queryByRole('listbox')).not.toBeInTheDocument()
    expect(errores).not.toHaveBeenCalled()
    errores.mockRestore()
  })
})

describe('Combos — asociación de errores y foco del editor', () => {
  function textoDe(elemento: HTMLElement) {
    const id = elemento.getAttribute('aria-describedby')
    return id === null ? null : (document.getElementById(id)?.textContent ?? null)
  }

  it('los errores de la fila quedan asociados a su tipo, a su buscador y a su cantidad', async () => {
    await abrirNuevo()
    await cambiarCantidad(1, '')
    await usuario.click(guardar())
    await fila(1).findByText('Elegí un artículo.')

    expect(textoDe(campoDeBusqueda(1))).toBe('Elegí un artículo.')
    expect(campoDeBusqueda(1)).toHaveAttribute('aria-invalid', 'true')
    expect(textoDe(fila(1).getByLabelText('Tipo del componente 1'))).toBe('Elegí un artículo.')
    expect(fila(1).getByLabelText('Tipo del componente 1')).toHaveAttribute('aria-invalid', 'true')
    expect(textoDe(fila(1).getByLabelText('Cantidad del componente 1'))).toBe('Ingresá la cantidad.')
    expect(fila(1).getByLabelText('Cantidad del componente 1')).toHaveAttribute('aria-invalid', 'true')
    expect(fila(2).getByLabelText('Cantidad del componente 2')).not.toHaveAttribute('aria-invalid')
    expect(fila(2).getByLabelText('Cantidad del componente 2')).not.toHaveAttribute('aria-describedby')
  })

  it('el select de familia sin elegir queda inválido y asociado a su mensaje', async () => {
    await abrirNuevo()
    await usuario.selectOptions(fila(2).getByLabelText('Tipo del componente 2'), 'Familia')
    await usuario.click(guardar())
    await fila(2).findByText('Elegí una familia.')

    const familia = fila(2).getByLabelText('Familia del componente 2')
    expect(familia).toHaveAttribute('aria-invalid', 'true')
    expect(textoDe(familia)).toBe('Elegí una familia.')
  })

  it('el error del conjunto queda asociado al grupo de componentes', async () => {
    await abrirNuevo()
    await usuario.type(screen.getByLabelText('Nombre'), 'Solo')
    await usuario.type(screen.getByLabelText('Precio del combo ($)'), '100')
    await elegirArticulo(1, 'pancho', 1)
    await usuario.click(screen.getByRole('button', { name: 'Quitar componente 2' }))
    await usuario.click(guardar())

    const grupo = await screen.findByRole('group', { name: 'Componentes' })
    await within(grupo).findByText('El combo tiene que sumar al menos 2 unidades entre todos sus componentes.')
    expect(textoDe(grupo)).toBe('El combo tiene que sumar al menos 2 unidades entre todos sus componentes.')
  })

  it('el error de componentes del servidor también queda asociado al grupo', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(400, 'combo_componentes_excedidos', 'x'))
    await abrirNuevo()
    await llenarAltaValida()
    await usuario.click(guardar())

    const grupo = screen.getByRole('group', { name: 'Componentes' })
    await within(grupo).findByText('Un combo admite hasta 50 componentes.')
    expect(textoDe(grupo)).toBe('Un combo admite hasta 50 componentes.')
  })

  it('el resumen no es una región viva: no se re-anuncia en cada tecla', async () => {
    await abrirNuevo()
    await elegirArticulo(1, 'pancho', 1)

    const resumen = await screen.findByText('1 × Pancho', { exact: false })

    expect(resumen.closest('[aria-live]')).toBeNull()
  })

  it('al quitar un componente el foco pasa al botón de quitar de la fila siguiente', async () => {
    await abrirNuevo()
    const quitarSegundo = screen.getByRole('button', { name: 'Quitar componente 2' })

    await usuario.click(screen.getByRole('button', { name: 'Quitar componente 1' }))

    expect(quitarSegundo).toHaveFocus()
    expect(quitarSegundo).toHaveAccessibleName('Quitar componente 1')
  })

  it('al quitar el último componente el foco pasa a Agregar componente', async () => {
    await abrirNuevo()

    await usuario.click(screen.getByRole('button', { name: 'Quitar componente 2' }))

    expect(screen.getByRole('button', { name: 'Agregar componente' })).toHaveFocus()
  })
})

describe('Combos — alta con resultado incierto', () => {
  const sinRespuesta = () => new ErrorApi(500, 'error_interno', 'boom')

  function leerCombos() {
    return apiGetMock.mock.calls.filter(([ruta]) => ruta === '/combos')
  }

  it('si tras el 5xx el combo ya figura en el listado cierra el formulario y avisa que se creó, sin reintentar', async () => {
    let lecturas = 0
    mockearApiGet({
      articulos: ARTICULOS,
      combos: () => {
        lecturas += 1
        return Promise.resolve(lecturas === 1 ? [] : [comboFixture({ id: 9, nombre: '  pancho Y coca ' })])
      },
    })
    apiPostMock.mockRejectedValue(sinRespuesta())
    await abrirNuevo()
    await llenarAltaValida()

    await usuario.click(guardar())

    expect(await screen.findByText('El combo se creó. No se pudo confirmar la respuesta del servidor.')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(apiPostMock).toHaveBeenCalledTimes(1)
    expect(screen.getByText('C12')).toBeInTheDocument()
  })

  it('un fallo sin respuesta también se resuelve releyendo el listado', async () => {
    let lecturas = 0
    mockearApiGet({
      articulos: ARTICULOS,
      combos: () => {
        lecturas += 1
        return Promise.resolve(lecturas === 1 ? [] : [comboFixture({ id: 9 })])
      },
    })
    apiPostMock.mockRejectedValue(new Error('sin red'))
    await abrirNuevo()
    await llenarAltaValida()

    await usuario.click(guardar())

    expect(await screen.findByText('El combo se creó. No se pudo confirmar la respuesta del servidor.')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('si el listado sigue sin el combo mantiene el formulario abierto con el aviso de resultado incierto', async () => {
    apiPostMock.mockRejectedValue(sinRespuesta())
    await abrirNuevo()
    await llenarAltaValida()

    await usuario.click(guardar())

    expect(await within(screen.getByRole('dialog')).findByRole('alert')).toHaveTextContent('No se pudo confirmar el resultado')
    expect(screen.getByRole('dialog')).toBeInTheDocument()
    await waitFor(() => expect(guardar()).toBeEnabled())
    expect(leerCombos()).toHaveLength(2)
    expect(screen.queryByText('El combo se creó. No se pudo confirmar la respuesta del servidor.')).not.toBeInTheDocument()
  })

  it('un combo del mismo nombre que ya estaba antes del intento no cuenta como creado', async () => {
    mockearApiGet({ articulos: ARTICULOS, combos: [comboFixture({ id: 4, nombre: 'Pancho y Coca' })] })
    apiPostMock.mockRejectedValue(sinRespuesta())
    render(<Combos />)
    await screen.findByText('C12')
    await usuario.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByRole('dialog')
    await llenarAltaValida()

    await usuario.click(guardar())

    expect(await within(screen.getByRole('dialog')).findByRole('alert')).toHaveTextContent('No se pudo confirmar el resultado')
    expect(screen.getByRole('dialog')).toBeInTheDocument()
    expect(screen.queryByText('El combo se creó. No se pudo confirmar la respuesta del servidor.')).not.toBeInTheDocument()
  })

  it('si la relectura falla mantiene el formulario abierto con el aviso de resultado incierto', async () => {
    let lecturas = 0
    mockearApiGet({
      articulos: ARTICULOS,
      combos: () => {
        lecturas += 1
        return lecturas === 1 ? Promise.resolve([]) : Promise.reject(new Error('sin red'))
      },
    })
    apiPostMock.mockRejectedValue(sinRespuesta())
    await abrirNuevo()
    await llenarAltaValida()

    await usuario.click(guardar())

    expect(await within(screen.getByRole('dialog')).findByRole('alert')).toHaveTextContent('No se pudo confirmar el resultado')
    expect(screen.getByRole('dialog')).toBeInTheDocument()
  })

  it('en una edición el 5xx sigue igual: aviso en el formulario, sin releer el listado', async () => {
    mockearApiGet({ combos: [comboFixture()] })
    apiPutMock.mockRejectedValue(sinRespuesta())
    render(<Combos />)
    await usuario.click(await screen.findByRole('button', { name: 'Editar Pancho y Coca' }))
    await screen.findByRole('dialog')

    await usuario.click(guardar())

    expect(await within(screen.getByRole('dialog')).findByRole('alert')).toHaveTextContent('No se pudo confirmar el resultado')
    expect(guardar()).toBeEnabled()
    expect(leerCombos()).toHaveLength(1)
  })
})

describe('Combos — rótulo de una empresa o familia que falta', () => {
  async function abrirEdicionDe(combo: ComboListado) {
    render(<Combos />)
    await usuario.click(await screen.findByRole('button', { name: `Editar ${combo.nombre}` }))
    await screen.findByRole('dialog')
  }

  const comboConFamilia = (idFamilia: number, disponible = true) =>
    comboFixture({
      idEmpresa: 9,
      componentes: [
        { idArticulo: 1, idFamilia: null, nombre: 'Pancho', cantidad: 1, disponible: true },
        { idArticulo: null, idFamilia, nombre: null, cantidad: 2, disponible },
      ],
    })

  it('con los listados caídos rotula por número, sin decir "no disponible"', async () => {
    mockearApiGet({ combos: [comboConFamilia(7)], familias: null, empresas: null })
    await abrirEdicionDe(comboConFamilia(7))
    await screen.findByText(/No se pudieron cargar las familias/)
    await screen.findByText(/No se pudieron cargar las empresas/)

    expect(screen.getByRole('option', { name: 'Empresa #9' })).toBeInTheDocument()
    expect(fila(2).getByRole('option', { name: 'Familia #7' })).toBeInTheDocument()
    expect(within(screen.getByRole('dialog')).queryByText(/no disponible/)).not.toBeInTheDocument()
  })

  it('con los listados cargados, un id que no figura se rotula "no disponible"', async () => {
    mockearApiGet({ combos: [comboConFamilia(8)] })
    await abrirEdicionDe(comboConFamilia(8))
    await screen.findByRole('option', { name: 'Alsina SA' })
    await fila(2).findByRole('option', { name: 'Línea Coca 500' })

    expect(screen.getByRole('option', { name: 'Empresa #9 (no disponible)' })).toBeInTheDocument()
    expect(fila(2).getByRole('option', { name: 'Familia #8 (no disponible)' })).toBeInTheDocument()
  })

  it('un componente que el servidor devolvió como no disponible lo dice aunque el listado haya caído', async () => {
    mockearApiGet({ combos: [comboConFamilia(7, false)], familias: null, empresas: null })
    await abrirEdicionDe(comboConFamilia(7, false))
    await screen.findByText(/No se pudieron cargar las familias/)

    expect(fila(2).getByRole('option', { name: 'Familia #7 (no disponible)' })).toBeInTheDocument()
    expect(fila(2).getByText('Este componente ya no está disponible: quitalo o reemplazalo para poder guardar.')).toBeInTheDocument()
  })
})
