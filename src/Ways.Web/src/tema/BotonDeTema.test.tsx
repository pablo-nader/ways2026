import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it } from 'vitest'
import { BotonDeTema } from './BotonDeTema'

beforeEach(() => {
  localStorage.clear()
  document.documentElement.dataset.bsTheme = 'dark'
})

describe('BotonDeTema', () => {
  it('en tema oscuro ofrece pasar a claro', () => {
    render(<BotonDeTema />)

    expect(screen.getByRole('button', { name: 'Cambiar a tema claro' })).toBeInTheDocument()
  })

  it('al hacer clic alterna el atributo, la etiqueta y persiste', async () => {
    const usuario = userEvent.setup()
    render(<BotonDeTema />)

    await usuario.click(screen.getByRole('button', { name: 'Cambiar a tema claro' }))

    expect(document.documentElement.dataset.bsTheme).toBe('light')
    expect(localStorage.getItem('ways.tema')).toBe('claro')

    await usuario.click(screen.getByRole('button', { name: 'Cambiar a tema oscuro' }))

    expect(document.documentElement.dataset.bsTheme).toBe('dark')
    expect(localStorage.getItem('ways.tema')).toBe('oscuro')
  })
})
