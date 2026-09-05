import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { PuntosVenta } from './PuntosVenta'
import { ETIQUETA_SIN_DUENIO } from '../api/organizacion'
import { ROL } from '../api/tipos'
import type { PuntoVentaListado, UsuarioAutenticado } from '../api/tipos'

// stage-20-organizacion-relaciones-y-bajas, slice 2 (tareas 2.11 y 2.13).

const apiGetMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: vi.fn(),
    put: vi.fn(),
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

function pvFixture(sobrescribir: Partial<PuntoVentaListado> = {}): PuntoVentaListado {
  return {
    id: 100,
    idTenant: 2,
    idEmpresa: 20,
    nombre: 'PV Centro',
    domicilio: null,
    horario: null,
    whatsapp: null,
    instagram: null,
    facebook: null,
    web: null,
    nombreTenant: 'Comercio Sur',
    razonSocialEmpresa: 'Sur SRL',
    ...sobrescribir,
  }
}

// Dos tenants, y el tenant 2 con DOS empresas: sin la segunda empresa del mismo dueño el
// angostamiento del filtro de empresa no tendría nada que angostar.
const pvSurCentro = pvFixture()
const pvSurAnexo = pvFixture({ id: 101, idEmpresa: 21, nombre: 'PV Anexo', razonSocialEmpresa: 'Sur Anexo SA' })
const pvEste = pvFixture({
  id: 102,
  idTenant: 3,
  idEmpresa: 30,
  nombre: 'PV Este',
  nombreTenant: 'Almacén Este',
  razonSocialEmpresa: 'Este SRL',
})

function montar(items: PuntoVentaListado[] = [pvSurCentro, pvSurAnexo, pvEste]) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/puntos-venta') return Promise.resolve(items)

    return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
  })

  return render(<PuntosVenta />)
}

/** Columnas (root): ID · Tenant · Empresa · Nombre · Domicilio · Acciones. */
function nombresVisibles() {
  return screen
    .getAllByRole('row')
    .slice(1)
    .map((f) => within(f).getAllByRole('cell')[3]?.textContent ?? '')
}

function opcionesDe(etiqueta: string) {
  return within(screen.getByLabelText(etiqueta))
    .getAllByRole('option')
    .map((o) => o.textContent)
}

