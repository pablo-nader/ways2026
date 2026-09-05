import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Empresas } from './Empresas'
import { ErrorApi } from '../api/cliente'
import { ETIQUETA_SIN_DUENIO } from '../api/organizacion'
import { ROL } from '../api/tipos'
import type { EmpresaListado, UsuarioAutenticado } from '../api/tipos'

// stage-20-organizacion-relaciones-y-bajas, slice 2 (tareas 2.10 y 2.13) y slice 5 (5.4, 5.7, 5.8).

const apiGetMock = vi.fn()
const apiPutMock = vi.fn()
const apiDeleteMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: vi.fn(),
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

function usuarioFixture(sobrescribir: Partial<UsuarioAutenticado> = {}): UsuarioAutenticado {
  return {
    id: 9,
    usuario: 'root',
    mail: 'root@ways.test',
    rolId: ROL.Root,
    rol: 'Root',
    ultimaConexion: null,
    idTenant: null,
    ...sobrescribir,
  }
}

let usuarioActual: UsuarioAutenticado | null = usuarioFixture()

vi.mock('../auth/useAuth', () => ({
  useAuth: () => ({ usuario: usuarioActual, cargando: false, iniciarSesion: vi.fn(), cerrarSesion: vi.fn() }),
}))

const empresaSur: EmpresaListado = {
  id: 10,
  idTenant: 2,
  razonSocial: 'Sur SRL',
  nombreFantasia: null,
  cuit: null,
  nombreTenant: 'Comercio Sur',
}

const empresaAnexo: EmpresaListado = {
  id: 11,
  idTenant: 2,
  razonSocial: 'Sur Anexo SA',
  nombreFantasia: null,
  cuit: null,
  nombreTenant: 'Comercio Sur',
}

const empresaEste: EmpresaListado = {
  id: 12,
  idTenant: 3,
  razonSocial: 'Este SRL',
  nombreFantasia: null,
  cuit: null,
  nombreTenant: 'Almacén Este',
}

function montar(items: EmpresaListado[] = [empresaSur, empresaAnexo, empresaEste]) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/empresas') return Promise.resolve(items)

    return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
  })

  return render(<Empresas />)
}

function razonesSocialesVisibles() {
  return screen
    .getAllByRole('row')
    .slice(1)
    .map((f) => within(f).getAllByRole('cell')[2]?.textContent ?? '')
}

