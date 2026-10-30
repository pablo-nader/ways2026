import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { PaginadorDePadron } from './PaginadorDePadron'

function montar(sobrescribir: Partial<Parameters<typeof PaginadorDePadron>[0]> = {}) {
  const onCambiarPagina = vi.fn()
  const onCambiarTamanio = vi.fn()
  render(
    <PaginadorDePadron
      pagina={2}
      tamanio={25}
      total={60}
      unidad="cliente(s)"
      deshabilitado={false}
      onCambiarPagina={onCambiarPagina}
      onCambiarTamanio={onCambiarTamanio}
      {...sobrescribir}
    />,
  )

  return { onCambiarPagina, onCambiarTamanio }
}

describe('PaginadorDePadron', () => {
  it('muestra la página, la última y el total', () => {
    montar()

    expect(screen.getByText('Página 2 de 3 — 60 cliente(s)')).toBeInTheDocument()
  })

  it('Anterior y Siguiente piden una página menos o más', async () => {
    const usuario = userEvent.setup()
    const { onCambiarPagina } = montar()

    await usuario.click(screen.getByRole('button', { name: 'Anterior' }))
    await usuario.click(screen.getByRole('button', { name: 'Siguiente' }))

    expect(onCambiarPagina.mock.calls).toEqual([[-1], [1]])
  })

  it('en la primera página Anterior está deshabilitado', () => {
    montar({ pagina: 1 })
    expect(screen.getByRole('button', { name: 'Anterior' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeEnabled()
  })

  it('en la última página Siguiente está deshabilitado', () => {
    montar({ pagina: 3 })
    expect(screen.getByRole('button', { name: 'Anterior' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeDisabled()
  })

  it('un conjunto vacío es la página 1 de 1', () => {
    montar({ pagina: 1, total: 0 })

    expect(screen.getByText('Página 1 de 1 — 0 cliente(s)')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeDisabled()
  })

  it('el tamaño de página ofrece 25, 50 y 100 y avisa el elegido', async () => {
    const usuario = userEvent.setup()
    const { onCambiarTamanio } = montar()

    expect(screen.getByLabelText('Filas por página')).toHaveValue('25')
    await usuario.selectOptions(screen.getByLabelText('Filas por página'), '100')

    expect(onCambiarTamanio).toHaveBeenCalledWith(100)
  })

  it('deshabilitado deja inertes todos los controles', () => {
    montar({ deshabilitado: true })

    expect(screen.getByRole('button', { name: 'Anterior' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Siguiente' })).toBeDisabled()
    expect(screen.getByLabelText('Filas por página')).toBeDisabled()
  })
})
