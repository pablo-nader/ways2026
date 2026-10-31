import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, useLocation } from 'react-router'
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

/** Sonda de la ruta actual: es la única forma de ver si un `<Link>` navegó. */
function SondaDeRuta() {
  const { pathname } = useLocation()

  return <span data-testid="ruta">{pathname}</span>
}

function renderPersonal() {
  return render(
    <>
      <Personal />
      <SondaDeRuta />
    </>,
    { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> },
  )
}

/** Promesa que el test resuelve o rechaza a mano. */
function diferido<T>() {
  let resolver!: (valor: T) => void
  let rechazar!: (motivo: unknown) => void
  const promesa = new Promise<T>((res, rej) => {
    resolver = res
    rechazar = rej
  })

  return { promesa, resolver, rechazar }
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
    horarioHabitual: null,
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

/** `listar` atiende los pedidos a `/personal`; por defecto devuelve `filas`. */
function mockearRutas(listar: (ruta: string) => Promise<PaginaDe<PersonalListado>> = () => Promise.resolve(pagina(filas))) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta.startsWith('/personal')) return listar(ruta)
    if (ruta.startsWith('/usuarios')) return Promise.resolve(pagina([usuario(11, 'ana'), usuario(12, 'luis')]))
    if (ruta.startsWith('/clientes')) {
      return Promise.resolve(
        pagina([cliente(21, 'Cliente Uno'), cliente(22, 'Consumidor final', { esConsumidorFinal: true })]),
      )
    }
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

function rutasDePersonal() {
  return apiGetMock.mock.calls.map(([ruta]) => String(ruta)).filter((r) => r.startsWith('/personal'))
}

type Usuario = ReturnType<typeof userEvent.setup>

/** Abre el formulario de alta. */
async function abrirAlta(usuarioEvento: Usuario) {
  await screen.findByText('Ana Gómez')
  await usuarioEvento.click(screen.getByRole('button', { name: 'Agregar' }))
  await screen.findByRole('dialog')
}

/** Completa los dos campos obligatorios del alta. */
async function llenarAlta(usuarioEvento: Usuario, nombre = 'Carla') {
  await usuarioEvento.type(screen.getByLabelText('Nombre'), nombre)
  await usuarioEvento.type(screen.getByLabelText('Valor hora'), '100')
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

    expect(await screen.findByText('No hay personal que coincida con los filtros.')).toBeInTheDocument()
  })

  it('el listado ya no pide la página más grande: la grilla pide la primera con el tamaño del servidor', async () => {
    renderPersonal()
    await screen.findByText('Ana Gómez')

    expect(rutasDePersonal()).toEqual(['/personal'])
  })

  it('buscar manda el término al servidor', async () => {
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await screen.findByText('Ana Gómez')

    await usuarioEvento.type(screen.getByLabelText('Buscar personal'), 'ana')
    await usuarioEvento.click(screen.getByRole('button', { name: 'Buscar' }))

    await waitFor(() => expect(apiGetMock).toHaveBeenCalledWith('/personal?busqueda=ana'))
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
        horarioHabitual: null,
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
        horarioHabitual: null,
      }),
    )
    expect(await screen.findByText('Personal "Ana Gómez" actualizado.')).toBeInTheDocument()
  })
})

