import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, useLocation } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Tenants } from './Tenants'
import { ErrorApi } from '../api/cliente'
import type { TenantListado } from '../api/tipos'

// stage-20-organizacion-relaciones-y-bajas, slice 2 (tareas 2.9 y 2.13) y slice 5 (5.3, 5.7, 5.8).

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

/**
 * Los tres contadores son pairwise-distintos ENTRE SÍ y distintos del id y de los contadores del
 * otro tenant (`mutation-proof-tests` regla 12b): con valores iguales, intercambiar dos columnas
 * de la tabla no cambiaría nada de lo que el test ve.
 */
const tenantUno: TenantListado = {
  id: 1,
  nombre: 'Comercio Sur',
  estado: 'Activo',
  createdAt: '2026-01-15T10:00:00-03:00',
  cantidadEmpresas: 2,
  cantidadPuntosVenta: 3,
  cantidadUsuarios: 4,
}

const tenantDos: TenantListado = {
  id: 2,
  nombre: 'Almacén Este',
  estado: 'Suspendido',
  createdAt: '2026-02-20T10:00:00-03:00',
  cantidadEmpresas: 5,
  cantidadPuntosVenta: 6,
  cantidadUsuarios: 7,
}

/** Sonda de la búsqueda de la URL: el `?nuevo=1` que abre el modal de alta se consume al llegar. */
function SondaDeBusqueda() {
  const { search } = useLocation()

  return <span data-testid="busqueda">{search}</span>
}

function montar(items: TenantListado[] = [tenantUno, tenantDos], ruta = '/organizacion/tenants') {
  apiGetMock.mockImplementation((rutaApi: string) => {
    if (rutaApi === '/plataforma/tenants') return Promise.resolve(items)

    return Promise.reject(new Error(`ruta inesperada: ${rutaApi}`))
  })

  return render(
    <MemoryRouter initialEntries={[ruta]}>
      <Tenants />
      <SondaDeBusqueda />
    </MemoryRouter>,
  )
}

function celdas(nombre: string) {
  const fila = screen.getByRole('row', { name: new RegExp(nombre) })

  return within(fila).getAllByRole('cell')
}

describe('Tenants (stage-20, slice 2 — contadores de hijos)', () => {
  beforeEach(() => {
    apiGetMock.mockReset()
    apiPostMock.mockReset()
    apiPutMock.mockReset()
    apiDeleteMock.mockReset()
    apiPostMock.mockResolvedValue(undefined)
    apiPutMock.mockResolvedValue(undefined)
    apiDeleteMock.mockResolvedValue(undefined)
  })

  it('rinde los tres contadores de cada tenant en su propia columna', async () => {
    montar()
    await waitFor(() => expect(screen.getByText('Comercio Sur')).toBeInTheDocument())

    // Columnas: ID · Nombre · Estado · Empresas · Puntos de venta · Usuarios · Creado · Acciones
    const uno = celdas('Comercio Sur')
    expect(uno[3]).toHaveTextContent('2')
    expect(uno[4]).toHaveTextContent('3')
    expect(uno[5]).toHaveTextContent('4')

    const dos = celdas('Almacén Este')
    expect(dos[3]).toHaveTextContent('5')
    expect(dos[4]).toHaveTextContent('6')
    expect(dos[5]).toHaveTextContent('7')
  })

  it('encabeza las tres columnas con su nombre, en orden', () => {
    montar()

    return waitFor(() => {
      const encabezados = screen.getAllByRole('columnheader').map((h) => h.textContent)
      expect(encabezados).toEqual([
        'ID',
        'Nombre',
        'Estado',
        'Empresas',
        'Puntos de venta',
        'Usuarios',
        'Creado',
        'Acciones',
      ])
    })
  })

  it('un tenant sin hijos rinde ceros, no celdas vacías', async () => {
    montar([{ ...tenantUno, cantidadEmpresas: 0, cantidadPuntosVenta: 0, cantidadUsuarios: 0 }])
    await waitFor(() => expect(screen.getByText('Comercio Sur')).toBeInTheDocument())

    const fila = celdas('Comercio Sur')
    expect(fila[3]).toHaveTextContent('0')
    expect(fila[4]).toHaveTextContent('0')
    expect(fila[5]).toHaveTextContent('0')
  })

  /** Tarea 2.13: ninguna celda presenta un id crudo como identidad de un dueño. En esta pantalla
   * el único id que se rinde es el del PROPIO tenant (columna ID, su identidad, no la de un
   * dueño) y ninguna columna nueva lo repite. */
  it('no presenta ids de dueño: el único id de la fila es el del propio tenant', async () => {
    montar([tenantUno])
    await waitFor(() => expect(screen.getByText('Comercio Sur')).toBeInTheDocument())

    const fila = celdas('Comercio Sur')
    expect(fila[0]).toHaveTextContent('0001')
    expect(fila.filter((c) => c.textContent === '1')).toHaveLength(0)
  })
})

