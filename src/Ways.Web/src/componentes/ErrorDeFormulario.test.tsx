import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ErrorDeFormulario } from './ErrorDeFormulario'

const scrollIntoViewMock = vi.fn()

beforeEach(() => {
  scrollIntoViewMock.mockReset()
  Object.defineProperty(HTMLElement.prototype, 'scrollIntoView', {
    configurable: true,
    writable: true,
    value: scrollIntoViewMock,
  })
})

afterEach(() => {
  cleanup()
  Reflect.deleteProperty(HTMLElement.prototype, 'scrollIntoView')
})

describe('ErrorDeFormulario', () => {
  it('con el mensaje vacío no renderiza nada ni mueve el scroll', () => {
    const { container } = render(<ErrorDeFormulario mensaje="" />)

    expect(container).toBeEmptyDOMElement()
    expect(scrollIntoViewMock).not.toHaveBeenCalled()
  })

  it('con un mensaje lo expone como role="alert"', () => {
    render(<ErrorDeFormulario mensaje="Ya existe." />)

    expect(screen.getByRole('alert')).toHaveTextContent('Ya existe.')
  })

  it('trae el error a la vista al aparecer', () => {
    render(<ErrorDeFormulario mensaje="Ya existe." />)

    expect(scrollIntoViewMock).toHaveBeenCalledTimes(1)
    expect(scrollIntoViewMock.mock.contexts[0]).toBe(screen.getByRole('alert'))
  })

  it('un mensaje idéntico que reaparece tras vaciarse vuelve a traerse a la vista', () => {
    const { rerender } = render(<ErrorDeFormulario mensaje="Ya existe." />)
    rerender(<ErrorDeFormulario mensaje="" />)
    rerender(<ErrorDeFormulario mensaje="Ya existe." />)

    expect(scrollIntoViewMock).toHaveBeenCalledTimes(2)
  })

  it('un mensaje idéntico sin vaciarse se trae a la vista otra vez solo si cambia el disparo', () => {
    const { rerender } = render(<ErrorDeFormulario mensaje="Falta el nombre." disparo={1} />)
    rerender(<ErrorDeFormulario mensaje="Falta el nombre." disparo={1} />)
    expect(scrollIntoViewMock).toHaveBeenCalledTimes(1)

    rerender(<ErrorDeFormulario mensaje="Falta el nombre." disparo={2} />)
    expect(scrollIntoViewMock).toHaveBeenCalledTimes(2)
  })

  it('un mensaje distinto se trae a la vista', () => {
    const { rerender } = render(<ErrorDeFormulario mensaje="Uno." />)
    rerender(<ErrorDeFormulario mensaje="Otro." />)

    expect(scrollIntoViewMock).toHaveBeenCalledTimes(2)
  })

  it('sin scrollIntoView en el entorno no falla', () => {
    Reflect.deleteProperty(HTMLElement.prototype, 'scrollIntoView')

    expect(() => render(<ErrorDeFormulario mensaje="Ya existe." />)).not.toThrow()
    expect(screen.getByRole('alert')).toBeInTheDocument()
  })
})
