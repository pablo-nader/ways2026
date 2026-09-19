import { act, render, screen, within } from '@testing-library/react'
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
  return within(screen.getByText(nombre).closest('li') as HTMLElement).getByRole('button', { name: 'Baja' })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiPutMock.mockReset()
  apiDeleteMock.mockReset()
  apiDeleteMock.mockResolvedValue(undefined)
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
      ...screen.getAllByRole('button', { name: 'Baja' }),
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
    expect(screen.getByText('Editando categoría 1')).toBeInTheDocument()

    await usuario.click(bajaDe('Bebidas'))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    await screen.findByText('Se dio de baja "Bebidas".')
    expect(screen.queryByText('Editando categoría 1')).not.toBeInTheDocument()
  })
})
