import { act, cleanup, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { CuentasTesoreria } from './CuentasTesoreria'
import { ErrorApi } from '../api/cliente'
import type { CuentaTesoreriaListado } from '../api/cuentasTesoreria'
import type { EmpresaListado } from '../api/tipos'

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

afterEach(cleanup)

function renderPagina() {
  return render(<CuentasTesoreria />, { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> })
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

function cuenta(sobrescribir: Partial<CuentaTesoreriaListado> = {}): CuentaTesoreriaListado {
  return {
    id: 1,
    idEmpresa: 7,
    nombreEmpresa: 'Comercio SA',
    nombre: 'Mercado Pago',
    saldoInicial: 1000,
    saldoActual: 2500.5,
    activo: true,
    saldoInicialEditable: true,
    ...sobrescribir,
  }
}

function empresa(id: number, razonSocial: string, nombreFantasia: string | null = null): EmpresaListado {
  return {
    id,
    idTenant: 1,
    razonSocial,
    nombreFantasia,
    cuit: null,
    nombreTenant: 'Tenant',
    alicuotaPercepcionIibb: null,
    alicuotaPercepcionIva: null,
  }
}

let filas: CuentaTesoreriaListado[]
let empresas: EmpresaListado[]

/** `listar` atiende los pedidos a `/cuentas-tesoreria`; por defecto devuelve `filas`. */
function mockearRutas(listar: () => Promise<CuentaTesoreriaListado[]> = () => Promise.resolve(filas)) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/cuentas-tesoreria') return listar()
    if (ruta === '/empresas') return Promise.resolve(empresas)
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

const llamadasAlListado = () => apiGetMock.mock.calls.filter((c) => c[0] === '/cuentas-tesoreria').length

type Usuario = ReturnType<typeof userEvent.setup>

/** Abre el formulario de alta esperando a que las empresas estén cargadas (el botón se habilita con ellas). */
async function abrirAlta(usuarioEvento: Usuario) {
  await screen.findByText('Mercado Pago')
  const agregar = screen.getByRole('button', { name: 'Agregar' })
  await waitFor(() => expect(agregar).toBeEnabled())
  await usuarioEvento.click(agregar)
  await screen.findByRole('dialog')
}

async function escribirSaldo(usuarioEvento: Usuario, texto: string) {
  const campo = screen.getByLabelText('Saldo inicial')
  await usuarioEvento.clear(campo)
  await usuarioEvento.type(campo, texto)
}

beforeEach(() => {
  filas = [cuenta()]
  empresas = [empresa(7, 'Comercio SA', 'Comercio')]
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiPutMock.mockReset()
  apiDeleteMock.mockReset()
  mockearRutas()
})

describe('CuentasTesoreria — listado', () => {
  it('muestra nombre, empresa, saldo actual, saldo inicial y estado de cada cuenta', async () => {
    filas = [
      cuenta(),
      cuenta({ id: 2, nombre: 'Banco', saldoInicial: -50, saldoActual: -75.25, activo: false, nombreEmpresa: null }),
    ]
    renderPagina()

    const mercado = (await screen.findByText('Mercado Pago')).closest('tr') as HTMLElement
    const banco = screen.getByText('Banco').closest('tr') as HTMLElement

    expect(within(mercado).getByText('Comercio SA')).toBeInTheDocument()
    expect(within(mercado).getByText('$ 2.500,50')).toBeInTheDocument()
    expect(within(mercado).getByText('$ 1.000,00')).toBeInTheDocument()
    expect(within(mercado).getByText('Activa')).toBeInTheDocument()
    expect(within(banco).getByText('sin empresa')).toBeInTheDocument()
    expect(within(banco).getByText('Inactiva')).toBeInTheDocument()
    expect(within(banco).getByText('-$ 75,25')).toBeInTheDocument()
  })

  it('sin cuentas muestra el vacío', async () => {
    filas = []
    renderPagina()

    expect(await screen.findByText('Todavía no hay cuentas de tesorería.')).toBeInTheDocument()
  })

  it('un listado que falla muestra el mensaje del servidor', async () => {
    mockearRutas(() => Promise.reject(new ErrorApi(500, 'error_interno', 'No se pudo leer.')))
    renderPagina()

    expect(await screen.findByText('No se pudo leer.')).toBeInTheDocument()
  })
})

describe('CuentasTesoreria — alta', () => {
  it('con una sola empresa la preselecciona, manda el cuerpo del DTO con el nombre recortado y refresca', async () => {
    apiPostMock.mockResolvedValue(cuenta({ id: 2, nombre: 'Banco Nación' }))
    const usuarioEvento = userEvent.setup()
    renderPagina()
    await abrirAlta(usuarioEvento)
    expect(screen.getByLabelText('Empresa')).toHaveValue('7')

    await usuarioEvento.type(screen.getByLabelText('Nombre'), '  Banco Nación  ')
    await escribirSaldo(usuarioEvento, '-250,5')
    filas = [cuenta(), cuenta({ id: 2, nombre: 'Banco Nación' })]
    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() =>
      expect(apiPostMock).toHaveBeenCalledWith('/cuentas-tesoreria', {
        idEmpresa: 7,
        nombre: 'Banco Nación',
        saldoInicial: -250.5,
        activo: true,
      }),
    )
    expect(await screen.findByText('Cuenta "Banco Nación" creada.')).toBeInTheDocument()
    expect(await screen.findByText('Banco Nación')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('con varias empresas hay que elegir una: sin elegir no llama a la API y marca el campo', async () => {
    empresas = [empresa(7, 'Comercio SA', 'Comercio'), empresa(8, 'Otra SRL')]
    const usuarioEvento = userEvent.setup()
    renderPagina()
    await abrirAlta(usuarioEvento)
    expect(screen.getByLabelText('Empresa')).toHaveValue('')
    expect(screen.getByRole('option', { name: 'Otra SRL' })).toBeInTheDocument()

    await usuarioEvento.type(screen.getByLabelText('Nombre'), 'Banco')
    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('Elegí la empresa.')).toBeInTheDocument()
    expect(screen.getByLabelText('Empresa')).toHaveAttribute('aria-invalid', 'true')
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('sin nombre no llama a la API y marca el campo', async () => {
    const usuarioEvento = userEvent.setup()
    renderPagina()
    await abrirAlta(usuarioEvento)

    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('Ingresá el nombre.')).toBeInTheDocument()
    expect(screen.getByLabelText('Nombre')).toHaveAttribute('aria-invalid', 'true')
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('un saldo inicial vacío no llama a la API', async () => {
    const usuarioEvento = userEvent.setup()
    renderPagina()
    await abrirAlta(usuarioEvento)
    await usuarioEvento.type(screen.getByLabelText('Nombre'), 'Banco')

    await usuarioEvento.clear(screen.getByLabelText('Saldo inicial'))
    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('Ingresá el saldo inicial.')).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('un 409 de nombre repetido se muestra en el campo nombre y la modal sigue abierta', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'cuenta_tesoreria_duplicada', 'texto del servidor'))
    const usuarioEvento = userEvent.setup()
    renderPagina()
    await abrirAlta(usuarioEvento)
    await usuarioEvento.type(screen.getByLabelText('Nombre'), 'Mercado Pago')

    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(
      await screen.findByText('Ya existe una cuenta de tesorería con ese nombre en esta empresa.'),
    ).toBeInTheDocument()
    expect(screen.getByLabelText('Nombre')).toHaveAttribute('aria-invalid', 'true')
    expect(screen.getByRole('dialog', { name: 'Nueva cuenta de tesorería' })).toBeInTheDocument()
  })

  it('un código sin campo propio se muestra arriba del formulario', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(403, 'prohibido', 'No tenés permiso.'))
    const usuarioEvento = userEvent.setup()
    renderPagina()
    await abrirAlta(usuarioEvento)
    await usuarioEvento.type(screen.getByLabelText('Nombre'), 'Banco')

    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('No tenés permiso.')).toBeInTheDocument()
  })

  it('si las empresas no cargan avisa y no deja crear cuentas', async () => {
    apiGetMock.mockImplementation((ruta: string) =>
      ruta === '/empresas' ? Promise.reject(new Error('sin red')) : Promise.resolve(filas),
    )
    renderPagina()

    expect(await screen.findByText(/No se pudieron cargar las empresas/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Agregar' })).toBeDisabled()
  })
})

describe('CuentasTesoreria — edición y saldo inicial', () => {
  it('edita sin mandar la empresa, que se ve de solo lectura', async () => {
    apiPutMock.mockResolvedValue(cuenta({ nombre: 'MP' }))
    const usuarioEvento = userEvent.setup()
    renderPagina()
    await screen.findByText('Mercado Pago')

    await usuarioEvento.click(screen.getByRole('button', { name: 'Editar Mercado Pago' }))
    expect(screen.getByLabelText('Empresa')).toHaveAttribute('readonly')
    expect(screen.getByLabelText('Empresa')).toHaveValue('Comercio')
    expect(screen.getByLabelText('Saldo inicial')).not.toHaveAttribute('readonly')
    await usuarioEvento.clear(screen.getByLabelText('Nombre'))
    await usuarioEvento.type(screen.getByLabelText('Nombre'), 'MP')
    await usuarioEvento.click(screen.getByLabelText('Activa'))
    await escribirSaldo(usuarioEvento, '1200')
    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() =>
      expect(apiPutMock).toHaveBeenCalledWith('/cuentas-tesoreria/1', {
        nombre: 'MP',
        saldoInicial: 1200,
        activo: false,
      }),
    )
    expect(await screen.findByText('Cuenta "MP" actualizada.')).toBeInTheDocument()
  })

  it('con saldoInicialEditable en falso el campo es de solo lectura, se explica y se manda el valor guardado', async () => {
    filas = [cuenta({ saldoInicialEditable: false, saldoInicial: 1000 })]
    apiPutMock.mockResolvedValue(cuenta())
    const usuarioEvento = userEvent.setup()
    renderPagina()
    await screen.findByText('Mercado Pago')

    await usuarioEvento.click(screen.getByRole('button', { name: 'Editar Mercado Pago' }))
    const saldo = screen.getByLabelText('Saldo inicial')
    expect(saldo).toHaveAttribute('readonly')
    expect(saldo).toHaveValue('1.000,00')
    expect(
      screen.getByText('El saldo inicial no se puede modificar porque la cuenta ya tiene movimientos.'),
    ).toBeInTheDocument()
    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() =>
      expect(apiPutMock).toHaveBeenCalledWith('/cuentas-tesoreria/1', {
        nombre: 'Mercado Pago',
        saldoInicial: 1000,
        activo: true,
      }),
    )
  })

  it('un 409 saldo_inicial_con_movimientos restituye el valor guardado, bloquea el campo y recarga el listado', async () => {
    apiPutMock.mockRejectedValue(new ErrorApi(409, 'saldo_inicial_con_movimientos', 'texto del servidor'))
    const usuarioEvento = userEvent.setup()
    renderPagina()
    await screen.findByText('Mercado Pago')
    await usuarioEvento.click(screen.getByRole('button', { name: 'Editar Mercado Pago' }))
    await escribirSaldo(usuarioEvento, '5000')
    filas = [cuenta({ saldoInicialEditable: false })]
    const antes = llamadasAlListado()

    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    const alerta = await screen.findByRole('alert')
    expect(alerta).toHaveTextContent('El saldo inicial no se puede modificar porque la cuenta ya tiene movimientos.')
    const saldo = screen.getByLabelText('Saldo inicial')
    expect(saldo).toHaveAttribute('readonly')
    expect(saldo).toHaveValue('1.000,00')
    await waitFor(() => expect(llamadasAlListado()).toBe(antes + 1))
    expect(screen.getByRole('dialog', { name: 'Editar cuenta de tesorería' })).toBeInTheDocument()
  })

  it('un 404 al guardar dice que la cuenta ya no existe y deja la modal abierta', async () => {
    apiPutMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'texto del servidor'))
    const usuarioEvento = userEvent.setup()
    renderPagina()
    await screen.findByText('Mercado Pago')

    await usuarioEvento.click(screen.getByRole('button', { name: 'Editar Mercado Pago' }))
    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('La cuenta ya no existe. Actualizá la pantalla.')).toBeInTheDocument()
    expect(screen.getByRole('dialog', { name: 'Editar cuenta de tesorería' })).toBeInTheDocument()
  })
})

