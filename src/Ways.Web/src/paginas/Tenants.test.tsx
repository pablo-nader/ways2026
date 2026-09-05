import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
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

function montar(items: TenantListado[] = [tenantUno, tenantDos]) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/plataforma/tenants') return Promise.resolve(items)

    return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
  })

  return render(
    <MemoryRouter>
      <Tenants />
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
    name: 'Baja',
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
    for (const boton of [...screen.getAllByRole('button', { name: 'Editar' }), ...screen.getAllByRole('button', { name: 'Baja' })]) {
      expect(boton).toBeDisabled()
    }

    await act(async () => {
      resolverDelete()
      await Promise.resolve()
    })

    await waitFor(() => expect(screen.getByText('Se dio de baja el tenant "Comercio Sur".')).toBeInTheDocument())
    expect(screen.getByText('Cargando…')).toBeInTheDocument()
    expect(screen.queryAllByRole('button', { name: 'Baja' })).toHaveLength(0)

    await act(async () => {
      resolverRefresco([tenantDos])
      await Promise.resolve()
    })

    await waitFor(() => expect(screen.getAllByRole('button', { name: 'Baja' })[0]).toBeEnabled())
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

/** El `<form>` de edición, que es el único disparador de escritura que sigue existiendo en el DOM
 * con la puerta abierta (sus controles están inertes, pero el elemento sigue ahí). Sirve de
 * palanca POR DEBAJO del confound (`mutation-proof-tests` regla 3): ningún operador puede llegar
 * acá con la puerta abierta —no hay campo ni botón habilitado que dispare el submit implícito—,
 * y por eso mismo es la única forma de acuñar una generación en esa ventana y ver qué hace el
 * token de la escritura. */
function formularioDeEdicion(contenedor: HTMLElement) {
  const form = contenedor.querySelector('form')
  if (!form) throw new Error('no hay formulario de edición abierto')

  return form
}

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

    await usuario.click(
      within(screen.getByRole('row', { name: /Comercio Sur/ })).getByRole('button', { name: 'Editar' }),
    )
    await usuario.click(botonDeBaja('Almacén Este'))

    const puerta = screen.getByRole('alertdialog', { name: 'Confirmar baja' })
    for (const boton of [
      ...screen.getAllByRole('button', { name: 'Editar' }),
      ...screen.getAllByRole('button', { name: 'Baja' }),
      ...screen.getAllByRole('button', { name: 'Suspender' }),
      ...screen.getAllByRole('button', { name: 'Reactivar' }),
      ...screen.getAllByRole('button', { name: 'Guardar' }),
    ]) {
      expect(boton).toBeDisabled()
    }
    expect(screen.getByLabelText('Nombre')).toBeDisabled()
    expect(screen.getByRole('link', { name: 'Nuevo tenant' })).toHaveAttribute('aria-disabled', 'true')

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
   * Cláusula bajo prueba: `const token = ++generacion.current` como PRIMERA sentencia síncrona de
   * `darDeBaja`, y la ausencia del chequeo de generación posterior a la red. Con el token acuñado
   * al ABRIR la puerta, una generación acuñada en el medio lo dejaba viejo: el DELETE salía igual,
   * el 204 volvía, y el `if (generacion.current !== token) return` se lo tragaba — la fila seguía
   * listada, la puerta seguía abierta y cada click repetía un DELETE silencioso.
   *
   * El submit se dispara sobre el `<form>` mismo, POR DEBAJO del confound: hoy `bloqueado` deja el
   * formulario inerte, así que este camino no es alcanzable a mano — y esa es exactamente la razón
   * por la que el test tiene que bajar hasta acá para ver el token.
   */
  it('una generación acuñada entre abrir y confirmar no se traga el 204', async () => {
    const usuario = userEvent.setup()
    const { container } = montar()
    await waitFor(() => expect(screen.getByText('Comercio Sur')).toBeInTheDocument())

    await usuario.click(
      within(screen.getByRole('row', { name: /Comercio Sur/ })).getByRole('button', { name: 'Editar' }),
    )
    await usuario.click(botonDeBaja('Almacén Este'))

    await act(async () => {
      fireEvent.submit(formularioDeEdicion(container))
    })
    await waitFor(() => expect(screen.getByText('Se actualizó el tenant "Comercio Sur".')).toBeInTheDocument())
    expect(apiPutMock).toHaveBeenCalledTimes(1)

    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/plataforma/tenants') return Promise.resolve([tenantUno])

      return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
    })
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await waitFor(() =>
      expect(screen.getByText('Se dio de baja el tenant "Almacén Este".')).toBeInTheDocument(),
    )
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.queryByText('Almacén Este')).not.toBeInTheDocument()
    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
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
    expect(screen.getByRole('button', { name: 'Baja' })).toBeDisabled()

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
