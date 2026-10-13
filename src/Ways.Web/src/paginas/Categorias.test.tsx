import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Categorias } from './Categorias'
import { ErrorApi } from '../api/cliente'
import type { CategoriaListado } from '../api/tipos'

// fix/web-bajas-catalogos: la baja de una categoría reusa `ConfirmacionDeBaja` +
// `copiaDeFalloDeBaja`, mismo patrón que `Empresas.test.tsx`/`PaginaCatalogo.test.tsx`
// (`react-async-state` regla 10).

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

function categoriaFixture(sobrescribir: Partial<CategoriaListado> = {}): CategoriaListado {
  return {
    id: 1,
    nombre: 'Bebidas',
    activo: true,
    idEmpresa: null,
    orden: 1,
    idCategoriaPadre: null,
    ...sobrescribir,
  }
}

function bajaDe(nombre: string) {
  return within(screen.getByText(nombre).closest('li') as HTMLElement).getByRole('button', { name: 'Eliminar' })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiPutMock.mockReset()
  apiDeleteMock.mockReset()
  apiDeleteMock.mockResolvedValue(undefined)
})

function editarDe(nombre: string) {
  return within(screen.getByText(nombre).closest('li') as HTMLElement).getByRole('button', { name: 'Editar' })
}

describe('Categorias — alta y edición en una modal', () => {
  it('la primera pantalla es solo la grilla: ni formulario ni diálogo', async () => {
    apiGetMock.mockResolvedValue([categoriaFixture()])
    render(<Categorias />)

    await screen.findByText('Bebidas')

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(document.querySelector('form')).toBeNull()
  })

  it('"Nueva categoría raíz" abre la modal con el título de alta y el formulario vacío', async () => {
    apiGetMock.mockResolvedValue([categoriaFixture()])
    render(<Categorias />)

    await screen.findByText('Bebidas')
    await userEvent.click(screen.getByRole('button', { name: 'Nueva categoría raíz' }))

    const dialogo = screen.getByRole('dialog', { name: 'Nueva categoría' })
    expect(within(dialogo).getByLabelText('Nombre')).toHaveValue('')
    expect(within(dialogo).getByLabelText('Categoría padre')).toHaveValue('')
  })

  it('"+ Subcategoría" abre la modal con el padre elegido', async () => {
    apiGetMock.mockResolvedValue([categoriaFixture()])
    render(<Categorias />)

    await screen.findByText('Bebidas')
    await userEvent.click(screen.getByRole('button', { name: '+ Subcategoría' }))

    expect(within(screen.getByRole('dialog', { name: 'Nueva categoría' })).getByLabelText('Categoría padre')).toHaveValue('1')
  })

  it('editar abre la modal con el título de edición y la fila cargada', async () => {
    apiGetMock.mockResolvedValue([categoriaFixture({ activo: false, orden: 7 })])
    render(<Categorias />)

    await screen.findByText('Bebidas')
    await userEvent.click(editarDe('Bebidas'))

    const dialogo = screen.getByRole('dialog', { name: 'Editar categoría' })
    expect(within(dialogo).getByLabelText('Nombre')).toHaveValue('Bebidas')
    expect(within(dialogo).getByLabelText('Orden')).toHaveValue(7)
    expect(within(dialogo).getByLabelText('Estado')).toHaveValue('inactivo')
  })

  it('cancelar cierra la modal sin escribir nada', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue([categoriaFixture()])
    render(<Categorias />)

    await screen.findByText('Bebidas')
    await usuario.click(screen.getByRole('button', { name: 'Nueva categoría raíz' }))
    await usuario.type(screen.getByLabelText('Nombre'), 'Limpieza')
    await usuario.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
    expect(apiPutMock).not.toHaveBeenCalled()
  })

  it('un guardado exitoso cierra la modal, deja el aviso en la pantalla y recarga el listado', async () => {
    const usuario = userEvent.setup()
    let cargas = 0
    apiGetMock.mockImplementation(() => {
      cargas += 1
      return Promise.resolve(
        cargas === 1 ? [categoriaFixture()] : [categoriaFixture(), categoriaFixture({ id: 2, nombre: 'Limpieza' })],
      )
    })
    apiPostMock.mockResolvedValue(undefined)
    render(<Categorias />)

    await screen.findByText('Bebidas')
    await usuario.click(screen.getByRole('button', { name: 'Nueva categoría raíz' }))
    await usuario.type(screen.getByLabelText('Nombre'), 'Limpieza')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await screen.findByText('Se creó "Limpieza".')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(apiPostMock).toHaveBeenCalledTimes(1)
    expect(apiPostMock.mock.calls[0][0]).toBe('/catalogos/categorias')
    expect(await screen.findByText('Limpieza')).toBeInTheDocument()
  })

  /** `user-event` solo busca el botón de envío dentro del `<form>`, así que no puede simular Enter
   * sobre un botón del pie asociado por el atributo `form`: se prueba la asociación misma, que es
   * lo que hace que el navegador envíe con Enter. */
  it('el Guardar del pie pertenece al formulario del cuerpo', async () => {
    apiGetMock.mockResolvedValue([categoriaFixture()])
    render(<Categorias />)

    await screen.findByText('Bebidas')
    await userEvent.click(screen.getByRole('button', { name: 'Nueva categoría raíz' }))

    const dialogo = screen.getByRole('dialog', { name: 'Nueva categoría' })
    expect(within(dialogo).getByRole('button', { name: 'Guardar' })).toHaveProperty('form', dialogo.querySelector('form'))
  })

  it('un guardado rechazado muestra el error adentro de la modal, la deja abierta y conserva lo tipeado', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue([categoriaFixture()])
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'nombre_duplicado', 'Ya existe una categoría con ese nombre.'))
    render(<Categorias />)

    await screen.findByText('Bebidas')
    await usuario.click(screen.getByRole('button', { name: 'Nueva categoría raíz' }))
    await usuario.type(screen.getByLabelText('Nombre'), 'Bebidas')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    const dialogo = screen.getByRole('dialog', { name: 'Nueva categoría' })
    expect(await within(dialogo).findByText('Ya existe una categoría con ese nombre.')).toBeInTheDocument()
    expect(within(dialogo).getByLabelText('Nombre')).toHaveValue('Bebidas')
    expect(within(dialogo).getByRole('button', { name: 'Guardar' })).toBeEnabled()
  })

  it('el error de un guardado anterior no sobrevive a cancelar y reabrir', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue([categoriaFixture()])
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'nombre_duplicado', 'Ya existe una categoría con ese nombre.'))
    render(<Categorias />)

    await screen.findByText('Bebidas')
    await usuario.click(screen.getByRole('button', { name: 'Nueva categoría raíz' }))
    await usuario.type(screen.getByLabelText('Nombre'), 'Bebidas')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))
    await screen.findByText('Ya existe una categoría con ese nombre.')

    await usuario.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancelar' }))
    await usuario.click(screen.getByRole('button', { name: 'Nueva categoría raíz' }))

    expect(screen.queryByText('Ya existe una categoría con ese nombre.')).not.toBeInTheDocument()
  })

  it('un guardado ya hecho cuyo refresco falla cierra la modal y avisa en la pantalla', async () => {
    const usuario = userEvent.setup()
    let cargas = 0
    apiGetMock.mockImplementation(() => {
      cargas += 1
      return cargas === 1
        ? Promise.resolve([categoriaFixture()])
        : Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó.'))
    })
    apiPostMock.mockResolvedValue(undefined)
    render(<Categorias />)

    await screen.findByText('Bebidas')
    await usuario.click(screen.getByRole('button', { name: 'Nueva categoría raíz' }))
    await usuario.type(screen.getByLabelText('Nombre'), 'Limpieza')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await screen.findByText('Se creó "Limpieza". Se guardó, pero no se pudo actualizar la vista. Recargá la pantalla.')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('con el guardado en vuelo la modal no se cierra ni con la × ni con Escape, y un segundo envío se descarta', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue([categoriaFixture()])
    apiPostMock.mockImplementation(() => new Promise<void>(() => {}))
    render(<Categorias />)

    await screen.findByText('Bebidas')
    await usuario.click(screen.getByRole('button', { name: 'Nueva categoría raíz' }))
    await usuario.type(screen.getByLabelText('Nombre'), 'Limpieza')
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
    expect(screen.getByRole('dialog', { name: 'Nueva categoría' })).toBeInTheDocument()
  })
})