// stage-20-organizacion-relaciones-y-bajas, slice 5 (tareas 5.3, 5.7 y 5.8).

function botonDeBaja(nombre: string) {
  return within(screen.getByRole('row', { name: new RegExp(nombre) })).getByRole('button', {
    name: 'Eliminar',
  })
}

describe('Tenants (stage-20, slice 5 — baja lógica)', () => {
  beforeEach(() => {
    apiGetMock.mockReset()
    apiPostMock.mockReset()
    apiPutMock.mockReset()
    apiDeleteMock.mockReset()
    apiPostMock.mockResolvedValue(undefined)
    apiPutMock.mockResolvedValue(undefined)
    apiDeleteMock.mockResolvedValue(undefined)
  })

  /**
   * Cláusula bajo prueba: el `{baja && <ConfirmacionDeBaja …>}` como PUERTA. Sin ella el botón
   * llamaría al DELETE directo, y esta baja arrastra empresas, puntos de venta y usuarios: es
   * exactamente la operación que no puede pasar de un solo click.
   */
  it('el botón de baja no llama a la API hasta que se confirma', async () => {
    const usuario = userEvent.setup()
    montar()
    await waitFor(() => expect(screen.getByText('Comercio Sur')).toBeInTheDocument())

    await usuario.click(botonDeBaja('Comercio Sur'))
    expect(apiDeleteMock).not.toHaveBeenCalled()

    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))
    await waitFor(() => expect(apiDeleteMock).toHaveBeenCalledWith('/plataforma/tenants/1'))
  })

  it('cancelar cierra la puerta y no llama nunca a la API', async () => {
    const usuario = userEvent.setup()
    montar()
    await waitFor(() => expect(screen.getByText('Comercio Sur')).toBeInTheDocument())

    await usuario.click(botonDeBaja('Comercio Sur'))
    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('button', { name: 'Confirmar baja' })).not.toBeInTheDocument()
    expect(apiDeleteMock).not.toHaveBeenCalled()
  })

  /**
   * Cláusula bajo prueba: el `arrastreDeTenant(baja.fila)` que alimenta la puerta. Sin él la
   * confirmación diría "¿dar de baja el tenant?" y callaría que en la misma cascada se van sus 2
   * empresas, sus 3 puntos de venta y sus 4 usuarios. Los tres contadores del fixture son
   * pairwise-distintos, así que intercambiar dos líneas se ve.
   */
  it('la puerta nombra lo que se va en la misma cascada', async () => {
    const usuario = userEvent.setup()
    montar()
    await waitFor(() => expect(screen.getByText('Comercio Sur')).toBeInTheDocument())

    await usuario.click(botonDeBaja('Comercio Sur'))

    const puerta = screen.getByRole('alertdialog', { name: 'Confirmar baja' })
    expect(puerta).toHaveTextContent('¿Dar de baja el tenant "Comercio Sur"?')
    expect(within(puerta).getAllByRole('listitem').map((i) => i.textContent)).toEqual([
      '2 empresas',
      '3 puntos de venta',
      '4 usuarios',
    ])
  })

  /**
   * Cláusula bajo prueba: `disabled={ocupado !== null}` sobre TODA la ventana —la puerta y las
   * acciones de la tabla— desde el click hasta que el refresco aterriza (`react-async-state`
   * reglas 5 y 9). Con el DELETE en vuelo, otra acción supersedería una escritura a medio hacer.
   */
  it('durante el DELETE y su refresco no queda ninguna acción alcanzable', async () => {
    const usuario = userEvent.setup()
    let resolverDelete!: () => void
    let resolverRefresco!: (items: TenantListado[]) => void

    let cargas = 0
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta !== '/plataforma/tenants') return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
      cargas += 1
      if (cargas === 1) return Promise.resolve([tenantUno, tenantDos])

      return new Promise<TenantListado[]>((resolver) => {
        resolverRefresco = resolver
      })
    })
    apiDeleteMock.mockImplementation(
      () =>
        new Promise<void>((resolver) => {
          resolverDelete = resolver
        }),
    )

    render(
      <MemoryRouter>
        <Tenants />
      </MemoryRouter>,
    )
    await waitFor(() => expect(screen.getByText('Comercio Sur')).toBeInTheDocument())

    await usuario.click(botonDeBaja('Comercio Sur'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    expect(screen.getByRole('button', { name: 'Dando de baja…' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    for (const boton of [...screen.getAllByRole('button', { name: 'Editar' }), ...screen.getAllByRole('button', { name: 'Eliminar' })]) {
      expect(boton).toBeDisabled()
    }

    await act(async () => {
      resolverDelete()
      await Promise.resolve()
    })

    await waitFor(() => expect(screen.getByText('Se dio de baja el tenant "Comercio Sur".')).toBeInTheDocument())
    expect(screen.getByText('Cargando…')).toBeInTheDocument()
    expect(screen.queryAllByRole('button', { name: 'Eliminar' })).toHaveLength(0)

    await act(async () => {
      resolverRefresco([tenantDos])
      await Promise.resolve()
    })

    await waitFor(() => expect(screen.getAllByRole('button', { name: 'Eliminar' })[0]).toBeEnabled())
    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
  })

  /**
   * Cláusula bajo prueba: el `if (ocupadoRef.current) return` de `darDeBaja`, la guarda de
   * re-entrancia de la regla 9. Un doble click en el MISMO tick le gana al atributo `disabled`,
   * que solo existe después del re-render, y mandaría dos DELETE sobre la misma fila.
   */
  it('un segundo click sobre la confirmación en vuelo se descarta', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockImplementation(() => new Promise<void>(() => {}))
    montar()
    await waitFor(() => expect(screen.getByText('Comercio Sur')).toBeInTheDocument())

    await usuario.click(botonDeBaja('Comercio Sur'))
    const confirmar = screen.getByRole('button', { name: 'Confirmar baja' })
    await act(async () => {
      confirmar.click()
      confirmar.click()
      await Promise.resolve()
    })

    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
  })

  /**
   * Cláusula bajo prueba: el `catch` PROPIO del refresco (`react-async-state` regla 6). La baja ya
   * commiteó: reportarla como fallida sería mentir sobre una escritura hecha.
   */
  it('un refresco fallido después de la baja no la reporta como fallida', async () => {
    const usuario = userEvent.setup()
    let cargas = 0
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta !== '/plataforma/tenants') return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
      cargas += 1
      if (cargas === 1) return Promise.resolve([tenantUno])

      return Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó.'))
    })

    render(
      <MemoryRouter>
        <Tenants />
      </MemoryRouter>,
    )
    await waitFor(() => expect(screen.getByText('Comercio Sur')).toBeInTheDocument())

    await usuario.click(botonDeBaja('Comercio Sur'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await waitFor(() =>
      expect(
        screen.getByText(
          'Se dio de baja el tenant "Comercio Sur". Se eliminó, pero no se pudo actualizar la vista. Recargá la pantalla.',
        ),
      ).toBeInTheDocument(),
    )
  })

  /**
   * Cláusula bajo prueba: `copiaDeFalloDeBaja(e, 'el tenant')` en vez del `e.message` pelado. El
   * `codigo` elige la guía; el `mensaje` —que es lo único que nombra QUÉ bloquea— se rinde igual.
   */
  it('un 409 tenant_en_uso rinde su guía propia sin tragarse el mensaje del servidor', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockRejectedValue(
      new ErrorApi(409, 'tenant_en_uso', 'No se puede dar de baja el tenant porque tiene 3 ventas.'),
    )
    montar()
    await waitFor(() => expect(screen.getByText('Comercio Sur')).toBeInTheDocument())

    await usuario.click(botonDeBaja('Comercio Sur'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await waitFor(() => expect(screen.getByText(/porque tiene 3 ventas/)).toBeInTheDocument())
    expect(screen.getByText(/Dá de baja o reasigná esos datos antes de eliminar el tenant\./)).toBeInTheDocument()
    // La puerta queda abierta: el motivo se lee sin perder de vista qué se iba a dar de baja.
    expect(screen.getByRole('button', { name: 'Confirmar baja' })).toBeEnabled()
  })

  /**
   * Cláusula bajo prueba: la rama del 404 de `copiaDeFalloDeBaja`, que corre ANTES de mirar el
   * código. Es el anti-oráculo de BO-R12 llevado a la UI: una fila fuera de alcance y una
   * inexistente se rinden idénticas, y ninguna insinúa uso.
   */
  it('un 404 rinde la copia neutra de inexistencia, nunca una pista de uso', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'No existe el tenant 1.'))
    montar()
    await waitFor(() => expect(screen.getByText('Comercio Sur')).toBeInTheDocument())

    await usuario.click(botonDeBaja('Comercio Sur'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await waitFor(() =>
      expect(
        screen.getByText('No se pudo dar de baja el tenant. Ya no existe o no está a tu alcance. Actualizá el listado.'),
      ).toBeInTheDocument(),
    )
    expect(screen.queryByText(/en uso|tiene \d+/)).not.toBeInTheDocument()
  })
})

