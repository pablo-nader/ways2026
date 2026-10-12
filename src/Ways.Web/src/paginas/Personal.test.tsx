import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Personal } from './Personal'
import { ROL } from '../api/tipos'
import { ErrorApi } from '../api/cliente'
import type { ClienteListado, PaginaDe, PersonalListado, UsuarioListado } from '../api/tipos'

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

let rolActual: number = ROL.Admin

vi.mock('../auth/useAuth', () => ({
  useAuth: () => ({
    usuario: { id: 1, usuario: 'yo', mail: 'yo@ways.test', rolId: rolActual, rol: 'Rol', ultimaConexion: null, idTenant: 1 },
    cargando: false,
    iniciarSesion: vi.fn(),
    cerrarSesion: vi.fn(),
  }),
}))

function renderPersonal() {
  return render(<Personal />, { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> })
}

function personal(sobrescribir: Partial<PersonalListado> = {}): PersonalListado {
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
    ...sobrescribir,
  }
}

function usuario(id: number, nombre: string): UsuarioListado {
  return {
    id,
    usuario: nombre,
    mail: `${nombre}@ways.test`,
    rolId: ROL.Vendedor,
    rol: 'Vendedor',
    estado: 'Activo',
    ultimaConexion: null,
    createdAt: '2026-10-01T00:00:00Z',
    idTenant: 1,
    nombreTenant: 'Tenant',
  }
}

function cliente(id: number, nombre: string, sobrescribir: Partial<ClienteListado> = {}): ClienteListado {
  return {
    id,
    numero: id,
    nombre,
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
    creditoIlimitado: false,
    saldo: 0,
    activo: true,
    idEmpresa: null,
    esConsumidorFinal: false,
    ...sobrescribir,
  }
}

function pagina<T>(items: T[]): PaginaDe<T> {
  return { items, total: items.length, pagina: 1, tamanio: 200 }
}

let filas: PersonalListado[]

function mockearRutas() {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta.startsWith('/personal')) return Promise.resolve(pagina(filas))
    if (ruta.startsWith('/usuarios')) return Promise.resolve(pagina([usuario(11, 'ana'), usuario(12, 'luis')]))
    if (ruta.startsWith('/clientes')) {
      return Promise.resolve(
        pagina([cliente(21, 'Cliente Uno'), cliente(22, 'Consumidor final', { esConsumidorFinal: true })]),
      )
    }
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

type Usuario = ReturnType<typeof userEvent.setup>

/** Abre el formulario de alta. */
async function abrirAlta(usuarioEvento: Usuario) {
  await screen.findByText('Ana Gómez')
  await usuarioEvento.click(screen.getByRole('button', { name: 'Agregar' }))
  await screen.findByRole('dialog')
}

/** Elige una opción de un selector de vínculo esperando a los DATOS (la opción), no al combobox, que
 * se renderiza antes que su contenido. */
async function elegirVinculo(usuarioEvento: Usuario, campo: string, opcion: string) {
  await usuarioEvento.click(screen.getByRole('combobox', { name: campo }))
  await usuarioEvento.click(await screen.findByRole('option', { name: opcion }))
}

beforeEach(() => {
  rolActual = ROL.Admin
  filas = [personal()]
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiPutMock.mockReset()
  apiDeleteMock.mockReset()
  mockearRutas()
})

