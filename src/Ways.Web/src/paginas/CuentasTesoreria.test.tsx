import { act, cleanup, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { CuentasTesoreria } from './CuentasTesoreria'
import { Toaster } from '../toasts/Toaster'
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
  return render(
    <>
      <CuentasTesoreria />
      <Toaster />
    </>,
    { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> },
  )
}

/** El resultado de una escritura se avisa con un toast de éxito, nunca con un banner. */
async function toastDeExito(texto: string) {
  const aviso = await screen.findByText(texto)
  expect(aviso.closest('[role="status"]')).not.toBeNull()
  expect(document.body.querySelector('.alert-success')).toBeNull()
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
    await toastDeExito('Cuenta "Banco Nación" creada.')
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
    await toastDeExito('Cuenta "MP" actualizada.')
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

describe('CuentasTesoreria — la cuenta ya no existe', () => {
  it('un 404 al guardar recarga el listado y la fila vieja desaparece', async () => {
    apiPutMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'x'))
    const usuarioEvento = userEvent.setup()
    renderPagina()
    await screen.findByText('Mercado Pago')
    await usuarioEvento.click(screen.getByRole('button', { name: 'Editar Mercado Pago' }))
    filas = []
    const antes = llamadasAlListado()

    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('La cuenta ya no existe. Actualizá la pantalla.')).toBeInTheDocument()
    await waitFor(() => expect(llamadasAlListado()).toBe(antes + 1))
    expect(await screen.findByText('Todavía no hay cuentas de tesorería.')).toBeInTheDocument()
    expect(screen.queryByText('Mercado Pago')).not.toBeInTheDocument()
  })

  it('un 404 al dar de baja recarga el listado, cierra la puerta y avisa el motivo con un toast de error', async () => {
    apiDeleteMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'x'))
    const usuarioEvento = userEvent.setup()
    renderPagina()
    await screen.findByText('Mercado Pago')
    await usuarioEvento.click(screen.getByRole('button', { name: 'Eliminar Mercado Pago' }))
    filas = []
    const antes = llamadasAlListado()

    await usuarioEvento.click(await screen.findByRole('button', { name: 'Confirmar baja' }))

    await waitFor(() => expect(llamadasAlListado()).toBe(antes + 1))
    expect(await screen.findByText('Todavía no hay cuentas de tesorería.')).toBeInTheDocument()
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    const aviso = screen.getByText(
      'No se pudo dar de baja la cuenta "Mercado Pago". Ya no existe o no está a tu alcance. Actualizá el listado.',
    )
    expect(aviso.closest('[role="alert"]')).not.toBeNull()
    expect(document.body.querySelector('.alert-danger')).toBeNull()
  })

  it('al editar muestra la empresa de la fila si el listado de empresas no cargó', async () => {
    apiGetMock.mockImplementation((ruta: string) =>
      ruta === '/empresas' ? Promise.reject(new Error('sin red')) : Promise.resolve(filas),
    )
    const usuarioEvento = userEvent.setup()
    renderPagina()
    await screen.findByText('Mercado Pago')

    await usuarioEvento.click(screen.getByRole('button', { name: 'Editar Mercado Pago' }))

    expect(screen.getByLabelText('Empresa')).toHaveValue('Comercio SA')
  })

  it('al editar sin empresa en el listado ni en la fila cae en "Empresa {id}"', async () => {
    filas = [cuenta({ nombreEmpresa: null })]
    empresas = []
    const usuarioEvento = userEvent.setup()
    renderPagina()
    await screen.findByText('Mercado Pago')

    await usuarioEvento.click(screen.getByRole('button', { name: 'Editar Mercado Pago' }))

    expect(screen.getByLabelText('Empresa')).toHaveValue('Empresa 7')
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
    await toastDeExito('Cuenta "Mercado Pago" dada de baja.')
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

    await toastDeExito('Cuenta "Mercado Pago" dada de baja.')
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
  it('un refresco fallido después de guardar avisa el éxito y el fallo con toasts y cierra la modal', async () => {
    let cargas = 0
    mockearRutas(() => (++cargas === 1 ? Promise.resolve(filas) : Promise.reject(new Error('sin red'))))
    apiPostMock.mockResolvedValue(cuenta({ id: 2, nombre: 'Banco' }))
    const usuarioEvento = userEvent.setup()
    renderPagina()
    await abrirAlta(usuarioEvento)
    await usuarioEvento.type(screen.getByLabelText('Nombre'), 'Banco')

    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    await toastDeExito('Cuenta "Banco" creada.')
    const advertencia = await screen.findByText('Se guardó, pero no se pudo actualizar la vista. Recargá la pantalla.')
    expect(advertencia.closest('[role="status"]')).not.toBeNull()
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

    await toastDeExito('Cuenta "Mercado Pago" dada de baja.')
    const advertencia = await screen.findByText('Se dio de baja, pero no se pudo actualizar la vista. Recargá la pantalla.')
    expect(advertencia.closest('[role="status"]')).not.toBeNull()
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

  /** Cláusula bajo prueba: la condición `habiaLecturaEnVuelo || rechazoDeSaldo || yaNoExiste` de esa relectura.
   * Con el listado ya cargado y un rechazo que no es 404 ni `saldo_inicial_con_movimientos`, los tres disyuntos
   * son falsos y el listado no se vuelve a pedir. */
  it('un guardado rechazado con el listado ya cargado no lo vuelve a pedir', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'cuenta_tesoreria_duplicada', 'x'))
    const usuarioEvento = userEvent.setup()
    renderPagina()
    await abrirAlta(usuarioEvento)
    await usuarioEvento.type(screen.getByLabelText('Nombre'), 'Mercado Pago')
    const antes = llamadasAlListado()

    await usuarioEvento.click(screen.getByRole('button', { name: 'Guardar' }))

    await screen.findByText('Ya existe una cuenta de tesorería con ese nombre en esta empresa.')
    await act(async () => {})
    expect(antes).toBe(1)
    expect(llamadasAlListado()).toBe(antes)
    expect(screen.getByText('Mercado Pago')).toBeInTheDocument()
  })
})

