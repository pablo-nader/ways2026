import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { ArticuloListado } from '../../api/tipos'
import { ComboboxDeArticulos } from './ComboboxDeArticulos'

const listarMock = vi.fn()

vi.mock('../../api/articulos', () => ({
  clienteDeArticulos: { listar: (...args: unknown[]) => listarMock(...args) },
}))

function articulo(id: number, nombre: string, extra: Partial<ArticuloListado> = {}): ArticuloListado {
  return { id, codigoInterno: `ART-${id}`, nombre, codigoProveedor: null, ...extra } as ArticuloListado
}

function pagina(items: ArticuloListado[]) {
  return Promise.resolve({ items, total: items.length, pagina: 1, tamanio: 25 })
}

const LECHE = articulo(1, 'Leche entera')
const LECHE_DESC = articulo(2, 'Leche descremada', { codigoProveedor: 'LD-9' })

type Eventos = { elegidos: ArticuloListado[]; creaciones: number }

function Arnes({
  eventos,
  idProveedor = 1,
  conArticulo = false,
  soloLectura = false,
  disabled = false,
}: {
  eventos: Eventos
  idProveedor?: number | null
  conArticulo?: boolean
  soloLectura?: boolean
  disabled?: boolean
}) {
  const [texto, setTexto] = useState('')
  return (
    <div>
      <ComboboxDeArticulos
        texto={texto}
        conArticulo={conArticulo}
        soloLectura={soloLectura}
        disabled={disabled}
        idProveedor={idProveedor}
        onTexto={setTexto}
        onElegir={(a) => {
          eventos.elegidos.push(a)
          setTexto(a.nombre)
        }}
        onCrear={() => {
          eventos.creaciones += 1
        }}
      />
      <input aria-label="Siguiente celda" />
    </div>
  )
}

function montar(props: Partial<Parameters<typeof Arnes>[0]> = {}) {
  const eventos: Eventos = { elegidos: [], creaciones: 0 }
  render(<Arnes eventos={eventos} {...props} />)
  return eventos
}

beforeEach(() => {
  listarMock.mockReset()
})

describe('ComboboxDeArticulos — búsqueda', () => {
  it('busca desde el segundo carácter con el proveedor, y no antes', async () => {
    listarMock.mockImplementation(() => pagina([LECHE]))
    const usuario = userEvent.setup()
    montar()

    await usuario.type(screen.getByRole('combobox', { name: 'Detalle' }), 'l')
    await act(async () => {
      await new Promise((r) => setTimeout(r, 450))
    })
    expect(listarMock).not.toHaveBeenCalled()

    await usuario.type(screen.getByRole('combobox', { name: 'Detalle' }), 'e')
    await screen.findByRole('option', { name: /Leche entera/ })
    expect(listarMock).toHaveBeenCalledWith('le', false, 1)
  })

  it('espera a que se deje de tipear: un solo pedido por ráfaga', async () => {
    listarMock.mockImplementation(() => pagina([LECHE]))
    const usuario = userEvent.setup()
    montar()

    await usuario.type(screen.getByRole('combobox', { name: 'Detalle' }), 'leche')
    await screen.findByRole('option', { name: /Leche entera/ })
    expect(listarMock).toHaveBeenCalledTimes(1)
    expect(listarMock).toHaveBeenCalledWith('leche', false, 1)
  })

  it('sin proveedor la búsqueda no manda idProveedor', async () => {
    listarMock.mockImplementation(() => pagina([LECHE]))
    const usuario = userEvent.setup()
    montar({ idProveedor: null })

    await usuario.type(screen.getByRole('combobox', { name: 'Detalle' }), 'leche')
    await screen.findByRole('option', { name: /Leche entera/ })
    expect(listarMock).toHaveBeenCalledWith('leche', false, undefined)
  })

  it('una respuesta tardía de un término anterior no pisa la del término vigente', async () => {
    const resolvers: ((r: unknown) => void)[] = []
    listarMock.mockImplementation(() => new Promise((resolve) => resolvers.push(resolve)))
    const usuario = userEvent.setup()
    montar()

    await usuario.type(screen.getByRole('combobox', { name: 'Detalle' }), 'lec')
    await waitFor(() => expect(resolvers).toHaveLength(1))
    await usuario.type(screen.getByRole('combobox', { name: 'Detalle' }), 'he')
    await waitFor(() => expect(resolvers).toHaveLength(2))

    // Llega primero la del término vigente y recién después la vieja.
    await act(async () => {
      resolvers[1]({ items: [LECHE_DESC], total: 1, pagina: 1, tamanio: 25 })
    })
    await act(async () => {
      resolvers[0]({ items: [LECHE], total: 1, pagina: 1, tamanio: 25 })
    })

    expect(screen.getByRole('option', { name: /Leche descremada/ })).toBeInTheDocument()
    expect(screen.queryByRole('option', { name: /Leche entera/ })).not.toBeInTheDocument()
  })

  it('un fallo de la búsqueda no deja resultados ni ofrece nada', async () => {
    listarMock.mockRejectedValue(new Error('red'))
    const usuario = userEvent.setup()
    montar()

    await usuario.type(screen.getByRole('combobox', { name: 'Detalle' }), 'leche')
    await waitFor(() => expect(listarMock).toHaveBeenCalled())
    await waitFor(() => expect(screen.queryByText('Buscando…')).not.toBeInTheDocument())
    expect(screen.queryByRole('option')).not.toBeInTheDocument()
  })

  it('sin resultados avisa que se carga como concepto', async () => {
    listarMock.mockImplementation(() => pagina([]))
    const usuario = userEvent.setup()
    montar()

    await usuario.type(screen.getByRole('combobox', { name: 'Detalle' }), 'yerba rara')
    expect(await screen.findByText('Sin resultados: se carga como concepto.')).toBeInTheDocument()
  })

  it('si el foco ya salió del campo cuando llega la respuesta, no abre la lista', async () => {
    let resolver: (r: unknown) => void = () => {}
    listarMock.mockImplementation(() => new Promise((resolve) => (resolver = resolve)))
    const usuario = userEvent.setup()
    montar()

    await usuario.type(screen.getByRole('combobox', { name: 'Detalle' }), 'leche')
    await waitFor(() => expect(listarMock).toHaveBeenCalled())
    await usuario.tab()
    await act(async () => {
      resolver({ items: [LECHE], total: 1, pagina: 1, tamanio: 25 })
    })

    expect(screen.queryByRole('option')).not.toBeInTheDocument()
  })
})

