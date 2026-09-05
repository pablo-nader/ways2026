import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Empresas } from './Empresas'
import { ETIQUETA_SIN_DUENIO } from '../api/organizacion'
import { ROL } from '../api/tipos'
import type { EmpresaListado, UsuarioAutenticado } from '../api/tipos'

// stage-20-organizacion-relaciones-y-bajas, slice 2 (tareas 2.10 y 2.13).

const apiGetMock = vi.fn()
const apiPutMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: vi.fn(),
    put: (...args: unknown[]) => apiPutMock(...(args as [string, unknown])),
    delete: vi.fn(),
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

  it('las opciones del filtro se deducen de las filas cargadas: un tenant, una opción (S5)', async () => {
    usuarioActual = usuarioFixture({ id: 4, usuario: 'admin', rolId: ROL.Admin, rol: 'Admin', idTenant: 2 })
    montar([empresaSur, empresaAnexo])
    await waitFor(() => expect(screen.getByText('Sur SRL')).toBeInTheDocument())

    const opciones = within(screen.getByLabelText('Tenant')).getAllByRole('option')
    expect(opciones.map((o) => o.textContent)).toEqual(['Todos', 'Comercio Sur'])
    expect(screen.queryByText('Almacén Este')).not.toBeInTheDocument()
  })
})