describe('CuentasTesoreria — orden y filtros por columna', () => {
  let u: Usuario

  beforeEach(() => {
    u = userEvent.setup()
  })

  const mercadoPago = cuenta({ id: 1, nombre: 'Mercado Pago', nombreEmpresa: 'Comercio SA', saldoActual: 1500, saldoInicial: 100 })
  const banco = cuenta({ id: 2, nombre: 'Banco', nombreEmpresa: 'Alfa SRL', saldoActual: 900, saldoInicial: 20000, activo: false })
  const cajaChica = cuenta({ id: 3, nombre: 'Caja chica', nombreEmpresa: null, saldoActual: 12000, saldoInicial: 5 })

  /** Las dos primeras filas son el encabezado y los filtros; la celda 0 de cada fila de datos es el nombre. */
  function nombresEnPantalla() {
    return screen
      .getAllByRole('row')
      .slice(2)
      .map((f) => within(f).getAllByRole('cell')[0]?.textContent)
  }

  async function montarConCuentas() {
    filas = [mercadoPago, banco, cajaChica]
    renderPagina()
    await screen.findByText('Mercado Pago')
  }

  it('la fila de filtros tiene una celda por columna del encabezado y limpiar queda bajo Acciones', async () => {
    await montarConCuentas()

    const [encabezados, filtros] = screen.getAllByRole('row')
    const celdasDeEncabezado = within(encabezados).getAllByRole('columnheader')
    const celdasDeFiltros = within(filtros).getAllByRole('columnheader')

    expect(celdasDeFiltros).toHaveLength(celdasDeEncabezado.length)
    expect(celdasDeEncabezado.at(-1)).toHaveTextContent('Acciones')
    expect(within(celdasDeFiltros.at(-1)!).getByRole('button', { name: 'Limpiar filtros' })).toBeInTheDocument()
  })

  it('cada columna de datos se puede ordenar y Acciones no', async () => {
    await montarConCuentas()

    for (const nombre of ['Nombre', 'Empresa', 'Saldo actual', 'Saldo inicial', 'Estado']) {
      expect(screen.getByRole('button', { name: nombre })).toBeInTheDocument()
    }
    expect(screen.getByRole('columnheader', { name: 'Acciones' })).not.toHaveAttribute('aria-sort')
  })

  it('el saldo actual se ordena como número (900 antes que 1.500) y el tercer clic vuelve al orden del servidor', async () => {
    await montarConCuentas()
    const boton = screen.getByRole('button', { name: 'Saldo actual' })
    const encabezado = screen.getByRole('columnheader', { name: 'Saldo actual' })
    expect(nombresEnPantalla()).toEqual(['Mercado Pago', 'Banco', 'Caja chica'])

    await u.click(boton)
    expect(nombresEnPantalla()).toEqual(['Banco', 'Mercado Pago', 'Caja chica'])
    expect(encabezado).toHaveAttribute('aria-sort', 'ascending')

    await u.click(boton)
    expect(nombresEnPantalla()).toEqual(['Caja chica', 'Mercado Pago', 'Banco'])
    expect(encabezado).toHaveAttribute('aria-sort', 'descending')

    await u.click(boton)
    expect(nombresEnPantalla()).toEqual(['Mercado Pago', 'Banco', 'Caja chica'])
    expect(encabezado).toHaveAttribute('aria-sort', 'none')
  })

  it('el saldo inicial se ordena como número', async () => {
    await montarConCuentas()

    await u.click(screen.getByRole('button', { name: 'Saldo inicial' }))

    expect(nombresEnPantalla()).toEqual(['Caja chica', 'Mercado Pago', 'Banco'])
  })

  it('una cuenta cuya empresa fue dada de baja va al final en cualquier dirección', async () => {
    await montarConCuentas()
    const boton = screen.getByRole('button', { name: 'Empresa' })

    await u.click(boton)
    expect(nombresEnPantalla()).toEqual(['Banco', 'Mercado Pago', 'Caja chica'])

    await u.click(boton)
    expect(nombresEnPantalla()).toEqual(['Mercado Pago', 'Banco', 'Caja chica'])
  })

  it('ordenar por nombre y por estado actúa sobre el texto mostrado', async () => {
    await montarConCuentas()

    await u.click(screen.getByRole('button', { name: 'Nombre' }))
    expect(nombresEnPantalla()).toEqual(['Banco', 'Caja chica', 'Mercado Pago'])

    await u.click(screen.getByRole('button', { name: 'Estado' }))
    expect(nombresEnPantalla()).toEqual(['Mercado Pago', 'Caja chica', 'Banco'])
  })

  it('cada filtro de texto acota las filas', async () => {
    await montarConCuentas()

    await u.type(screen.getByRole('searchbox', { name: 'Filtrar por nombre' }), 'CAJA')
    expect(nombresEnPantalla()).toEqual(['Caja chica'])

    await u.click(screen.getByRole('button', { name: 'Limpiar filtros' }))
    await u.type(screen.getByRole('searchbox', { name: 'Filtrar por empresa' }), 'alfa')
    expect(nombresEnPantalla()).toEqual(['Banco'])

    await u.click(screen.getByRole('button', { name: 'Limpiar filtros' }))
    await u.type(screen.getByRole('searchbox', { name: 'Filtrar por saldo actual' }), '1.500')
    expect(nombresEnPantalla()).toEqual(['Mercado Pago'])

    await u.click(screen.getByRole('button', { name: 'Limpiar filtros' }))
    await u.type(screen.getByRole('searchbox', { name: 'Filtrar por saldo inicial' }), '20.000')
    expect(nombresEnPantalla()).toEqual(['Banco'])
  })

  it('el filtro de estado es un select y se combina con el de texto; limpiar los quita', async () => {
    await montarConCuentas()

    await u.selectOptions(screen.getByRole('combobox', { name: 'Filtrar por estado' }), 'Inactiva')
    expect(nombresEnPantalla()).toEqual(['Banco'])

    await u.type(screen.getByRole('searchbox', { name: 'Filtrar por nombre' }), 'mercado')
    expect(screen.getByText('Ningún registro coincide con los filtros.')).toBeInTheDocument()

    await u.click(screen.getByRole('button', { name: 'Limpiar filtros' }))
    expect(nombresEnPantalla()).toEqual(['Mercado Pago', 'Banco', 'Caja chica'])
    expect(screen.getByRole('searchbox', { name: 'Filtrar por nombre' })).toHaveValue('')
  })

  it('el campo de filtro sigue montado y con su valor mientras el listado se recarga tras una baja', async () => {
    const refresco = diferido<CuentaTesoreriaListado[]>()
    let cargas = 0
    mockearRutas(() => (++cargas === 1 ? Promise.resolve(filas) : refresco.promesa))
    await montarConCuentas()
    apiDeleteMock.mockResolvedValue(undefined)
    await u.type(screen.getByRole('searchbox', { name: 'Filtrar por nombre' }), 'a')
    const campo = screen.getByRole('searchbox', { name: 'Filtrar por nombre' })

    await u.click(screen.getByRole('button', { name: 'Eliminar Caja chica' }))
    await u.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await toastDeExito('Cuenta "Caja chica" dada de baja.')
    expect(screen.getByText('Cargando…')).toBeInTheDocument()
    expect(screen.getByRole('searchbox', { name: 'Filtrar por nombre' })).toBe(campo)
    expect(campo).toHaveValue('a')

    await act(async () => {
      refresco.resolver([mercadoPago, banco])
      await refresco.promesa
    })

    await waitFor(() => expect(screen.queryByText('Cargando…')).not.toBeInTheDocument())
    expect(screen.getByRole('searchbox', { name: 'Filtrar por nombre' })).toBe(campo)
    expect(campo).toHaveValue('a')
    expect(nombresEnPantalla()).toEqual(['Mercado Pago', 'Banco'])
  })

  it('con la puerta de baja abierta los filtros quedan inertes', async () => {
    await montarConCuentas()

    await u.click(screen.getByRole('button', { name: 'Eliminar Banco' }))

    expect(screen.getByRole('searchbox', { name: 'Filtrar por nombre' })).toBeDisabled()
    expect(screen.getByRole('combobox', { name: 'Filtrar por estado' })).toBeDisabled()
  })
})

