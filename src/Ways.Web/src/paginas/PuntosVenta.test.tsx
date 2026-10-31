import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { PuntosVenta } from './PuntosVenta'
import { ErrorApi } from '../api/cliente'
import { ETIQUETA_SIN_DUENIO } from '../api/organizacion'
import { ROL } from '../api/tipos'
import type { PuntoVentaListado, SucursalListado, UsuarioAutenticado } from '../api/tipos'
import type { EstadoDePuntoVenta } from '../puntoVenta/PuntoVentaContext'

// stage-20-organizacion-relaciones-y-bajas, slice 2 (tareas 2.11 y 2.13) y slice 5 (5.5, 5.7, 5.8).

const apiGetMock = vi.fn()
const apiPutMock = vi.fn()
const apiPostMock = vi.fn()
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

/** El encabezado de la columna también es un botón "Modo": el de la fila es el que vive en el cuerpo de la tabla. */
function botonDeModo() {
  const boton = screen.getAllByRole('button', { name: 'Modo' }).find((b) => b.closest('tbody'))
  if (!boton) throw new Error('No hay botón Modo en el cuerpo de la tabla')

  return boton
}

/** El encabezado de la columna también es un botón "Sucursal": el de la fila es el que vive en el cuerpo de la tabla. */
function botonDeSucursal() {
  const boton = screen.getAllByRole('button', { name: 'Sucursal' }).find((b) => b.closest('tbody'))
  if (!boton) throw new Error('No hay botón Sucursal en el cuerpo de la tabla')

  return boton
}

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
    modo: 'Web',
    idSucursal: 500,
    nombreSucursal: 'Sucursal Sur',
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

/** La pantalla vive bajo `PuertaDePuntoVenta` y le pide `recargar` a la sesión tras cada escritura
 * exitosa: el hook se mockea con un estado mutable que se reinicia antes de cada test. */
function estadoDePuntoVentaPorDefecto(): EstadoDePuntoVenta {
  return {
    puntosVenta: [pvSurCentro],
    puntoVenta: pvSurCentro,
    elegir: vi.fn(),
    recargar: vi.fn(() => Promise.resolve()),
  }
}

let estadoDePuntoVenta = estadoDePuntoVentaPorDefecto()

vi.mock('../puntoVenta/usePuntoVenta', () => ({
  usePuntoVenta: () => estadoDePuntoVenta,
}))

beforeEach(() => {
  estadoDePuntoVenta = estadoDePuntoVentaPorDefecto()
})

function sucursalFixture(sobrescribir: Partial<SucursalListado> = {}): SucursalListado {
  return {
    id: 500,
    idTenant: 2,
    idEmpresa: 20,
    nombre: 'Sucursal Sur',
    razonSocialEmpresa: 'Sur SRL',
    cantidadPuntosVenta: 1,
    ...sobrescribir,
  }
}

function montar(items: PuntoVentaListado[] = [pvSurCentro, pvSurAnexo, pvEste]) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/puntos-venta') return Promise.resolve(items)

    return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
  })

  return render(<PuntosVenta />)
}

/** Columnas (root): ID · Tenant · Empresa · Nombre · Domicilio · Modo · Acciones. */
function nombresVisibles() {
  return screen
    .getAllByRole('row')
    .slice(2)
    .map((f) => within(f).getAllByRole('cell')[3]?.textContent ?? '')
}

function opcionesDeSelect(select: HTMLElement) {
  return within(select)
    .getAllByRole('option')
    .map((o) => o.textContent)
}

function opcionesDe(etiqueta: string) {
  return within(screen.getByLabelText(etiqueta))
    .getAllByRole('option')
    .map((o) => o.textContent)
}