describe('Personal — horario habitual', () => {
  const dias = ['Lun', 'Mar', 'Mié', 'Jue', 'Vie', 'Sáb', 'Dom']

  async function abrirEdicion(usuarioEvento: Usuario) {
    renderPersonal()
    await screen.findByText('Ana Gómez')
    await usuarioEvento.click(screen.getByRole('button', { name: 'Editar Ana Gómez' }))
  }

  it('al editar muestra el horario existente día por día', async () => {
    filas = [personal({ horarioHabitual: [8, 8, 7.5, 8, 8, 4, 0] })]
    const usuarioEvento = userEvent.setup()
    await abrirEdicion(usuarioEvento)

    const valores = dias.map((d) => (screen.getByLabelText(d) as HTMLInputElement).value)
    expect(valores).toEqual(['8', '8', '7,5', '8', '8', '4', '0'])
  })

  it('un horario sin cargar queda vacío y se guarda como null', async () => {
    filas = [personal()]
    apiPutMock.mockResolvedValue(personal())
    const usuarioEvento = userEvent.setup()
    await abrirEdicion(usuarioEvento)

    for (const d of dias) expect(screen.getByLabelText(d)).toHaveValue('')
    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledWith('/personal/1', expect.objectContaining({ horarioHabitual: null })))
  })

  it('al cargar algunos días, los vacíos viajan como franco y acepta coma decimal', async () => {
    filas = [personal()]
    apiPutMock.mockResolvedValue(personal())
    const usuarioEvento = userEvent.setup()
    await abrirEdicion(usuarioEvento)

    await usuarioEvento.type(screen.getByLabelText('Lun'), '8')
    await usuarioEvento.type(screen.getByLabelText('Mié'), '7,5')
    await usuarioEvento.type(screen.getByLabelText('Sáb'), '4.25')
    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() =>
      expect(apiPutMock).toHaveBeenCalledWith(
        '/personal/1',
        expect.objectContaining({ horarioHabitual: [8, 0, 7.5, 0, 0, 4.25, 0] }),
      ),
    )
  })

  it('un valor mayor a 24 marca el horario y no manda nada', async () => {
    filas = [personal()]
    const usuarioEvento = userEvent.setup()
    await abrirEdicion(usuarioEvento)

    await usuarioEvento.type(screen.getByLabelText('Mar'), '25')
    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText(/Cada día del horario habitual/)).toBeInTheDocument()
    expect(apiPutMock).not.toHaveBeenCalled()
  })

  it('el rechazo horario_habitual_invalido del servidor se muestra legible', async () => {
    filas = [personal()]
    apiPutMock.mockRejectedValue(new ErrorApi(400, 'horario_habitual_invalido', 'inválido'))
    const usuarioEvento = userEvent.setup()
    await abrirEdicion(usuarioEvento)

    await usuarioEvento.type(screen.getByLabelText('Lun'), '8')
    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText(/Cada día del horario habitual/)).toBeInTheDocument()
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

describe('Personal — semántica de la edición', () => {
  /** Los tres ids viajan tal como vinieron de la fila: pairwise distintos para que un cruce se vea. */
  it('guardar sin tocar los vínculos manda el idUsuario, el idCliente y el idEmpresa originales', async () => {
    filas = [personal({ idEmpresa: 4, idUsuario: 12, nombreUsuario: 'luis', idCliente: 21, nombreCliente: 'Cliente Uno' })]
    apiPutMock.mockResolvedValue(personal())
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await screen.findByText('Ana Gómez')

    await usuarioEvento.click(screen.getByRole('button', { name: 'Editar Ana Gómez' }))
    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() =>
      expect(apiPutMock).toHaveBeenCalledWith('/personal/1', {
        nombre: 'Ana Gómez',
        documento: null,
        telefono: null,
        observaciones: null,
        valorHora: 3500,
        idEmpresa: 4,
        idUsuario: 12,
        idCliente: 21,
        horarioHabitual: null,
      }),
    )
  })

  it('un usuario fuera de la primera página se encuentra buscándolo en el servidor y se vincula', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta.startsWith('/personal')) return Promise.resolve(pagina(filas))
      if (ruta === '/usuarios?busqueda=paz&tamanio=200') return Promise.resolve(pagina([usuario(77, 'paz')]))
      if (ruta.startsWith('/usuarios')) return Promise.resolve(pagina([usuario(11, 'ana')]))
      if (ruta.startsWith('/clientes')) return Promise.resolve(pagina([cliente(21, 'Cliente Uno')]))
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })
    apiPostMock.mockResolvedValue(personal({ id: 2 }))
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await abrirAlta(usuarioEvento)

    await llenarAlta(usuarioEvento)
    await usuarioEvento.type(screen.getByRole('combobox', { name: 'Usuario vinculado' }), 'paz')
    await usuarioEvento.click(await screen.findByRole('option', { name: 'paz (paz@ways.test)' }))
    expect(screen.getByRole('combobox', { name: 'Usuario vinculado' })).toHaveValue('paz (paz@ways.test)')
    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/personal', expect.objectContaining({ idUsuario: 77 })))
  })
})