describe('ComboboxDeArticulos — teclado y ARIA', () => {
  async function escribirYEsperar(usuario: ReturnType<typeof userEvent.setup>, items = [LECHE, LECHE_DESC]) {
    listarMock.mockImplementation(() => pagina(items))
    await usuario.type(screen.getByRole('combobox', { name: 'Detalle' }), 'leche')
    await screen.findByRole('listbox', { name: 'Artículos encontrados' })
    await screen.findAllByRole('option')
  }

  it('expone un combobox con listbox y opciones; la opción con código del proveedor lo muestra', async () => {
    const usuario = userEvent.setup()
    montar()
    const caja = screen.getByRole('combobox', { name: 'Detalle' })
    expect(caja).toHaveAttribute('aria-expanded', 'false')
    expect(caja).toHaveAttribute('aria-autocomplete', 'list')

    await escribirYEsperar(usuario)

    expect(caja).toHaveAttribute('aria-expanded', 'true')
    expect(caja).toHaveAttribute('aria-controls', screen.getByRole('listbox').id)
    expect(screen.getAllByRole('option').map((o) => o.textContent)).toEqual([
      'ART-1 — Leche entera',
      'ART-2 — Leche descremada (Cód. prov.: LD-9)',
    ])
  })

  it('ArrowDown y ArrowUp mueven la opción activa (con vuelta) y la anuncian con aria-activedescendant', async () => {
    const usuario = userEvent.setup()
    montar()
    const caja = screen.getByRole('combobox', { name: 'Detalle' })
    await escribirYEsperar(usuario)
    const [primera, segunda] = screen.getAllByRole('option')
    expect(caja).not.toHaveAttribute('aria-activedescendant')

    await usuario.keyboard('{ArrowDown}')
    expect(primera).toHaveAttribute('aria-selected', 'true')
    expect(caja).toHaveAttribute('aria-activedescendant', primera.id)

    await usuario.keyboard('{ArrowDown}')
    expect(segunda).toHaveAttribute('aria-selected', 'true')
    expect(primera).toHaveAttribute('aria-selected', 'false')

    await usuario.keyboard('{ArrowDown}')
    expect(primera).toHaveAttribute('aria-selected', 'true')

    await usuario.keyboard('{ArrowUp}')
    expect(segunda).toHaveAttribute('aria-selected', 'true')
  })

  it('Enter sobre la opción activa la elige y cierra la lista', async () => {
    const usuario = userEvent.setup()
    const eventos = montar()
    await escribirYEsperar(usuario)

    await usuario.keyboard('{ArrowDown}{ArrowDown}{Enter}')

    expect(eventos.elegidos.map((a) => a.id)).toEqual([2])
    expect(screen.queryByRole('option')).not.toBeInTheDocument()
    expect(screen.getByRole('combobox', { name: 'Detalle' })).toHaveValue('Leche descremada')
    // elegir no dispara otra búsqueda con el nombre del artículo
    await act(async () => {
      await new Promise((r) => setTimeout(r, 450))
    })
    expect(listarMock).toHaveBeenCalledTimes(1)
  })

  it('Tab sobre la opción activa la elige y deja que el foco siga a la celda siguiente', async () => {
    const usuario = userEvent.setup()
    const eventos = montar()
    await escribirYEsperar(usuario)

    await usuario.keyboard('{ArrowDown}{Tab}')

    expect(eventos.elegidos.map((a) => a.id)).toEqual([1])
    expect(screen.getByLabelText('Siguiente celda')).toHaveFocus()
  })

  it('Tab sin opción activa no elige nada y sigue de largo', async () => {
    const usuario = userEvent.setup()
    const eventos = montar()
    await escribirYEsperar(usuario)

    await usuario.keyboard('{Tab}')

    expect(eventos.elegidos).toEqual([])
    expect(screen.getByLabelText('Siguiente celda')).toHaveFocus()
    expect(screen.getByRole('combobox', { name: 'Detalle' })).toHaveValue('leche')
  })

  it('Enter con la lista abierta pero sin opción activa solo la cierra', async () => {
    const usuario = userEvent.setup()
    const eventos = montar()
    await escribirYEsperar(usuario)

    await usuario.keyboard('{Enter}')

    expect(eventos.elegidos).toEqual([])
    expect(screen.queryByRole('option')).not.toBeInTheDocument()
    expect(screen.getByRole('combobox', { name: 'Detalle' })).toHaveFocus()
  })

  it('Escape cierra la lista y conserva el texto', async () => {
    const usuario = userEvent.setup()
    const eventos = montar()
    await escribirYEsperar(usuario)

    await usuario.keyboard('{Escape}')

    expect(screen.queryByRole('option')).not.toBeInTheDocument()
    expect(screen.getByRole('combobox', { name: 'Detalle' })).toHaveValue('leche')
    expect(eventos.elegidos).toEqual([])
    expect(screen.getByRole('combobox', { name: 'Detalle' })).toHaveAttribute('aria-expanded', 'false')
  })

  it('ArrowDown reabre la lista cerrada con la primera opción activa', async () => {
    const usuario = userEvent.setup()
    montar()
    await escribirYEsperar(usuario)
    await usuario.keyboard('{Escape}')

    await usuario.keyboard('{ArrowDown}')

    expect(screen.getAllByRole('option')[0]).toHaveAttribute('aria-selected', 'true')
  })

  it('un click sobre una opción la elige sin perder el foco del campo', async () => {
    const usuario = userEvent.setup()
    const eventos = montar()
    await escribirYEsperar(usuario)

    await usuario.click(screen.getByRole('option', { name: /Leche descremada/ }))

    expect(eventos.elegidos.map((a) => a.id)).toEqual([2])
  })

  it('el campo es de solo lectura cuando el artículo salió del código, y no ofrece crear', () => {
    montar({ soloLectura: true, conArticulo: true })
    const caja = screen.getByRole('combobox', { name: 'Detalle' })
    expect(caja).toHaveAttribute('readonly')
    expect(caja).toHaveClass('bg-body-secondary')
    expect(screen.queryByRole('button', { name: 'Crear artículo' })).not.toBeInTheDocument()
  })
})