describe('PuntosVenta (stage-20, slice 2 — nombres de dueño y dos filtros)', () => {
  beforeEach(() => {
    apiGetMock.mockReset()
    apiDeleteMock.mockReset()
    apiDeleteMock.mockResolvedValue(undefined)
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

    for (const fila of screen.getAllByRole('row').slice(2)) {
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

// stage-20-organizacion-relaciones-y-bajas, slice 5 (tareas 5.5, 5.7 y 5.8). El patrón es el
// MISMO que el de `Tenants.tsx` y se replica en la misma PR (`react-async-state` regla 10).

function botonDeBajaDe(nombre: string) {
  return within(screen.getByRole('row', { name: new RegExp(nombre) })).getByRole('button', {
    name: 'Eliminar',
  })
}

describe('PuntosVenta (stage-20, slice 5 — baja lógica)', () => {
  beforeEach(() => {
    apiGetMock.mockReset()
    apiDeleteMock.mockReset()
    apiDeleteMock.mockResolvedValue(undefined)
    usuarioActual = usuarioFixture()
  })

  it('el botón de baja no llama a la API hasta que se confirma', async () => {
    const usuario = userEvent.setup()
    montar()
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    await usuario.click(botonDeBajaDe('PV Centro'))
    expect(apiDeleteMock).not.toHaveBeenCalled()
    expect(screen.getByRole('alertdialog', { name: 'Confirmar baja' })).toHaveTextContent(
      '¿Dar de baja el punto de venta "PV Centro"?',
    )

    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))
    await waitFor(() => expect(apiDeleteMock).toHaveBeenCalledWith('/puntos-venta/100'))
    await waitFor(() =>
      expect(screen.getByText('Se dio de baja el punto de venta "PV Centro".')).toBeInTheDocument(),
    )
  })

  it('cancelar cierra la puerta y no llama nunca a la API', async () => {
    const usuario = userEvent.setup()
    montar()
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    await usuario.click(botonDeBajaDe('PV Centro'))
    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(apiDeleteMock).not.toHaveBeenCalled()
  })

  /** Cláusula bajo prueba: la ventana inerte completa — ver el test gemelo de `Tenants.test.tsx`. */
  it('durante el DELETE y su refresco no queda ninguna acción alcanzable', async () => {
    const usuario = userEvent.setup()
    let resolverDelete!: () => void
    let resolverRefresco!: (items: PuntoVentaListado[]) => void

    let cargas = 0
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta !== '/puntos-venta') return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
      cargas += 1
      if (cargas === 1) return Promise.resolve([pvSurCentro, pvEste])

      return new Promise<PuntoVentaListado[]>((resolver) => {
        resolverRefresco = resolver
      })
    })
    apiDeleteMock.mockImplementation(
      () =>
        new Promise<void>((resolver) => {
          resolverDelete = resolver
        }),
    )

    render(<PuntosVenta />)
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    await usuario.click(botonDeBajaDe('PV Centro'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    expect(screen.getByRole('button', { name: 'Dando de baja…' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    for (const boton of [
      ...screen.getAllByRole('button', { name: 'Editar' }),
      ...screen.getAllByRole('button', { name: 'Eliminar' }),
    ]) {
      expect(boton).toBeDisabled()
    }

    await act(async () => {
      resolverDelete()
      await Promise.resolve()
    })

    await waitFor(() =>
      expect(screen.getByText('Se dio de baja el punto de venta "PV Centro".')).toBeInTheDocument(),
    )
    expect(screen.getByText('Cargando…')).toBeInTheDocument()

    await act(async () => {
      resolverRefresco([pvEste])
      await Promise.resolve()
    })

    await waitFor(() => expect(screen.getAllByRole('button', { name: 'Eliminar' })[0]).toBeEnabled())
    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
  })

  /** Cláusula bajo prueba: `ocupadoRef`, la guarda de re-entrancia del mismo tick (regla 9). */
  it('un segundo click sobre la confirmación en vuelo se descarta', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockImplementation(() => new Promise<void>(() => {}))
    montar()
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    await usuario.click(botonDeBajaDe('PV Centro'))
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
      if (ruta !== '/puntos-venta') return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
      cargas += 1
      if (cargas === 1) return Promise.resolve([pvSurCentro])

      return Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó.'))
    })

    render(<PuntosVenta />)
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    await usuario.click(botonDeBajaDe('PV Centro'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await waitFor(() =>
      expect(
        screen.getByText(
          'Se dio de baja el punto de venta "PV Centro". Se eliminó, pero no se pudo actualizar la vista. Recargá la pantalla.',
        ),
      ).toBeInTheDocument(),
    )
    // La sesión se recarga recién DESPUÉS de un refresco exitoso: con el refresco caído, nada.
    expect(estadoDePuntoVenta.recargar).not.toHaveBeenCalled()
  })

  /**
   * Cláusula bajo prueba: la elección de copia por `codigo` sobre el mínimo estructural de esta
   * pantalla, que es distinto del de `Empresas` — cada uno manda a dar de baja a su propio padre.
   */
  it('un 409 ultimo_punto_venta_de_la_empresa rinde su guía propia y el mensaje del servidor', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockRejectedValue(
      new ErrorApi(
        409,
        'ultimo_punto_venta_de_la_empresa',
        'Es el único punto de venta de la empresa: si querés eliminarlo, dá de baja la empresa.',
      ),
    )
    montar()
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    await usuario.click(botonDeBajaDe('PV Centro'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await waitFor(() =>
      expect(screen.getByText(/Es el único punto de venta de la empresa/)).toBeInTheDocument(),
    )
    expect(
      screen.getByText(/La baja de la empresa se hace desde la pantalla de Empresas\./),
    ).toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba: la copia de `punto_venta_en_uso`, que NO es la del mínimo estructural.
   * El mensaje del servidor nombra qué bloquea y se rinde entero.
   */
  it('un 409 punto_venta_en_uso rinde su guía propia sin tragarse el mensaje del servidor', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockRejectedValue(
      new ErrorApi(
        409,
        'punto_venta_en_uso',
        'No se puede dar de baja el punto de venta porque tiene 5 ventas.',
      ),
    )
    montar()
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    await usuario.click(botonDeBajaDe('PV Centro'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await waitFor(() => expect(screen.getByText(/porque tiene 5 ventas/)).toBeInTheDocument())
    expect(
      screen.getByText(/Dá de baja o reasigná esos datos antes de eliminar el punto de venta\./),
    ).toBeInTheDocument()
  })

  /** Anti-oráculo (BO-R12) en la capa de UI: un 404 nunca insinúa uso ni alcance. */
  it('un 404 rinde la copia neutra de inexistencia, nunca una pista de uso', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'No existe el punto de venta 100.'))
    montar()
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    await usuario.click(botonDeBajaDe('PV Centro'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await waitFor(() =>
      expect(
        screen.getByText(
          'No se pudo dar de baja el punto de venta. Ya no existe o no está a tu alcance. Actualizá el listado.',
        ),
      ).toBeInTheDocument(),
    )
    expect(screen.queryByText(/en uso|tiene \d+/)).not.toBeInTheDocument()
  })

  /** Paridad con `Empresas`: `DELETE /api/puntos-venta/{id}` es `GestionDeOrganizacion`, que un
   * admin de tenant tiene — el botón no se le esconde. */
  it('un admin de tenant ve el botón de baja, que su policy sí le permite', async () => {
    usuarioActual = usuarioFixture({ id: 4, usuario: 'admin', rolId: ROL.Admin, rol: 'Admin', idTenant: 2 })
    montar([pvSurCentro, pvSurAnexo])
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    expect(botonDeBajaDe('PV Centro')).toBeEnabled()
  })
})

// stage-20-organizacion-relaciones-y-bajas, slice 5, judgment-day ronda 1 (C1 y C4). Mismo patrón
// que `Tenants.test.tsx`, replicado en la misma PR (`react-async-state` regla 10).

describe('PuntosVenta (slice 5, ronda 1 — la puerta es modal y el token se acuña al confirmar)', () => {
  beforeEach(() => {
    apiGetMock.mockReset()
    apiPutMock.mockReset()
    apiDeleteMock.mockReset()
    apiPutMock.mockResolvedValue(undefined)
    apiDeleteMock.mockResolvedValue(undefined)
    usuarioActual = usuarioFixture()
  })

  /** Cláusula bajo prueba: `bloqueado = ocupado !== null || baja !== null` en todos los `disabled`
   * — ver el test gemelo de `Tenants.test.tsx`. */
  it('con la puerta abierta no queda ninguna otra acción alcanzable', async () => {
    const usuario = userEvent.setup()
    montar()
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    await usuario.click(
      within(screen.getByRole('row', { name: /PV Centro/ })).getAllByRole('button', { name: 'Editar' })[0],
    )
    await usuario.click(botonDeBajaDe('PV Este'))

    const puerta = screen.getByRole('alertdialog', { name: 'Confirmar baja' })
    for (const boton of [
      ...screen.getAllByRole('button', { name: 'Editar' }),
      ...screen.getAllByRole('button', { name: 'Eliminar' }),
      ...screen.getAllByRole('button', { name: 'Guardar' }),
    ]) {
      expect(boton).toBeDisabled()
    }
    expect(screen.getByLabelText('Domicilio')).toBeDisabled()
    expect(screen.getByLabelText('Empresa')).toBeDisabled()
    expect(within(puerta).getByRole('button', { name: 'Confirmar baja' })).toBeEnabled()

    await usuario.click(within(puerta).getByRole('button', { name: 'Confirmar baja' }))

    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument())
    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
  })

  /** Cláusula bajo prueba: el token acuñado al CONFIRMAR — ver el test gemelo de `Tenants.test.tsx`,
   * incluido el porqué de bajar hasta el `<form>` para acuñar una generación en esa ventana. */
  it('una generación acuñada entre abrir y confirmar no se traga el 204', async () => {
    const usuario = userEvent.setup()
    montar()
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    await usuario.click(
      within(screen.getByRole('row', { name: /PV Centro/ })).getAllByRole('button', { name: 'Editar' })[0],
    )
    await usuario.click(botonDeBajaDe('PV Este'))

    // El formulario vive en el modal (portal), fuera del contenedor del render.
    const form = within(screen.getByRole('dialog', { name: 'Editando punto de venta 100' }))
      .getByLabelText('Nombre')
      .closest('form')
    if (!form) throw new Error('no hay formulario de edición abierto')
    await act(async () => {
      fireEvent.submit(form)
    })
    await waitFor(() => expect(screen.getByText('Se actualizó "PV Centro".')).toBeInTheDocument())
    expect(apiPutMock).toHaveBeenCalledTimes(1)

    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await waitFor(() =>
      expect(screen.getByText('Se dio de baja el punto de venta "PV Este".')).toBeInTheDocument(),
    )
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
  })

  /** Cláusula bajo prueba: el `setError('')` de `cancelarBaja` — ver `Tenants.test.tsx`. */
  it('cancelar después de un rechazo se lleva el motivo con la puerta', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockRejectedValue(
      new ErrorApi(409, 'punto_venta_en_uso', 'No se puede dar de baja el punto de venta porque tiene 5 ventas.'),
    )
    montar()
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    await usuario.click(botonDeBajaDe('PV Centro'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))
    await waitFor(() => expect(screen.getByText(/porque tiene 5 ventas/)).toBeInTheDocument())

    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.queryByText(/porque tiene 5 ventas/)).not.toBeInTheDocument()
  })
})

// Punto de venta de sesión (slice 4): la sesión mira el mismo listado que esta pantalla edita, así
// que un rename o una baja se propagan con `recargar` — una sola vez, y solo después de que el
// refresco propio de la pantalla resolvió bien (`react-async-state` regla 6: fuera del try/catch
// de la escritura, sin reportar nunca una escritura confirmada como fallida).

describe('PuntosVenta — propagación al punto de venta de sesión', () => {
  beforeEach(() => {
    apiGetMock.mockReset()
    apiPutMock.mockReset()
    apiDeleteMock.mockReset()
    apiPutMock.mockResolvedValue(undefined)
    apiDeleteMock.mockResolvedValue(undefined)
    usuarioActual = usuarioFixture()
  })

  /** Cláusula bajo prueba: la posición de `recargar` DESPUÉS del `await cargar(token, true)`. Con
   * el refresco todavía en vuelo no se llamó; recién al resolver, exactamente una vez. */
  it('una edición exitosa recarga la sesión exactamente una vez, y recién después del refresco', async () => {
    const usuario = userEvent.setup()
    let cargas = 0
    let resolverRefresco!: (items: PuntoVentaListado[]) => void
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta !== '/puntos-venta') return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
      cargas += 1
      if (cargas === 1) return Promise.resolve([pvSurCentro])

      return new Promise<PuntoVentaListado[]>((resolver) => {
        resolverRefresco = resolver
      })
    })

    render(<PuntosVenta />)
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    await usuario.click(screen.getByRole('button', { name: 'Editar' }))
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(cargas).toBe(2))
    expect(apiPutMock).toHaveBeenCalledTimes(1)
    expect(estadoDePuntoVenta.recargar).not.toHaveBeenCalled()

    await act(async () => {
      resolverRefresco([pvFixture({ nombre: 'PV Centro renombrado' })])
      await Promise.resolve()
    })

    await waitFor(() => expect(screen.getByText('PV Centro renombrado')).toBeInTheDocument())
    expect(estadoDePuntoVenta.recargar).toHaveBeenCalledTimes(1)
  })

  it('una baja exitosa recarga la sesión exactamente una vez', async () => {
    const usuario = userEvent.setup()
    montar([pvSurCentro, pvEste])
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    await usuario.click(botonDeBajaDe('PV Centro'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await waitFor(() =>
      expect(screen.getByText('Se dio de baja el punto de venta "PV Centro".')).toBeInTheDocument(),
    )
    await waitFor(() => expect(estadoDePuntoVenta.recargar).toHaveBeenCalledTimes(1))
  })

  it('una edición rechazada no recarga la sesión', async () => {
    const usuario = userEvent.setup()
    apiPutMock.mockRejectedValue(new ErrorApi(400, 'validacion', 'El nombre no puede quedar vacío.'))
    montar([pvSurCentro])
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    await usuario.click(screen.getByRole('button', { name: 'Editar' }))
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(screen.getByText('El nombre no puede quedar vacío.')).toBeInTheDocument())
    expect(apiPutMock).toHaveBeenCalledTimes(1)
    expect(estadoDePuntoVenta.recargar).not.toHaveBeenCalled()
  })

  it('una baja rechazada no recarga la sesión', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockRejectedValue(
      new ErrorApi(409, 'punto_venta_en_uso', 'No se puede dar de baja el punto de venta porque tiene 5 ventas.'),
    )
    montar([pvSurCentro])
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    await usuario.click(botonDeBajaDe('PV Centro'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await waitFor(() => expect(screen.getByText(/porque tiene 5 ventas/)).toBeInTheDocument())
    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
    expect(estadoDePuntoVenta.recargar).not.toHaveBeenCalled()
  })
})

// stage-desktop-pos (DB CHANGE GATE aprobado): el flip de modo — su propio botón, su propio
// formulario, su propio endpoint (`POST /puntos-venta/{id}/modo`), independiente del de edición
// descriptiva.

describe('PuntosVenta — flip de modo', () => {
  beforeEach(() => {
    apiGetMock.mockReset()
    apiPostMock.mockReset()
    usuarioActual = usuarioFixture()
  })

  it('rinde el modo vigente en su columna', async () => {
    montar([pvFixture({ modo: 'Escritorio' })])
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    const celdas = within(screen.getByRole('row', { name: /PV Centro/ })).getAllByRole('cell')
    expect(celdas.at(-2)).toHaveTextContent('Escritorio')
  })

  it('cambiar el modo llama al endpoint dedicado y refresca', async () => {
    const usuario = userEvent.setup()
    apiPostMock.mockResolvedValue(pvFixture({ modo: 'Escritorio' }))
    montar([pvFixture({ modo: 'Web' })])
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    await usuario.click(botonDeModo())
    await usuario.selectOptions(screen.getByLabelText('Modo'), 'Escritorio')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() =>
      expect(apiPostMock).toHaveBeenCalledWith('/puntos-venta/100/modo', { modo: 'Escritorio' }),
    )
    await waitFor(() =>
      expect(screen.getByText('Se cambió el modo de "PV Centro" a Escritorio.')).toBeInTheDocument(),
    )
    expect(screen.queryByLabelText('Modo')).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `ocupadoRef`, la misma guarda de re-entrancia que `confirmarBaja`
   * (ver "un segundo click sobre la confirmación en vuelo se descarta" más arriba) — acá sobre el
   * submit de `guardarModo` (regla 9 de `react-async-state`). */
  it('un segundo click sobre "Guardar" en vuelo se descarta', async () => {
    const usuario = userEvent.setup()
    apiPostMock.mockImplementation(() => new Promise(() => {}))
    montar([pvFixture({ modo: 'Web' })])
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    await usuario.click(botonDeModo())
    await usuario.selectOptions(screen.getByLabelText('Modo'), 'Escritorio')

    const guardar = screen.getByRole('button', { name: 'Guardar' })
    await act(async () => {
      guardar.click()
      guardar.click()
      await Promise.resolve()
    })

    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })

  it('un 409 por dispositivo activo rinde el mensaje del servidor sin cerrar el formulario', async () => {
    const usuario = userEvent.setup()
    apiPostMock.mockRejectedValue(
      new ErrorApi(
        409,
        'punto_venta_con_dispositivo_activo',
        'Este punto de venta tiene un dispositivo vinculado: revocalo antes de cambiar el modo.',
      ),
    )
    montar([pvFixture({ modo: 'Web' })])
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    await usuario.click(botonDeModo())
    await usuario.selectOptions(screen.getByLabelText('Modo'), 'Escritorio')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() =>
      expect(
        screen.getByText('Este punto de venta tiene un dispositivo vinculado: revocalo antes de cambiar el modo.'),
      ).toBeInTheDocument(),
    )
    expect(screen.getByLabelText('Modo')).toBeInTheDocument()
  })

  it('cancelar cierra el formulario sin llamar a la API', async () => {
    const usuario = userEvent.setup()
    montar([pvFixture({ modo: 'Web' })])
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    await usuario.click(botonDeModo())
    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByLabelText('Modo')).not.toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })
})

describe('PuntosVenta — mover la caja de sucursal', () => {
  const sucursalActual = sucursalFixture()
  const sucursalHermana = sucursalFixture({ id: 501, nombre: 'Depósito Sur' })
  const sucursalDeOtraEmpresa = sucursalFixture({ id: 600, idEmpresa: 30, nombre: 'Sucursal Este', razonSocialEmpresa: 'Este SRL' })

  beforeEach(() => {
    apiGetMock.mockReset()
    apiPostMock.mockReset()
    usuarioActual = usuarioFixture()
  })

  function montarConSucursales(items: PuntoVentaListado[] = [pvFixture()]) {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/puntos-venta') return Promise.resolve(items)
      if (ruta === '/sucursales') return Promise.resolve([sucursalActual, sucursalHermana, sucursalDeOtraEmpresa])

      return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
    })

    return render(<PuntosVenta />)
  }

  /** Abre el modal y espera al DATO (las sucursales), no al elemento: el select existe antes de que lleguen. */
  async function abrirMovimiento(usuario: ReturnType<typeof userEvent.setup>) {
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())
    await usuario.click(botonDeSucursal())
    await waitFor(() => expect(screen.getByLabelText('Sucursal', { selector: 'select' })).toBeEnabled())
  }

  it('rinde la sucursal de la caja en su columna', async () => {
    montarConSucursales([pvFixture({ nombreSucursal: 'Sucursal Sur' })])
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    const celdas = within(screen.getByRole('row', { name: /PV Centro/ })).getAllByRole('cell')
    expect(celdas[4]).toHaveTextContent('Sucursal Sur')
  })

  /** Cláusula bajo prueba: `delaEmpresa` en `ModalDeSucursalDePuntoVenta` — solo las sucursales de la empresa de la caja
   * (el servidor rechazaría las demás con 409) — y el POST dedicado con el id elegido. */
  it('ofrece solo las sucursales de su empresa y mueve la caja con el endpoint dedicado', async () => {
    const usuario = userEvent.setup()
    apiPostMock.mockResolvedValue(pvFixture({ idSucursal: 501, nombreSucursal: 'Depósito Sur' }))
    montarConSucursales()
    await abrirMovimiento(usuario)

    const selector = screen.getByLabelText('Sucursal', { selector: 'select' })
    expect(opcionesDeSelect(selector)).toEqual(['Sucursal Sur', 'Depósito Sur'])
    expect(selector).toHaveValue('500')
    // Sin cambiar la sucursal no hay nada que guardar.
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeDisabled()

    await usuario.selectOptions(selector, '501')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledWith('/puntos-venta/100/sucursal', { idSucursal: 501 }))
    await waitFor(() => expect(screen.getByText('Se movió "PV Centro" de sucursal.')).toBeInTheDocument())
    expect(screen.queryByLabelText('Sucursal', { selector: 'select' })).not.toBeInTheDocument()
  })

  it('un segundo click sobre "Guardar" en vuelo se descarta', async () => {
    const usuario = userEvent.setup()
    apiPostMock.mockImplementation(() => new Promise(() => {}))
    montarConSucursales()
    await abrirMovimiento(usuario)
    await usuario.selectOptions(screen.getByLabelText('Sucursal', { selector: 'select' }), '501')

    const guardar = screen.getByRole('button', { name: 'Guardar' })
    await act(async () => {
      guardar.click()
      guardar.click()
      await Promise.resolve()
    })

    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })

  it('un 409 rinde el mensaje del servidor sin cerrar el formulario', async () => {
    const usuario = userEvent.setup()
    apiPostMock.mockRejectedValue(
      new ErrorApi(
        409,
        'sucursal_en_uso',
        'No se puede mover la caja: su sucursal quedaría sin cajas y todavía tiene stock.',
      ),
    )
    montarConSucursales()
    await abrirMovimiento(usuario)

    await usuario.selectOptions(screen.getByLabelText('Sucursal', { selector: 'select' }), '501')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() =>
      expect(
        screen.getByText('No se puede mover la caja: su sucursal quedaría sin cajas y todavía tiene stock.'),
      ).toBeInTheDocument(),
    )
    expect(screen.getByLabelText('Sucursal', { selector: 'select' })).toBeInTheDocument()
  })

  it('si no cargan las sucursales lo dice y no deja guardar', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/puntos-venta') return Promise.resolve([pvFixture()])

      return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
    })
    render(<PuntosVenta />)
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    await usuario.click(botonDeSucursal())

    expect(await screen.findByText('No se pudieron cargar las sucursales.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeDisabled()
  })

  it('cancelar cierra el formulario sin llamar a la API', async () => {
    const usuario = userEvent.setup()
    montarConSucursales()
    await abrirMovimiento(usuario)

    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByLabelText('Sucursal', { selector: 'select' })).not.toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })
})