describe('Personal — esqueleto asíncrono', () => {
  /** Cláusula bajo prueba: `ocupadoRef` en `guardar`, la guarda de re-entrancia del mismo tick. */
  it('dos clics sincrónicos sobre Guardar emiten una sola request', async () => {
    apiPostMock.mockImplementation(() => new Promise(() => {}))
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await abrirAlta(usuarioEvento)
    await llenarAlta(usuarioEvento)

    const guardar = screen.getByRole('button', { name: 'Guardar' })
    await act(async () => {
      guardar.click()
      guardar.click()
      await Promise.resolve()
    })

    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })

  /** Cláusula bajo prueba: `ocupadoRef` en `confirmarBaja`. */
  it('dos clics sincrónicos sobre la confirmación de baja emiten un solo DELETE', async () => {
    apiDeleteMock.mockImplementation(() => new Promise<void>(() => {}))
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await screen.findByText('Ana Gómez')

    await usuarioEvento.click(screen.getByRole('button', { name: 'Eliminar Ana Gómez' }))
    const confirmar = screen.getByRole('button', { name: 'Confirmar baja' })
    await act(async () => {
      confirmar.click()
      confirmar.click()
      await Promise.resolve()
    })

    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
  })

  /** Cláusula bajo prueba: `bloqueado` cubre el DELETE y su refresco, no solo la request. */
  it('durante el DELETE y su refresco no queda ninguna acción alcanzable', async () => {
    const usuarioEvento = userEvent.setup()
    const beto = personal({ id: 2, nombre: 'Beto Ruiz' })
    filas = [personal(), beto]
    const delete_ = diferido<void>()
    const refresco = diferido<PaginaDe<PersonalListado>>()
    let cargas = 0
    mockearRutas(() => (++cargas === 1 ? Promise.resolve(pagina(filas)) : refresco.promesa))
    apiDeleteMock.mockReturnValue(delete_.promesa)
    renderPersonal()
    await screen.findByText('Ana Gómez')

    await usuarioEvento.click(screen.getByRole('button', { name: 'Eliminar Ana Gómez' }))
    await usuarioEvento.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    expect(screen.getByRole('button', { name: 'Dando de baja…' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Agregar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Buscar' })).toBeDisabled()
    expect(screen.getByLabelText('Buscar personal')).toBeDisabled()
    for (const nombre of ['Ana Gómez', 'Beto Ruiz']) {
      expect(screen.getByRole('button', { name: `Editar ${nombre}` })).toBeDisabled()
      expect(screen.getByRole('button', { name: `Eliminar ${nombre}` })).toBeDisabled()
      expect(screen.getByRole('link', { name: `Cuenta de ${nombre}` })).toHaveAttribute('aria-disabled', 'true')
    }

    await act(async () => {
      delete_.resolver()
      await delete_.promesa
    })

    expect(await screen.findByText('Personal "Ana Gómez" dado de baja.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Agregar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Buscar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Editar Beto Ruiz' })).toBeDisabled()
    expect(screen.getByLabelText('Filtrar por nombre')).toBeDisabled()

    await act(async () => {
      refresco.resolver(pagina([beto]))
      await refresco.promesa
    })

    expect(await screen.findByText('Beto Ruiz')).toBeInTheDocument()
    expect(screen.queryByText('Ana Gómez')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Agregar' })).toBeEnabled()
    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
  })

  /** Cláusula bajo prueba: la rama `catch` de `refrescarTrasEscribir` con `AVISO_REFRESCO_FALLIDO`. Un
   * guardado que ya commiteó nunca se reporta como fallido, aunque el refresco posterior explote. */
  it('un refresco fallido después de guardar avisa que se guardó y cierra la modal', async () => {
    let cargas = 0
    mockearRutas(() =>
      ++cargas === 1 ? Promise.resolve(pagina(filas)) : Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó.')),
    )
    apiPostMock.mockResolvedValue(personal({ id: 2 }))
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await abrirAlta(usuarioEvento)

    await llenarAlta(usuarioEvento)
    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(
      await screen.findByText(
        'Personal "Carla" creado. Se guardó, pero no se pudo actualizar la vista. Recargá la pantalla.',
      ),
    ).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.queryByText('Se cayó.')).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: la rama `catch` de `refrescarTrasEscribir` con `AVISO_REFRESCO_FALLIDO_BAJA`. */
  it('un refresco fallido después de la baja avisa que se dio de baja', async () => {
    let cargas = 0
    mockearRutas(() =>
      ++cargas === 1 ? Promise.resolve(pagina(filas)) : Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó.')),
    )
    apiDeleteMock.mockResolvedValue(undefined)
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await screen.findByText('Ana Gómez')

    await usuarioEvento.click(screen.getByRole('button', { name: 'Eliminar Ana Gómez' }))
    await usuarioEvento.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    expect(
      await screen.findByText(
        'Personal "Ana Gómez" dado de baja. Se dio de baja, pero no se pudo actualizar la vista. Recargá la pantalla.',
      ),
    ).toBeInTheDocument()
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
  })

  it('el error de un guardado anterior no sobrevive a cancelar y reabrir', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(400, 'referencia_invalida', 'No existe el cliente 21.'))
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await abrirAlta(usuarioEvento)
    await llenarAlta(usuarioEvento)
    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))
    await screen.findByText('Alguno de los datos elegidos ya no existe. Actualizá la pantalla e intentá de nuevo.')

    await usuarioEvento.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancelar' }))
    await usuarioEvento.click(screen.getByRole('button', { name: 'Agregar' }))

    await screen.findByRole('dialog')
    expect(screen.getByLabelText('Nombre')).toHaveValue('')
    expect(
      screen.queryByText('Alguno de los datos elegidos ya no existe. Actualizá la pantalla e intentá de nuevo.'),
    ).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `ocupado={bloqueado}` de la modal. */
  it('con el guardado en vuelo la modal no se cierra ni con la × ni con Escape', async () => {
    apiPostMock.mockImplementation(() => new Promise(() => {}))
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await abrirAlta(usuarioEvento)
    await llenarAlta(usuarioEvento)

    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(screen.getByRole('button', { name: 'Guardando…' })).toBeDisabled()
    expect(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cerrar' })).toBeDisabled()
    await usuarioEvento.keyboard('{Escape}')
    expect(screen.getByRole('dialog', { name: 'Nuevo personal' })).toBeInTheDocument()
  })

  it('un 404 al guardar dice que el personal ya no existe y deja la modal abierta', async () => {
    apiPutMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'texto del servidor'))
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await screen.findByText('Ana Gómez')

    await usuarioEvento.click(screen.getByRole('button', { name: 'Editar Ana Gómez' }))
    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('El personal ya no existe. Actualizá la pantalla.')).toBeInTheDocument()
    expect(screen.getByRole('dialog', { name: 'Editar personal' })).toBeInTheDocument()
  })
})