describe('Categorias — el warning de subcategorías huérfanas se retiró', () => {
  it('la puerta de confirmación no menciona que las subcategorías quedan sin padre visible', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue([categoriaFixture()])
    render(<Categorias />)

    await screen.findByText('Bebidas')
    await usuario.click(bajaDe('Bebidas'))

    const puerta = screen.getByRole('alertdialog', { name: 'Confirmar baja' })
    expect(puerta).toHaveTextContent('¿Dar de baja la categoría "Bebidas"?')
    expect(puerta).not.toHaveTextContent(/subcategorías/)
    expect(screen.queryByText(/quedan sin este padre visible/)).not.toBeInTheDocument()
  })
})

describe('Categorias — baja lógica', () => {
  it('el botón de baja no llama a la API hasta que se confirma', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue([categoriaFixture()])
    render(<Categorias />)

    await screen.findByText('Bebidas')
    await usuario.click(bajaDe('Bebidas'))
    expect(apiDeleteMock).not.toHaveBeenCalled()

    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))
    await screen.findByText('Se dio de baja "Bebidas".')
    expect(apiDeleteMock).toHaveBeenCalledWith('/catalogos/categorias/1')
  })

  it('cancelar cierra la puerta y no llama nunca a la API', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue([categoriaFixture()])
    render(<Categorias />)

    await screen.findByText('Bebidas')
    await usuario.click(bajaDe('Bebidas'))
    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(apiDeleteMock).not.toHaveBeenCalled()
  })

  /** Cláusula bajo prueba: `ocupadoRef`, la guarda de re-entrancia del mismo tick. */
  it('un segundo click sobre la confirmación en vuelo se descarta', async () => {
    apiGetMock.mockResolvedValue([categoriaFixture()])
    apiDeleteMock.mockImplementation(() => new Promise<void>(() => {}))
    render(<Categorias />)

    await screen.findByText('Bebidas')
    await userEvent.setup().click(bajaDe('Bebidas'))
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
    let resolverRefresco!: (items: CategoriaListado[]) => void

    let cargas = 0
    apiGetMock.mockImplementation(() => {
      cargas += 1
      if (cargas === 1) return Promise.resolve([categoriaFixture(), categoriaFixture({ id: 2, nombre: 'Limpieza' })])

      return new Promise<CategoriaListado[]>((resolver) => {
        resolverRefresco = resolver
      })
    })
    apiDeleteMock.mockImplementation(
      () =>
        new Promise<void>((resolver) => {
          resolverDelete = resolver
        }),
    )

    render(<Categorias />)
    await screen.findByText('Bebidas')

    await usuario.click(bajaDe('Bebidas'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    expect(screen.getByRole('button', { name: 'Dando de baja…' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Nueva categoría raíz' })).toBeDisabled()
    expect(screen.getByLabelText('Incluir inactivas')).toBeDisabled()
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

    await screen.findByText('Se dio de baja "Bebidas".')
    expect(screen.getByText('Cargando…')).toBeInTheDocument()

    await act(async () => {
      resolverRefresco([categoriaFixture({ id: 2, nombre: 'Limpieza' })])
      await Promise.resolve()
    })

    await screen.findByText('Limpieza')
    expect(screen.queryByText('Bebidas')).not.toBeInTheDocument()
    expect(apiDeleteMock).toHaveBeenCalledTimes(1)
  })

  /** Cláusula bajo prueba: `AVISO_REFRESCO_FALLIDO_BAJA` — ver `Empresas.test.tsx`. Un DELETE que
   * ya commiteó nunca se reporta como fallido, aunque el refresco posterior explote. */
  it('un refresco fallido después de la baja no la reporta como fallida', async () => {
    const usuario = userEvent.setup()
    let cargas = 0
    apiGetMock.mockImplementation(() => {
      cargas += 1
      if (cargas === 1) return Promise.resolve([categoriaFixture()])

      return Promise.reject(new ErrorApi(500, 'error_interno', 'Se cayó.'))
    })
    render(<Categorias />)
    await screen.findByText('Bebidas')

    await usuario.click(bajaDe('Bebidas'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await screen.findByText(
      'Se dio de baja "Bebidas". Se eliminó, pero no se pudo actualizar la vista. Recargá la pantalla.',
    )
  })

  /** Cláusula bajo prueba: la elección de copia por `codigo` — `categoria_en_uso` es la que el
   * backend rinde cuando la categoría tiene subcategorías. */
  it('un 409 categoria_en_uso rinde el mensaje del servidor y la guía propia, y deja la puerta abierta', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue([categoriaFixture()])
    apiDeleteMock.mockRejectedValue(
      new ErrorApi(409, 'categoria_en_uso', 'No se puede dar de baja la categoría porque tiene subcategorías.'),
    )
    render(<Categorias />)

    await screen.findByText('Bebidas')
    await usuario.click(bajaDe('Bebidas'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await screen.findByText(/porque tiene subcategorías/)
    expect(screen.getByText(/Reasigná esos datos o desactivá la categoría/)).toBeInTheDocument()
    expect(screen.getByRole('alertdialog', { name: 'Confirmar baja' })).toBeInTheDocument()
  })

  /** Cláusula bajo prueba: el `setError('')` de `cancelarBaja` — ver `Empresas.test.tsx`. */
  it('cancelar después de un rechazo se lleva el motivo con la puerta', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue([categoriaFixture()])
    apiDeleteMock.mockRejectedValue(
      new ErrorApi(409, 'categoria_en_uso', 'No se puede dar de baja la categoría porque tiene subcategorías.'),
    )
    render(<Categorias />)

    await screen.findByText('Bebidas')
    await usuario.click(bajaDe('Bebidas'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))
    await screen.findByText(/porque tiene subcategorías/)

    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.queryByText(/porque tiene subcategorías/)).not.toBeInTheDocument()
  })

  it('la baja de la categoría que se está editando se lleva también su formulario', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue([categoriaFixture()])
    render(<Categorias />)

    await screen.findByText('Bebidas')
    await usuario.click(within(screen.getByText('Bebidas').closest('li') as HTMLElement).getByRole('button', { name: 'Editar' }))
    expect(screen.getByRole('dialog', { name: 'Editar categoría' })).toBeInTheDocument()

    await usuario.click(bajaDe('Bebidas'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await screen.findByText('Se dio de baja "Bebidas".')
    expect(screen.queryByRole('dialog', { name: 'Editar categoría' })).not.toBeInTheDocument()
  })
})

describe('Categorias — error y foco de la modal', () => {
  it('un guardado rechazado expone el error como alerta adentro del diálogo', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue([categoriaFixture()])
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'nombre_duplicado', 'Ya existe una categoría con ese nombre.'))
    render(<Categorias />)

    await screen.findByText('Bebidas')
    await usuario.click(screen.getByRole('button', { name: 'Nueva categoría raíz' }))
    await usuario.type(screen.getByLabelText('Nombre'), 'Bebidas')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    const alerta = await within(screen.getByRole('dialog')).findByRole('alert')
    expect(alerta).toHaveTextContent('Ya existe una categoría con ese nombre.')
  })

  it('al abrir, el foco queda en el primer campo y no en la ×', async () => {
    apiGetMock.mockResolvedValue([categoriaFixture()])
    render(<Categorias />)

    await screen.findByText('Bebidas')
    await userEvent.click(screen.getByRole('button', { name: 'Nueva categoría raíz' }))

    await waitFor(() => expect(within(screen.getByRole('dialog')).getByLabelText('Nombre')).toHaveFocus())
  })

  it('con el refresco en vuelo tras guardar, el foco queda en el contenedor de la pantalla y no en <body>', async () => {
    const usuario = userEvent.setup()
    let liberarRefresco: (filas: CategoriaListado[]) => void = () => {}
    let cargas = 0
    apiGetMock.mockImplementation(() => {
      cargas += 1
      return cargas === 1
        ? Promise.resolve([categoriaFixture()])
        : new Promise<CategoriaListado[]>((resolver) => {
            liberarRefresco = resolver
          })
    })
    apiPostMock.mockResolvedValue(undefined)
    const { container } = render(<Categorias />)

    await screen.findByText('Bebidas')
    await usuario.click(screen.getByRole('button', { name: 'Nueva categoría raíz' }))
    await usuario.type(screen.getByLabelText('Nombre'), 'Limpieza')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await screen.findByText('Se creó "Limpieza".')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Nueva categoría raíz' })).toBeDisabled()
    expect(document.activeElement).not.toBe(document.body)
    expect(document.activeElement).toBe(container.firstElementChild)

    await act(async () => liberarRefresco([categoriaFixture()]))
    expect(screen.getByRole('button', { name: 'Nueva categoría raíz' })).toBeEnabled()
    expect(document.activeElement).toBe(container.firstElementChild)
  })

  it('cancelar devuelve el foco al botón que abrió la modal', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue([categoriaFixture()])
    render(<Categorias />)

    await screen.findByText('Bebidas')
    const abrir = screen.getByRole('button', { name: 'Nueva categoría raíz' })
    await usuario.click(abrir)
    await usuario.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancelar' }))

    expect(abrir).toHaveFocus()
  })
})

describe('Categorias — empresa de la fila al guardar', () => {
  it('editar una categoría de una empresa manda la empresa de la fila en el PUT', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue([categoriaFixture({ idEmpresa: 5 })])
    apiPutMock.mockResolvedValue(undefined)
    render(<Categorias />)

    await screen.findByText('Bebidas')
    await usuario.click(editarDe('Bebidas'))
    await usuario.click(within(screen.getByRole('dialog', { name: 'Editar categoría' })).getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    expect(apiPutMock.mock.calls[0][0]).toBe('/catalogos/categorias/1')
    expect(apiPutMock.mock.calls[0][1]).toMatchObject({ idEmpresa: 5 })
  })

  it('editar una categoría de todo el tenant manda null', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue([categoriaFixture({ idEmpresa: null })])
    apiPutMock.mockResolvedValue(undefined)
    render(<Categorias />)

    await screen.findByText('Bebidas')
    await usuario.click(editarDe('Bebidas'))
    await usuario.click(within(screen.getByRole('dialog', { name: 'Editar categoría' })).getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPutMock).toHaveBeenCalledTimes(1))
    expect(apiPutMock.mock.calls[0][1]).toMatchObject({ idEmpresa: null })
  })

  it('un alta manda null aunque antes se haya abierto la edición de una categoría de una empresa', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue([categoriaFixture({ idEmpresa: 5 })])
    apiPostMock.mockResolvedValue(undefined)
    render(<Categorias />)

    await screen.findByText('Bebidas')
    await usuario.click(editarDe('Bebidas'))
    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))
    await usuario.click(screen.getByRole('button', { name: 'Nueva categoría raíz' }))
    await usuario.type(screen.getByLabelText('Nombre'), 'Limpieza')
    await usuario.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    expect(apiPostMock.mock.calls[0][1]).toMatchObject({ nombre: 'Limpieza', idEmpresa: null })
  })
})