describe('PuntosVenta — alta', () => {
  const empresaSur = {
    id: 20,
    idTenant: 2,
    razonSocial: 'Sur SRL',
    nombreFantasia: null,
    cuit: null,
    nombreTenant: 'Comercio Sur',
    alicuotaPercepcionIibb: null,
    alicuotaPercepcionIva: null,
  }
  const empresaEste = { ...empresaSur, id: 30, idTenant: 3, razonSocial: 'Este SRL', nombreTenant: 'Almacén Este' }

  beforeEach(() => {
    apiGetMock.mockReset()
    apiPostMock.mockReset()
    usuarioActual = usuarioFixture()
  })

  const sucursalSur = sucursalFixture()
  const sucursalSurDos = sucursalFixture({ id: 501, nombre: 'Depósito Sur' })
  const sucursalEste = sucursalFixture({ id: 600, idTenant: 3, idEmpresa: 30, nombre: 'Sucursal Este', razonSocialEmpresa: 'Este SRL' })

  function montarConEmpresas(
    items: PuntoVentaListado[] = [pvSurCentro],
    empresas: unknown[] = [empresaSur, empresaEste],
  ) {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/puntos-venta') return Promise.resolve(items)
      if (ruta === '/empresas') return Promise.resolve(empresas)
      if (ruta === '/sucursales') return Promise.resolve([sucursalSur, sucursalSurDos, sucursalEste])

      return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
    })

    return render(<PuntosVenta />)
  }

  /** Abre el formulario y espera al DATO (las empresas), no al elemento: el select se renderiza
   * antes y queda deshabilitado hasta que la lectura aterriza (`web-test-data-gates`). */
  async function abrirAlta(usuario: ReturnType<typeof userEvent.setup>, opcionEsperada = 'Sur SRL (Comercio Sur)') {
    await screen.findByLabelText('Empresa')
    await usuario.click(screen.getByRole('button', { name: 'Nuevo punto de venta' }))
    await waitFor(() => expect(opcionesDe('Empresa destino')).toContain(opcionEsperada))
    await waitFor(() => expect(screen.getByLabelText('Empresa destino')).toBeEnabled())
  }

  async function completarAlta(usuario: ReturnType<typeof userEvent.setup>, idEmpresa = '20') {
    await usuario.selectOptions(screen.getByLabelText('Empresa destino'), idEmpresa)
    await usuario.type(screen.getByLabelText('Nombre'), 'PV Norte')
    await usuario.selectOptions(screen.getByLabelText('Modo'), 'Web')
  }

  /** Cláusula bajo prueba: el filtro de sucursales por la empresa elegida (`ModalDeAltaDePuntoVenta`) y el
   * `idSucursal` del POST. Sin elegir, el cuerpo manda `null` (la caja nace con una sucursal propia); elegida,
   * manda su id; cambiar de empresa descarta la sucursal de la anterior. */
  it('ofrece solo las sucursales de la empresa elegida y manda la elegida (o null) en el alta', async () => {
    const usuario = userEvent.setup()
    apiPostMock.mockResolvedValue(pvFixture({ id: 201, nombre: 'PV Norte' }))
    montarConEmpresas()
    await abrirAlta(usuario)
    await waitFor(() => expect(screen.getByLabelText('Empresa destino')).toBeEnabled())

    expect(screen.getByLabelText('Sucursal', { selector: 'select' })).toBeDisabled()

    await usuario.selectOptions(screen.getByLabelText('Empresa destino'), '20')
    const selector = screen.getByLabelText('Sucursal', { selector: 'select' })
    expect(opcionesDeSelect(selector)).toEqual(['Una nueva, con el nombre del punto de venta', 'Sucursal Sur', 'Depósito Sur'])

    await usuario.selectOptions(selector, '501')
    await usuario.selectOptions(screen.getByLabelText('Empresa destino'), '30')
    expect(screen.getByLabelText('Sucursal', { selector: 'select' })).toHaveValue('')
    expect(opcionesDeSelect(screen.getByLabelText('Sucursal', { selector: 'select' }))).toEqual([
      'Una nueva, con el nombre del punto de venta',
      'Sucursal Este',
    ])

    await usuario.selectOptions(screen.getByLabelText('Empresa destino'), '20')
    await usuario.selectOptions(screen.getByLabelText('Sucursal', { selector: 'select' }), '501')
    await usuario.type(screen.getByLabelText('Nombre'), 'PV Norte')
    await usuario.selectOptions(screen.getByLabelText('Modo'), 'Web')
    await usuario.click(screen.getByRole('button', { name: 'Crear' }))

    await waitFor(() =>
      expect(apiPostMock).toHaveBeenCalledWith(
        '/puntos-venta',
        expect.objectContaining({ idEmpresa: 20, idSucursal: 501, nombre: 'PV Norte' }),
      ),
    )
  })

  it('Root ve el botón de alta', async () => {
    montarConEmpresas()
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())
    expect(screen.getByRole('button', { name: 'Nuevo punto de venta' })).toBeInTheDocument()
  })

  it('Admin ve el botón de alta', async () => {
    usuarioActual = usuarioFixture({ id: 4, usuario: 'admin', rolId: ROL.Admin, rol: 'Admin', idTenant: 2 })
    montarConEmpresas()
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())
    expect(screen.getByRole('button', { name: 'Nuevo punto de venta' })).toBeInTheDocument()
  })

  it('un rol sin gestión de organización no ve el botón de alta', async () => {
    usuarioActual = usuarioFixture({ id: 5, usuario: 'vendedor', rolId: ROL.Vendedor, rol: 'Vendedor', idTenant: 2 })
    montarConEmpresas()
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    expect(screen.queryByRole('button', { name: 'Nuevo punto de venta' })).not.toBeInTheDocument()
  })

  it('el formulario no elige ni la empresa ni el modo por el usuario', async () => {
    const usuario = userEvent.setup()
    montarConEmpresas()
    await abrirAlta(usuario)

    expect(screen.getByLabelText('Empresa destino')).toHaveValue('')
    expect(screen.getByLabelText('Modo')).toHaveValue('')
    expect(opcionesDe('Modo')).toEqual(['Elegí un modo…', 'Escritorio', 'Web'])
    expect(apiGetMock.mock.calls.filter(([ruta]) => ruta === '/empresas')).toHaveLength(1)
  })

  it('un admin de tenant ve las empresas sin el nombre del tenant', async () => {
    const usuario = userEvent.setup()
    usuarioActual = usuarioFixture({ id: 4, usuario: 'admin', rolId: ROL.Admin, rol: 'Admin', idTenant: 2 })
    montarConEmpresas([pvSurCentro], [empresaSur])
    await abrirAlta(usuario, 'Sur SRL')

    expect(opcionesDe('Empresa destino')).toEqual(['Elegí una empresa…', 'Sur SRL'])
  })

  it('crea el punto de venta con todos los campos, refresca el listado y recarga la sesión una vez', async () => {
    const usuario = userEvent.setup()
    const creado = pvFixture({ id: 200, nombre: 'PV Norte', modo: 'Escritorio' })
    apiPostMock.mockResolvedValue(creado)
    let listados = 0
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/empresas') return Promise.resolve([empresaSur, empresaEste])
      if (ruta !== '/puntos-venta') return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
      listados += 1

      return Promise.resolve(listados === 1 ? [pvSurCentro] : [pvSurCentro, creado])
    })
    render(<PuntosVenta />)
    await abrirAlta(usuario)

    await usuario.selectOptions(screen.getByLabelText('Empresa destino'), '30')
    await usuario.type(screen.getByLabelText('Nombre'), '  PV Norte  ')
    await usuario.selectOptions(screen.getByLabelText('Modo'), 'Escritorio')
    await usuario.type(screen.getByLabelText('Domicilio'), 'Calle 1')
    await usuario.type(screen.getByLabelText('WhatsApp'), '1155550000')
    await usuario.click(screen.getByRole('button', { name: 'Crear' }))

    await waitFor(() =>
      expect(apiPostMock).toHaveBeenCalledWith('/puntos-venta', {
        idEmpresa: 30,
        idSucursal: null,
        nombre: 'PV Norte',
        modo: 'Escritorio',
        domicilio: 'Calle 1',
        horario: null,
        whatsapp: '1155550000',
        instagram: null,
        facebook: null,
        web: null,
      }),
    )
    await waitFor(() => expect(screen.getByText('Se creó el punto de venta "PV Norte".')).toBeInTheDocument())
    await waitFor(() => expect(screen.getByText('PV Norte')).toBeInTheDocument())
    expect(screen.queryByLabelText('Empresa destino')).not.toBeInTheDocument()
    await waitFor(() => expect(estadoDePuntoVenta.recargar).toHaveBeenCalledTimes(1))
  })

  it('tras crear se limpian los filtros para que la fila nueva se vea', async () => {
    const usuario = userEvent.setup()
    const creado = pvFixture({ id: 200, nombre: 'PV Norte', modo: 'Web' })
    apiPostMock.mockResolvedValue(creado)
    let listados = 0
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/empresas') return Promise.resolve([empresaSur, empresaEste])
      if (ruta !== '/puntos-venta') return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
      listados += 1

      return Promise.resolve(listados === 1 ? [pvSurCentro, pvEste] : [pvSurCentro, pvEste, creado])
    })
    render(<PuntosVenta />)
    await waitFor(() => expect(screen.getByText('PV Este')).toBeInTheDocument())

    await usuario.selectOptions(screen.getByLabelText('Tenant'), '3')
    expect(screen.queryByText('PV Centro')).not.toBeInTheDocument()

    await abrirAlta(usuario)
    await completarAlta(usuario)
    await usuario.click(screen.getByRole('button', { name: 'Crear' }))

    await waitFor(() => expect(screen.getByText('PV Norte')).toBeInTheDocument())
    expect(screen.getByLabelText('Tenant')).toHaveValue('')
    expect(screen.getByText('PV Centro')).toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `limpiarFiltros()` tras crear. Mutación registrada: quitarla deja el filtro por nombre puesto
   * y la fila nueva oculta, y esta prueba falla. */
  it('tras crear se limpian también los filtros por columna para que la fila nueva se vea', async () => {
    const usuario = userEvent.setup()
    const creado = pvFixture({ id: 200, nombre: 'PV Norte', modo: 'Web' })
    apiPostMock.mockResolvedValue(creado)
    let listados = 0
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/empresas') return Promise.resolve([empresaSur, empresaEste])
      if (ruta !== '/puntos-venta') return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
      listados += 1

      return Promise.resolve(listados === 1 ? [pvSurCentro, pvEste] : [pvSurCentro, pvEste, creado])
    })
    render(<PuntosVenta />)
    await waitFor(() => expect(screen.getByText('PV Este')).toBeInTheDocument())

    await usuario.type(screen.getByLabelText('Filtrar por nombre'), 'Centro')
    expect(screen.queryByText('PV Este')).not.toBeInTheDocument()

    await abrirAlta(usuario)
    await completarAlta(usuario)
    await usuario.click(screen.getByRole('button', { name: 'Crear' }))

    await waitFor(() => expect(screen.getByText('PV Norte')).toBeInTheDocument())
    expect(screen.getByLabelText('Filtrar por nombre')).toHaveValue('')
    expect(screen.getByText('PV Este')).toBeInTheDocument()
  })

  it('con el modo sin elegir no llama a la API y lo dice', async () => {
    const usuario = userEvent.setup()
    montarConEmpresas()
    await abrirAlta(usuario)

    await usuario.selectOptions(screen.getByLabelText('Empresa destino'), '20')
    await usuario.type(screen.getByLabelText('Nombre'), 'PV Norte')
    // `fireEvent.submit` esquiva la validación nativa de `required`: lo que se prueba es la guarda del handler.
    fireEvent.submit(screen.getByLabelText('Nombre').closest('form')!)

    expect(await screen.findByText('Elegí la empresa y el modo, y completá el nombre.')).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  /** `ocupadoRef`, la guarda de re-entrancia (`react-async-state`): dos clicks del MISMO tick leen el
   * mismo render y el estado `ocupado` los dejaría pasar a los dos. */
  it('un segundo click sobre "Crear" en vuelo se descarta', async () => {
    const usuario = userEvent.setup()
    apiPostMock.mockImplementation(() => new Promise(() => {}))
    montarConEmpresas()
    await abrirAlta(usuario)
    await completarAlta(usuario)

    const crear = screen.getByRole('button', { name: 'Crear' })
    await act(async () => {
      crear.click()
      crear.click()
      await Promise.resolve()
    })

    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })

  it('durante la escritura no queda ningún control alcanzable', async () => {
    const usuario = userEvent.setup()
    apiPostMock.mockImplementation(() => new Promise(() => {}))
    montarConEmpresas()
    await abrirAlta(usuario)
    await completarAlta(usuario)
    await usuario.click(screen.getByRole('button', { name: 'Crear' }))

    await waitFor(() => expect(screen.getByRole('button', { name: 'Guardando…' })).toBeDisabled())
    expect(screen.getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(screen.getByLabelText('Empresa destino')).toBeDisabled()
    expect(screen.getByLabelText('Nombre')).toBeDisabled()
    expect(screen.getByLabelText('Modo')).toBeDisabled()
    expect(screen.getByLabelText('Sitio web')).toBeDisabled()
  })

  it('un rechazo del servidor rinde su mensaje, deja el formulario abierto y no recarga la sesión', async () => {
    const usuario = userEvent.setup()
    apiPostMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'No existe la empresa 20.'))
    montarConEmpresas()
    await abrirAlta(usuario)
    await completarAlta(usuario)
    await usuario.click(screen.getByRole('button', { name: 'Crear' }))

    expect(await screen.findByText('No existe la empresa 20.')).toBeInTheDocument()
    expect(screen.getByLabelText('Nombre')).toHaveValue('PV Norte')
    expect(estadoDePuntoVenta.recargar).not.toHaveBeenCalled()
    expect(apiGetMock.mock.calls.filter(([ruta]) => ruta === '/puntos-venta')).toHaveLength(1)
  })

  it('un refresco fallido después de crear no la reporta como fallida', async () => {
    const usuario = userEvent.setup()
    apiPostMock.mockResolvedValue(pvFixture({ id: 200, nombre: 'PV Norte' }))
    let listados = 0
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/empresas') return Promise.resolve([empresaSur])
      if (ruta !== '/puntos-venta') return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
      listados += 1

      return listados === 1 ? Promise.resolve([pvSurCentro]) : Promise.reject(new Error('sin red'))
    })
    render(<PuntosVenta />)
    await abrirAlta(usuario)
    await completarAlta(usuario)
    await usuario.click(screen.getByRole('button', { name: 'Crear' }))

    expect(
      await screen.findByText(
        'Se creó el punto de venta "PV Norte". Se guardó, pero no se pudo actualizar la vista. Recargá la pantalla.',
      ),
    ).toBeInTheDocument()
    expect(screen.queryByText('No se pudo crear el punto de venta.')).not.toBeInTheDocument()
  })

  it('si no cargan las empresas lo dice y no deja crear', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/puntos-venta') return Promise.resolve([pvSurCentro])
      if (ruta === '/empresas') return Promise.reject(new ErrorApi(500, 'error', 'Falló la lectura de empresas.'))

      return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
    })
    render(<PuntosVenta />)
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    await usuario.click(screen.getByRole('button', { name: 'Nuevo punto de venta' }))

    expect(await screen.findByText('Falló la lectura de empresas.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Crear' })).toBeDisabled()
  })

  /** La lectura de empresas del alta lleva su propia generación: cerrar el formulario descarta la
   * respuesta en vuelo, y una respuesta tardía de la primera apertura no puede pisar la segunda. */
  it('la respuesta tardía de una apertura anterior no pisa la de la apertura vigente', async () => {
    const usuario = userEvent.setup()
    const pendientes: Array<(empresas: unknown[]) => void> = []
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/puntos-venta') return Promise.resolve([pvSurCentro])
      if (ruta !== '/empresas') return Promise.reject(new Error(`ruta inesperada: ${ruta}`))

      return new Promise<unknown[]>((resolver) => {
        pendientes.push(resolver)
      })
    })
    render(<PuntosVenta />)
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    await usuario.click(screen.getByRole('button', { name: 'Nuevo punto de venta' }))
    await waitFor(() => expect(pendientes).toHaveLength(1))
    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))
    await usuario.click(screen.getByRole('button', { name: 'Nuevo punto de venta' }))
    await waitFor(() => expect(pendientes).toHaveLength(2))

    await act(async () => {
      pendientes[0]([empresaSur])
      await Promise.resolve()
    })
    expect(screen.queryByRole('option', { name: 'Sur SRL (Comercio Sur)' })).not.toBeInTheDocument()
    expect(screen.getByLabelText('Empresa destino')).toBeDisabled()

    await act(async () => {
      pendientes[1]([empresaEste])
      await Promise.resolve()
    })
    expect(await screen.findByRole('option', { name: 'Este SRL (Almacén Este)' })).toBeInTheDocument()
    expect(screen.queryByRole('option', { name: 'Sur SRL (Comercio Sur)' })).not.toBeInTheDocument()
  })

  it('cancelar cierra el formulario sin llamar a la API de escritura', async () => {
    const usuario = userEvent.setup()
    montarConEmpresas()
    await abrirAlta(usuario)

    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByLabelText('Empresa destino')).not.toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })
})