describe('Personal — cada acción borra lo que dijo la anterior', () => {
  /** Cláusula bajo prueba: `setErrorFormulario(null)` de `guardar`, con el segundo POST pendiente. */
  it('reintentar un guardado rechazado borra el rechazo mientras la nueva request está en vuelo', async () => {
    apiPostMock
      .mockRejectedValueOnce(new ErrorApi(400, 'referencia_invalida', 'No existe el cliente 21.'))
      .mockImplementationOnce(() => new Promise(() => {}))
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await abrirAlta(usuarioEvento)
    await llenarAlta(usuarioEvento)
    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))
    await screen.findByText('Alguno de los datos elegidos ya no existe. Actualizá la pantalla e intentá de nuevo.')

    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(apiPostMock).toHaveBeenCalledTimes(2)
    expect(
      screen.queryByText('Alguno de los datos elegidos ya no existe. Actualizá la pantalla e intentá de nuevo.'),
    ).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `setError('')` de `confirmarBaja`, con el segundo DELETE pendiente. */
  it('reintentar una baja rechazada borra el motivo mientras el nuevo DELETE está en vuelo', async () => {
    apiDeleteMock
      .mockRejectedValueOnce(new ErrorApi(409, 'personal_en_uso', 'texto genérico del servidor'))
      .mockImplementationOnce(() => new Promise<void>(() => {}))
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await screen.findByText('Ana Gómez')
    await usuarioEvento.click(screen.getByRole('button', { name: 'Eliminar Ana Gómez' }))
    await usuarioEvento.click(screen.getByRole('button', { name: 'Confirmar baja' }))
    await screen.findByText(/tiene movimientos registrados en su cuenta/)

    await usuarioEvento.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    expect(apiDeleteMock).toHaveBeenCalledTimes(2)
    expect(screen.queryByText(/tiene movimientos registrados en su cuenta/)).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `setError('')` de `cancelarBaja`. */
  it('cancelar después de una baja rechazada se lleva el motivo con la puerta', async () => {
    apiDeleteMock.mockRejectedValue(new ErrorApi(409, 'personal_en_uso', 'texto genérico del servidor'))
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await screen.findByText('Ana Gómez')
    await usuarioEvento.click(screen.getByRole('button', { name: 'Eliminar Ana Gómez' }))
    await usuarioEvento.click(screen.getByRole('button', { name: 'Confirmar baja' }))
    await screen.findByText(/tiene movimientos registrados en su cuenta/)

    await usuarioEvento.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.queryByText(/tiene movimientos registrados en su cuenta/)).not.toBeInTheDocument()
  })

  /** Cláusulas bajo prueba: el `setAviso('')` de `abrirFormulario` (Agregar y Editar comparten la
   * función) y el de `pedirBaja`. */
  it.each([
    ['Agregar', 'dialog'],
    ['Editar Ana Gómez', 'dialog'],
    ['Eliminar Ana Gómez', 'alertdialog'],
  ] as const)('el aviso de un guardado anterior no sobrevive a "%s"', async (boton, rol) => {
    apiPostMock.mockResolvedValue(personal({ id: 2 }))
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await abrirAlta(usuarioEvento)
    await llenarAlta(usuarioEvento)
    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))
    await screen.findByText('Personal "Carla" creado.')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Agregar' })).toBeEnabled())

    await usuarioEvento.click(screen.getByRole('button', { name: boton }))

    await screen.findByRole(rol)
    expect(screen.queryByText('Personal "Carla" creado.')).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: el `setError('')` de `abrirFormulario`. */
  it('el error de una lectura fallida no sobrevive a abrir el formulario de alta', async () => {
    mockearRutas(() => Promise.reject(new Error('sin red')))
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await screen.findByText('No se pudo cargar el personal.')

    await usuarioEvento.click(screen.getByRole('button', { name: 'Agregar' }))

    await screen.findByRole('dialog')
    expect(screen.queryByText('No se pudo cargar el personal.')).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: el `setError('')` de `guardar`. La lectura inicial cae con la modal ya
   * abierta, así que su error está en pantalla cuando empieza el guardado. */
  it('el error de una lectura que cayó con el formulario abierto se borra al guardar', async () => {
    const inicial = diferido<PaginaDe<PersonalListado>>()
    mockearRutas(() => inicial.promesa)
    apiPostMock.mockImplementation(() => new Promise(() => {}))
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await usuarioEvento.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByRole('dialog')
    await llenarAlta(usuarioEvento)
    await act(async () => {
      inicial.rechazar(new Error('sin red'))
      await inicial.promesa.catch(() => {})
    })
    await screen.findByText('No se pudo cargar el personal.')

    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(apiPostMock).toHaveBeenCalledTimes(1)
    expect(screen.queryByText('No se pudo cargar el personal.')).not.toBeInTheDocument()
  })
})

describe('Personal — respuestas tardías del listado', () => {
  /** Dos lecturas: la inicial (pendiente) y la de un Buscar posterior, ambas en manos del test. */
  function dosLecturas() {
    const primera = diferido<PaginaDe<PersonalListado>>()
    const segunda = diferido<PaginaDe<PersonalListado>>()
    let llamadas = 0
    mockearRutas(() => (++llamadas === 1 ? primera.promesa : segunda.promesa))

    return { primera, segunda }
  }

  async function buscarConLaInicialEnVuelo() {
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await usuarioEvento.type(screen.getByLabelText('Buscar personal'), 'ana')
    await usuarioEvento.click(screen.getByRole('button', { name: 'Buscar' }))
    expect(apiGetMock.mock.calls.filter((c) => String(c[0]).startsWith('/personal'))).toHaveLength(2)
  }

  /** Cláusula bajo prueba: la guarda de generación tras el `await` de `cargar` (rama de éxito). */
  it('una respuesta vieja que llega después de la nueva no pisa la pantalla', async () => {
    const { primera, segunda } = dosLecturas()
    await buscarConLaInicialEnVuelo()
    await act(async () => {
      segunda.resolver(pagina([personal({ id: 2, nombre: 'Beto Ruiz' })]))
      await segunda.promesa
    })
    expect(screen.getByText('Beto Ruiz')).toBeInTheDocument()

    await act(async () => {
      primera.resolver(pagina([personal()]))
      await primera.promesa
    })

    expect(screen.getByText('Beto Ruiz')).toBeInTheDocument()
    expect(screen.queryByText('Ana Gómez')).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: la guarda de generación de la rama `catch` de `cargar`. */
  it('el rechazo de una respuesta vieja no deja un error en pantalla', async () => {
    const { primera } = dosLecturas()
    await buscarConLaInicialEnVuelo()

    await act(async () => {
      primera.rechazar(new Error('sin red'))
      await primera.promesa.catch(() => {})
    })

    expect(screen.queryByText('No se pudo cargar el personal.')).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: la guarda de generación del `finally` de `cargar`. */
  it('una respuesta vieja no apaga el "Cargando…" de la lectura nueva', async () => {
    const { primera } = dosLecturas()
    await buscarConLaInicialEnVuelo()

    await act(async () => {
      primera.resolver(pagina([personal()]))
      await primera.promesa
    })

    expect(screen.getByText('Cargando…')).toBeInTheDocument()
    expect(screen.queryByText('Ana Gómez')).not.toBeInTheDocument()
  })
})

describe('Personal — lectura en vuelo al guardar y término aplicado', () => {
  /** Cláusula bajo prueba: la relectura de la rama de fallo de `guardar` (`habiaLecturaEnVuelo`). */
  it('un guardado rechazado con la lectura inicial en vuelo no deja la tabla en el spinner', async () => {
    const inicial = diferido<PaginaDe<PersonalListado>>()
    const post = diferido<PersonalListado>()
    let llamadas = 0
    mockearRutas(() => (++llamadas === 1 ? inicial.promesa : Promise.resolve(pagina(filas))))
    apiPostMock.mockReturnValue(post.promesa)
    const usuarioEvento = userEvent.setup()
    renderPersonal()

    await usuarioEvento.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByRole('dialog')
    await llenarAlta(usuarioEvento)
    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))
    await act(async () => {
      inicial.resolver(pagina(filas))
      await inicial.promesa
    })
    await act(async () => {
      post.rechazar(new ErrorApi(409, 'usuario_ya_vinculado', 'texto del servidor'))
      await post.promesa.catch(() => {})
    })

    expect(await screen.findByText('Ana Gómez')).toBeInTheDocument()
    expect(screen.queryByText('Cargando…')).not.toBeInTheDocument()
    expect(llamadas).toBe(2)
    expect(screen.getByRole('dialog', { name: 'Nuevo personal' })).toBeInTheDocument()
  })

  /** Cláusula bajo prueba: la condición `habiaLecturaEnVuelo` de esa relectura. */
  it('un guardado rechazado con el listado ya cargado no lo vuelve a pedir', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'usuario_ya_vinculado', 'Ese usuario ya está vinculado a otro personal.'))
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await abrirAlta(usuarioEvento)
    await llenarAlta(usuarioEvento)

    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    await screen.findByText('Ese usuario ya está vinculado a otro personal.')
    expect(apiGetMock.mock.calls.filter((c) => String(c[0]).startsWith('/personal'))).toHaveLength(1)
    expect(screen.getByText('Ana Gómez')).toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `terminoVigente` en el refresco posterior a la escritura. El texto tipeado
   * y todavía no enviado no viaja; el último término enviado, sí. */
  it.each([
    { enviado: '', tipeado: 'zzz', esperada: '/personal' },
    { enviado: 'luis', tipeado: 'luis zzz', esperada: '/personal?busqueda=luis' },
  ])('el refresco tras guardar recarga con el término aplicado "$enviado", no con "$tipeado"', async ({ enviado, tipeado, esperada }) => {
    apiPostMock.mockResolvedValue(personal({ id: 2 }))
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await screen.findByText('Ana Gómez')
    if (enviado) {
      await usuarioEvento.type(screen.getByLabelText('Buscar personal'), enviado)
      await usuarioEvento.click(screen.getByRole('button', { name: 'Buscar' }))
      await waitFor(() => expect(apiGetMock).toHaveBeenCalledWith(esperada))
      await screen.findByText('Ana Gómez')
    }
    await usuarioEvento.clear(screen.getByLabelText('Buscar personal'))
    await usuarioEvento.type(screen.getByLabelText('Buscar personal'), tipeado)
    await usuarioEvento.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByRole('dialog')
    await llenarAlta(usuarioEvento)
    const antes = apiGetMock.mock.calls.filter((c) => String(c[0]).startsWith('/personal')).length

    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() =>
      expect(apiGetMock.mock.calls.filter((c) => String(c[0]).startsWith('/personal'))).toHaveLength(antes + 1),
    )
    const rutas = apiGetMock.mock.calls.map((c) => String(c[0])).filter((r) => r.startsWith('/personal'))
    expect(rutas.at(-1)).toBe(esperada)
    expect(rutas.some((r) => r.includes('zzz'))).toBe(false)
  })
})

describe('Personal — el enlace a la cuenta respeta la compuerta', () => {
  /** Cláusula bajo prueba: el `preventDefault` del `<Link>` "Cuenta de …" bajo `bloqueado`. */
  it('con la confirmación de baja abierta el enlace queda inerte y no navega; cerrada, navega', async () => {
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await screen.findByText('Ana Gómez')

    await usuarioEvento.click(screen.getByRole('button', { name: 'Eliminar Ana Gómez' }))
    const enlace = screen.getByRole('link', { name: 'Cuenta de Ana Gómez' })
    expect(enlace).toHaveAttribute('aria-disabled', 'true')
    expect(enlace).toHaveClass('disabled')
    expect(enlace).toHaveAttribute('tabindex', '-1')
    await usuarioEvento.click(enlace)

    expect(screen.getByTestId('ruta')).toHaveTextContent(/^\/$/)
    expect(screen.getByRole('alertdialog')).toBeInTheDocument()

    await usuarioEvento.click(screen.getByRole('button', { name: 'Cancelar' }))
    expect(screen.getByRole('link', { name: 'Cuenta de Ana Gómez' })).not.toHaveAttribute('aria-disabled', 'true')
    await usuarioEvento.click(screen.getByRole('link', { name: 'Cuenta de Ana Gómez' }))

    expect(screen.getByTestId('ruta')).toHaveTextContent('/personal/1/cuenta')
  })

  it('con un guardado en vuelo el enlace queda inerte y no navega', async () => {
    apiPostMock.mockImplementation(() => new Promise(() => {}))
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await abrirAlta(usuarioEvento)
    await llenarAlta(usuarioEvento)
    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    const enlace = screen.getByRole('link', { name: 'Cuenta de Ana Gómez' })
    expect(enlace).toHaveAttribute('aria-disabled', 'true')
    await usuarioEvento.click(enlace)

    expect(screen.getByTestId('ruta')).toHaveTextContent(/^\/$/)
  })
})

describe('Personal — accesibilidad de los selectores de vínculo', () => {
  it('el rechazo de un vínculo marca su combobox como inválido y lo describe con el mensaje', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'usuario_ya_vinculado', 'texto del servidor'))
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await abrirAlta(usuarioEvento)
    await llenarAlta(usuarioEvento)
    await elegirVinculo(usuarioEvento, 'Usuario vinculado', 'ana (ana@ways.test)')
    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    const alerta = await screen.findByText('Ese usuario ya está vinculado a otro personal.')
    const campoUsuario = screen.getByRole('combobox', { name: 'Usuario vinculado' })
    const campoCliente = screen.getByRole('combobox', { name: 'Cliente vinculado' })

    expect(campoUsuario).toHaveAttribute('aria-invalid', 'true')
    expect(campoUsuario).toHaveAttribute('aria-describedby', alerta.id)
    expect(alerta.id).not.toBe('')
    expect(campoCliente).not.toHaveAttribute('aria-invalid')
    expect(campoCliente).not.toHaveAttribute('aria-describedby')
  })

  it.each([
    ['Usuario vinculado', 'Quitar usuario vinculado'],
    ['Cliente vinculado', 'Quitar cliente vinculado'],
  ])('quitar el vínculo de "%s" devuelve el foco a su combobox', async (campo, quitar) => {
    filas = [personal({ idUsuario: 11, nombreUsuario: 'ana', idCliente: 21, nombreCliente: 'Cliente Uno' })]
    const usuarioEvento = userEvent.setup()
    renderPersonal()
    await screen.findByText('Ana Gómez')
    await usuarioEvento.click(screen.getByRole('button', { name: 'Editar Ana Gómez' }))

    await usuarioEvento.click(screen.getByRole('button', { name: quitar }))

    expect(screen.queryByRole('button', { name: quitar })).not.toBeInTheDocument()
    expect(screen.getByRole('combobox', { name: campo })).toHaveFocus()
  })
})

describe('Personal — lectura del saldo', () => {
  it('explica con texto visible el signo del saldo y marca el negativo como adelanto', async () => {
    filas = [
      personal({ id: 1, nombre: 'Ana Gómez', saldo: 12000 }),
      personal({ id: 2, nombre: 'Beto Ruiz', saldo: -250 }),
      personal({ id: 3, nombre: 'Carla Paz', saldo: 0 }),
    ]
    renderPersonal()

    const ana = (await screen.findByText('Ana Gómez')).closest('tr') as HTMLElement
    const beto = screen.getByText('Beto Ruiz').closest('tr') as HTMLElement
    const carla = screen.getByText('Carla Paz').closest('tr') as HTMLElement

    expect(
      screen.getByText('Saldo positivo: la empresa le debe al empleado. Saldo negativo: adelanto ya pagado al empleado.'),
    ).toBeVisible()
    expect(within(beto).getByText('Adelanto ya pagado')).toBeInTheDocument()
    expect(within(ana).queryByText('Adelanto ya pagado')).not.toBeInTheDocument()
    expect(within(carla).queryByText('Adelanto ya pagado')).not.toBeInTheDocument()
  })
})
