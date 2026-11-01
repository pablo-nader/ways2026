import { act, cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { useEnLinea } from './useEnLinea'

function Sonda() {
  return <span>{useEnLinea() ? 'en línea' : 'sin conexión'}</span>
}

afterEach(() => {
  cleanup()
  vi.restoreAllMocks()
})

describe('useEnLinea', () => {
  it('arranca con el estado del navegador y sigue los eventos online/offline', () => {
    const estado = vi.spyOn(window.navigator, 'onLine', 'get').mockReturnValue(true)
    render(<Sonda />)
    expect(screen.getByText('en línea')).toBeInTheDocument()

    estado.mockReturnValue(false)
    act(() => {
      window.dispatchEvent(new Event('offline'))
    })
    expect(screen.getByText('sin conexión')).toBeInTheDocument()

    estado.mockReturnValue(true)
    act(() => {
      window.dispatchEvent(new Event('online'))
    })
    expect(screen.getByText('en línea')).toBeInTheDocument()
  })

  it('arranca sin conexión si el navegador ya la perdió', () => {
    vi.spyOn(window.navigator, 'onLine', 'get').mockReturnValue(false)
    render(<Sonda />)
    expect(screen.getByText('sin conexión')).toBeInTheDocument()
  })
})