describe('PuntosVenta — lectura en vuelo al crear', () => {
  const empresaSur = {
    id: 20,
    idTenant: 2,
    razonSocial: 'Sur SRL',
    nombreFantasia: null,
    cuit: null,
    nombreTenant: 'Comercio Sur',
    alicuotaPercepcionIibb: null,
    alicuotaPercepcionIva: null,
  }

  beforeEach(() => {
    apiGetMock.mockReset()
    apiPostMock.mockReset()
    usuarioActual = usuarioFixture()
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

  /** Monta la pantalla con el listado inicial a cargo del test y cuenta cuántas veces se lo pidió. */
  function montarConListadoInicial(inicial: Promise<PuntoVentaListado[]> | null) {
    const pedidos = { listados: 0 }
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/empresas') return Promise.resolve([empresaSur])
      if (ruta !== '/puntos-venta') return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
      pedidos.listados += 1

      return pedidos.listados === 1 && inicial ? inicial : Promise.resolve([pvSurCentro])
    })
    render(<PuntosVenta />)

    return pedidos
  }

  async function completarYEnviar(usuario: ReturnType<typeof userEvent.setup>) {
    await waitFor(() => expect(within(screen.getByLabelText('Empresa destino')).getByRole('option', { name: 'Sur SRL (Comercio Sur)' })).toBeInTheDocument())
    await waitFor(() => expect(screen.getByLabelText('Empresa destino')).toBeEnabled())
    await usuario.selectOptions(screen.getByLabelText('Empresa destino'), '20')
    await usuario.type(screen.getByLabelText('Nombre'), 'PV Norte')
    await usuario.selectOptions(screen.getByLabelText('Modo'), 'Web')
    await usuario.click(screen.getByRole('button', { name: 'Crear' }))
  }

  /** Cláusula bajo prueba: la relectura de la rama de fallo de `guardarAlta` (`habiaLecturaEnVuelo`). */
  it('un alta rechazada con el listado inicial en vuelo no deja la tabla en el spinner', async () => {
    const usuario = userEvent.setup()
    const inicial = diferido<PuntoVentaListado[]>()
    const post = diferido<PuntoVentaListado>()
    apiPostMock.mockReturnValue(post.promesa)
    const pedidos = montarConListadoInicial(inicial.promesa)

    await usuario.click(screen.getByRole('button', { name: 'Nuevo punto de venta' }))
    await completarYEnviar(usuario)
    await act(async () => {
      inicial.resolver([pvSurCentro])
      await inicial.promesa
    })
    await act(async () => {
      post.rechazar(new ErrorApi(409, 'nombre_duplicado', 'Ya existe un punto de venta con ese nombre.'))
      await post.promesa.catch(() => {})
    })

    expect(await screen.findByText('PV Centro')).toBeInTheDocument()
    expect(screen.queryByText('Cargando…')).not.toBeInTheDocument()
    expect(pedidos.listados).toBe(2)
    expect(screen.getByText('Ya existe un punto de venta con ese nombre.')).toBeInTheDocument()
    expect(screen.getByLabelText('Empresa destino')).toBeInTheDocument()
  })

  /** Monta con el primer y el segundo pedido del listado a cargo del test; los siguientes resuelven con la fila. */
  function montarConDosListados(primero: Promise<PuntoVentaListado[]>, segundo: Promise<PuntoVentaListado[]>) {
    const pedidos = { listados: 0 }
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/empresas') return Promise.resolve([empresaSur])
      if (ruta !== '/puntos-venta') return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
      pedidos.listados += 1
      if (pedidos.listados === 1) return primero
      if (pedidos.listados === 2) return segundo

      return Promise.resolve([pvSurCentro])
    })
    render(<PuntosVenta />)

    return pedidos
  }

  /** Cláusula bajo prueba: el `setErrorFormulario('')` de `abrirAlta`. El rechazo vive en `errorFormulario`, aparte del `error` de la
   * pantalla que recibe los fallos de la relectura; reabrir el formulario con la relectura en vuelo arranca sin ese rechazo y la relectura
   * que aterriza después no lo trae de vuelta. */
  it('reabrir el formulario con la relectura en vuelo arranca sin el rechazo anterior y la relectura no lo resucita', async () => {
    const usuario = userEvent.setup()
    const inicial = diferido<PuntoVentaListado[]>()
    const relectura = diferido<PuntoVentaListado[]>()
    const post = diferido<PuntoVentaListado>()
    apiPostMock.mockReturnValue(post.promesa)
    const pedidos = montarConDosListados(inicial.promesa, relectura.promesa)

    await usuario.click(screen.getByRole('button', { name: 'Nuevo punto de venta' }))
    await completarYEnviar(usuario)
    await act(async () => {
      inicial.resolver([pvSurCentro])
      await inicial.promesa
    })
    await act(async () => {
      post.rechazar(new ErrorApi(409, 'nombre_duplicado', 'Ya existe un punto de venta con ese nombre.'))
      await post.promesa.catch(() => {})
    })
    await waitFor(() => expect(pedidos.listados).toBe(2))
    expect(screen.getByText('Ya existe un punto de venta con ese nombre.')).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))
    await usuario.click(screen.getByRole('button', { name: 'Nuevo punto de venta' }))
    const reabierto = await screen.findByRole('dialog', { name: 'Nuevo punto de venta' })
    expect(within(reabierto).queryByRole('alert')).toBeNull()
    expect(screen.queryByText('Ya existe un punto de venta con ese nombre.')).not.toBeInTheDocument()
    await act(async () => {
      relectura.resolver([pvSurCentro])
      await relectura.promesa
    })

    expect(await screen.findByText('PV Centro')).toBeInTheDocument()
    expect(within(reabierto).queryByRole('alert')).toBeNull()
    expect(screen.queryByText('Ya existe un punto de venta con ese nombre.')).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: el rechazo vive en el modal y el fallo de la carga en la pantalla; ninguno pisa al otro. */
  it('una relectura que falla tras un alta rechazada muestra el fallo de la carga sin tapar el rechazo', async () => {
    const usuario = userEvent.setup()
    const inicial = diferido<PuntoVentaListado[]>()
    const relectura = diferido<PuntoVentaListado[]>()
    const post = diferido<PuntoVentaListado>()
    apiPostMock.mockReturnValue(post.promesa)
    const pedidos = montarConDosListados(inicial.promesa, relectura.promesa)

    await usuario.click(screen.getByRole('button', { name: 'Nuevo punto de venta' }))
    await completarYEnviar(usuario)
    await act(async () => {
      inicial.resolver([pvSurCentro])
      await inicial.promesa
    })
    await act(async () => {
      post.rechazar(new ErrorApi(409, 'nombre_duplicado', 'Ya existe un punto de venta con ese nombre.'))
      await post.promesa.catch(() => {})
    })
    await waitFor(() => expect(pedidos.listados).toBe(2))
    await act(async () => {
      relectura.rechazar(new ErrorApi(500, 'error_interno', 'Se cayó.'))
      await relectura.promesa.catch(() => {})
    })

    expect(await screen.findByText('Se cayó.')).toBeInTheDocument()
    expect(screen.queryByText('Cargando…')).not.toBeInTheDocument()
    expect(
      within(screen.getByRole('dialog', { name: 'Nuevo punto de venta' })).getByRole('alert'),
    ).toHaveTextContent('Ya existe un punto de venta con ese nombre.')
  })

  /** Cláusula bajo prueba: la respuesta de la escritura no se compara con la generación del listado.
   * El alta acuña un token que descarta la lectura inicial en vuelo; si la respuesta exigiera la
   * generación del listado, el modal se quedaría abierto y ocupado para siempre. */
  it('un alta exitosa con el listado inicial en vuelo cierra el modal y devuelve el foco al botón', async () => {
    const usuario = userEvent.setup()
    const inicial = diferido<PuntoVentaListado[]>()
    apiPostMock.mockResolvedValue(pvFixture({ id: 200, nombre: 'PV Norte' }))
    montarConListadoInicial(inicial.promesa)

    await usuario.click(screen.getByRole('button', { name: 'Nuevo punto de venta' }))
    await completarYEnviar(usuario)

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(await screen.findByText('PV Centro')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Nuevo punto de venta' })).toHaveFocus()
  })

  /** Cláusula bajo prueba: la condición `habiaLecturaEnVuelo`; con el listado ya cargado, un rechazo no relee. */
  it('un alta rechazada con el listado ya cargado no vuelve a pedirlo', async () => {
    const usuario = userEvent.setup()
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'nombre_duplicado', 'Ya existe un punto de venta con ese nombre.'))
    const pedidos = montarConListadoInicial(null)
    await screen.findByText('PV Centro')

    await usuario.click(screen.getByRole('button', { name: 'Nuevo punto de venta' }))
    await completarYEnviar(usuario)

    expect(await screen.findByText('Ya existe un punto de venta con ese nombre.')).toBeInTheDocument()
    expect(pedidos.listados).toBe(1)
  })
})