describe('Personal — listado', () => {
  it('muestra nombre, valor hora, saldo y los vínculos, con "sin vincular" donde no hay', async () => {
    filas = [
      personal({ id: 1, nombre: 'Ana Gómez', idUsuario: 11, nombreUsuario: 'ana', idCliente: null }),
      personal({ id: 2, nombre: 'Beto Ruiz', valorHora: 4000.5, saldo: -250, idCliente: 21, nombreCliente: 'Cliente Uno' }),
    ]
    renderPersonal()

    const ana = (await screen.findByText('Ana Gómez')).closest('tr') as HTMLElement
    const beto = screen.getByText('Beto Ruiz').closest('tr') as HTMLElement

    expect(within(ana).getByText('$ 3.500,00')).toBeInTheDocument()
    expect(within(ana).getByText('$ 12.000,00')).toBeInTheDocument()
    expect(within(ana).getByText('ana')).toBeInTheDocument()
    expect(within(ana).getAllByText('sin vincular')).toHaveLength(1)

    expect(within(beto).getByText('Cliente Uno')).toBeInTheDocument()
    expect(within(beto).getAllByText('sin vincular')).toHaveLength(1)
    expect(within(beto).getByText('$ 4.000,50')).toBeInTheDocument()
    expect(within(beto).getByText('-$ 250,00')).toBeInTheDocument()
  })

  it('cada fila lleva un enlace a la cuenta del empleado', async () => {
    renderPersonal()

    const enlace = await screen.findByRole('link', { name: 'Cuenta de Ana Gómez' })

    expect(enlace).toHaveAttribute('href', '/personal/1/cuenta')
  })

  it('sin filas muestra el vacío', async () => {
    filas = []
    renderPersonal()

    expect(await screen.findByText('No hay personal que coincida con la búsqueda.')).toBeInTheDocument()
  })

  it('avisa cuando la página no trae todo el resultado', async () => {
    apiGetMock.mockImplementation((ruta: string) =>
      ruta.startsWith('/personal')
        ? Promise.resolve({ items: filas, total: 250, pagina: 1, tamanio: 200 })
        : Promise.resolve(pagina([])),
    )
    renderPersonal()

    expect(await screen.findByText('Se muestran 1 de 250. Refiná la búsqueda para ver el resto.')).toBeInTheDocument()
  })

  it('buscar manda el término al servidor', async () => {
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await screen.findByText('Ana Gómez')

    await usuarioEvento.type(screen.getByLabelText('Buscar personal'), 'ana')
    await usuarioEvento.click(screen.getByRole('button', { name: 'Buscar' }))

    await waitFor(() => expect(apiGetMock).toHaveBeenCalledWith('/personal?busqueda=ana&tamanio=200'))
  })
})