// stage-20-organizacion-relaciones-y-bajas, slice 5, judgment-day ronda 1 (C1, C2, C4 y C8).

describe('Tenants (slice 5, ronda 1 — la puerta es modal y el token se acuña al confirmar)', () => {
  beforeEach(() => {
    apiGetMock.mockReset()
    apiPostMock.mockReset()
    apiPutMock.mockReset()
    apiDeleteMock.mockReset()
    apiPostMock.mockResolvedValue(undefined)
    apiPutMock.mockResolvedValue(undefined)
    apiDeleteMock.mockResolvedValue(undefined)
  })

  /**
   * Cláusula bajo prueba: `bloqueado = ocupado !== null || confirmacion !== null` en TODOS los
   * `disabled` de la pantalla. Con `ocupado` solo, la puerta abierta dejaba vivos Guardar,
   * Suspender, Editar y Baja: cualquiera de ellos acuñaba una generación nueva y el DELETE que
   * salía después de la puerta ya no aplicaba nada (`react-async-state` regla 9 — bloquear la
   * ventana, no reconciliar tokens).
   */
  it('con la puerta abierta no queda ninguna otra acción alcanzable', async () => {
    const usuario = userEvent.setup()
    montar()
    await waitFor(() => expect(screen.getByText('Comercio Sur')).toBeInTheDocument())

    await usuario.click(botonDeBaja('Almacén Este'))

    const puerta = screen.getByRole('alertdialog', { name: 'Confirmar baja' })
    for (const boton of [
      ...screen.getAllByRole('button', { name: 'Editar' }),
      ...screen.getAllByRole('button', { name: 'Eliminar' }),
      ...screen.getAllByRole('button', { name: 'Suspender' }),
      ...screen.getAllByRole('button', { name: 'Reactivar' }),
    ]) {
      expect(boton).toBeDisabled()
    }
    expect(screen.getByRole('button', { name: 'Nuevo tenant' })).toBeDisabled()

    // La puerta misma sigue viva: es lo único que el operador puede tocar.
    expect(within(puerta).getByRole('button', { name: 'Confirmar baja' })).toBeEnabled()
    expect(within(puerta).getByRole('button', { name: 'Cancelar' })).toBeEnabled()

    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/plataforma/tenants') return Promise.resolve([tenantUno])

      return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
    })
    await usuario.click(within(puerta).getByRole('button', { name: 'Confirmar baja' }))

    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument())
    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
    expect(apiDeleteMock).toHaveBeenCalledWith('/plataforma/tenants/2')
    expect(screen.queryByText('Almacén Este')).not.toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba: `disabled={bloqueado}` del botón "Nuevo tenant". Con la puerta abierta no puede abrir el modal
   * de alta encima: el operador no abandona a medias la decisión de la baja.
   */
  it('con la puerta abierta, activar "Nuevo tenant" no abre el alta', async () => {
    const usuario = userEvent.setup()
    montar()
    await waitFor(() => expect(screen.getByText('Comercio Sur')).toBeInTheDocument())

    await usuario.click(botonDeBaja('Almacén Este'))
    expect(screen.getByRole('alertdialog', { name: 'Confirmar baja' })).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Nuevo tenant' }))

    expect(screen.queryByRole('dialog', { name: 'Nuevo tenant' })).not.toBeInTheDocument()
    // Y la puerta sigue ahí: no se abandonó la decisión a medio tomar.
    expect(screen.getByRole('alertdialog', { name: 'Confirmar baja' })).toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba: el `setError('')` de `cancelarConfirmacion`. Tras un 409 la puerta queda
   * abierta con el motivo en rojo al lado; cancelarla sin limpiarlo dejaba el banner huérfano,
   * hablando de una baja que ya nadie está por hacer.
   */
  it('cancelar después de un rechazo se lleva el motivo con la puerta', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockRejectedValue(
      new ErrorApi(409, 'tenant_en_uso', 'No se puede dar de baja el tenant porque tiene 3 ventas.'),
    )
    montar()
    await waitFor(() => expect(screen.getByText('Comercio Sur')).toBeInTheDocument())

    await usuario.click(botonDeBaja('Comercio Sur'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))
    await waitFor(() => expect(screen.getByText(/porque tiene 3 ventas/)).toBeInTheDocument())

    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.queryByText(/porque tiene 3 ventas/)).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: el listener de `Escape`, cableado de punta a punta desde la pantalla. */
  it('Escape cierra la puerta sin llamar a la API', async () => {
    const usuario = userEvent.setup()
    montar()
    await waitFor(() => expect(screen.getByText('Comercio Sur')).toBeInTheDocument())

    await usuario.click(botonDeBaja('Comercio Sur'))
    fireEvent.keyDown(document, { key: 'Escape' })

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(apiDeleteMock).not.toHaveBeenCalled()
  })

  /**
   * Cláusula bajo prueba: que Suspender y Reactivar pasen por `pedirConfirmacion` y no por el
   * `confirm()` nativo que tenían en ESTE MISMO archivo (`react-async-state` regla 10 puertas
   * adentro). El diálogo del navegador no se puede dejar inerte mientras el POST está en vuelo, y
   * convivía con una puerta en app a tres líneas de distancia.
   */
  it('suspender pasa por la misma puerta y no llama a la API hasta confirmar', async () => {
    const usuario = userEvent.setup()
    montar([tenantUno])
    await waitFor(() => expect(screen.getByText('Comercio Sur')).toBeInTheDocument())

    await usuario.click(screen.getByRole('button', { name: 'Suspender' }))
    expect(apiPostMock).not.toHaveBeenCalled()

    const puerta = screen.getByRole('alertdialog', { name: 'Confirmar suspensión' })
    expect(puerta).toHaveTextContent('¿Suspender el tenant "Comercio Sur"?')
    // Suspender no borra nada: la nota de la baja lógica no puede colarse.
    expect(puerta).not.toHaveTextContent(/baja es lógica/)
    expect(screen.getByRole('button', { name: 'Editar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Eliminar' })).toBeDisabled()

    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/plataforma/tenants') return Promise.resolve([{ ...tenantUno, estado: 'Suspendido' }])

      return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
    })
    await usuario.click(within(puerta).getByRole('button', { name: 'Confirmar suspensión' }))

    await waitFor(() => expect(screen.getByText('Tenant "Comercio Sur" suspendido.')).toBeInTheDocument())
    expect(apiPostMock).toHaveBeenCalledTimes(1)
    expect(apiPostMock).toHaveBeenCalledWith('/plataforma/tenants/1/suspender')
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
  })

  it('cancelar la suspensión no llama nunca a la API', async () => {
    const usuario = userEvent.setup()
    montar([tenantUno])
    await waitFor(() => expect(screen.getByText('Comercio Sur')).toBeInTheDocument())

    await usuario.click(screen.getByRole('button', { name: 'Suspender' }))
    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  /** Rama gemela: reactivar comparte la puerta con su propia copia, no la de la baja. */
  it('reactivar pasa por la misma puerta, con su propia copia', async () => {
    const usuario = userEvent.setup()
    montar([tenantDos])
    await waitFor(() => expect(screen.getByText('Almacén Este')).toBeInTheDocument())

    await usuario.click(screen.getByRole('button', { name: 'Reactivar' }))

    const puerta = screen.getByRole('alertdialog', { name: 'Confirmar reactivación' })
    expect(puerta).toHaveTextContent('¿Reactivar el tenant "Almacén Este"?')

    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/plataforma/tenants') return Promise.resolve([{ ...tenantDos, estado: 'Activo' }])

      return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
    })
    await usuario.click(within(puerta).getByRole('button', { name: 'Confirmar reactivación' }))

    await waitFor(() => expect(screen.getByText('Tenant "Almacén Este" reactivado.')).toBeInTheDocument())
    expect(apiPostMock).toHaveBeenCalledWith('/plataforma/tenants/2/reactivar')
  })
})

