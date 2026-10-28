import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { EquiposPos, NOTA_DE_REVOCACION } from './EquiposPos'
import { ErrorApi } from '../api/cliente'
import type { DispositivoListado } from '../api/dispositivos'
import { fechaHoraATexto } from '../formato/fechas'

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

const caja1: DispositivoListado = {
  id: 11,
  nombre: 'Caja 1',
  puntoVenta: { numero: 100, nombre: 'PV Centro' },
  createdAt: '2026-03-02T13:15:00Z',
  ultimoUsoAt: '2026-08-20T21:40:00Z',
}
const caja2: DispositivoListado = {
  id: 12,
  nombre: 'Caja 2',
  puntoVenta: { numero: 101, nombre: 'PV Anexo' },
  createdAt: '2026-05-11T09:05:00Z',
  ultimoUsoAt: null,
}

function fecha(iso: string) {
  return fechaHoraATexto(iso)
}

function montar(items: DispositivoListado[] = [caja1, caja2]) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/dispositivos') return Promise.resolve(items)

    return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
  })

  return render(<EquiposPos />)
}

function botonRevocarDe(nombre: string) {
  return within(screen.getByRole('row', { name: new RegExp(nombre) })).getByRole('button', { name: 'Revocar' })
}

function celdas(nombre: string) {
  return within(screen.getByRole('row', { name: new RegExp(nombre) }))
    .getAllByRole('cell')
    .map((c) => c.textContent)
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiDeleteMock.mockReset()
  apiDeleteMock.mockResolvedValue(undefined)
})

describe('EquiposPos — listado', () => {
  it('rinde nombre, punto de venta, fecha de vinculación y último uso de cada equipo', async () => {
    montar()
    await waitFor(() => expect(screen.getByText('Caja 1')).toBeInTheDocument())

    expect(apiGetMock).toHaveBeenCalledWith('/dispositivos')
    const encabezados = within(screen.getAllByRole('row')[0]).getAllByRole('columnheader')
    expect(encabezados.map((c) => c.textContent?.replace('↕', ''))).toEqual([
      'Nombre',
      'Punto de venta',
      'Vinculado el',
      'Último uso',
      'Acciones',
    ])
    expect(celdas('Caja 1')).toEqual(['Caja 1', 'PV Centro', fecha(caja1.createdAt), fecha('2026-08-20T21:40:00Z'), 'Revocar'])
    expect(celdas('Caja 2')).toEqual(['Caja 2', 'PV Anexo', fecha(caja2.createdAt), 'Nunca', 'Revocar'])
  })

  it('sin equipos rinde el aviso de listado vacío', async () => {
    montar([])

    await waitFor(() => expect(screen.getByText('No hay equipos vinculados.')).toBeInTheDocument())
  })

  it('un fallo de carga rinde el mensaje del servidor', async () => {
    apiGetMock.mockRejectedValue(new ErrorApi(500, 'error_interno', 'Se cayó el listado.'))
    render(<EquiposPos />)

    await waitFor(() => expect(screen.getByText('Se cayó el listado.')).toBeInTheDocument())
  })
})