describe('CuentasTesoreria — baja', () => {
  it('pide confirmación y, al confirmar, da de baja y refresca', async () => {
    apiDeleteMock.mockResolvedValue(undefined)
    const usuarioEvento = userEvent.setup()
    renderPagina()
    await screen.findByText('Mercado Pago')

    await usuarioEvento.click(screen.getByRole('button', { name: 'Eliminar Mercado Pago' }))
    expect(apiDeleteMock).not.toHaveBeenCalled()
    filas = []
    await usuarioEvento.click(await screen.findByRole('button', { name: 'Confirmar baja' }))

    await waitFor(() => expect(apiDeleteMock).toHaveBeenCalledWith('/cuentas-tesoreria/1'))
    expect(await screen.findByText('Cuenta "Mercado Pago" dada de baja.')).toBeInTheDocument()
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
  })

  it('un 409 cuenta_tesoreria_en_uso muestra lo que la bloquea y sugiere desactivarla', async () => {
    apiDeleteMock.mockRejectedValue(
      new ErrorApi(
        409,
        'cuenta_tesoreria_en_uso',
        'No se puede dar de baja la cuenta de tesorería porque tiene medios de pago.',
      ),
    )
    const usuarioEvento = userEvent.setup()
    renderPagina()
    await screen.findByText('Mercado Pago')

    await usuarioEvento.click(screen.getByRole('button', { name: 'Eliminar Mercado Pago' }))
    await usuarioEvento.click(await screen.findByRole('button', { name: 'Confirmar baja' }))

    expect(
      await screen.findByText(
        'No se puede dar de baja la cuenta de tesorería porque tiene medios de pago. Desactivala para que no se ofrezca más.',
      ),
    ).toBeInTheDocument()
  })

  it('cancelar la puerta no llama a la API', async () => {
    const usuarioEvento = userEvent.setup()
    renderPagina()
    await screen.findByText('Mercado Pago')

    await usuarioEvento.click(screen.getByRole('button', { name: 'Eliminar Mercado Pago' }))
    await usuarioEvento.click(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Cancelar' }))

    expect(apiDeleteMock).not.toHaveBeenCalled()
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
  })
})

describe('CuentasTesoreria — esqueleto asíncrono', () => {
  /** Cláusula bajo prueba: `ocupadoRef` en `guardar`, la guarda de re-entrancia del mismo tick. */
  it('dos clics sincrónicos sobre Guardar emiten una sola request', async () => {
    apiPostMock.mockImplementation(() => new Promise(() => {}))
    const usuarioEvento = userEvent.setup()
    renderPagina()
    await abrirAlta(usuarioEvento)
    await usuarioEvento.type(screen.getByLabelText('Nombre'), 'Banco')

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
    renderPagina()
    await screen.findByText('Mercado Pago')

    await usuarioEvento.click(screen.getByRole('button', { name: 'Eliminar Mercado Pago' }))
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
    const banco = cuenta({ id: 2, nombre: 'Banco' })
    filas = [cuenta(), banco]
    const delete_ = diferido<void>()
    const refresco = diferido<CuentaTesoreriaListado[]>()
    let cargas = 0
    mockearRutas(() => (++cargas === 1 ? Promise.resolve(filas) : refresco.promesa))
    apiDeleteMock.mockReturnValue(delete_.promesa)
    renderPagina()
    await screen.findByText('Mercado Pago')

    await usuarioEvento.click(screen.getByRole('button', { name: 'Eliminar Mercado Pago' }))
    await usuarioEvento.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    expect(screen.getByRole('button', { name: 'Dando de baja…' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Agregar' })).toBeDisabled()
    for (const nombre of ['Mercado Pago', 'Banco']) {
      expect(screen.getByRole('button', { name: `Editar ${nombre}` })).toBeDisabled()
      expect(screen.getByRole('button', { name: `Eliminar ${nombre}` })).toBeDisabled()
    }

    await act(async () => {
      delete_.resolver()
      await delete_.promesa
    })

    expect(await screen.findByText('Cuenta "Mercado Pago" dada de baja.')).toBeInTheDocument()
    expect(screen.getByText('Cargando…')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Agregar' })).toBeDisabled()

    await act(async () => {
      refresco.resolver([banco])
      await refresco.promesa
    })

    expect(await screen.findByText('Banco')).toBeInTheDocument()
    expect(screen.queryByText('Mercado Pago')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Agregar' })).toBeEnabled()
    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
  })

  /** Cláusula bajo prueba: la rama `catch` de `refrescarTrasEscribir`. Un guardado que ya commiteó nunca se
   * reporta como fallido, aunque el refresco posterior explote. */
  it('un refresco fallido después de guardar avisa que se guardó y cierra la modal', async () => {
    let cargas = 0
    mockearRutas(() => (++cargas === 1 ? Promise.resolve(filas) : Promise.reject(new Error('sin red'))))
    apiPostMock.mockResolvedValue(cuenta({ id: 2, nombre: 'Banco' }))
    const usuarioEvento = userEvent.setup()
    renderPagina()
    await abrirAlta(usuarioEvento)
    await usuarioEvento.type(screen.getByLabelText('Nombre'), 'Banco')

    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(
      await screen.findByText('Cuenta "Banco" creada. Se guardó, pero no se pudo actualizar la vista. Recargá la pantalla.'),
    ).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('un refresco fallido después de la baja avisa que se dio de baja', async () => {
    let cargas = 0
    mockearRutas(() => (++cargas === 1 ? Promise.resolve(filas) : Promise.reject(new Error('sin red'))))
    apiDeleteMock.mockResolvedValue(undefined)
    const usuarioEvento = userEvent.setup()
    renderPagina()
    await screen.findByText('Mercado Pago')

    await usuarioEvento.click(screen.getByRole('button', { name: 'Eliminar Mercado Pago' }))
    await usuarioEvento.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    expect(
      await screen.findByText(
        'Cuenta "Mercado Pago" dada de baja. Se dio de baja, pero no se pudo actualizar la vista. Recargá la pantalla.',
      ),
    ).toBeInTheDocument()
  })

  it('el error de un guardado anterior no sobrevive a cancelar y reabrir', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'cuenta_tesoreria_duplicada', 'x'))
    const usuarioEvento = userEvent.setup()
    renderPagina()
    await abrirAlta(usuarioEvento)
    await usuarioEvento.type(screen.getByLabelText('Nombre'), 'Mercado Pago')
    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))
    await screen.findByText('Ya existe una cuenta de tesorería con ese nombre en esta empresa.')

    await usuarioEvento.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancelar' }))
    await usuarioEvento.click(screen.getByRole('button', { name: 'Agregar' }))

    await screen.findByRole('dialog')
    expect(screen.getByLabelText('Nombre')).toHaveValue('')
    expect(
      screen.queryByText('Ya existe una cuenta de tesorería con ese nombre en esta empresa.'),
    ).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `ocupado={bloqueado}` de la modal. */
  it('con el guardado en vuelo la modal no se cierra ni con la × ni con Escape', async () => {
    apiPostMock.mockImplementation(() => new Promise(() => {}))
    const usuarioEvento = userEvent.setup()
    renderPagina()
    await abrirAlta(usuarioEvento)
    await usuarioEvento.type(screen.getByLabelText('Nombre'), 'Banco')

    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(screen.getByRole('button', { name: 'Guardando…' })).toBeDisabled()
    expect(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cerrar' })).toBeDisabled()
    await usuarioEvento.keyboard('{Escape}')
    expect(screen.getByRole('dialog', { name: 'Nueva cuenta de tesorería' })).toBeInTheDocument()
  })
})

describe('CuentasTesoreria — respuestas tardías del listado', () => {
  /** Cláusula bajo prueba: la guarda de generación de `cargar`. La lectura inicial queda en vuelo, un guardado
   * acuña una generación nueva y su refresco gana; la respuesta vieja llega después y no pisa la pantalla. */
  it('una respuesta vieja que llega después del refresco de un guardado no pisa la pantalla', async () => {
    const inicial = diferido<CuentaTesoreriaListado[]>()
    let llamadas = 0
    mockearRutas(() => (++llamadas === 1 ? inicial.promesa : Promise.resolve([cuenta({ id: 2, nombre: 'Banco' })])))
    apiPostMock.mockResolvedValue(cuenta({ id: 2, nombre: 'Banco' }))
    const usuarioEvento = userEvent.setup()
    renderPagina()
    const agregar = screen.getByRole('button', { name: 'Agregar' })
    await waitFor(() => expect(agregar).toBeEnabled())
    await usuarioEvento.click(agregar)
    await usuarioEvento.type(await screen.findByLabelText('Nombre'), 'Banco')
    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))
    expect(await screen.findByText('Banco')).toBeInTheDocument()

    await act(async () => {
      inicial.resolver([cuenta()])
      await inicial.promesa
    })

    expect(screen.getByText('Banco')).toBeInTheDocument()
    expect(screen.queryByText('Mercado Pago')).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: la guarda de generación del `catch` de `cargar`. */
  it('el rechazo de una lectura vieja no deja un error en pantalla', async () => {
    const inicial = diferido<CuentaTesoreriaListado[]>()
    let llamadas = 0
    mockearRutas(() => (++llamadas === 1 ? inicial.promesa : Promise.resolve([cuenta({ id: 2, nombre: 'Banco' })])))
    apiPostMock.mockResolvedValue(cuenta({ id: 2, nombre: 'Banco' }))
    const usuarioEvento = userEvent.setup()
    renderPagina()
    const agregar = screen.getByRole('button', { name: 'Agregar' })
    await waitFor(() => expect(agregar).toBeEnabled())
    await usuarioEvento.click(agregar)
    await usuarioEvento.type(await screen.findByLabelText('Nombre'), 'Banco')
    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))
    await screen.findByText('Banco')

    await act(async () => {
      inicial.rechazar(new Error('sin red'))
      await inicial.promesa.catch(() => {})
    })

    expect(screen.queryByText('No se pudieron cargar las cuentas de tesorería.')).not.toBeInTheDocument()
    expect(screen.getByText('Banco')).toBeInTheDocument()
  })

  /** Cláusula bajo prueba: la relectura de `guardar` cuando el guardado falla con la lectura inicial en vuelo,
   * que quedó invalidada por el token del guardado. */
  it('un guardado rechazado con la lectura inicial en vuelo no deja la tabla en el spinner', async () => {
    const inicial = diferido<CuentaTesoreriaListado[]>()
    let llamadas = 0
    mockearRutas(() => (++llamadas === 1 ? inicial.promesa : Promise.resolve(filas)))
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'cuenta_tesoreria_duplicada', 'x'))
    const usuarioEvento = userEvent.setup()
    renderPagina()
    const agregar = screen.getByRole('button', { name: 'Agregar' })
    await waitFor(() => expect(agregar).toBeEnabled())
    await usuarioEvento.click(agregar)
    await usuarioEvento.type(await screen.findByLabelText('Nombre'), 'Mercado Pago')

    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))
    await screen.findByText('Ya existe una cuenta de tesorería con ese nombre en esta empresa.')

    await act(async () => {
      inicial.resolver([cuenta({ nombre: 'Vieja' })])
      await inicial.promesa
    })

    expect(await screen.findByText('Mercado Pago')).toBeInTheDocument()
    expect(screen.queryByText('Vieja')).not.toBeInTheDocument()
    expect(screen.queryByText('Cargando…')).not.toBeInTheDocument()
  })
})