describe('Empresas (stage-20, slice 2 — nombre de tenant y filtro)', () => {
  beforeEach(() => {
    apiGetMock.mockReset()
    apiPutMock.mockReset()
    usuarioActual = usuarioFixture()
  })

  it('rinde el NOMBRE del tenant en la columna, nunca el id', async () => {
    montar()
    await waitFor(() => expect(screen.getByText('Sur SRL')).toBeInTheDocument())

    const fila = screen.getByRole('row', { name: /Sur SRL/ })
    const celdas = within(fila).getAllByRole('cell')

    // Columnas: ID · Tenant · Razón social · Nombre de fantasía · CUIT · Acciones
    expect(celdas[1]).toHaveTextContent('Comercio Sur')
    expect(celdas[1]).not.toHaveTextContent('2')
  })

  /** Tarea 2.13: ningún `<td>` presenta `idTenant` como identidad del dueño; el id sobrevive
   * solo como `value` del `<option>` del filtro. */
  it('no presenta idTenant como identidad del dueño en ninguna celda', async () => {
    montar()
    await waitFor(() => expect(screen.getByText('Sur SRL')).toBeInTheDocument())

    for (const fila of screen.getAllByRole('row').slice(1)) {
      expect(within(fila).getAllByRole('cell')[1]).not.toHaveTextContent(/^\d+$/)
    }

    expect(screen.getByLabelText('Tenant')).toHaveValue('')
    expect(within(screen.getByLabelText('Tenant')).getByRole('option', { name: 'Comercio Sur' })).toHaveValue('2')
  })

  /** Un `nombreTenant` nulo con `idTenant` presente es el HUÉRFANO de design D13: se rinde como
   * anomalía, y jamás como "Plataforma" (Reconciliación 9). */
  it('una empresa cuyo tenant fue dado de baja rinde la marca de sin dueño, no "Plataforma"', async () => {
    montar([{ ...empresaSur, nombreTenant: null }])
    await waitFor(() => expect(screen.getByText('Sur SRL')).toBeInTheDocument())

    const celdas = within(screen.getByRole('row', { name: /Sur SRL/ })).getAllByRole('cell')
    expect(celdas[1]).toHaveTextContent(ETIQUETA_SIN_DUENIO)
    expect(screen.queryByText('Plataforma')).not.toBeInTheDocument()
  })

  it('elegir un tenant angosta las filas SIN pedir nada más a la API', async () => {
    const usuario = userEvent.setup()
    montar()
    await waitFor(() => expect(screen.getByText('Sur SRL')).toBeInTheDocument())

    const llamadasAlCargar = apiGetMock.mock.calls.length
    await usuario.selectOptions(screen.getByLabelText('Tenant'), '3')

    expect(razonesSocialesVisibles()).toEqual(['Este SRL'])
    expect(apiGetMock.mock.calls).toHaveLength(llamadasAlCargar)
  })

  it('limpiar el filtro restaura la lista cargada completa', async () => {
    const usuario = userEvent.setup()
    montar()
    await waitFor(() => expect(screen.getByText('Sur SRL')).toBeInTheDocument())

    await usuario.selectOptions(screen.getByLabelText('Tenant'), '3')
    expect(razonesSocialesVisibles()).toEqual(['Este SRL'])

    await usuario.selectOptions(screen.getByLabelText('Tenant'), '')
    expect(razonesSocialesVisibles()).toEqual(['Sur SRL', 'Sur Anexo SA', 'Este SRL'])
  })

  /**
   * Cláusula bajo prueba: la ventana inerte cubre TODA la escritura y su refresco
   * (`react-async-state` reglas 5 y 9), no solo el botón de Guardar. Mientras el PUT está en
   * vuelo, abrir OTRA fila supersedería una escritura a medio terminar, así que "Editar" también
   * tiene que estar inerte; y mientras el refresco posterior está en vuelo no hay tabla en
   * pantalla, así que no queda ninguna acción alcanzable.
   */
  it('durante el guardado y su refresco no queda ninguna acción alcanzable', async () => {
    const usuario = userEvent.setup()
    let resolverPut!: (empresa: EmpresaListado) => void
    let resolverRefresco!: (items: EmpresaListado[]) => void

    let cargas = 0
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta !== '/empresas') return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
      cargas += 1
      if (cargas === 1) return Promise.resolve([empresaSur, empresaEste])

      return new Promise<EmpresaListado[]>((resolver) => {
        resolverRefresco = resolver
      })
    })
    apiPutMock.mockImplementation(
      () =>
        new Promise<EmpresaListado>((resolver) => {
          resolverPut = resolver
        }),
    )

    render(<Empresas />)
    await waitFor(() => expect(screen.getByText('Sur SRL')).toBeInTheDocument())

    await usuario.click(screen.getAllByRole('button', { name: 'Editar' })[0])
    await usuario.click(screen.getByRole('button', { name: /Guardar/ }))

    // PUT en vuelo: el formulario y TODOS los "Editar" de la tabla están inertes.
    expect(screen.getByRole('button', { name: 'Guardando…' })).toBeDisabled()
    expect(screen.getByLabelText('Razón social')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    for (const boton of screen.getAllByRole('button', { name: 'Editar' })) {
      expect(boton).toBeDisabled()
    }

    await act(async () => {
      resolverPut(empresaSur)
      await Promise.resolve()
    })

    // Refresco en vuelo: el aviso de éxito ya está, la tabla no — no hay acción que apretar.
    await waitFor(() => expect(screen.getByText('Se actualizó "Sur SRL".')).toBeInTheDocument())
    expect(screen.getByText('Cargando…')).toBeInTheDocument()
    expect(screen.queryAllByRole('button', { name: 'Editar' })).toHaveLength(0)

    await act(async () => {
      resolverRefresco([empresaSur, empresaEste])
      await Promise.resolve()
    })

    await waitFor(() => expect(screen.getAllByRole('button', { name: 'Editar' })[0]).toBeEnabled())
  })

  /**
   * Cláusula bajo prueba: el `esPlataforma &&` que gatea el filtro por tenant, con el MISMO
   * criterio que ya gateaba la columna. Un admin de tenant solo puede recibir filas de su propio
   * tenant (S5), así que el filtro le ofrecía una única opción y no angostaba nada: un control
   * muerto. La otra mitad —que un actor de plataforma SÍ lo ve— la cubre el test del filtro.
   */
  it('un admin de tenant no ve el filtro por tenant, igual que no ve la columna', async () => {
    usuarioActual = usuarioFixture({ id: 4, usuario: 'admin', rolId: ROL.Admin, rol: 'Admin', idTenant: 2 })
    montar([empresaSur, empresaAnexo])
    await waitFor(() => expect(screen.getByText('Sur SRL')).toBeInTheDocument())

    expect(screen.queryByLabelText('Tenant')).not.toBeInTheDocument()
    expect(screen.queryByRole('columnheader', { name: 'Tenant' })).not.toBeInTheDocument()
    expect(screen.queryByText('Almacén Este')).not.toBeInTheDocument()
  })
})