describe('EquiposPos — revocación', () => {
  it('no llama a la API hasta confirmar, y la puerta advierte qué bajas sigue bloqueando', async () => {
    const usuario = userEvent.setup()
    montar()
    await waitFor(() => expect(screen.getByText('Caja 1')).toBeInTheDocument())

    await usuario.click(botonRevocarDe('Caja 1'))

    expect(apiDeleteMock).not.toHaveBeenCalled()
    const puerta = screen.getByRole('alertdialog', { name: 'Confirmar revocación' })
    expect(puerta).toHaveTextContent('¿Revocar el equipo "Caja 1"?')
    expect(puerta).toHaveTextContent(NOTA_DE_REVOCACION)
    expect(NOTA_DE_REVOCACION).toMatch(/su punto de venta, el tenant y el usuario que lo vinculó no se van a poder dar de baja/)
    expect(NOTA_DE_REVOCACION).toMatch(/ventas sin conexión que todavía no sincronizó, se pierden/)
  })

  it('confirmar revoca ese equipo, cierra la puerta y refresca el listado', async () => {
    const usuario = userEvent.setup()
    let cargas = 0
    apiGetMock.mockImplementation(() => {
      cargas += 1

      return Promise.resolve(cargas === 1 ? [caja1, caja2] : [caja2])
    })
    render(<EquiposPos />)
    await waitFor(() => expect(screen.getByText('Caja 1')).toBeInTheDocument())

    await usuario.click(botonRevocarDe('Caja 1'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar revocación' }))

    await waitFor(() => expect(screen.getByText('Se revocó el equipo "Caja 1".')).toBeInTheDocument())
    expect(apiDeleteMock).toHaveBeenCalledWith('/dispositivos/11')
    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    await waitFor(() => expect(screen.queryByText('Caja 1')).not.toBeInTheDocument())
    expect(screen.getByText('Caja 2')).toBeInTheDocument()
  })

  it('cancelar cierra la puerta sin llamar a la API', async () => {
    const usuario = userEvent.setup()
    montar()
    await waitFor(() => expect(screen.getByText('Caja 1')).toBeInTheDocument())

    await usuario.click(botonRevocarDe('Caja 1'))
    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(apiDeleteMock).not.toHaveBeenCalled()
    expect(botonRevocarDe('Caja 2')).toBeEnabled()
  })

  /** Cláusula bajo prueba: `revocacion !== null` dentro de `bloqueado`. */
  it('con la puerta abierta ningún otro Revocar queda alcanzable', async () => {
    const usuario = userEvent.setup()
    montar()
    await waitFor(() => expect(screen.getByText('Caja 1')).toBeInTheDocument())

    await usuario.click(botonRevocarDe('Caja 1'))

    for (const boton of screen.getAllByRole('button', { name: 'Revocar' })) {
      expect(boton).toBeDisabled()
    }
    expect(screen.getByRole('button', { name: 'Confirmar revocación' })).toBeEnabled()
  })

  /** Cláusula bajo prueba: la ventana inerte cubre el DELETE y también su refresco. */
  it('durante el DELETE y su refresco no queda ninguna acción alcanzable', async () => {
    const usuario = userEvent.setup()
    let resolverDelete!: () => void
    let resolverRefresco!: (items: DispositivoListado[]) => void
    let cargas = 0
    apiGetMock.mockImplementation(() => {
      cargas += 1
      if (cargas === 1) return Promise.resolve([caja1, caja2])

      return new Promise<DispositivoListado[]>((resolver) => {
        resolverRefresco = resolver
      })
    })
    apiDeleteMock.mockImplementation(
      () =>
        new Promise<void>((resolver) => {
          resolverDelete = resolver
        }),
    )
    render(<EquiposPos />)
    await waitFor(() => expect(screen.getByText('Caja 1')).toBeInTheDocument())

    await usuario.click(botonRevocarDe('Caja 1'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar revocación' }))

    expect(screen.getByRole('button', { name: 'Revocando…' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    for (const boton of screen.getAllByRole('button', { name: 'Revocar' })) {
      expect(boton).toBeDisabled()
    }

    await act(async () => {
      resolverDelete()
      await Promise.resolve()
    })
    await waitFor(() => expect(screen.getByText('Se revocó el equipo "Caja 1".')).toBeInTheDocument())
    expect(screen.getByText('Cargando…')).toBeInTheDocument()

    await act(async () => {
      resolverRefresco([caja2])
      await Promise.resolve()
    })
    await waitFor(() => expect(botonRevocarDe('Caja 2')).toBeEnabled())
    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
  })

  /** Cláusula bajo prueba: `ocupadoRef`, la guarda de re-entrancia del mismo tick. */
  it('un segundo click sobre la confirmación en vuelo se descarta', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockImplementation(() => new Promise<void>(() => {}))
    montar()
    await waitFor(() => expect(screen.getByText('Caja 1')).toBeInTheDocument())

    await usuario.click(botonRevocarDe('Caja 1'))
    const confirmar = screen.getByRole('button', { name: 'Confirmar revocación' })
    await act(async () => {
      confirmar.click()
      confirmar.click()
      await Promise.resolve()
    })

    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
  })

  /** Cláusula bajo prueba: el refresco aislado del try/catch del DELETE. */
  it('un refresco fallido después de revocar no reporta la revocación como fallida', async () => {
    const usuario = userEvent.setup()
    let cargas = 0
    apiGetMock.mockImplementation(() => {
      cargas += 1
      if (cargas === 1) return Promise.resolve([caja1])

      return Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó.'))
    })
    render(<EquiposPos />)
    await waitFor(() => expect(screen.getByText('Caja 1')).toBeInTheDocument())

    await usuario.click(botonRevocarDe('Caja 1'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar revocación' }))

    await waitFor(() =>
      expect(
        screen.getByText(
          'Se revocó el equipo "Caja 1". Se revocó, pero no se pudo actualizar la vista. Recargá la pantalla.',
        ),
      ).toBeInTheDocument(),
    )
    expect(screen.queryByText(/No se pudo revocar/)).not.toBeInTheDocument()
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
  })

  it('un 404 rinde la copia neutra con la puerta abierta, y cancelar se lleva el motivo', async () => {
    const usuario = userEvent.setup()
    apiDeleteMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'No existe el dispositivo 11.'))
    montar()
    await waitFor(() => expect(screen.getByText('Caja 1')).toBeInTheDocument())

    await usuario.click(botonRevocarDe('Caja 1'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar revocación' }))

    const copia = 'No se pudo revocar el equipo. Ya no existe o no está a tu alcance. Actualizá el listado.'
    await waitFor(() => expect(screen.getByText(copia)).toBeInTheDocument())
    expect(screen.getByRole('alertdialog', { name: 'Confirmar revocación' })).toBeInTheDocument()
    expect(screen.queryByText(/dispositivo 11/)).not.toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByText(copia)).not.toBeInTheDocument()
  })
})

describe('EquiposPos — orden y filtros por columna', () => {
  const zeta: DispositivoListado = {
    id: 21,
    nombre: 'Zeta',
    puntoVenta: { numero: 1, nombre: 'PV Norte' },
    createdAt: '2026-10-11T12:00:00Z',
    ultimoUsoAt: '2026-12-01T12:00:00Z',
  }
  const alfa: DispositivoListado = {
    id: 22,
    nombre: 'Alfa',
    puntoVenta: { numero: 2, nombre: 'PV Sur' },
    createdAt: '2026-12-01T12:00:00Z',
    ultimoUsoAt: null,
  }
  const medio: DispositivoListado = {
    id: 23,
    nombre: 'Medio',
    puntoVenta: { numero: 3, nombre: 'PV Centro' },
    createdAt: '2026-05-02T12:00:00Z',
    ultimoUsoAt: '2026-10-11T12:00:00Z',
  }

  /** Las dos primeras filas son el encabezado y los filtros; la celda 0 de cada fila de datos es el nombre. */
  function nombresEnPantalla() {
    return screen
      .getAllByRole('row')
      .slice(2)
      .map((f) => within(f).getAllByRole('cell')[0]?.textContent)
  }

  async function montarConEquipos() {
    montar([zeta, alfa, medio])
    await waitFor(() => expect(screen.getByText('Zeta')).toBeInTheDocument())
  }

  it('la fila de filtros tiene una celda por columna del encabezado y limpiar queda bajo Acciones', async () => {
    await montarConEquipos()

    const [encabezados, filtros] = screen.getAllByRole('row')
    const celdasDeEncabezado = within(encabezados).getAllByRole('columnheader')
    const celdasDeFiltros = within(filtros).getAllByRole('columnheader')

    expect(celdasDeFiltros).toHaveLength(celdasDeEncabezado.length)
    expect(celdasDeEncabezado.at(-1)).toHaveTextContent('Acciones')
    expect(within(celdasDeFiltros.at(-1)!).getByRole('button', { name: 'Limpiar filtros' })).toBeInTheDocument()
  })

  it('cada columna de datos se puede ordenar y Acciones no', async () => {
    await montarConEquipos()

    for (const nombre of ['Nombre', 'Punto de venta', 'Vinculado el', 'Último uso']) {
      expect(screen.getByRole('button', { name: nombre })).toBeInTheDocument()
    }
    expect(screen.getByRole('columnheader', { name: 'Acciones' })).not.toHaveAttribute('aria-sort')
  })

  it('el nombre se ordena ascendente, descendente y vuelve al orden del servidor', async () => {
    const usuario = userEvent.setup()
    await montarConEquipos()
    const boton = screen.getByRole('button', { name: 'Nombre' })
    const encabezado = screen.getByRole('columnheader', { name: 'Nombre' })
    expect(nombresEnPantalla()).toEqual(['Zeta', 'Alfa', 'Medio'])

    await usuario.click(boton)
    expect(nombresEnPantalla()).toEqual(['Alfa', 'Medio', 'Zeta'])
    expect(encabezado).toHaveAttribute('aria-sort', 'ascending')

    await usuario.click(boton)
    expect(nombresEnPantalla()).toEqual(['Zeta', 'Medio', 'Alfa'])
    expect(encabezado).toHaveAttribute('aria-sort', 'descending')

    await usuario.click(boton)
    expect(nombresEnPantalla()).toEqual(['Zeta', 'Alfa', 'Medio'])
    expect(encabezado).toHaveAttribute('aria-sort', 'none')
  })

  it('el punto de venta se ordena por su nombre', async () => {
    const usuario = userEvent.setup()
    await montarConEquipos()

    await usuario.click(screen.getByRole('button', { name: 'Punto de venta' }))

    expect(nombresEnPantalla()).toEqual(['Medio', 'Zeta', 'Alfa'])
  })

  it('la fecha de vinculación se ordena cronológicamente (2/5 antes que 11/10 y que 1/12)', async () => {
    const usuario = userEvent.setup()
    await montarConEquipos()
    const boton = screen.getByRole('button', { name: 'Vinculado el' })

    await usuario.click(boton)
    expect(nombresEnPantalla()).toEqual(['Medio', 'Zeta', 'Alfa'])

    await usuario.click(boton)
    expect(nombresEnPantalla()).toEqual(['Alfa', 'Zeta', 'Medio'])
  })

  it('el último uso se ordena por fecha y los que nunca se usaron van al final en ambas direcciones', async () => {
    const usuario = userEvent.setup()
    await montarConEquipos()
    const boton = screen.getByRole('button', { name: 'Último uso' })

    await usuario.click(boton)
    expect(nombresEnPantalla()).toEqual(['Medio', 'Zeta', 'Alfa'])

    await usuario.click(boton)
    expect(nombresEnPantalla()).toEqual(['Zeta', 'Medio', 'Alfa'])
  })

  it('cada filtro acota las filas y se combinan', async () => {
    const usuario = userEvent.setup()
    await montarConEquipos()

    await usuario.type(screen.getByRole('searchbox', { name: 'Filtrar por nombre' }), 'ALF')
    expect(nombresEnPantalla()).toEqual(['Alfa'])

    await usuario.click(screen.getByRole('button', { name: 'Limpiar filtros' }))
    await usuario.type(screen.getByRole('searchbox', { name: 'Filtrar por punto de venta' }), 'centro')
    expect(nombresEnPantalla()).toEqual(['Medio'])

    await usuario.click(screen.getByRole('button', { name: 'Limpiar filtros' }))
    await usuario.type(screen.getByRole('searchbox', { name: 'Filtrar por último uso' }), 'nunca')
    expect(nombresEnPantalla()).toEqual(['Alfa'])

    await usuario.click(screen.getByRole('button', { name: 'Limpiar filtros' }))
    await usuario.type(screen.getByRole('searchbox', { name: 'Filtrar por vinculado el' }), fecha(alfa.createdAt).split(',')[0])
    expect(nombresEnPantalla()).toEqual(['Alfa'])

    await usuario.type(screen.getByRole('searchbox', { name: 'Filtrar por nombre' }), 'zeta')
    expect(screen.getByText('Ningún registro coincide con los filtros.')).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Limpiar filtros' }))
    expect(nombresEnPantalla()).toEqual(['Zeta', 'Alfa', 'Medio'])
    expect(screen.getByRole('searchbox', { name: 'Filtrar por nombre' })).toHaveValue('')
  })

  it('el campo de filtro sigue montado y con su valor mientras el listado se recarga tras revocar', async () => {
    const usuario = userEvent.setup()
    let resolverRefresco!: (items: DispositivoListado[]) => void
    let cargas = 0
    apiGetMock.mockImplementation(() =>
      ++cargas === 1
        ? Promise.resolve([zeta, alfa, medio])
        : new Promise<DispositivoListado[]>((resolver) => {
            resolverRefresco = resolver
          }),
    )
    render(<EquiposPos />)
    await waitFor(() => expect(screen.getByText('Zeta')).toBeInTheDocument())
    await usuario.type(screen.getByRole('searchbox', { name: 'Filtrar por nombre' }), 'a')
    const campo = screen.getByRole('searchbox', { name: 'Filtrar por nombre' })

    await usuario.click(botonRevocarDe('Alfa'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar revocación' }))

    expect(await screen.findByText('Se revocó el equipo "Alfa".')).toBeInTheDocument()
    expect(screen.getByText('Cargando…')).toBeInTheDocument()
    expect(screen.getByRole('searchbox', { name: 'Filtrar por nombre' })).toBe(campo)
    expect(campo).toHaveValue('a')

    await act(async () => {
      resolverRefresco([zeta, medio])
      await Promise.resolve()
    })

    await waitFor(() => expect(screen.queryByText('Cargando…')).not.toBeInTheDocument())
    expect(screen.getByRole('searchbox', { name: 'Filtrar por nombre' })).toBe(campo)
    expect(campo).toHaveValue('a')
    expect(nombresEnPantalla()).toEqual(['Zeta'])
  })

  it('con la puerta de revocación abierta los filtros quedan inertes', async () => {
    const usuario = userEvent.setup()
    await montarConEquipos()

    await usuario.click(botonRevocarDe('Zeta'))

    expect(screen.getByRole('searchbox', { name: 'Filtrar por nombre' })).toBeDisabled()
    expect(screen.getByRole('searchbox', { name: 'Filtrar por último uso' })).toBeDisabled()
  })
})
