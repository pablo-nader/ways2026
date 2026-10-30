import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { describe, expect, it, vi, beforeEach } from 'vitest'
import { Proveedores } from './Proveedores'
import { ErrorApi } from '../api/cliente'
import type { PaginaDe, ProveedorListado, SaldoDeProveedor } from '../api/tipos'

function renderProveedores() {
  return render(<Proveedores />, { wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter> })
}

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

function paginaFixture(items: ProveedorListado[]): PaginaDe<ProveedorListado> {
  return { items, total: items.length, pagina: 1, tamanio: 25 }
}

function mockearRutasBase(sobrescribir?: (ruta: string) => Promise<unknown> | undefined) {
  apiGetMock.mockImplementation((ruta: string) => {
    const propia = sobrescribir?.(ruta)
    if (propia) return propia
    if (ruta === '/proveedores') return Promise.resolve(paginaFixture([proveedorFixture()]))
    if (ruta === '/catalogos-fiscales/condiciones-fiscales') return Promise.resolve([])
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiPutMock.mockReset()
  apiDeleteMock.mockReset()
})

describe('Proveedores — listado', () => {
  it('renderiza las filas con el botón "Ver saldo"', async () => {
    mockearRutasBase()
    renderProveedores()

    expect(await screen.findByText('Proveedor Uno SA')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Ver saldo' })).toBeInTheDocument()
  })
})

describe('Proveedores — panel de saldo', () => {
  it('muestra el saldo derivado y el estado de pago por compra', async () => {
    const saldo: SaldoDeProveedor = {
      idProveedor: 1,
      saldo: 2000,
      compras: [{ idComprobanteCompra: 1, numeroExterno: '0003-00012345', total: 5000, pagado: 3000, estadoPago: 'Parcial' }],
    }
    mockearRutasBase((ruta) => (ruta === '/proveedores/1/saldo' ? Promise.resolve(saldo) : undefined))
    const usuario = userEvent.setup()

    renderProveedores()
    await screen.findByText('Proveedor Uno SA')
    await usuario.click(screen.getByRole('button', { name: 'Ver saldo' }))

    expect(await screen.findByText('Saldo de Proveedor Uno SA')).toBeInTheDocument()
    expect(screen.getByText('$ 2.000,00')).toBeInTheDocument()
    expect(screen.getByText('0003-00012345')).toBeInTheDocument()
    expect(screen.getByText('Parcial')).toBeInTheDocument()
  })

  // stage-15-cc-proveedores-ledger (Slice 6): `ResumenSaldoDeProveedor` fue re-apuntada al ledger —
  // el callout "aproximación, no invariante" describía la fórmula RETIRADA por esta etapa, ahora
  // dice simplemente "Saldo a favor." (mutation target #28, `ResumenSaldoDeProveedor.test.tsx`).
  it('un saldo negativo se muestra tal cual, con el callout de saldo a favor', async () => {
    const saldo: SaldoDeProveedor = {
      idProveedor: 1,
      saldo: -500,
      compras: [{ idComprobanteCompra: 1, numeroExterno: '0003-00012345', total: 2000, pagado: 0, estadoPago: 'Impaga' }],
    }
    mockearRutasBase((ruta) => (ruta === '/proveedores/1/saldo' ? Promise.resolve(saldo) : undefined))
    const usuario = userEvent.setup()

    renderProveedores()
    await screen.findByText('Proveedor Uno SA')
    await usuario.click(screen.getByRole('button', { name: 'Ver saldo' }))

    expect(await screen.findByText('-$ 500,00')).toBeInTheDocument()
    expect(screen.getByText('Saldo a favor.')).toBeInTheDocument()
    // la compra sigue impaga individualmente aunque el saldo total ya sea negativo — honesto, no invariante.
    expect(screen.getByText('Impaga')).toBeInTheDocument()
  })

  it('un proveedor sin compras confirmadas muestra el estado vacío de la tabla', async () => {
    const saldo: SaldoDeProveedor = { idProveedor: 1, saldo: 0, compras: [] }
    mockearRutasBase((ruta) => (ruta === '/proveedores/1/saldo' ? Promise.resolve(saldo) : undefined))
    const usuario = userEvent.setup()

    renderProveedores()
    await screen.findByText('Proveedor Uno SA')
    await usuario.click(screen.getByRole('button', { name: 'Ver saldo' }))

    expect(await screen.findByText('Este proveedor no tiene compras confirmadas.')).toBeInTheDocument()
  })

  it('cerrar el panel lo desmonta', async () => {
    const saldo: SaldoDeProveedor = { idProveedor: 1, saldo: 0, compras: [] }
    mockearRutasBase((ruta) => (ruta === '/proveedores/1/saldo' ? Promise.resolve(saldo) : undefined))
    const usuario = userEvent.setup()

    renderProveedores()
    await screen.findByText('Proveedor Uno SA')
    await usuario.click(screen.getByRole('button', { name: 'Ver saldo' }))
    await screen.findByText('Saldo de Proveedor Uno SA')

    await usuario.click(screen.getByRole('button', { name: 'Cerrar' }))
    expect(screen.queryByText('Saldo de Proveedor Uno SA')).not.toBeInTheDocument()
  })
})

// fix/web-bajas-catalogos: la baja de un proveedor reusa `ConfirmacionDeBaja` + `copiaDeFalloDeBaja`,
// mismo patrón que `Empresas.test.tsx`/`PaginaCatalogo.test.tsx` (`react-async-state` regla 10).

function bajaDe(razonSocial: string) {
  return within(screen.getByRole('row', { name: new RegExp(razonSocial) })).getByRole('button', { name: 'Eliminar' })
}

describe('Proveedores — baja lógica (fix/web-bajas-catalogos)', () => {
  beforeEach(() => {
    apiDeleteMock.mockResolvedValue(undefined)
  })

  it('el botón de baja no llama a la API hasta que se confirma', async () => {
    const usuario = userEvent.setup()
    mockearRutasBase()
    renderProveedores()

    await screen.findByText('Proveedor Uno SA')
    await usuario.click(bajaDe('Proveedor Uno SA'))
    expect(apiDeleteMock).not.toHaveBeenCalled()
    expect(screen.getByRole('alertdialog', { name: 'Confirmar baja' })).toHaveTextContent(
      '¿Dar de baja el proveedor "Proveedor Uno SA"?',
    )

    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))
    await screen.findByText('Proveedor "Proveedor Uno SA" dado de baja.')
    expect(apiDeleteMock).toHaveBeenCalledWith('/proveedores/1')
  })

  it('cancelar cierra la puerta y no llama nunca a la API', async () => {
    const usuario = userEvent.setup()
    mockearRutasBase()
    renderProveedores()

    await screen.findByText('Proveedor Uno SA')
    await usuario.click(bajaDe('Proveedor Uno SA'))
    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(apiDeleteMock).not.toHaveBeenCalled()
  })

  /** Cláusula bajo prueba: `ocupadoRef`, la guarda de re-entrancia del mismo tick. */
  it('un segundo click sobre la confirmación en vuelo se descarta', async () => {
    mockearRutasBase()
    apiDeleteMock.mockImplementation(() => new Promise<void>(() => {}))
    renderProveedores()

    await screen.findByText('Proveedor Uno SA')
    await userEvent.setup().click(bajaDe('Proveedor Uno SA'))
    const confirmar = screen.getByRole('button', { name: 'Confirmar baja' })
    await act(async () => {
      confirmar.click()
      confirmar.click()
      await Promise.resolve()
    })

    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
  })

  /** Cláusula bajo prueba: la ventana inerte completa (`bloqueado`) — ver `Empresas.test.tsx`. */
  it('durante el DELETE y su refresco no queda ninguna acción alcanzable', async () => {
    const usuario = userEvent.setup()
    let resolverDelete!: () => void
    let resolverRefresco!: (pagina: PaginaDe<ProveedorListado>) => void

    const otro = proveedorFixture({ id: 2, razonSocial: 'Proveedor Dos SA' })
    let cargas = 0
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/catalogos-fiscales/condiciones-fiscales') return Promise.resolve([])
      if (ruta !== '/proveedores') return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
      cargas += 1
      if (cargas === 1) return Promise.resolve(paginaFixture([proveedorFixture(), otro]))

      return new Promise<PaginaDe<ProveedorListado>>((resolver) => {
        resolverRefresco = resolver
      })
    })
    apiDeleteMock.mockImplementation(
      () =>
        new Promise<void>((resolver) => {
          resolverDelete = resolver
        }),
    )

    renderProveedores()
    await screen.findByText('Proveedor Uno SA')

    await usuario.click(bajaDe('Proveedor Uno SA'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    expect(screen.getByRole('button', { name: 'Dando de baja…' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Agregar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Buscar' })).toBeDisabled()
    for (const boton of [
      ...screen.getAllByRole('button', { name: 'Editar' }),
      ...screen.getAllByRole('button', { name: 'Eliminar' }),
      ...screen.getAllByRole('button', { name: 'Ver saldo' }),
    ]) {
      expect(boton).toBeDisabled()
    }

    await act(async () => {
      resolverDelete()
      await Promise.resolve()
    })

    await screen.findByText('Proveedor "Proveedor Uno SA" dado de baja.')
    // La tabla no se desmonta durante el refresco (sus filtros conservan el foco): las filas de antes quedan atenuadas
    // y sus acciones siguen inertes hasta que el refresco aterrice.
    expect(screen.getAllByRole('rowgroup')[1]).toHaveStyle({ opacity: '0.6' })
    for (const boton of [
      ...screen.getAllByRole('button', { name: 'Editar' }),
      ...screen.getAllByRole('button', { name: 'Eliminar' }),
    ]) {
      expect(boton).toBeDisabled()
    }

    await act(async () => {
      resolverRefresco(paginaFixture([otro]))
      await Promise.resolve()
    })

    await screen.findByText('Proveedor Dos SA')
    expect(screen.queryByText('Proveedor Uno SA')).not.toBeInTheDocument()
    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
  })

  /** Cláusula bajo prueba: `AVISO_REFRESCO_FALLIDO_BAJA` — ver `Empresas.test.tsx`. Un DELETE que
   * ya commiteó nunca se reporta como fallido, aunque el refresco posterior explote. */
  it('un refresco fallido después de la baja no la reporta como fallida', async () => {
    const usuario = userEvent.setup()
    let cargas = 0
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/catalogos-fiscales/condiciones-fiscales') return Promise.resolve([])
      if (ruta !== '/proveedores') return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
      cargas += 1
      if (cargas === 1) return Promise.resolve(paginaFixture([proveedorFixture()]))

      return Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó.'))
    })
    renderProveedores()
    await screen.findByText('Proveedor Uno SA')

    await usuario.click(bajaDe('Proveedor Uno SA'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await screen.findByText(
      'Proveedor "Proveedor Uno SA" dado de baja. Se eliminó, pero no se pudo actualizar la vista. Recargá la pantalla.',
    )
  })

  /** Cláusula bajo prueba: la elección de copia por `codigo` vía `copiaDeFalloDeBaja`, con el
   * sujeto `'el proveedor'`. */
  it('un 409 proveedor_en_uso rinde el mensaje del servidor y su guía, y deja la puerta abierta', async () => {
    const usuario = userEvent.setup()
    mockearRutasBase()
    apiDeleteMock.mockRejectedValue(
      new ErrorApi(409, 'proveedor_en_uso', 'No se puede dar de baja el proveedor porque tiene compras.'),
    )
    renderProveedores()

    await screen.findByText('Proveedor Uno SA')
    await usuario.click(bajaDe('Proveedor Uno SA'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await screen.findByText(/porque tiene compras/)
    expect(screen.getByText(/Reasigná esos datos o desactivá el proveedor/)).toBeInTheDocument()
    expect(screen.getByRole('alertdialog', { name: 'Confirmar baja' })).toBeInTheDocument()
  })

  /** Cláusula bajo prueba: el `setError('')` de `cancelarBaja` — ver `Empresas.test.tsx`. */
  it('cancelar después de un rechazo se lleva el motivo con la puerta', async () => {
    const usuario = userEvent.setup()
    mockearRutasBase()
    apiDeleteMock.mockRejectedValue(
      new ErrorApi(409, 'proveedor_en_uso', 'No se puede dar de baja el proveedor porque tiene compras.'),
    )
    renderProveedores()

    await screen.findByText('Proveedor Uno SA')
    await usuario.click(bajaDe('Proveedor Uno SA'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))
    await screen.findByText(/porque tiene compras/)

    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.queryByText(/porque tiene compras/)).not.toBeInTheDocument()
  })

  it('la baja del proveedor que se está editando se lleva también su formulario', async () => {
    const usuario = userEvent.setup()
    mockearRutasBase()
    renderProveedores()

    await screen.findByText('Proveedor Uno SA')
    await usuario.click(screen.getByRole('button', { name: 'Editar' }))
    expect(screen.getByRole('dialog', { name: 'Editar proveedor' })).toBeInTheDocument()

    await usuario.click(bajaDe('Proveedor Uno SA'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await screen.findByText('Proveedor "Proveedor Uno SA" dado de baja.')
    expect(screen.queryByRole('dialog', { name: 'Editar proveedor' })).not.toBeInTheDocument()
  })

  it('la baja del proveedor cuyo saldo está a la vista cierra también ese panel', async () => {
    const usuario = userEvent.setup()
    mockearRutasBase((ruta) =>
      ruta === '/proveedores/1/saldo' ? Promise.resolve({ idProveedor: 1, saldo: 0, compras: [] }) : undefined,
    )
    renderProveedores()

    await screen.findByText('Proveedor Uno SA')
    await usuario.click(screen.getByRole('button', { name: 'Ver saldo' }))
    await screen.findByText('Saldo de Proveedor Uno SA')

    await usuario.click(bajaDe('Proveedor Uno SA'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await screen.findByText('Proveedor "Proveedor Uno SA" dado de baja.')
    expect(screen.queryByText('Saldo de Proveedor Uno SA')).not.toBeInTheDocument()
  })
})

const CONDICION_RI = { id: 1, codigo: 'RI', nombre: 'Responsable Inscripto', codigoAfip: 1, activo: true }

const BUSCADOR = 'Buscar por razón social, nombre de fantasía o CUIT…'

function mockearConCondicion(items: ProveedorListado[] = [proveedorFixture()]) {
  mockearRutasBase((ruta) => {
    if (ruta === '/proveedores') return Promise.resolve(paginaFixture(items))
    if (ruta === '/catalogos-fiscales/condiciones-fiscales') return Promise.resolve([CONDICION_RI])
    return undefined
  })
}

async function abrirAlta(usuario: ReturnType<typeof userEvent.setup>) {
  await screen.findByText('Proveedor Uno SA')
  await usuario.click(screen.getByRole('button', { name: 'Agregar' }))
  await screen.findByRole('option', { name: 'Responsable Inscripto' })
}

describe('Proveedores — alta y edición en una modal', () => {
  it('la primera pantalla es solo la grilla: ni formulario ni diálogo', async () => {
    mockearConCondicion()
    renderProveedores()

    await screen.findByText('Proveedor Uno SA')

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(document.querySelector('form')).toBeNull()
  })

  it('agregar abre la modal con el título de alta y el formulario vacío', async () => {
    mockearConCondicion()
    const usuario = userEvent.setup()
    renderProveedores()

    await abrirAlta(usuario)

    const dialogo = screen.getByRole('dialog', { name: 'Nuevo proveedor' })
    expect(within(dialogo).getByLabelText('Razón social')).toHaveValue('')
    expect(within(dialogo).getByRole('button', { name: 'Guardar' })).toBeInTheDocument()
  })

  it('editar abre la modal con el título de edición y la fila cargada', async () => {
    mockearConCondicion([proveedorFixture({ nombreFantasia: 'Uno', telefono: '4444' })])
    const usuario = userEvent.setup()
    renderProveedores()

    await screen.findByText('Proveedor Uno SA')
    await usuario.click(screen.getByRole('button', { name: 'Editar' }))

    const dialogo = screen.getByRole('dialog', { name: 'Editar proveedor' })
    expect(within(dialogo).getByLabelText('Razón social')).toHaveValue('Proveedor Uno SA')
    expect(within(dialogo).getByLabelText('Nombre de fantasía')).toHaveValue('Uno')
    expect(within(dialogo).getByLabelText('Teléfono')).toHaveValue('4444')
  })

  it('cancelar cierra la modal sin escribir nada', async () => {
    mockearConCondicion()
    const usuario = userEvent.setup()
    renderProveedores()

    await abrirAlta(usuario)
    await usuario.type(screen.getByLabelText('Razón social'), 'Nuevo SA')
    await usuario.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
    expect(apiPutMock).not.toHaveBeenCalled()
  })

  it('un guardado exitoso cierra la modal, deja el aviso en la pantalla y recarga el listado', async () => {
    let cargas = 0
    mockearRutasBase((ruta) => {
      if (ruta === '/proveedores') {
        cargas += 1
        return Promise.resolve(
          paginaFixture(cargas === 1 ? [proveedorFixture()] : [proveedorFixture(), proveedorFixture({ id: 2, razonSocial: 'Nuevo SA' })]),
        )
      }
      if (ruta === '/catalogos-fiscales/condiciones-fiscales') return Promise.resolve([CONDICION_RI])
      return undefined
    })
    apiPostMock.mockResolvedValue(proveedorFixture({ id: 2 }))
    const usuario = userEvent.setup()
    renderProveedores()

    await abrirAlta(usuario)
    await usuario.type(screen.getByLabelText('Razón social'), 'Nuevo SA')
    await usuario.selectOptions(screen.getByLabelText('Condición fiscal'), '1')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await screen.findByText('Proveedor "Nuevo SA" creado.')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(apiPostMock).toHaveBeenCalledTimes(1)
    expect(await screen.findByRole('cell', { name: 'Nuevo SA' })).toBeInTheDocument()
  })

  /** `user-event` solo busca el botón de envío dentro del `<form>`, así que no puede simular Enter
   * sobre un botón del pie asociado por el atributo `form`: se prueba la asociación misma, que es
   * lo que hace que el navegador envíe con Enter. */
  it('el Guardar del pie pertenece al formulario del cuerpo', async () => {
    mockearConCondicion()
    const usuario = userEvent.setup()
    renderProveedores()

    await abrirAlta(usuario)

    const dialogo = screen.getByRole('dialog', { name: 'Nuevo proveedor' })
    expect(within(dialogo).getByRole('button', { name: 'Guardar' })).toHaveProperty('form', dialogo.querySelector('form'))
  })

  it('un guardado rechazado muestra el error adentro de la modal, la deja abierta y conserva lo tipeado', async () => {
    mockearConCondicion()
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'cuit_duplicado', 'Ya existe un proveedor con ese CUIT.'))
    const usuario = userEvent.setup()
    renderProveedores()

    await abrirAlta(usuario)
    await usuario.type(screen.getByLabelText('Razón social'), 'Nuevo SA')
    await usuario.selectOptions(screen.getByLabelText('Condición fiscal'), '1')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    const dialogo = screen.getByRole('dialog', { name: 'Nuevo proveedor' })
    expect(await within(dialogo).findByText('Ya existe un proveedor con ese CUIT.')).toBeInTheDocument()
    expect(within(dialogo).getByLabelText('Razón social')).toHaveValue('Nuevo SA')
    expect(within(dialogo).getByRole('button', { name: 'Guardar' })).toBeEnabled()
  })

  it('el error de un guardado anterior no sobrevive a cancelar y reabrir', async () => {
    mockearConCondicion()
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'cuit_duplicado', 'Ya existe un proveedor con ese CUIT.'))
    const usuario = userEvent.setup()
    renderProveedores()

    await abrirAlta(usuario)
    await usuario.type(screen.getByLabelText('Razón social'), 'Nuevo SA')
    await usuario.selectOptions(screen.getByLabelText('Condición fiscal'), '1')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))
    await screen.findByText('Ya existe un proveedor con ese CUIT.')

    await usuario.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancelar' }))
    await usuario.click(screen.getByRole('button', { name: 'Agregar' }))

    expect(screen.queryByText('Ya existe un proveedor con ese CUIT.')).not.toBeInTheDocument()
  })

  it('un guardado ya hecho cuyo refresco falla cierra la modal y avisa en la pantalla', async () => {
    let cargas = 0
    mockearRutasBase((ruta) => {
      if (ruta === '/proveedores') {
        cargas += 1
        return cargas === 1
          ? Promise.resolve(paginaFixture([proveedorFixture()]))
          : Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó.'))
      }
      if (ruta === '/catalogos-fiscales/condiciones-fiscales') return Promise.resolve([CONDICION_RI])
      return undefined
    })
    apiPostMock.mockResolvedValue(proveedorFixture({ id: 2 }))
    const usuario = userEvent.setup()
    renderProveedores()

    await abrirAlta(usuario)
    await usuario.type(screen.getByLabelText('Razón social'), 'Nuevo SA')
    await usuario.selectOptions(screen.getByLabelText('Condición fiscal'), '1')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await screen.findByText('Proveedor "Nuevo SA" creado. Se guardó, pero no se pudo actualizar la vista. Recargá la pantalla.')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('con el guardado en vuelo la modal no se cierra ni con la × ni con Escape, y un segundo envío se descarta', async () => {
    mockearConCondicion()
    apiPostMock.mockImplementation(() => new Promise<void>(() => {}))
    const usuario = userEvent.setup()
    renderProveedores()

    await abrirAlta(usuario)
    await usuario.type(screen.getByLabelText('Razón social'), 'Nuevo SA')
    await usuario.selectOptions(screen.getByLabelText('Condición fiscal'), '1')
    const guardar = screen.getByRole('button', { name: 'Guardar' })
    await act(async () => {
      guardar.click()
      guardar.click()
      await Promise.resolve()
    })

    expect(apiPostMock).toHaveBeenCalledTimes(1)
    expect(screen.getByRole('button', { name: 'Guardando…' })).toBeDisabled()
    expect(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cerrar' })).toBeDisabled()
    await usuario.keyboard('{Escape}')
    expect(screen.getByRole('dialog', { name: 'Nuevo proveedor' })).toBeInTheDocument()
  })
})


describe('Proveedores — impuestos de la compra', () => {
  it('el alta manda los tres flags apagados por defecto', async () => {
    mockearRutasBase((ruta) =>
      ruta === '/catalogos-fiscales/condiciones-fiscales'
        ? Promise.resolve([{ id: 1, codigo: 'RI', nombre: 'Responsable Inscripto', codigoAfip: 1, activo: true }])
        : undefined,
    )
    apiPostMock.mockResolvedValue(proveedorFixture({ id: 2 }))
    const usuario = userEvent.setup()

    renderProveedores()
    await screen.findByText('Proveedor Uno SA')
    await usuario.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByRole('option', { name: 'Responsable Inscripto' })
    await usuario.type(screen.getByLabelText('Razón social'), 'Nuevo SA')
    await usuario.selectOptions(screen.getByLabelText('Condición fiscal'), '1')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    expect(apiPostMock.mock.calls[0][1]).toMatchObject({
      percibeIibb: false,
      percibeIva: false,
      preciosIncluyenIva: false,
    })
  })

  it('editar trae los flags del proveedor y manda los que se cambiaron', async () => {
    mockearRutasBase((ruta) => {
      if (ruta === '/proveedores') {
        return Promise.resolve(paginaFixture([proveedorFixture({ percibeIibb: true, preciosIncluyenIva: true })]))
      }
      if (ruta === '/catalogos-fiscales/condiciones-fiscales') {
        return Promise.resolve([{ id: 1, codigo: 'RI', nombre: 'Responsable Inscripto', codigoAfip: 1, activo: true }])
      }
      return undefined
    })
    apiPutMock.mockResolvedValue(proveedorFixture())
    const usuario = userEvent.setup()

    renderProveedores()
    await screen.findByText('Proveedor Uno SA')
    await usuario.click(screen.getByRole('button', { name: 'Editar' }))
    await screen.findByRole('option', { name: 'Responsable Inscripto' })

    expect(screen.getByLabelText('Percibe IIBB')).toBeChecked()
    expect(screen.getByLabelText('Percibe IVA')).not.toBeChecked()
    expect(screen.getByLabelText('Sus precios incluyen IVA')).toBeChecked()

    await usuario.click(screen.getByLabelText('Percibe IIBB'))
    await usuario.click(screen.getByLabelText('Percibe IVA'))
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    expect(apiPutMock.mock.calls[0][1]).toMatchObject({
      percibeIibb: false,
      percibeIva: true,
      preciosIncluyenIva: true,
    })
  })
})

describe('Proveedores — error y foco de la modal', () => {
  it('un guardado rechazado expone el error como alerta adentro del diálogo', async () => {
    mockearConCondicion()
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'cuit_duplicado', 'Ya existe un proveedor con ese CUIT.'))
    const usuario = userEvent.setup()
    renderProveedores()

    await abrirAlta(usuario)
    await usuario.type(screen.getByLabelText('Razón social'), 'Nuevo SA')
    await usuario.selectOptions(screen.getByLabelText('Condición fiscal'), '1')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    const alerta = await within(screen.getByRole('dialog')).findByRole('alert')
    expect(alerta).toHaveTextContent('Ya existe un proveedor con ese CUIT.')
  })

  it('al abrir, el foco queda en el primer campo y no en la ×', async () => {
    mockearConCondicion()
    const usuario = userEvent.setup()
    renderProveedores()

    await abrirAlta(usuario)

    await waitFor(() => expect(within(screen.getByRole('dialog')).getByLabelText('Razón social')).toHaveFocus())
  })

  it('con el refresco en vuelo tras guardar, el foco queda en el contenedor de la pantalla y no en <body>', async () => {
    let liberarRefresco: (pagina: PaginaDe<ProveedorListado>) => void = () => {}
    let cargas = 0
    mockearRutasBase((ruta) => {
      if (ruta === '/proveedores') {
        cargas += 1
        return cargas === 1
          ? Promise.resolve(paginaFixture([proveedorFixture()]))
          : new Promise<PaginaDe<ProveedorListado>>((resolver) => {
              liberarRefresco = resolver
            })
      }
      if (ruta === '/catalogos-fiscales/condiciones-fiscales') return Promise.resolve([CONDICION_RI])
      return undefined
    })
    apiPostMock.mockResolvedValue(proveedorFixture({ id: 2 }))
    const usuario = userEvent.setup()
    const { container } = renderProveedores()

    await abrirAlta(usuario)
    await usuario.type(screen.getByLabelText('Razón social'), 'Nuevo SA')
    await usuario.selectOptions(screen.getByLabelText('Condición fiscal'), '1')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await screen.findByText('Proveedor "Nuevo SA" creado.')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Agregar' })).toBeDisabled()
    expect(document.activeElement).not.toBe(document.body)
    expect(document.activeElement).toBe(container.firstElementChild)

    await act(async () => liberarRefresco(paginaFixture([proveedorFixture()])))
    expect(screen.getByRole('button', { name: 'Agregar' })).toBeEnabled()
    expect(document.activeElement).toBe(container.firstElementChild)
  })

  it('cancelar devuelve el foco al botón que abrió la modal', async () => {
    mockearConCondicion()
    const usuario = userEvent.setup()
    renderProveedores()

    await abrirAlta(usuario)
    await usuario.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancelar' }))

    expect(screen.getByRole('button', { name: 'Agregar' })).toHaveFocus()
  })
})

describe('Proveedores — empresa de la fila al guardar', () => {
  function mockearUnProveedor(idEmpresa: number | null) {
    mockearRutasBase((ruta) => {
      if (ruta === '/proveedores') return Promise.resolve(paginaFixture([proveedorFixture({ idEmpresa })]))
      if (ruta === '/catalogos-fiscales/condiciones-fiscales') {
        return Promise.resolve([{ id: 1, codigo: 'RI', nombre: 'Responsable Inscripto', codigoAfip: 1, activo: true }])
      }
      return undefined
    })
  }

  it('editar un proveedor de una empresa manda la empresa de la fila en el PUT', async () => {
    mockearUnProveedor(5)
    apiPutMock.mockResolvedValue(proveedorFixture())
    const usuario = userEvent.setup()

    renderProveedores()
    await screen.findByText('Proveedor Uno SA')
    await usuario.click(screen.getByRole('button', { name: 'Editar' }))
    await screen.findByRole('option', { name: 'Responsable Inscripto' })
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    expect(apiPutMock.mock.calls[0][1]).toMatchObject({ idEmpresa: 5 })
  })

  it('editar un proveedor de todo el tenant manda null', async () => {
    mockearUnProveedor(null)
    apiPutMock.mockResolvedValue(proveedorFixture())
    const usuario = userEvent.setup()

    renderProveedores()
    await screen.findByText('Proveedor Uno SA')
    await usuario.click(screen.getByRole('button', { name: 'Editar' }))
    await screen.findByRole('option', { name: 'Responsable Inscripto' })
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    expect(apiPutMock.mock.calls[0][1]).toMatchObject({ idEmpresa: null })
  })
})

function diferido<T>() {
  let resolver!: (valor: T) => void
  let rechazar!: (motivo: unknown) => void
  const promesa = new Promise<T>((res, rej) => {
    resolver = res
    rechazar = rej
  })

  return { promesa, resolver, rechazar }
}

function lecturasDelListado(): string[] {
  return apiGetMock.mock.calls.map((c) => String(c[0])).filter((r) => r === '/proveedores' || r.startsWith('/proveedores?'))
}

describe('Proveedores — lectura en vuelo al guardar y término aplicado', () => {
  /** Cláusula bajo prueba: la relectura de la rama de fallo de `guardar` (`habiaLecturaEnVuelo`). */
  it('un guardado rechazado con la lectura inicial en vuelo no deja la tabla en el spinner', async () => {
    const inicial = diferido<PaginaDe<ProveedorListado>>()
    const post = diferido<ProveedorListado>()
    let lecturas = 0
    mockearRutasBase((ruta) => {
      if (ruta === '/proveedores') {
        return ++lecturas === 1 ? inicial.promesa : Promise.resolve(paginaFixture([proveedorFixture()]))
      }
      if (ruta === '/catalogos-fiscales/condiciones-fiscales') return Promise.resolve([CONDICION_RI])
      return undefined
    })
    apiPostMock.mockReturnValue(post.promesa)
    const usuario = userEvent.setup()
    renderProveedores()

    await usuario.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByRole('option', { name: 'Responsable Inscripto' })
    await usuario.type(screen.getByLabelText('Razón social'), 'Nuevo SA')
    await usuario.selectOptions(screen.getByLabelText('Condición fiscal'), '1')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))
    await act(async () => {
      inicial.resolver(paginaFixture([proveedorFixture()]))
      await inicial.promesa
    })
    await act(async () => {
      post.rechazar(new ErrorApi(409, 'cuit_duplicado', 'Ya existe un proveedor con ese CUIT.'))
      await post.promesa.catch(() => {})
    })

    expect(await screen.findByText('Proveedor Uno SA')).toBeInTheDocument()
    expect(screen.queryByText('Cargando…')).not.toBeInTheDocument()
    expect(lecturas).toBe(2)
    const dialogo = screen.getByRole('dialog', { name: 'Nuevo proveedor' })
    expect(within(dialogo).getByText('Ya existe un proveedor con ese CUIT.')).toBeInTheDocument()
  })

  /** Cláusula bajo prueba: la condición `habiaLecturaEnVuelo`. Con el listado ya en pantalla, un
   * rechazo no lo vuelve a pedir. */
  it('un guardado rechazado con el listado ya cargado no lo vuelve a pedir', async () => {
    mockearConCondicion()
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'cuit_duplicado', 'Ya existe un proveedor con ese CUIT.'))
    const usuario = userEvent.setup()
    renderProveedores()

    await abrirAlta(usuario)
    await usuario.type(screen.getByLabelText('Razón social'), 'Nuevo SA')
    await usuario.selectOptions(screen.getByLabelText('Condición fiscal'), '1')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await screen.findByText('Ya existe un proveedor con ese CUIT.')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Guardar' })).toBeEnabled())
    expect(lecturasDelListado()).toHaveLength(1)
    expect(screen.getByText('Proveedor Uno SA')).toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `consultaRef` en el refresco posterior a la escritura. El texto tipeado
   * y todavía no enviado no viaja; el último término enviado, sí. */
  it.each([
    { enviado: '', tipeado: 'zzz', esperada: '/proveedores' },
    { enviado: 'uno', tipeado: 'uno zzz', esperada: '/proveedores?busqueda=uno' },
  ])('el refresco tras guardar recarga con el término aplicado "$enviado", no con "$tipeado"', async ({ enviado, tipeado, esperada }) => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/proveedores' || ruta.startsWith('/proveedores?')) return Promise.resolve(paginaFixture([proveedorFixture()]))
      if (ruta === '/catalogos-fiscales/condiciones-fiscales') return Promise.resolve([CONDICION_RI])
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })
    apiPostMock.mockResolvedValue(proveedorFixture({ id: 2 }))
    const usuario = userEvent.setup()
    renderProveedores()
    await screen.findByText('Proveedor Uno SA')
    if (enviado) {
      await usuario.type(screen.getByPlaceholderText(BUSCADOR), enviado)
      await usuario.click(screen.getByRole('button', { name: 'Buscar' }))
      await waitFor(() => expect(apiGetMock).toHaveBeenCalledWith(esperada))
    }
    await usuario.clear(screen.getByPlaceholderText(BUSCADOR))
    await usuario.type(screen.getByPlaceholderText(BUSCADOR), tipeado)
    await usuario.click(screen.getByRole('button', { name: 'Agregar' }))
    await screen.findByRole('option', { name: 'Responsable Inscripto' })
    await usuario.type(screen.getByLabelText('Razón social'), 'Nuevo SA')
    await usuario.selectOptions(screen.getByLabelText('Condición fiscal'), '1')
    const antes = lecturasDelListado().length

    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(lecturasDelListado()).toHaveLength(antes + 1))
    expect(lecturasDelListado().at(-1)).toBe(esperada)
    expect(lecturasDelListado().some((r) => r.includes('zzz'))).toBe(false)
  })
})