function diferida<T>() {
  let resolver!: (valor: T) => void
  let rechazar!: (motivo: unknown) => void
  const promesa = new Promise<T>((resolve, reject) => {
    resolver = resolve
    rechazar = reject
  })

  return { promesa, resolver, rechazar }
}

function botonEditar(nombre: string) {
  return within(screen.getByRole('row', { name: new RegExp(nombre) })).getByRole('button', { name: 'Editar' })
}

function lecturasDelListado() {
  return apiGetMock.mock.calls.filter((c) => c[0] === '/plataforma/tenants').length
}

describe('Tenants — el renombrado es un modal', () => {
  beforeEach(() => {
    apiGetMock.mockReset()
    apiPostMock.mockReset()
    apiPutMock.mockReset()
    apiDeleteMock.mockReset()
    apiPutMock.mockResolvedValue(undefined)
  })

  async function abrirEdicion(nombre = 'Comercio Sur') {
    montar()
    await waitFor(() => expect(screen.getByText(nombre)).toBeInTheDocument())
    await userEvent.click(botonEditar(nombre))
  }

  it('"Editar" abre un modal con el nombre del tenant; "Cancelar" lo descarta sin escribir y devuelve el foco', async () => {
    await abrirEdicion()

    const dialogo = screen.getByRole('dialog', { name: 'Editando tenant 1' })
    expect(within(dialogo).getByLabelText('Nombre')).toHaveValue('Comercio Sur')
    await userEvent.type(within(dialogo).getByLabelText('Nombre'), ' 2')
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(apiPutMock).not.toHaveBeenCalled()
    expect(botonEditar('Comercio Sur')).toHaveFocus()
    await userEvent.click(botonEditar('Comercio Sur'))
    expect(screen.getByLabelText('Nombre')).toHaveValue('Comercio Sur')
  })

  it('Escape cierra el modal sin escribir y devuelve el foco al botón que lo abrió', async () => {
    await abrirEdicion()

    await userEvent.keyboard('{Escape}')

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(apiPutMock).not.toHaveBeenCalled()
    expect(botonEditar('Comercio Sur')).toHaveFocus()
  })

  it('guardar manda el nombre, refresca el listado y cierra el modal con el foco en "Nuevo tenant" (la fila se reemplazó)', async () => {
    await abrirEdicion()
    await userEvent.clear(screen.getByLabelText('Nombre'))
    await userEvent.type(screen.getByLabelText('Nombre'), 'Comercio Norte')

    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('Se actualizó el tenant "Comercio Norte".')).toBeInTheDocument()
    expect(apiPutMock).toHaveBeenCalledExactlyOnceWith('/plataforma/tenants/1', { nombre: 'Comercio Norte' })
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(lecturasDelListado()).toBe(2)
    expect(screen.getByRole('button', { name: 'Nuevo tenant' })).toHaveFocus()
  })

  it('un rechazo del servidor queda adentro del modal, que sigue abierto con lo tipeado y se puede reintentar', async () => {
    apiPutMock.mockRejectedValueOnce(new ErrorApi(409, 'tenant_duplicado', 'Ya existe un tenant con ese nombre.'))
    await abrirEdicion()
    await userEvent.clear(screen.getByLabelText('Nombre'))
    await userEvent.type(screen.getByLabelText('Nombre'), 'Almacén Este')

    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    const dialogo = screen.getByRole('dialog', { name: 'Editando tenant 1' })
    expect(await within(dialogo).findByText('Ya existe un tenant con ese nombre.')).toBeInTheDocument()
    expect(within(dialogo).getByLabelText('Nombre')).toHaveValue('Almacén Este')
    expect(within(dialogo).getByRole('button', { name: 'Guardar' })).toBeEnabled()
    expect(screen.queryByText(/Se actualizó el tenant/)).not.toBeInTheDocument()
    // Cerrar y volver a abrir no arrastra el rechazo.
    await userEvent.click(within(dialogo).getByRole('button', { name: 'Cancelar' }))
    await userEvent.click(botonEditar('Comercio Sur'))
    expect(screen.queryByText('Ya existe un tenant con ese nombre.')).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `setErrorDeEdicion('')` al empezar `guardarNombre`: el rechazo del intento anterior no puede
   * seguir en pantalla mientras el nuevo PUT está en vuelo. */
  it('guardar de nuevo borra el rechazo del intento anterior apenas empieza', async () => {
    const segundo = diferida<undefined>()
    apiPutMock.mockRejectedValueOnce(new ErrorApi(409, 'tenant_duplicado', 'Ya existe un tenant con ese nombre.')).mockReturnValueOnce(segundo.promesa)
    await abrirEdicion()
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))
    await screen.findByText('Ya existe un tenant con ese nombre.')

    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(apiPutMock).toHaveBeenCalledTimes(2)
    expect(screen.queryByText('Ya existe un tenant con ese nombre.')).not.toBeInTheDocument()
    await act(async () => {
      segundo.resolver(undefined)
    })
  })

  it('mientras guarda el modal queda inerte y no se cierra, y un doble clic sincrónico emite un solo PUT', async () => {
    const escritura = diferida<undefined>()
    apiPutMock.mockReturnValue(escritura.promesa)
    await abrirEdicion()
    const guardar = screen.getByRole('button', { name: 'Guardar' })

    await act(async () => {
      guardar.click()
      guardar.click()
      await Promise.resolve()
    })

    expect(apiPutMock).toHaveBeenCalledTimes(1)
    expect(screen.getByRole('button', { name: 'Guardando…' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cerrar' })).toBeDisabled()
    expect(screen.getByLabelText('Nombre')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Nuevo tenant' })).toBeDisabled()
    await userEvent.keyboard('{Escape}')
    expect(screen.getByRole('dialog', { name: 'Editando tenant 1' })).toBeInTheDocument()

    await act(async () => {
      escritura.resolver(undefined)
    })
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })

  /** Cláusula bajo prueba: el refresco aislado del try/catch de la escritura (react-async-state regla 6). */
  it('un refresco fallido después de guardar no reporta el guardado como fallido y el modal se cierra igual', async () => {
    montar()
    await waitFor(() => expect(screen.getByText('Comercio Sur')).toBeInTheDocument())
    apiGetMock.mockImplementation(() => Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó el refresco.')))
    await userEvent.click(botonEditar('Comercio Sur'))

    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(
      await screen.findByText('Se actualizó el tenant "Comercio Sur". Se guardó, pero no se pudo actualizar la vista. Recargá la pantalla.'),
    ).toBeInTheDocument()
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(screen.queryByText(/No se pudo guardar/)).not.toBeInTheDocument()
  })
})

describe('Tenants — el alta es un modal', () => {
  const creado = { idTenant: 9, idEmpresa: 10, idPuntoVenta: 11, idUsuarioAdmin: 12, passwordTemporal: 'Temp-1234' }

  beforeEach(() => {
    apiGetMock.mockReset()
    apiPostMock.mockReset()
    apiPutMock.mockReset()
    apiDeleteMock.mockReset()
    apiPostMock.mockResolvedValue(creado)
  })

  async function abrirAlta() {
    montar()
    await waitFor(() => expect(screen.getByText('Comercio Sur')).toBeInTheDocument())
    await userEvent.click(screen.getByRole('button', { name: 'Nuevo tenant' }))
    await screen.findByRole('dialog', { name: 'Nuevo tenant' })
  }

  async function completar() {
    await userEvent.type(screen.getByLabelText('Nombre del tenant'), 'Kiosco Oeste')
    await userEvent.type(screen.getByLabelText('Razón social de la empresa'), 'Kiosco Oeste SRL')
    await userEvent.type(screen.getByLabelText('Nombre del punto de venta'), 'Caja 1')
    await userEvent.type(screen.getByLabelText('Mail del admin del tenant'), 'admin@kiosco.com')
  }

  it('"Nuevo tenant" abre el modal de alta con sus campos', async () => {
    await abrirAlta()

    const dialogo = screen.getByRole('dialog', { name: 'Nuevo tenant' })
    for (const etiqueta of ['Nombre del tenant', 'Razón social de la empresa', 'Nombre del punto de venta', 'Mail del admin del tenant', 'Modo del punto de venta']) {
      expect(within(dialogo).getByLabelText(etiqueta)).toBeInTheDocument()
    }
    expect(within(dialogo).getByLabelText('Modo del punto de venta')).toHaveValue('Web')
  })

  it('aprovisionar manda el pedido, muestra la contraseña temporal SIN cerrar el modal, vacía el formulario y refresca el listado', async () => {
    await abrirAlta()
    await completar()

    await userEvent.click(screen.getByRole('button', { name: 'Aprovisionar tenant' }))

    expect(await screen.findByText('Temp-1234')).toBeInTheDocument()
    expect(apiPostMock).toHaveBeenCalledExactlyOnceWith('/plataforma/tenants', {
      nombreTenant: 'Kiosco Oeste',
      razonSocialEmpresa: 'Kiosco Oeste SRL',
      nombrePuntoVenta: 'Caja 1',
      mailAdmin: 'admin@kiosco.com',
      modo: 'Web',
    })
    expect(screen.getByText(/Tenant #9 creado: empresa #10, punto de venta #11, usuario admin #12\./)).toBeInTheDocument()
    expect(screen.getByRole('dialog', { name: 'Nuevo tenant' })).toBeInTheDocument()
    expect(screen.getByLabelText('Nombre del tenant')).toHaveValue('')
    expect(screen.getByRole('button', { name: 'Aprovisionar tenant' })).toBeEnabled()
    await waitFor(() => expect(lecturasDelListado()).toBe(2))
  })

  it('"Cerrar" después de un alta cierra el modal, descarta la contraseña y devuelve el foco a "Nuevo tenant"', async () => {
    await abrirAlta()
    await completar()
    await userEvent.click(screen.getByRole('button', { name: 'Aprovisionar tenant' }))
    await screen.findByText('Temp-1234')

    await userEvent.click(screen.getByRole('button', { name: 'Cerrar' }))

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.queryByText('Temp-1234')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Nuevo tenant' })).toHaveFocus()
  })

  it('un rechazo del servidor queda adentro del modal, con lo tipeado, y no se pierde nada', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'mail_duplicado', 'Ese mail ya está en uso.'))
    await abrirAlta()
    await completar()

    await userEvent.click(screen.getByRole('button', { name: 'Aprovisionar tenant' }))

    const dialogo = screen.getByRole('dialog', { name: 'Nuevo tenant' })
    expect(await within(dialogo).findByText('Ese mail ya está en uso.')).toBeInTheDocument()
    expect(within(dialogo).getByLabelText('Nombre del tenant')).toHaveValue('Kiosco Oeste')
    expect(within(dialogo).getByLabelText('Mail del admin del tenant')).toHaveValue('admin@kiosco.com')
    expect(within(dialogo).getByRole('button', { name: 'Aprovisionar tenant' })).toBeEnabled()
    expect(lecturasDelListado()).toBe(1)
  })

  it('un fallo que no es del servidor rinde el mensaje genérico', async () => {
    apiPostMock.mockRejectedValue(new TypeError('Failed to fetch'))
    await abrirAlta()
    await completar()

    await userEvent.click(screen.getByRole('button', { name: 'Aprovisionar tenant' }))

    expect(await screen.findByText('No se pudo aprovisionar el tenant.')).toBeInTheDocument()
  })

  it('"Cancelar" descarta lo tipeado y devuelve el foco al botón; al reabrir arranca vacío', async () => {
    await abrirAlta()
    await userEvent.type(screen.getByLabelText('Nombre del tenant'), 'Kiosco')

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
    expect(screen.getByRole('button', { name: 'Nuevo tenant' })).toHaveFocus()
    await userEvent.click(screen.getByRole('button', { name: 'Nuevo tenant' }))
    expect(screen.getByLabelText('Nombre del tenant')).toHaveValue('')
  })

  it('Escape cierra el modal sin escribir y devuelve el foco al botón que lo abrió', async () => {
    await abrirAlta()

    await userEvent.keyboard('{Escape}')

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
    expect(screen.getByRole('button', { name: 'Nuevo tenant' })).toHaveFocus()
  })

  /** Cláusula bajo prueba: `enviandoRef`, el espejo sincrónico (react-async-state regla 11), y la ventana inerte del POST:
   * dos clics en el mismo tick no aprovisionan dos tenants, y el modal no se cierra con el POST en vuelo. */
  it('un doble clic sincrónico emite un solo POST y mientras tanto el modal queda inerte y no se cierra', async () => {
    const escritura = diferida<typeof creado>()
    apiPostMock.mockReturnValue(escritura.promesa)
    await abrirAlta()
    await completar()
    const enviar = screen.getByRole('button', { name: 'Aprovisionar tenant' })

    await act(async () => {
      enviar.click()
      enviar.click()
      await Promise.resolve()
    })

    expect(apiPostMock).toHaveBeenCalledTimes(1)
    expect(screen.getByRole('button', { name: 'Aprovisionando…' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cerrar ventana' })).toBeDisabled()
    expect(screen.getByLabelText('Nombre del tenant')).toBeDisabled()
    await userEvent.keyboard('{Escape}')
    expect(screen.getByRole('dialog', { name: 'Nuevo tenant' })).toBeInTheDocument()

    await act(async () => {
      escritura.resolver(creado)
    })
    expect(await screen.findByText('Temp-1234')).toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `setResultado(null)` al empezar `enviar`: la contraseña del alta anterior no puede seguir en
   * pantalla mientras la siguiente está en vuelo. */
  it('aprovisionar de nuevo borra el resultado del alta anterior apenas empieza', async () => {
    await abrirAlta()
    await completar()
    await userEvent.click(screen.getByRole('button', { name: 'Aprovisionar tenant' }))
    await screen.findByText('Temp-1234')
    apiPostMock.mockImplementation(() => new Promise(() => {}))
    await completar()

    await userEvent.click(screen.getByRole('button', { name: 'Aprovisionar tenant' }))

    expect(apiPostMock).toHaveBeenCalledTimes(2)
    expect(screen.queryByText('Temp-1234')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Aprovisionando…' })).toBeDisabled()
  })

  it('"Copiar" manda la contraseña al portapapeles y lo dice', async () => {
    const escribir = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(navigator, 'clipboard', { value: { writeText: escribir }, configurable: true })
    await abrirAlta()
    await completar()
    await userEvent.click(screen.getByRole('button', { name: 'Aprovisionar tenant' }))
    await screen.findByText('Temp-1234')

    await userEvent.click(screen.getByRole('button', { name: 'Copiar' }))

    expect(escribir).toHaveBeenCalledExactlyOnceWith('Temp-1234')
    expect(await screen.findByRole('button', { name: 'Copiada' })).toBeInTheDocument()
  })

  it('`?nuevo=1` abre el modal de alta al llegar y se consume: la URL queda limpia', async () => {
    montar([tenantUno, tenantDos], '/organizacion/tenants?nuevo=1')

    expect(await screen.findByRole('dialog', { name: 'Nuevo tenant' })).toBeInTheDocument()
    await waitFor(() => expect(screen.getByTestId('busqueda').textContent).toBe(''))
    await userEvent.keyboard('{Escape}')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    // Sin un clic previo no hay disparador: el foco cae en el botón de alta.
    expect(screen.getByRole('button', { name: 'Nuevo tenant' })).toHaveFocus()
  })

  it('sin el parámetro la pantalla no abre ningún modal', async () => {
    montar()
    await waitFor(() => expect(screen.getByText('Comercio Sur')).toBeInTheDocument())

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('abrir el alta borra el aviso de la escritura anterior', async () => {
    montar()
    await waitFor(() => expect(screen.getByText('Comercio Sur')).toBeInTheDocument())
    await userEvent.click(botonEditar('Comercio Sur'))
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))
    await screen.findByText('Se actualizó el tenant "Comercio Sur".')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Nuevo tenant' })).toBeEnabled())

    await userEvent.click(screen.getByRole('button', { name: 'Nuevo tenant' }))

    expect(screen.queryByText(/Se actualizó el tenant/)).not.toBeInTheDocument()
  })
})