describe('Personal — según el rol', () => {
  it('el Supervisor ve el listado y la cuenta, pero ningún control de escritura', async () => {
    rolActual = ROL.Supervisor
    renderPersonal()

    await screen.findByText('Ana Gómez')

    expect(screen.getByRole('link', { name: 'Cuenta de Ana Gómez' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Agregar' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Editar Ana Gómez' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Eliminar Ana Gómez' })).not.toBeInTheDocument()
  })

  it('el Supervisor no pide usuarios ni clientes, que la API le negaría', async () => {
    rolActual = ROL.Supervisor
    renderPersonal()
    await screen.findByText('Ana Gómez')

    const rutas = apiGetMock.mock.calls.map((c) => String(c[0]))

    expect(rutas.some((r) => r.startsWith('/usuarios'))).toBe(false)
    expect(rutas.some((r) => r.startsWith('/clientes'))).toBe(false)
  })

  it('el Admin ve agregar, editar y eliminar', async () => {
    renderPersonal()

    await screen.findByText('Ana Gómez')

    expect(screen.getByRole('button', { name: 'Agregar' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Editar Ana Gómez' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Eliminar Ana Gómez' })).toBeInTheDocument()
  })
})

describe('Personal — alta', () => {
  it('crea un personal con ambos vínculos, manda el cuerpo del DTO y refresca el listado', async () => {
    apiPostMock.mockResolvedValue(personal({ id: 2, nombre: 'Carla' }))
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await abrirAlta(usuarioEvento)

    await usuarioEvento.type(screen.getByLabelText('Nombre'), '  Carla  ')
    await usuarioEvento.type(screen.getByLabelText('Valor hora'), '1500')
    await usuarioEvento.type(screen.getByLabelText('Documento'), '30111222')
    await elegirVinculo(usuarioEvento, 'Usuario vinculado', 'ana (ana@ways.test)')
    await elegirVinculo(usuarioEvento, 'Cliente vinculado', '#21 — Cliente Uno')
    filas = [personal(), personal({ id: 2, nombre: 'Carla' })]
    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() =>
      expect(apiPostMock).toHaveBeenCalledWith('/personal', {
        nombre: 'Carla',
        documento: '30111222',
        telefono: null,
        observaciones: null,
        valorHora: 1500,
        idEmpresa: null,
        idUsuario: 11,
        idCliente: 21,
      }),
    )
    expect(await screen.findByText('Personal "Carla" creado.')).toBeInTheDocument()
    expect(await screen.findByText('Carla')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('no ofrece al consumidor final como cliente vinculable', async () => {
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await abrirAlta(usuarioEvento)

    await usuarioEvento.click(screen.getByRole('combobox', { name: 'Cliente vinculado' }))
    await screen.findByRole('option', { name: '#21 — Cliente Uno' })

    expect(screen.queryByRole('option', { name: /Consumidor final/ })).not.toBeInTheDocument()
  })

  it('un cliente fuera de la primera página se encuentra buscándolo en el servidor y se vincula', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta.startsWith('/personal')) return Promise.resolve(pagina(filas))
      if (ruta.startsWith('/usuarios')) return Promise.resolve(pagina([]))
      if (ruta === '/clientes?busqueda=paz&tamanio=200') return Promise.resolve(pagina([cliente(77, 'Paz Lejana')]))
      if (ruta.startsWith('/clientes')) return Promise.resolve(pagina([cliente(21, 'Cliente Uno')]))
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })
    apiPostMock.mockResolvedValue(personal({ id: 2 }))
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await abrirAlta(usuarioEvento)

    await usuarioEvento.type(screen.getByLabelText('Nombre'), 'Carla')
    await usuarioEvento.type(screen.getByLabelText('Valor hora'), '100')
    await usuarioEvento.type(screen.getByRole('combobox', { name: 'Cliente vinculado' }), 'paz')
    await usuarioEvento.click(await screen.findByRole('option', { name: '#77 — Paz Lejana' }))
    expect(screen.getByRole('combobox', { name: 'Cliente vinculado' })).toHaveValue('#77 — Paz Lejana')
    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/personal', expect.objectContaining({ idCliente: 77 })))
  })

  it('sin nombre no llama a la API y marca el campo', async () => {
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await abrirAlta(usuarioEvento)

    await usuarioEvento.type(screen.getByLabelText('Valor hora'), '10')
    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('Ingresá el nombre.')).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('un 409 de usuario ya vinculado se muestra en el campo usuario y la modal sigue abierta', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'usuario_ya_vinculado', 'Ese usuario ya está vinculado a otro personal.'))
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await abrirAlta(usuarioEvento)

    await usuarioEvento.type(screen.getByLabelText('Nombre'), 'Carla')
    await usuarioEvento.type(screen.getByLabelText('Valor hora'), '100')
    await elegirVinculo(usuarioEvento, 'Usuario vinculado', 'ana (ana@ways.test)')
    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    const alerta = await screen.findByText('Ese usuario ya está vinculado a otro personal.')
    const campoUsuario = screen.getByRole('combobox', { name: 'Usuario vinculado' })
    const campoCliente = screen.getByRole('combobox', { name: 'Cliente vinculado' })

    expect(campoUsuario).toHaveClass('is-invalid')
    expect(campoUsuario.closest('.col-md-4')).toContainElement(alerta)
    expect(campoCliente).not.toHaveClass('is-invalid')
    expect(screen.getByRole('dialog')).toBeInTheDocument()
  })

  it('un 409 de cliente ya vinculado se muestra en el campo cliente', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'cliente_ya_vinculado', 'texto del servidor'))
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await abrirAlta(usuarioEvento)

    await usuarioEvento.type(screen.getByLabelText('Nombre'), 'Carla')
    await usuarioEvento.type(screen.getByLabelText('Valor hora'), '100')
    await elegirVinculo(usuarioEvento, 'Cliente vinculado', '#21 — Cliente Uno')
    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    const alerta = await screen.findByText('Ese cliente ya está vinculado a otro personal.')
    const campoCliente = screen.getByRole('combobox', { name: 'Cliente vinculado' })

    expect(campoCliente).toHaveClass('is-invalid')
    expect(campoCliente.closest('.col-md-4')).toContainElement(alerta)
    expect(screen.getByRole('combobox', { name: 'Usuario vinculado' })).not.toHaveClass('is-invalid')
  })

  it('un código sin campo propio se muestra arriba del formulario', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(400, 'referencia_invalida', 'No existe el cliente 21.'))
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await abrirAlta(usuarioEvento)

    await usuarioEvento.type(screen.getByLabelText('Nombre'), 'Carla')
    await usuarioEvento.type(screen.getByLabelText('Valor hora'), '100')
    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    const alerta = await screen.findByText(
      'Alguno de los datos elegidos ya no existe. Actualizá la pantalla e intentá de nuevo.',
    )

    expect(alerta).toHaveAttribute('role', 'alert')
  })
})