describe('PuntosVenta — envío por defecto', () => {
  beforeEach(() => {
    apiGetMock.mockReset()
    apiPutMock.mockReset()
    apiPutMock.mockResolvedValue(undefined)
    usuarioActual = usuarioFixture()
  })

  it('el checkbox parte del valor del listado y el PUT lleva lo que se tildó', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue([pvFixture({ envioPorDefecto: false })])
    render(<PuntosVenta />)
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    await usuario.click(screen.getByRole('button', { name: 'Editar' }))
    const casilla = screen.getByLabelText('Envío por defecto en el POS')
    expect(casilla).not.toBeChecked()

    await usuario.click(casilla)
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    expect(apiPutMock).toHaveBeenCalledWith(
      '/puntos-venta/100',
      expect.objectContaining({ envioPorDefecto: true }),
    )
  })

  it('un punto de venta con el envío activo abre el formulario tildado', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue([pvFixture({ envioPorDefecto: true })])
    render(<PuntosVenta />)
    await waitFor(() => expect(screen.getByText('PV Centro')).toBeInTheDocument())

    await usuario.click(screen.getByRole('button', { name: 'Editar' }))

    expect(screen.getByLabelText('Envío por defecto en el POS')).toBeChecked()
  })
})

describe('PuntosVenta — formularios en modal', () => {
  const empresaSur = {
    id: 20,
    idTenant: 2,
    razonSocial: 'Sur SRL',
    nombreFantasia: null,
    cuit: null,
    nombreTenant: 'Comercio Sur',
    alicuotaPercepcionIibb: null,
    alicuotaPercepcionIva: null,
  }

  beforeEach(() => {
    apiGetMock.mockReset()
    apiPostMock.mockReset()
    apiPutMock.mockReset()
    apiPutMock.mockResolvedValue(undefined)
    apiPostMock.mockResolvedValue(pvFixture({ id: 200, nombre: 'PV Norte' }))
    usuarioActual = usuarioFixture()
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/puntos-venta') return Promise.resolve([pvSurCentro])
      if (ruta === '/empresas') return Promise.resolve([empresaSur])

      return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
    })
  })

  async function montarYEsperar() {
    render(<PuntosVenta />)
    await screen.findByText('PV Centro')
  }

  async function abrirAltaCompleta(usuario: ReturnType<typeof userEvent.setup>) {
    await montarYEsperar()
    await usuario.click(screen.getByRole('button', { name: 'Nuevo punto de venta' }))
    const dialogo = await screen.findByRole('dialog', { name: 'Nuevo punto de venta' })
    await within(dialogo).findByRole('option', { name: 'Sur SRL (Comercio Sur)' })
    await waitFor(() => expect(within(dialogo).getByLabelText('Empresa destino')).toBeEnabled())
    await usuario.selectOptions(within(dialogo).getByLabelText('Empresa destino'), '20')
    await usuario.type(within(dialogo).getByLabelText('Nombre'), 'PV Norte')
    await usuario.selectOptions(within(dialogo).getByLabelText('Modo'), 'Web')

    return dialogo
  }

  describe('alta', () => {
    it('el botón abre el formulario en un modal y no en la página', async () => {
      const usuario = userEvent.setup()
      await montarYEsperar()
      expect(screen.queryByRole('dialog')).toBeNull()

      await usuario.click(screen.getByRole('button', { name: 'Nuevo punto de venta' }))

      const dialogo = await screen.findByRole('dialog', { name: 'Nuevo punto de venta' })
      expect(within(dialogo).getByLabelText('Empresa destino')).toBeInTheDocument()
    })

    it('un alta cierra el modal, avisa y el foco vuelve al botón', async () => {
      const usuario = userEvent.setup()
      const dialogo = await abrirAltaCompleta(usuario)

      await usuario.click(within(dialogo).getByRole('button', { name: 'Crear' }))

      await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
      await screen.findByText('Se creó el punto de venta "PV Norte".')
      expect(apiPostMock).toHaveBeenCalledTimes(1)
      expect(screen.getByRole('button', { name: 'Nuevo punto de venta' })).toHaveFocus()
    })

    it('cancelar descarta lo escrito y el próximo alta abre vacío', async () => {
      const usuario = userEvent.setup()
      const dialogo = await abrirAltaCompleta(usuario)

      await usuario.click(within(dialogo).getByRole('button', { name: 'Cancelar' }))

      expect(screen.queryByRole('dialog')).toBeNull()
      expect(apiPostMock).not.toHaveBeenCalled()
      await usuario.click(screen.getByRole('button', { name: 'Nuevo punto de venta' }))
      expect(await screen.findByLabelText('Nombre')).toHaveValue('')
    })

    it('Escape cierra el modal sin guardar y devuelve el foco al botón', async () => {
      const usuario = userEvent.setup()
      await montarYEsperar()
      await usuario.click(screen.getByRole('button', { name: 'Nuevo punto de venta' }))
      await screen.findByRole('dialog', { name: 'Nuevo punto de venta' })

      await usuario.keyboard('{Escape}')

      expect(screen.queryByRole('dialog')).toBeNull()
      expect(apiPostMock).not.toHaveBeenCalled()
      expect(screen.getByRole('button', { name: 'Nuevo punto de venta' })).toHaveFocus()
    })

    it('un rechazo del servidor deja el modal abierto con el error adentro y lo escrito', async () => {
      const usuario = userEvent.setup()
      apiPostMock.mockRejectedValue(new ErrorApi(409, 'nombre_duplicado', 'Ya existe un punto de venta con ese nombre.'))
      const dialogo = await abrirAltaCompleta(usuario)

      await usuario.click(within(dialogo).getByRole('button', { name: 'Crear' }))

      expect(await within(dialogo).findByRole('alert')).toHaveTextContent('Ya existe un punto de venta con ese nombre.')
      expect(within(dialogo).getByLabelText('Nombre')).toHaveValue('PV Norte')
      expect(within(dialogo).getByRole('button', { name: 'Crear' })).toBeEnabled()
    })

    /** Cláusula bajo prueba: `disparoDeError` — el mismo mensaje local repetido vuelve a la vista. */
    it('repetir la misma validación local vuelve a anunciar el error', async () => {
      const usuario = userEvent.setup()
      const scrollIntoView = vi.fn()
      const original = Element.prototype.scrollIntoView
      Element.prototype.scrollIntoView = scrollIntoView
      try {
        await montarYEsperar()
        await usuario.click(screen.getByRole('button', { name: 'Nuevo punto de venta' }))
        const dialogo = await screen.findByRole('dialog', { name: 'Nuevo punto de venta' })
        await within(dialogo).findByRole('option', { name: 'Sur SRL (Comercio Sur)' })
        const form = within(dialogo).getByLabelText('Nombre').closest('form') as HTMLFormElement

        fireEvent.submit(form)
        await within(dialogo).findByRole('alert')
        expect(scrollIntoView).toHaveBeenCalledTimes(1)

        fireEvent.submit(form)
        await waitFor(() => expect(scrollIntoView).toHaveBeenCalledTimes(2))
      } finally {
        Element.prototype.scrollIntoView = original
      }
    })
  })

  describe('edición descriptiva', () => {
    async function abrirEdicion(usuario: ReturnType<typeof userEvent.setup>) {
      await montarYEsperar()
      await usuario.click(screen.getByRole('button', { name: 'Editar' }))

      return screen.findByRole('dialog', { name: 'Editando punto de venta 100' })
    }

    it('el botón abre el formulario precargado en un modal y no en la página', async () => {
      const usuario = userEvent.setup()
      await montarYEsperar()
      expect(screen.queryByRole('dialog')).toBeNull()

      await usuario.click(screen.getByRole('button', { name: 'Editar' }))

      const dialogo = await screen.findByRole('dialog', { name: 'Editando punto de venta 100' })
      expect(within(dialogo).getByLabelText('Nombre')).toHaveValue('PV Centro')
    })

    it('guardar manda el PUT, cierra el modal y avisa', async () => {
      const usuario = userEvent.setup()
      const dialogo = await abrirEdicion(usuario)

      await usuario.clear(within(dialogo).getByLabelText('Nombre'))
      await usuario.type(within(dialogo).getByLabelText('Nombre'), 'PV Centro 2')
      await usuario.click(within(dialogo).getByRole('button', { name: 'Guardar' }))

      await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
      expect(apiPutMock).toHaveBeenCalledTimes(1)
      expect(apiPutMock.mock.calls[0][0]).toBe('/puntos-venta/100')
      expect(apiPutMock.mock.calls[0][1]).toMatchObject({ nombre: 'PV Centro 2' })
      expect(await screen.findByText('Se actualizó "PV Centro 2".')).toBeInTheDocument()
    })

    it('un rechazo del servidor deja el modal abierto con el error adentro y lo escrito', async () => {
      const usuario = userEvent.setup()
      apiPutMock.mockRejectedValue(new ErrorApi(409, 'nombre_duplicado', 'Ya existe un punto de venta con ese nombre.'))
      const dialogo = await abrirEdicion(usuario)

      await usuario.type(within(dialogo).getByLabelText('Domicilio'), 'Calle 1')
      await usuario.click(within(dialogo).getByRole('button', { name: 'Guardar' }))

      expect(await within(dialogo).findByRole('alert')).toHaveTextContent('Ya existe un punto de venta con ese nombre.')
      expect(within(dialogo).getByLabelText('Domicilio')).toHaveValue('Calle 1')
      expect(within(dialogo).getByRole('button', { name: 'Guardar' })).toBeEnabled()
    })

    it('cancelar descarta lo escrito y la próxima edición abre con los datos del listado', async () => {
      const usuario = userEvent.setup()
      const dialogo = await abrirEdicion(usuario)
      await usuario.type(within(dialogo).getByLabelText('Domicilio'), 'Calle 1')

      await usuario.click(within(dialogo).getByRole('button', { name: 'Cancelar' }))

      expect(screen.queryByRole('dialog')).toBeNull()
      expect(apiPutMock).not.toHaveBeenCalled()
      await usuario.click(screen.getByRole('button', { name: 'Editar' }))
      expect(await screen.findByLabelText('Domicilio')).toHaveValue('')
    })

    it('un doble clic sincrónico en Guardar emite una sola escritura y el modal no se cierra mientras guarda', async () => {
      const usuario = userEvent.setup()
      let resolver: () => void = () => {}
      apiPutMock.mockReturnValue(new Promise<void>((r) => (resolver = r)))
      const dialogo = await abrirEdicion(usuario)
      const guardar = within(dialogo).getByRole('button', { name: 'Guardar' })

      act(() => {
        guardar.click()
        guardar.click()
      })

      expect(apiPutMock).toHaveBeenCalledTimes(1)
      await waitFor(() => expect(within(dialogo).getByRole('button', { name: 'Guardando…' })).toBeDisabled())
      await usuario.keyboard('{Escape}')
      expect(screen.getByRole('dialog', { name: 'Editando punto de venta 100' })).toBeInTheDocument()

      await act(async () => resolver())
      await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    })

    it('Escape cierra el modal sin guardar y devuelve el foco al botón de editar', async () => {
      const usuario = userEvent.setup()
      await abrirEdicion(usuario)

      await usuario.keyboard('{Escape}')

      expect(screen.queryByRole('dialog')).toBeNull()
      expect(apiPutMock).not.toHaveBeenCalled()
      expect(screen.getByRole('button', { name: 'Editar' })).toHaveFocus()
    })

    it('tras guardar el foco cae en el botón de alta, porque la fila se reemplaza durante el refresco', async () => {
      const usuario = userEvent.setup()
      const dialogo = await abrirEdicion(usuario)

      await usuario.click(within(dialogo).getByRole('button', { name: 'Guardar' }))

      await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
      await screen.findByText('Se actualizó "PV Centro".')
      expect(screen.getByRole('button', { name: 'Nuevo punto de venta' })).toHaveFocus()
    })
  })

  /** El flip de modo es un cambio de un atributo del registro con su propio endpoint, no un ajuste
   * operativo de la pantalla: se trata como edición y vive en su propio modal. */
  describe('cambio de modo', () => {
    beforeEach(() => {
      apiPostMock.mockResolvedValue(pvFixture({ modo: 'Escritorio' }))
    })

    async function abrirModo(usuario: ReturnType<typeof userEvent.setup>) {
      await montarYEsperar()
      await usuario.click(botonDeModo())

      return screen.findByRole('dialog', { name: 'Modo de PV Centro' })
    }

    it('el botón abre el formulario en un modal y no en la página', async () => {
      const usuario = userEvent.setup()
      await montarYEsperar()
      expect(screen.queryByRole('dialog')).toBeNull()

      await usuario.click(botonDeModo())

      const dialogo = await screen.findByRole('dialog', { name: 'Modo de PV Centro' })
      expect(within(dialogo).getByLabelText('Modo')).toHaveValue('Web')
    })

    it('guardar llama al endpoint dedicado, cierra el modal y avisa', async () => {
      const usuario = userEvent.setup()
      const dialogo = await abrirModo(usuario)

      await usuario.selectOptions(within(dialogo).getByLabelText('Modo'), 'Escritorio')
      await usuario.click(within(dialogo).getByRole('button', { name: 'Guardar' }))

      await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
      expect(apiPostMock).toHaveBeenCalledWith('/puntos-venta/100/modo', { modo: 'Escritorio' })
      expect(await screen.findByText('Se cambió el modo de "PV Centro" a Escritorio.')).toBeInTheDocument()
    })

    it('un rechazo del servidor deja el modal abierto con el error adentro y lo elegido', async () => {
      const usuario = userEvent.setup()
      apiPostMock.mockRejectedValue(
        new ErrorApi(409, 'punto_venta_con_dispositivo_activo', 'Este punto de venta tiene un dispositivo vinculado.'),
      )
      const dialogo = await abrirModo(usuario)

      await usuario.selectOptions(within(dialogo).getByLabelText('Modo'), 'Escritorio')
      await usuario.click(within(dialogo).getByRole('button', { name: 'Guardar' }))

      expect(await within(dialogo).findByRole('alert')).toHaveTextContent('Este punto de venta tiene un dispositivo vinculado.')
      expect(within(dialogo).getByLabelText('Modo')).toHaveValue('Escritorio')
      expect(within(dialogo).getByRole('button', { name: 'Guardar' })).toBeEnabled()
    })

    it('cancelar descarta lo elegido y la próxima apertura vuelve al modo del listado', async () => {
      const usuario = userEvent.setup()
      const dialogo = await abrirModo(usuario)
      await usuario.selectOptions(within(dialogo).getByLabelText('Modo'), 'Escritorio')

      await usuario.click(within(dialogo).getByRole('button', { name: 'Cancelar' }))

      expect(screen.queryByRole('dialog')).toBeNull()
      expect(apiPostMock).not.toHaveBeenCalled()
      await usuario.click(botonDeModo())
      expect(await screen.findByLabelText('Modo')).toHaveValue('Web')
    })

    it('un doble clic sincrónico en Guardar emite una sola escritura y el modal no se cierra mientras guarda', async () => {
      const usuario = userEvent.setup()
      let resolver: (pv: PuntoVentaListado) => void = () => {}
      apiPostMock.mockReturnValue(new Promise<PuntoVentaListado>((r) => (resolver = r)))
      const dialogo = await abrirModo(usuario)
      await usuario.selectOptions(within(dialogo).getByLabelText('Modo'), 'Escritorio')
      const guardar = within(dialogo).getByRole('button', { name: 'Guardar' })

      act(() => {
        guardar.click()
        guardar.click()
      })

      expect(apiPostMock).toHaveBeenCalledTimes(1)
      await waitFor(() => expect(within(dialogo).getByRole('button', { name: 'Guardando…' })).toBeDisabled())
      await usuario.keyboard('{Escape}')
      expect(screen.getByRole('dialog', { name: 'Modo de PV Centro' })).toBeInTheDocument()

      await act(async () => resolver(pvFixture({ modo: 'Escritorio' })))
      await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    })

    it('Escape cierra el modal sin guardar y devuelve el foco al botón de modo', async () => {
      const usuario = userEvent.setup()
      await abrirModo(usuario)

      await usuario.keyboard('{Escape}')

      expect(screen.queryByRole('dialog')).toBeNull()
      expect(apiPostMock).not.toHaveBeenCalled()
      expect(botonDeModo()).toHaveFocus()
    })

    it('tras guardar el foco cae en el botón de alta, porque la fila se reemplaza durante el refresco', async () => {
      const usuario = userEvent.setup()
      const dialogo = await abrirModo(usuario)

      await usuario.selectOptions(within(dialogo).getByLabelText('Modo'), 'Escritorio')
      await usuario.click(within(dialogo).getByRole('button', { name: 'Guardar' }))

      await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
      await screen.findByText('Se cambió el modo de "PV Centro" a Escritorio.')
      expect(screen.getByRole('button', { name: 'Nuevo punto de venta' })).toHaveFocus()
    })
  })
})