describe('CuentasTesoreria — reconstruir historial', () => {
  it('abre el modal, reconstruye y vuelve a leer las cuentas con un aviso', async () => {
    const usuario = userEvent.setup()
    mockearRutas()
    const planEscrito = {
      idEmpresa: 7,
      desde: '2026-10-01',
      hasta: '2026-10-31',
      turnos: 2,
      filas: 6,
      acreditaciones: [],
      cuentas: [],
    }
    apiPostMock.mockImplementation((ruta: string) =>
      Promise.resolve(ruta.endsWith('/vista-previa') ? { ...planEscrito, acreditaciones: [], cuentas: [] } : planEscrito),
    )
    renderPagina()
    await screen.findByText('Mercado Pago')
    const boton = screen.getByRole('button', { name: 'Reconstruir historial' })
    await waitFor(() => expect(boton).toBeEnabled())
    expect(llamadasAlListado()).toBe(1)

    await usuario.click(boton)
    await usuario.click(await screen.findByRole('button', { name: 'Vista previa' }))
    const confirmar = screen.getByRole('button', { name: 'Confirmar reconstrucción' })
    await waitFor(() => expect(confirmar).toBeEnabled())
    await usuario.click(confirmar)

    await waitFor(() => expect(llamadasAlListado()).toBe(2))
    const aviso = await screen.findByText(/Historial reconstruido: 6 filas de 2 turnos\./)
    expect(aviso.closest('.ws-toaster [role="status"]')).not.toBeNull()
  })
})