// stage-20-organizacion-relaciones-y-bajas, slice 5 (tareas 5.4, 5.7 y 5.8). El patrón es el
// MISMO que el de `Tenants.tsx` y se replica en la misma PR (`react-async-state` regla 10).

function botonDeBajaDe(razonSocial: string) {
  return within(screen.getByRole('row', { name: new RegExp(razonSocial) })).getByRole('button', {
    name: 'Baja',
  })
}

describe('Empresas (stage-20, slice 5 — baja lógica)', () => {
  beforeEach(() => {
    apiGetMock.mockReset()
    apiPutMock.mockReset()
    apiDeleteMock.mockReset()
    apiDeleteMock.mockResolvedValue(undefined)
    usuarioActual = usuarioFixture()
  })

  it('el botón de baja no llama a la API hasta que se confirma', async () => {
    const usuario = userEvent.setup()
    montar()
    await waitFor(() => expect(screen.getByText('Sur SRL')).toBeInTheDocument())

    await usuario.click(botonDeBajaDe('Sur SRL'))
    expect(apiDeleteMock).not.toHaveBeenCalled()
    expect(screen.getByRole('alertdialog', { name: 'Confirmar baja' })).toHaveTextContent(
      '¿Dar de baja la empresa "Sur SRL"?',
    )

    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))
    await waitFor(() => expect(apiDeleteMock).toHaveBeenCalledWith('/empresas/10'))
    await waitFor(() => expect(screen.getByText('Se dio de baja la empresa "Sur SRL".')).toBeInTheDocument())
  })

  it('cancelar cierra la puerta y no llama nunca a la API', async () => {
    const usuario = userEvent.setup()
    montar()
    await waitFor(() => expect(screen.getByText('Sur SRL')).toBeInTheDocument())

    await usuario.click(botonDeBajaDe('Sur SRL'))
    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(apiDeleteMock).not.toHaveBeenCalled()
  })

  /** Cláusula bajo prueba: la ventana inerte completa — ver el test gemelo de `Tenants.test.tsx`. */
  it('durante el DELETE y su refresco no queda ninguna acción alcanzable', async () => {
    const usuario = userEvent.setup()
    let resolverDelete!: () => void
    let resolverRefresco!: (items: EmpresaListado[]) => void

    let cargas = 0
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta !== '/empresas') return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
      cargas += 1
      if (cargas === 1) return Promise.resolve([empresaSur, empresaEste])

      return new Promise<EmpresaListado[]>((resolver) => {
        resolverRefresco = resolver
      })
    })
    apiDeleteMock.mockImplementation(
      () =>
        new Promise<void>((resolver) => {
          resolverDelete = resolver
        }),
    )

    render(<Empresas />)
    await waitFor(() => expect(screen.getByText('Sur SRL')).toBeInTheDocument())

    await usuario.click(botonDeBajaDe('Sur SRL'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    expect(screen.getByRole('button', { name: 'Dando de baja…' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    for (const boton of [
      ...screen.getAllByRole('button', { name: 'Editar' }),
      ...screen.getAllByRole('button', { name: 'Baja' }),
    ]) {
      expect(boton).toBeDisabled()
    }

    await act(async () => {
      resolverDelete()
      await Promise.resolve()
    })

    await waitFor(() => expect(screen.getByText('Se dio de baja la empresa "Sur SRL".')).toBeInTheDocument())
    expect(screen.getByText('Cargando…')).toBeInTheDocument()

    await act(async () => {
      resolverRefresco([empresaEste])
      await Promise.resolve()
    })

    await waitFor(() => expect(screen.getAllByRole('button', { name: 'Baja' })[0]).toBeEnabled())
    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
  })

  /** Cláusula bajo prueba: `ocupadoRef`, la guarda de re-entrancia del mismo tick (regla 9). */
  it('un segundo click sobre la confirmación en vuelo se descarta', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockImplementation(() => new Promise<void>(() => {}))
    montar()
    await waitFor(() => expect(screen.getByText('Sur SRL')).toBeInTheDocument())

    await usuario.click(botonDeBajaDe('Sur SRL'))
    const confirmar = screen.getByRole('button', { name: 'Confirmar baja' })
    await act(async () => {
      confirmar.click()
      confirmar.click()
      await Promise.resolve()
    })

    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
  })

  it('un refresco fallido después de la baja no la reporta como fallida', async () => {
    const usuario = userEvent.setup()
    let cargas = 0
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta !== '/empresas') return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
      cargas += 1
      if (cargas === 1) return Promise.resolve([empresaSur])

      return Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó.'))
    })

    render(<Empresas />)
    await waitFor(() => expect(screen.getByText('Sur SRL')).toBeInTheDocument())

    await usuario.click(botonDeBajaDe('Sur SRL'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await waitFor(() =>
      expect(
        screen.getByText(
          'Se dio de baja la empresa "Sur SRL". Se eliminó, pero no se pudo actualizar la vista. Recargá la pantalla.',
        ),
      ).toBeInTheDocument(),
    )
  })

  /**
   * Cláusula bajo prueba: la elección de copia por `codigo`. `ultima_empresa_del_tenant` es el
   * mínimo estructural, que es OTRA cosa que `empresa_en_uso`: la acción que corresponde no es
   * limpiar datos, es dar de baja el tenant. Con una copia genérica compartida el operador se iba
   * a borrar filas que no eran el problema.
   */
  it('un 409 ultima_empresa_del_tenant rinde su guía propia y el mensaje del servidor', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockRejectedValue(
      new ErrorApi(
        409,
        'ultima_empresa_del_tenant',
        'Es la única empresa del tenant: si querés eliminarla, dá de baja el tenant.',
      ),
    )
    montar()
    await waitFor(() => expect(screen.getByText('Sur SRL')).toBeInTheDocument())

    await usuario.click(botonDeBajaDe('Sur SRL'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await waitFor(() => expect(screen.getByText(/Es la única empresa del tenant/)).toBeInTheDocument())
    expect(screen.getByText(/La baja del tenant se hace desde la pantalla de Tenants\./)).toBeInTheDocument()
  })

  /** Anti-oráculo (BO-R12) en la capa de UI: un 404 nunca insinúa uso ni alcance. */
  it('un 404 rinde la copia neutra de inexistencia, nunca una pista de uso', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'No existe la empresa 10.'))
    montar()
    await waitFor(() => expect(screen.getByText('Sur SRL')).toBeInTheDocument())

    await usuario.click(botonDeBajaDe('Sur SRL'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await waitFor(() =>
      expect(
        screen.getByText('No se pudo dar de baja la empresa. Ya no existe o no está a tu alcance. Actualizá el listado.'),
      ).toBeInTheDocument(),
    )
    expect(screen.queryByText(/en uso|tiene \d+/)).not.toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba: que el botón de baja NO esté gateado en `esPlataforma`, a diferencia de
   * la columna y el filtro de tenant. `DELETE /api/empresas/{id}` reusa la policy del grupo
   * (`GestionDeOrganizacion`, root+admin), así que un admin de tenant puede dar de baja las
   * propias — esconderle el botón sería una restricción que el servidor no tiene.
   */
  it('un admin de tenant ve el botón de baja, que su policy sí le permite', async () => {
    usuarioActual = usuarioFixture({ id: 4, usuario: 'admin', rolId: ROL.Admin, rol: 'Admin', idTenant: 2 })
    montar([empresaSur, empresaAnexo])
    await waitFor(() => expect(screen.getByText('Sur SRL')).toBeInTheDocument())

    expect(botonDeBajaDe('Sur SRL')).toBeEnabled()
  })
})
