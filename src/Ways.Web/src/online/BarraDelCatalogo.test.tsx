import { act, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { BarraDelCatalogo } from './BarraDelCatalogo'
import { categoriaPublica } from './datosDePrueba'

const alBuscar = vi.fn<(buscar: string) => void>()
const alElegirCategoria = vi.fn<(idCategoria: number | null) => void>()

function verBarra(props: Partial<Parameters<typeof BarraDelCatalogo>[0]> = {}) {
  render(
    <BarraDelCatalogo
      categorias={[]}
      idCategoria={null}
      alBuscar={alBuscar}
      alElegirCategoria={alElegirCategoria}
      {...props}
    />,
  )

  return screen.getByRole('searchbox', { name: 'Buscar artículos' })
}

function escribir(campo: HTMLElement, texto: string) {
  fireEvent.change(campo, { target: { value: texto } })
}

beforeEach(() => {
  alBuscar.mockReset()
  alElegirCategoria.mockReset()
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
})

afterEach(() => {
  vi.useRealTimers()
})

describe('BarraDelCatalogo', () => {
  describe('búsqueda', () => {
    it('al montarse no agenda ninguna búsqueda', () => {
      verBarra()

      expect(vi.getTimerCount()).toBe(0)
      expect(alBuscar).not.toHaveBeenCalled()
    })

    it('busca una sola vez, con el último texto sin espacios sobrantes, pasada la pausa de 300 ms', () => {
      const campo = verBarra()

      escribir(campo, 'ta')
      escribir(campo, 'taladro ')
      act(() => void vi.advanceTimersByTime(299))
      expect(alBuscar).not.toHaveBeenCalled()
      act(() => void vi.advanceTimersByTime(1))

      expect(alBuscar.mock.calls).toEqual([['taladro']])
    })

    it('volver al texto de antes dentro de la pausa no busca nada', () => {
      const campo = verBarra()

      escribir(campo, 'a')
      escribir(campo, '')
      act(() => void vi.advanceTimersByTime(1000))

      expect(alBuscar).not.toHaveBeenCalled()
      expect(vi.getTimerCount()).toBe(0)
    })

    it('Enter busca de inmediato, y repetirlo o esperar la pausa después no repite la búsqueda', () => {
      const campo = verBarra()

      escribir(campo, 'sierra')
      fireEvent.submit(campo.closest('form') as HTMLFormElement)
      fireEvent.submit(campo.closest('form') as HTMLFormElement)
      act(() => void vi.advanceTimersByTime(1000))

      expect(alBuscar.mock.calls).toEqual([['sierra']])
    })

    it('limpiar el texto después de una búsqueda vuelve a buscar, ahora sin texto', () => {
      const campo = verBarra()
      escribir(campo, 'sierra')
      fireEvent.submit(campo.closest('form') as HTMLFormElement)

      escribir(campo, '')
      act(() => void vi.advanceTimersByTime(300))

      expect(alBuscar.mock.calls).toEqual([['sierra'], ['']])
    })
  })

  describe('categorías', () => {
    it('sin categorías y sin filtro no hay chips', () => {
      verBarra()

      expect(screen.queryByRole('group', { name: 'Categorías' })).not.toBeInTheDocument()
    })

    it('con una categoría elegida "Todos" se ve aunque la respuesta no traiga categorías, y sale del filtro', () => {
      verBarra({ idCategoria: 6 })

      const todos = screen.getByRole('button', { name: 'Todos' })
      expect(todos).toHaveAttribute('aria-pressed', 'false')
      fireEvent.click(todos)

      expect(alElegirCategoria).toHaveBeenCalledWith(null)
    })

    it('con categorías, "Todos" y cada categoría con su cuenta', () => {
      verBarra({ categorias: [categoriaPublica(5, 'Herramientas', 3)] })

      expect(screen.getByRole('button', { name: 'Todos' })).toHaveAttribute('aria-pressed', 'true')
      fireEvent.click(screen.getByRole('button', { name: 'Herramientas 3' }))

      expect(alElegirCategoria).toHaveBeenCalledWith(5)
    })
  })
})