describe('PuntosVenta (stage-20, slice 2 — nombres de dueño y dos filtros)', () => {
  beforeEach(() => {
    apiGetMock.mockReset()
    usuarioActual = usuarioFixture()
  })

  it('rinde los DOS nombres de dueño, nunca los dos enteros', async () => {
    montar()
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    const celdas = within(screen.getByRole('row', { name: /PV Centro/ })).getAllByRole('cell')
    expect(celdas[1]).toHaveTextContent('Comercio Sur')
    expect(celdas[2]).toHaveTextContent('Sur SRL')
    expect(celdas[1]).not.toHaveTextContent('2')
    expect(celdas[2]).not.toHaveTextContent('20')
  })

  /** Tarea 2.13: ni `idTenant` ni `idEmpresa` se presentan como identidad de un dueño; los dos
   * sobreviven solo como `value` de los `<option>` de los filtros. */
  it('no presenta idTenant ni idEmpresa como identidad del dueño en ninguna celda', async () => {
    montar()
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    for (const fila of screen.getAllByRole('row').slice(1)) {
      const celdas = within(fila).getAllByRole('cell')
      expect(celdas[1]).not.toHaveTextContent(/^\d+$/)
      expect(celdas[2]).not.toHaveTextContent(/^\d+$/)
    }

    expect(within(screen.getByLabelText('Tenant')).getByRole('option', { name: 'Comercio Sur' })).toHaveValue('2')
    expect(within(screen.getByLabelText('Empresa')).getByRole('option', { name: 'Sur SRL' })).toHaveValue('20')
  })

  it('un punto de venta huérfano rinde la marca de sin dueño en las dos columnas', async () => {
    montar([pvFixture({ nombreTenant: null, razonSocialEmpresa: null })])
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    const celdas = within(screen.getByRole('row', { name: /PV Centro/ })).getAllByRole('cell')
    expect(celdas[1]).toHaveTextContent(ETIQUETA_SIN_DUENIO)
    expect(celdas[2]).toHaveTextContent(ETIQUETA_SIN_DUENIO)
    expect(screen.queryByText('Plataforma')).not.toBeInTheDocument()
  })

  it('elegir un tenant angosta las filas y las opciones del filtro de empresa (D15)', async () => {
    const usuario = userEvent.setup()
    montar()
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    expect(opcionesDe('Empresa')).toEqual(['Todas', 'Este SRL', 'Sur Anexo SA', 'Sur SRL'])

    const llamadasAlCargar = apiGetMock.mock.calls.length
    await usuario.selectOptions(screen.getByLabelText('Tenant'), '2')

    expect(nombresVisibles()).toEqual(['PV Centro', 'PV Anexo'])
    expect(opcionesDe('Empresa')).toEqual(['Todas', 'Sur Anexo SA', 'Sur SRL'])
    expect(apiGetMock.mock.calls).toHaveLength(llamadasAlCargar)
  })

  /** Cláusula bajo prueba: `cambiarFiltroDeTenant` limpia la empresa elegida SOLO cuando deja de
   * pertenecer al tenant nuevo. Las dos ramas se ejercitan: la que limpia y la que respeta. */
  it('elegir un tenant limpia la empresa que ya no le pertenece, y respeta la que sí', async () => {
    const usuario = userEvent.setup()
    montar()
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    await usuario.selectOptions(screen.getByLabelText('Empresa'), '30')
    expect(nombresVisibles()).toEqual(['PV Este'])

    await usuario.selectOptions(screen.getByLabelText('Tenant'), '2')
    expect(screen.getByLabelText('Empresa')).toHaveValue('')
    expect(nombresVisibles()).toEqual(['PV Centro', 'PV Anexo'])

    // Confundidor a esquivar (`mutation-proof-tests` regla 3): la deriva de `empresaVigente` sola
    // ya blanquea el `<select>`, así que asertar eso no distingue "limpió el estado" de "no lo
    // limpió pero la opción no existe". Lo que solo la limpieza REAL del estado produce es que
    // volver a "Todos" NO resucite la empresa 30: si `filtroEmpresa` siguiera valiendo '30',
    // volvería a ser una opción válida y el filtro se reaplicaría solo.
    await usuario.selectOptions(screen.getByLabelText('Tenant'), '')
    expect(nombresVisibles()).toEqual(['PV Centro', 'PV Anexo', 'PV Este'])

    await usuario.selectOptions(screen.getByLabelText('Tenant'), '2')

    await usuario.selectOptions(screen.getByLabelText('Empresa'), '21')
    expect(nombresVisibles()).toEqual(['PV Anexo'])

    // El tenant 2 sigue siendo el dueño de la empresa 21: la selección se respeta.
    await usuario.selectOptions(screen.getByLabelText('Tenant'), '2')
    expect(screen.getByLabelText('Empresa')).toHaveValue('21')
    expect(nombresVisibles()).toEqual(['PV Anexo'])
  })

  it('el filtro de empresa angosta las filas sin pedir nada más a la API', async () => {
    const usuario = userEvent.setup()
    montar()
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    const llamadasAlCargar = apiGetMock.mock.calls.length
    await usuario.selectOptions(screen.getByLabelText('Empresa'), '20')

    expect(nombresVisibles()).toEqual(['PV Centro'])
    expect(apiGetMock.mock.calls).toHaveLength(llamadasAlCargar)

    await usuario.selectOptions(screen.getByLabelText('Empresa'), '')
    expect(nombresVisibles()).toEqual(['PV Centro', 'PV Anexo', 'PV Este'])
  })

  /**
   * Cláusula bajo prueba: el `esPlataforma &&` que gatea el filtro por TENANT, con el MISMO
   * criterio que ya gateaba la columna. Un admin de tenant solo puede recibir filas de su propio
   * tenant (S5), así que ese filtro le ofrecía una única opción y no angostaba nada. El filtro por
   * EMPRESA sí le sirve —tiene varias— y por eso se queda: el test asserta las dos mitades.
   */
  it('un admin de tenant no ve el filtro por tenant, pero sí el de empresa', async () => {
    usuarioActual = usuarioFixture({ id: 4, usuario: 'admin', rolId: ROL.Admin, rol: 'Admin', idTenant: 2 })
    montar([pvSurCentro, pvSurAnexo])
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    expect(screen.queryByLabelText('Tenant')).not.toBeInTheDocument()
    expect(screen.queryByRole('columnheader', { name: 'Tenant' })).not.toBeInTheDocument()
    expect(opcionesDe('Empresa')).toEqual(['Todas', 'Sur Anexo SA', 'Sur SRL'])
    expect(screen.queryByText('Almacén Este')).not.toBeInTheDocument()
    expect(screen.queryByText('Este SRL')).not.toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba (ronda 2, R2-8): la reconciliación ESCRITA de `filtroEmpresa` en `cargar`
   * corre contra el conjunto ANGOSTADO por el tenant vigente —`opcionesDeEmpresa(filas, tenant)`—,
   * el mismo que rinde la pantalla, y no contra `opcionesDeEmpresa(filas)` sin angostar.
   *
   * El confundidor es el fallback derivado de siempre (`empresaVigente`): mientras el filtro de
   * tenant sigue puesto, las dos versiones pintan lo mismo, porque la opción inválida no está en el
   * `<select>` igual. La observación discriminante es la de M4/M23: volver el tenant a "Todos" no
   * puede RESUCITAR una empresa que ya no le pertenece al tenant que estaba elegido.
   */
  it('la empresa elegida no resucita cuando un refresco la saca del tenant vigente', async () => {
    const usuario = userEvent.setup()
    const pvAnexoMudado = pvFixture({
      id: 101,
      idTenant: 3,
      nombreTenant: 'Almacén Este',
      idEmpresa: 21,
      nombre: 'PV Anexo',
      razonSocialEmpresa: 'Sur Anexo SA',
    })

    let cargas = 0
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/puntos-venta') {
        cargas += 1

        return Promise.resolve(
          cargas === 1 ? [pvSurCentro, pvSurAnexo, pvEste] : [pvSurCentro, pvAnexoMudado, pvEste],
        )
      }

      return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
    })

    render(<PuntosVenta />)
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    await usuario.selectOptions(screen.getByLabelText('Tenant'), '2')
    await usuario.selectOptions(screen.getByLabelText('Empresa'), '21')
    expect(nombresVisibles()).toEqual(['PV Anexo'])

    // Única vía de recarga de esta pantalla: el refresco post-escritura.
    await usuario.click(screen.getByRole('button', { name: 'Editar' }))
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))
    await waitFor(() => expect(cargas).toBe(2))

    await usuario.selectOptions(screen.getByLabelText('Tenant'), '')

    expect(nombresVisibles()).toEqual(['PV Centro', 'PV Anexo', 'PV Este'])
    expect(screen.getByLabelText('Empresa')).toHaveValue('')
  })
})