// Filtros por columna y orden por encabezado (grillas, slice S5b2).

const pvNorte = pvFixture({ id: 9, idEmpresa: 20, nombre: 'Zona Norte', domicilio: 'Calle 1500', modo: 'Escritorio' })
const pvAnexoEste = pvFixture({
  id: 100,
  idTenant: 3,
  idEmpresa: 30,
  nombre: 'Anexo',
  domicilio: null,
  modo: 'Web',
  nombreTenant: 'Almacén Este',
  razonSocialEmpresa: 'Beta SA',
})
const pvMedio = pvFixture({ id: 20, idEmpresa: 21, nombre: 'Medio', domicilio: 'Calle 900', modo: 'Web', razonSocialEmpresa: 'Alpha SA' })
/** El orden del servidor (Zona Norte, Anexo, Medio) no coincide con ningún orden por columna. */
const ENTREGADOS = [pvNorte, pvAnexoEste, pvMedio]

function columnaVisible(indice: number) {
  return screen
    .getAllByRole('row')
    .slice(2)
    .map((f) => within(f).getAllByRole('cell')[indice]?.textContent ?? '')
}

async function esperarFilas() {
  await waitFor(() => expect(screen.getByText('Zona Norte')).toBeInTheDocument())
}

describe('PuntosVenta — filtros por columna y orden', () => {
  beforeEach(() => {
    apiGetMock.mockReset()
    apiPutMock.mockReset()
    apiDeleteMock.mockReset()
    usuarioActual = usuarioFixture()
  })

  /** Mutación registrada: pasar `filtrados` sin ordenar a la tabla deja el orden del servidor y rompe las aserciones. */
  it('ordena por cada encabezado: el id como número, "Calle 900" antes que "Calle 1500" y los vacíos al final', async () => {
    const usuario = userEvent.setup()
    montar(ENTREGADOS)
    await esperarFilas()

    expect(columnaVisible(3)).toEqual(['Zona Norte', 'Anexo', 'Medio'])

    await usuario.click(screen.getByRole('button', { name: 'Nombre' }))
    expect(columnaVisible(3)).toEqual(['Anexo', 'Medio', 'Zona Norte'])
    await usuario.click(screen.getByRole('button', { name: 'Nombre' }))
    expect(columnaVisible(3)).toEqual(['Zona Norte', 'Medio', 'Anexo'])
    await usuario.click(screen.getByRole('button', { name: 'Nombre' }))
    expect(columnaVisible(3)).toEqual(['Zona Norte', 'Anexo', 'Medio'])

    // Como texto "100" < "20" < "9"; como número, 9 < 20 < 100.
    await usuario.click(screen.getByRole('button', { name: 'ID' }))
    expect(columnaVisible(0)).toEqual(['0009', '0020', '0100'])

    await usuario.click(screen.getByRole('button', { name: 'Domicilio' }))
    expect(columnaVisible(5)).toEqual(['Calle 900', 'Calle 1500', '—'])
    await usuario.click(screen.getByRole('button', { name: 'Domicilio' }))
    expect(columnaVisible(5)).toEqual(['Calle 1500', 'Calle 900', '—'])

    await usuario.click(screen.getByRole('button', { name: 'Empresa' }))
    expect(columnaVisible(2)).toEqual(['Alpha SA', 'Beta SA', 'Sur SRL'])

    await usuario.click(screen.getByRole('button', { name: 'Tenant' }))
    expect(columnaVisible(1)).toEqual(['Almacén Este', 'Comercio Sur', 'Comercio Sur'])

    // El primer botón "Modo" es el del encabezado; los siguientes son los de cada fila.
    await usuario.click(screen.getAllByRole('button', { name: 'Modo' })[0])
    expect(columnaVisible(6)).toEqual(['Escritorio', 'Web', 'Web'])
  })

  /** Mutación registrada: pasar `alcanzados` sin filtrar a `useOrdenamiento` deja todas las filas con un filtro puesto. */
  it('filtra con un campo por columna de texto y un selector por modo, y combina los filtros con Y', async () => {
    const usuario = userEvent.setup()
    montar(ENTREGADOS)
    await esperarFilas()

    await usuario.type(screen.getByLabelText('Filtrar por nombre'), 'MED')
    expect(columnaVisible(3)).toEqual(['Medio'])
    await usuario.clear(screen.getByLabelText('Filtrar por nombre'))

    await usuario.type(screen.getByLabelText('Filtrar por tenant'), 'almacen')
    expect(columnaVisible(3)).toEqual(['Anexo'])
    await usuario.clear(screen.getByLabelText('Filtrar por tenant'))

    await usuario.type(screen.getByLabelText('Filtrar por empresa'), 'alpha')
    expect(columnaVisible(3)).toEqual(['Medio'])
    await usuario.clear(screen.getByLabelText('Filtrar por empresa'))

    await usuario.type(screen.getByLabelText('Filtrar por domicilio'), 'calle 1500')
    expect(columnaVisible(3)).toEqual(['Zona Norte'])
    await usuario.clear(screen.getByLabelText('Filtrar por domicilio'))

    await usuario.type(screen.getByLabelText('Filtrar por id'), '0020')
    expect(columnaVisible(3)).toEqual(['Medio'])
    await usuario.clear(screen.getByLabelText('Filtrar por id'))

    await usuario.selectOptions(screen.getByLabelText('Filtrar por modo'), 'Web')
    expect(columnaVisible(3)).toEqual(['Anexo', 'Medio'])
    await usuario.selectOptions(screen.getByLabelText('Filtrar por modo'), 'Escritorio')
    expect(columnaVisible(3)).toEqual(['Zona Norte'])

    await usuario.selectOptions(screen.getByLabelText('Filtrar por modo'), 'Web')
    await usuario.type(screen.getByLabelText('Filtrar por empresa'), 'beta')
    expect(columnaVisible(3)).toEqual(['Anexo'])
  })

  it('"Limpiar filtros" restaura las filas; sin coincidencias el estado vacío lo dice', async () => {
    const usuario = userEvent.setup()
    montar(ENTREGADOS)
    await esperarFilas()

    expect(screen.getByRole('button', { name: 'Limpiar filtros' })).toBeDisabled()
    await usuario.type(screen.getByLabelText('Filtrar por nombre'), 'no existe')
    expect(screen.getByText('Ningún registro coincide con los filtros.')).toBeInTheDocument()
    expect(screen.queryByText('No hay puntos de venta cargados.')).not.toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Limpiar filtros' }))

    expect(columnaVisible(3)).toEqual(['Zona Norte', 'Anexo', 'Medio'])
    expect(screen.getByLabelText('Filtrar por nombre')).toHaveValue('')
  })

  it('sin puntos de venta dice que no hay, no que los filtros no coinciden', async () => {
    montar([])

    expect(await screen.findByText('No hay puntos de venta cargados.')).toBeInTheDocument()
  })

  it('el filtro por columna se combina con los selectores de tenant y de empresa', async () => {
    const usuario = userEvent.setup()
    montar(ENTREGADOS)
    await esperarFilas()

    await usuario.selectOptions(screen.getByLabelText('Tenant'), '2')
    expect(columnaVisible(3)).toEqual(['Zona Norte', 'Medio'])
    await usuario.type(screen.getByLabelText('Filtrar por nombre'), 'zona')
    expect(columnaVisible(3)).toEqual(['Zona Norte'])
    await usuario.selectOptions(screen.getByLabelText('Empresa'), '21')
    expect(screen.getByText('Ningún registro coincide con los filtros.')).toBeInTheDocument()
  })

  it('la fila de filtros tiene una celda por encabezado para plataforma', async () => {
    montar(ENTREGADOS)
    await esperarFilas()
    const filas = screen.getAllByRole('row')
    expect(within(filas[0]).getAllByRole('columnheader')).toHaveLength(8)
    expect(within(filas[1]).getAllByRole('columnheader')).toHaveLength(8)
  })

  it('un admin de tenant tiene los filtros por columna, sin el de tenant, y su estado vacío usa el span correcto', async () => {
    const usuario = userEvent.setup()
    usuarioActual = usuarioFixture({ id: 4, usuario: 'admin', rolId: ROL.Admin, rol: 'Admin', idTenant: 2 })
    montar([pvNorte, pvMedio])
    await esperarFilas()

    const filas = screen.getAllByRole('row')
    expect(within(filas[0]).getAllByRole('columnheader')).toHaveLength(7)
    expect(within(filas[1]).getAllByRole('columnheader')).toHaveLength(7)
    expect(screen.queryByLabelText('Filtrar por tenant')).not.toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Nombre' }))
    expect(columnaVisible(2)).toEqual(['Medio', 'Zona Norte'])
    await usuario.selectOptions(screen.getByLabelText('Filtrar por modo'), 'Escritorio')
    expect(columnaVisible(2)).toEqual(['Zona Norte'])

    await usuario.type(screen.getByLabelText('Filtrar por nombre'), 'zzz')
    expect(screen.getByText('Ningún registro coincide con los filtros.')).toHaveAttribute('colspan', '7')
  })

  it('un punto de venta huérfano se rotula "—" y en el orden por empresa va al final', async () => {
    const usuario = userEvent.setup()
    montar([pvFixture({ ...pvNorte, nombreTenant: null, razonSocialEmpresa: null }), pvAnexoEste, pvMedio])
    await esperarFilas()

    await usuario.click(screen.getByRole('button', { name: 'Empresa' }))
    expect(columnaVisible(2)).toEqual(['Alpha SA', 'Beta SA', ETIQUETA_SIN_DUENIO])
    await usuario.click(screen.getByRole('button', { name: 'Empresa' }))
    expect(columnaVisible(2)).toEqual(['Beta SA', 'Alpha SA', ETIQUETA_SIN_DUENIO])
  })

  /** Mutación registrada: devolver la marca "—" como valor de filtro de empresa o tenant hace que escribir "—" encuentre al huérfano. */
  it('escribir "—" en los filtros de empresa y de tenant no encuentra al huérfano: para filtrar cuenta como vacío', async () => {
    const usuario = userEvent.setup()
    montar([pvFixture({ ...pvNorte, nombreTenant: null, razonSocialEmpresa: null }), pvAnexoEste, pvMedio])
    await esperarFilas()

    await usuario.type(screen.getByLabelText('Filtrar por empresa'), ETIQUETA_SIN_DUENIO)
    expect(screen.getByText('Ningún registro coincide con los filtros.')).toBeInTheDocument()
    expect(screen.queryByText('Zona Norte')).not.toBeInTheDocument()
    await usuario.clear(screen.getByLabelText('Filtrar por empresa'))

    await usuario.type(screen.getByLabelText('Filtrar por tenant'), ETIQUETA_SIN_DUENIO)
    expect(screen.getByText('Ningún registro coincide con los filtros.')).toBeInTheDocument()
  })

  /** Mutación registrada: volver a `cargando ? <Cargando /> : <table>` desmonta la fila de filtros durante el refresco. */
  it('los filtros siguen montados, con lo escrito, mientras el refresco está en vuelo y quedan inertes', async () => {
    const usuario = userEvent.setup()
    let resolverRefresco!: (items: PuntoVentaListado[]) => void
    let cargas = 0
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta !== '/puntos-venta') return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
      cargas += 1
      if (cargas === 1) return Promise.resolve(ENTREGADOS)

      return new Promise<PuntoVentaListado[]>((resolver) => {
        resolverRefresco = resolver
      })
    })
    apiPutMock.mockResolvedValue(pvNorte)
    render(<PuntosVenta />)
    await esperarFilas()

    const filtro = screen.getByLabelText('Filtrar por nombre')
    await usuario.type(filtro, 'zon')
    await usuario.click(within(screen.getByRole('row', { name: /Zona Norte/ })).getByRole('button', { name: 'Editar' }))
    await usuario.click(screen.getByRole('button', { name: /Guardar/ }))

    await waitFor(() => expect(screen.getByText('Se actualizó "Zona Norte".')).toBeInTheDocument())
    expect(screen.getByText('Cargando…')).toBeInTheDocument()
    expect(screen.getByLabelText('Filtrar por nombre')).toBe(filtro)
    expect(filtro.isConnected).toBe(true)
    expect(filtro).toHaveValue('zon')
    expect(filtro).toBeDisabled()
    expect(screen.getByLabelText('Filtrar por modo')).toBeDisabled()

    await act(async () => {
      resolverRefresco(ENTREGADOS)
      await Promise.resolve()
    })
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(screen.getByLabelText('Filtrar por nombre')).toBe(filtro)
    expect(filtro).toBeEnabled()
    expect(columnaVisible(3)).toEqual(['Zona Norte'])
  })

  it('los filtros quedan inertes mientras el guardado está en vuelo y con la puerta de baja abierta', async () => {
    const usuario = userEvent.setup()
    let resolverPut!: (pv: PuntoVentaListado) => void
    apiGetMock.mockResolvedValue(ENTREGADOS)
    apiPutMock.mockImplementation(
      () =>
        new Promise<PuntoVentaListado>((resolver) => {
          resolverPut = resolver
        }),
    )
    render(<PuntosVenta />)
    await esperarFilas()

    await usuario.type(screen.getByLabelText('Filtrar por nombre'), 'zon')
    await usuario.click(within(screen.getByRole('row', { name: /Zona Norte/ })).getByRole('button', { name: 'Editar' }))
    await usuario.click(screen.getByRole('button', { name: /Guardar/ }))

    expect(screen.getByLabelText('Filtrar por nombre')).toBeDisabled()
    expect(screen.getByLabelText('Filtrar por modo')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Limpiar filtros' })).toBeDisabled()

    await act(async () => {
      resolverPut(pvNorte)
      await Promise.resolve()
    })
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(screen.getByLabelText('Filtrar por nombre')).toBeEnabled()

    await usuario.click(screen.getByRole('button', { name: 'Eliminar' }))
    expect(screen.getByRole('alertdialog', { name: 'Confirmar baja' })).toBeInTheDocument()
    expect(screen.getByLabelText('Filtrar por nombre')).toBeDisabled()
    expect(screen.getByLabelText('Filtrar por domicilio')).toBeDisabled()
  })
})
