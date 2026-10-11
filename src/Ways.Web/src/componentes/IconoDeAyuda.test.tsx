import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it } from 'vitest'
import { IconoDeAyuda } from './IconoDeAyuda'

afterEach(cleanup)

const TEXTO = 'Desmarcado, cada vez que se agrega suma una línea nueva.'

function montar() {
  return render(<IconoDeAyuda id="ayuda-1" texto={TEXTO} etiqueta="Ayuda sobre la acumulación" />)
}

describe('IconoDeAyuda', () => {
  it('empieza oculto pero el texto está en el DOM como tooltip', () => {
    montar()

    expect(screen.getByRole('tooltip', { hidden: true })).toHaveTextContent(TEXTO)
    expect(screen.getByRole('tooltip', { hidden: true })).toHaveAttribute('data-visible', 'false')
  })

  it('el botón es focusable y su descripción accesible es el texto de ayuda', () => {
    montar()

    const boton = screen.getByRole('button', { name: 'Ayuda sobre la acumulación' })
    expect(boton).toHaveAccessibleDescription(TEXTO)
  })

  it('se muestra al pasar el mouse y se oculta al salir', async () => {
    montar()
    const usuario = userEvent.setup()
    const boton = screen.getByRole('button', { name: 'Ayuda sobre la acumulación' })

    await usuario.hover(boton)
    expect(screen.getByRole('tooltip', { hidden: true })).toHaveAttribute('data-visible', 'true')

    await usuario.unhover(boton)
    expect(screen.getByRole('tooltip', { hidden: true })).toHaveAttribute('data-visible', 'false')
  })

  it('se muestra al enfocar con el teclado y se oculta al perder el foco', async () => {
    montar()
    const usuario = userEvent.setup()

    await usuario.tab()
    expect(screen.getByRole('button', { name: 'Ayuda sobre la acumulación' })).toHaveFocus()
    expect(screen.getByRole('tooltip', { hidden: true })).toHaveAttribute('data-visible', 'true')

    await usuario.tab()
    expect(screen.getByRole('tooltip', { hidden: true })).toHaveAttribute('data-visible', 'false')
  })

  it('Escape lo cierra sin quitar el foco', async () => {
    montar()
    const usuario = userEvent.setup()
    await usuario.tab()

    fireEvent.keyDown(screen.getByRole('button', { name: 'Ayuda sobre la acumulación' }), { key: 'Escape' })

    expect(screen.getByRole('tooltip', { hidden: true })).toHaveAttribute('data-visible', 'false')
    expect(screen.getByRole('button', { name: 'Ayuda sobre la acumulación' })).toHaveFocus()
  })
})
