import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Tenants } from './Tenants'
import { ErrorApi } from '../api/cliente'
import type { TenantListado } from '../api/tipos'

// stage-20-organizacion-relaciones-y-bajas, slice 2 (tareas 2.9 y 2.13) y slice 5 (5.3, 5.7, 5.8).

const apiGetMock = vi.fn()
const apiDeleteMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: vi.fn(),
    put: vi.fn(),
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
    apiDeleteMock.mockReset()
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
    apiDeleteMock.mockReset()
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
   * Cláusula bajo prueba: el `if (!baja || ocupado !== null) return` de `confirmarBaja`, la guarda
   * de re-entrancia de la regla 9. Un doble click en el MISMO tick le gana al atributo `disabled`,
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
