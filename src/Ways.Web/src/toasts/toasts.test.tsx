import { act, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { Toaster } from './Toaster'
import { reiniciarToasts, toast } from './toasts'

beforeEach(() => {
  vi.useFakeTimers()
})

afterEach(() => {
  vi.useRealTimers()
})

function avanzar(ms: number) {
  act(() => {
    vi.advanceTimersByTime(ms)
  })
}

function emitir(accion: () => void) {
  act(accion)
}

describe('Toaster', () => {
  it('muestra el texto del toast', () => {
    render(<Toaster />)
    emitir(() => toast.exito('Pago eliminado.'))

    expect(screen.getByText('Pago eliminado.')).toBeInTheDocument()
  })

  it('no renderiza ningún status ni alert cuando no hay toasts', () => {
    render(<Toaster />)

    expect(screen.queryByRole('status')).toBeNull()
    expect(screen.queryByRole('alert')).toBeNull()
  })

  it('éxito y advertencia usan role status; el error usa role alert', () => {
    render(<Toaster />)
    emitir(() => {
      toast.exito('Guardado.')
      toast.advertencia('Revisá el stock.')
      toast.error('No se pudo eliminar.')
    })

    expect(screen.getAllByRole('status').map((e) => e.textContent)).toEqual(['Guardado.', 'Revisá el stock.'])
    expect(screen.getByRole('alert')).toHaveTextContent('No se pudo eliminar.')
  })

  it('el éxito se cierra a los 4000 ms y no antes', () => {
    render(<Toaster />)
    emitir(() => toast.exito('Guardado.'))

    avanzar(3999)
    expect(screen.getByText('Guardado.')).toBeInTheDocument()

    avanzar(1)
    expect(screen.queryByText('Guardado.')).toBeNull()
  })

  it('la advertencia se cierra a los 4000 ms', () => {
    render(<Toaster />)
    emitir(() => toast.advertencia('Ojo.'))

    avanzar(3999)
    expect(screen.getByText('Ojo.')).toBeInTheDocument()

    avanzar(1)
    expect(screen.queryByText('Ojo.')).toBeNull()
  })

  it('el error se cierra a los 7000 ms y no antes', () => {
    render(<Toaster />)
    emitir(() => toast.error('Falló.'))

    avanzar(6999)
    expect(screen.getByText('Falló.')).toBeInTheDocument()

    avanzar(1)
    expect(screen.queryByText('Falló.')).toBeNull()
  })

  it('el puntero encima pausa el cierre y al salir se reinicia la cuenta', () => {
    render(<Toaster />)
    emitir(() => toast.exito('Guardado.'))
    const aviso = screen.getByRole('status')

    avanzar(3000)
    fireEvent.mouseEnter(aviso)
    avanzar(60000)
    expect(screen.getByText('Guardado.')).toBeInTheDocument()

    fireEvent.mouseLeave(aviso)
    avanzar(3999)
    expect(screen.getByText('Guardado.')).toBeInTheDocument()

    avanzar(1)
    expect(screen.queryByText('Guardado.')).toBeNull()
  })

  it('el foco dentro del toast pausa el cierre y al perderlo se reanuda', () => {
    render(<Toaster />)
    emitir(() => toast.exito('Guardado.'))
    const cerrar = screen.getByRole('button', { name: 'Cerrar aviso' })

    fireEvent.focus(cerrar)
    avanzar(60000)
    expect(screen.getByText('Guardado.')).toBeInTheDocument()

    fireEvent.blur(cerrar)
    avanzar(4000)
    expect(screen.queryByText('Guardado.')).toBeNull()
  })

  it('el botón Cerrar aviso quita el toast', () => {
    render(<Toaster />)
    emitir(() => toast.exito('Guardado.'))

    fireEvent.click(screen.getByRole('button', { name: 'Cerrar aviso' }))

    expect(screen.queryByText('Guardado.')).toBeNull()
  })

  it('un duplicado reinicia el temporizador en vez de apilar una copia', () => {
    render(<Toaster />)
    let primero = 0
    let segundo = 0
    emitir(() => {
      primero = toast.exito('Guardado.')
    })
    avanzar(3000)
    emitir(() => {
      segundo = toast.exito('Guardado.')
    })

    expect(segundo).toBe(primero)
    expect(screen.getAllByText('Guardado.')).toHaveLength(1)

    avanzar(3999)
    expect(screen.getByText('Guardado.')).toBeInTheDocument()

    avanzar(1)
    expect(screen.queryByText('Guardado.')).toBeNull()
  })

  it('el mismo texto con otro tipo se apila como un toast distinto', () => {
    render(<Toaster />)
    emitir(() => {
      toast.exito('Listo.')
      toast.error('Listo.')
    })

    expect(screen.getAllByText('Listo.')).toHaveLength(2)
  })

  it('cada toast se cierra por su propio plazo', () => {
    render(<Toaster />)
    emitir(() => toast.error('Falló.'))
    avanzar(5000)
    emitir(() => toast.exito('Guardado.'))

    avanzar(4000)
    expect(screen.queryByText('Guardado.')).toBeNull()
    expect(screen.queryByText('Falló.')).toBeNull()
  })

  it('cerrar con un id inexistente no falla', () => {
    render(<Toaster />)
    emitir(() => toast.exito('Guardado.'))

    emitir(() => toast.cerrar(9999))

    expect(screen.getByText('Guardado.')).toBeInTheDocument()
  })

  it('reiniciarToasts vacía la pila', () => {
    render(<Toaster />)
    emitir(() => toast.exito('Guardado.'))

    emitir(() => reiniciarToasts())

    expect(screen.queryByText('Guardado.')).toBeNull()
  })
})

describe('reinicio entre tests', () => {
  it('deja un toast vivo para comprobar el reinicio del siguiente test', () => {
    toast.error('Quedó colgado.')
  })

  it('el setup global ya vació la pila', () => {
    render(<Toaster />)

    expect(screen.queryByText('Quedó colgado.')).toBeNull()
  })
})