describe('ComboboxDeArticulos — crear artículo', () => {
  it('el botón "+" pide crear, está fuera del orden de tabulación y se oculta con un artículo ya elegido', async () => {
    const usuario = userEvent.setup()
    const eventos = montar()
    const mas = screen.getByRole('button', { name: 'Crear artículo' })
    expect(mas).toHaveAttribute('tabindex', '-1')

    await usuario.click(mas)
    expect(eventos.creaciones).toBe(1)
  })

  it('Ctrl+Enter también pide crear', async () => {
    const usuario = userEvent.setup()
    const eventos = montar()
    await usuario.click(screen.getByRole('combobox', { name: 'Detalle' }))

    await usuario.keyboard('{Control>}{Enter}{/Control}')

    expect(eventos.creaciones).toBe(1)
  })

  it('con un artículo elegido no hay botón y Ctrl+Enter no hace nada', async () => {
    const usuario = userEvent.setup()
    const eventos = montar({ conArticulo: true })
    expect(screen.queryByRole('button', { name: 'Crear artículo' })).not.toBeInTheDocument()
    await usuario.click(screen.getByRole('combobox', { name: 'Detalle' }))

    await usuario.keyboard('{Control>}{Enter}{/Control}')

    expect(eventos.creaciones).toBe(0)
  })

  it('deshabilitado, no se puede escribir ni crear', () => {
    montar({ disabled: true })
    expect(screen.getByRole('combobox', { name: 'Detalle' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Crear artículo' })).toBeDisabled()
  })
})