describe('Personal — edición', () => {
  it('conserva la empresa, manda el id al PUT y permite quitar los dos vínculos', async () => {
    filas = [personal({ idEmpresa: 4, idUsuario: 11, nombreUsuario: 'ana', idCliente: 21, nombreCliente: 'Cliente Uno' })]
    apiPutMock.mockResolvedValue(personal())
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await screen.findByText('Ana Gómez')

    await usuarioEvento.click(screen.getByRole('button', { name: 'Editar Ana Gómez' }))
    // Los vínculos se muestran con el nombre de la fila y, cuando llegan los catálogos, con su etiqueta.
    const campoUsuario = screen.getByRole('combobox', { name: 'Usuario vinculado' })
    const campoCliente = screen.getByRole('combobox', { name: 'Cliente vinculado' })
    await waitFor(() => expect(campoUsuario).toHaveValue('ana (ana@ways.test)'))
    await waitFor(() => expect(campoCliente).toHaveValue('#21 — Cliente Uno'))

    await usuarioEvento.click(screen.getByRole('button', { name: 'Quitar usuario vinculado' }))
    await usuarioEvento.click(screen.getByRole('button', { name: 'Quitar cliente vinculado' }))
    expect(campoUsuario).toHaveValue('')
    expect(campoCliente).toHaveValue('')
    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() =>
      expect(apiPutMock).toHaveBeenCalledWith('/personal/1', {
        nombre: 'Ana Gómez',
        documento: null,
        telefono: null,
        observaciones: null,
        valorHora: 3500,
        idEmpresa: 4,
        idUsuario: null,
        idCliente: null,
      }),
    )
    expect(await screen.findByText('Personal "Ana Gómez" actualizado.')).toBeInTheDocument()
  })
})

describe('Personal — baja', () => {
  it('pide confirmación y, al confirmar, da de baja y refresca', async () => {
    apiDeleteMock.mockResolvedValue(undefined)
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await screen.findByText('Ana Gómez')

    await usuarioEvento.click(screen.getByRole('button', { name: 'Eliminar Ana Gómez' }))
    expect(apiDeleteMock).not.toHaveBeenCalled()
    filas = []
    await usuarioEvento.click(await screen.findByRole('button', { name: 'Confirmar baja' }))

    await waitFor(() => expect(apiDeleteMock).toHaveBeenCalledWith('/personal/1'))
    expect(await screen.findByText('Personal "Ana Gómez" dado de baja.')).toBeInTheDocument()
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
  })

  it('un 409 personal_en_uso explica que el empleado tiene movimientos', async () => {
    apiDeleteMock.mockRejectedValue(new ErrorApi(409, 'personal_en_uso', 'texto genérico del servidor'))
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await screen.findByText('Ana Gómez')

    await usuarioEvento.click(screen.getByRole('button', { name: 'Eliminar Ana Gómez' }))
    await usuarioEvento.click(await screen.findByRole('button', { name: 'Confirmar baja' }))

    expect(
      await screen.findByText(
        'No se puede dar de baja a "Ana Gómez": tiene movimientos registrados en su cuenta (horas, pagos o ajustes).',
      ),
    ).toBeInTheDocument()
  })
})
