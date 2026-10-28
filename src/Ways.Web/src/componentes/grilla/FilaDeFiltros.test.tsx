import { act, render, renderHook, screen } from '@testing-library/react'
import type { ComponentProps } from 'react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { FilaDeFiltros } from './FilaDeFiltros'
import type { ColumnasFiltrables } from './filtros'
import { useFiltrosDeColumna } from './useFiltrosDeColumna'
import { useOrdenamiento } from './useOrdenamiento'

type Fila = { id: number; nombre: string; modo: string }

const columnas: ColumnasFiltrables<Fila> = {
  nombre: { etiqueta: 'Nombre', valor: (f) => f.nombre },
  modo: { etiqueta: 'Modo', valor: (f) => f.modo, opciones: [{ valor: 'Fija', etiqueta: 'Fija' }] },
}

const filas: Fila[] = [
  { id: 1, nombre: 'Café', modo: 'Fija' },
  { id: 2, nombre: 'Té', modo: 'Derivada' },
]

function dibujar(sobrescribir: Partial<ComponentProps<typeof FilaDeFiltros>> = {}) {
  const props = {
    celdas: ['nombre', null, 'modo'],
    columnas,
    filtros: {},
    onCambio: vi.fn(),
    onLimpiar: vi.fn(),
    hayFiltros: false,
    ...sobrescribir,
  }
  render(
    <table>
      <thead>
        <FilaDeFiltros {...props} />
      </thead>
    </table>,
  )

  return props
}

describe('FilaDeFiltros', () => {
  it('rinde un campo de texto, un select con "Todos" y una celda vacía, más la de limpiar', () => {
    dibujar()

    expect(screen.getByRole('searchbox', { name: 'Filtrar por nombre' })).toBeInTheDocument()
    expect(screen.getByRole('combobox', { name: 'Filtrar por modo' })).toBeInTheDocument()
    expect(screen.getByRole('option', { name: 'Todos' })).toBeInTheDocument()
    expect(screen.getAllByRole('columnheader')).toHaveLength(4)
  })

  it('escribir y elegir avisan la columna y el valor', async () => {
    const usuario = userEvent.setup()
    const { onCambio } = dibujar()

    await usuario.type(screen.getByRole('searchbox'), 'c')
    await usuario.selectOptions(screen.getByRole('combobox'), 'Fija')

    expect(onCambio).toHaveBeenNthCalledWith(1, 'nombre', 'c')
    expect(onCambio).toHaveBeenNthCalledWith(2, 'modo', 'Fija')
  })

  it('limpiar está deshabilitado mientras no hay filtros', () => {
    dibujar()

    expect(screen.getByRole('button', { name: 'Limpiar filtros' })).toBeDisabled()
  })

  it('con filtros activos limpiar se puede pulsar', async () => {
    const usuario = userEvent.setup()
    const { onLimpiar } = dibujar({ filtros: { nombre: 'c' }, hayFiltros: true })

    await usuario.click(screen.getByRole('button', { name: 'Limpiar filtros' }))

    expect(onLimpiar).toHaveBeenCalledTimes(1)
  })

  it('sin conLimpiar no agrega la celda final', () => {
    dibujar({ conLimpiar: false })

    expect(screen.queryByRole('button', { name: 'Limpiar filtros' })).not.toBeInTheDocument()
    expect(screen.getAllByRole('columnheader')).toHaveLength(3)
  })
})

describe('useFiltrosDeColumna', () => {
  it('filtra, informa si hay filtros y limpia', () => {
    const { result } = renderHook(() => useFiltrosDeColumna(filas, columnas))
    expect(result.current.filas).toHaveLength(2)
    expect(result.current.hayFiltros).toBe(false)

    act(() => result.current.cambiar('nombre', 'cafe'))
    expect(result.current.filas.map((f) => f.id)).toEqual([1])
    expect(result.current.hayFiltros).toBe(true)

    act(() => result.current.limpiar())
    expect(result.current.filas).toHaveLength(2)
    expect(result.current.hayFiltros).toBe(false)
  })
})

describe('useOrdenamiento', () => {
  const ordenables = { nombre: { valor: (f: Fila) => f.nombre } }

  it('alterna ascendente, descendente y el orden original', () => {
    const { result } = renderHook(() => useOrdenamiento(filas, ordenables))
    expect(result.current.filas.map((f) => f.id)).toEqual([1, 2])

    act(() => result.current.alternar('nombre'))
    expect(result.current.orden).toEqual({ columna: 'nombre', direccion: 'ascendente' })
    expect(result.current.filas.map((f) => f.id)).toEqual([1, 2])

    act(() => result.current.alternar('nombre'))
    expect(result.current.filas.map((f) => f.id)).toEqual([2, 1])

    act(() => result.current.alternar('nombre'))
    expect(result.current.orden).toBeNull()
    expect(result.current.filas.map((f) => f.id)).toEqual([1, 2])
  })

  it('ordenar aplica el orden vigente a un grupo', () => {
    const { result } = renderHook(() => useOrdenamiento(filas, ordenables))
    act(() => result.current.alternar('nombre'))
    act(() => result.current.alternar('nombre'))

    expect(result.current.ordenar([filas[0], filas[1]]).map((f) => f.id)).toEqual([2, 1])
  })

  it('arranca con el orden inicial que se le da', () => {
    const { result } = renderHook(() =>
      useOrdenamiento(filas, ordenables, { columna: 'nombre', direccion: 'descendente' }),
    )

    expect(result.current.filas.map((f) => f.id)).toEqual([2, 1])
  })
})
