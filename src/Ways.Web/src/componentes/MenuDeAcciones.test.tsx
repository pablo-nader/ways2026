import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
import { MenuDeAcciones } from './MenuDeAcciones'
import type { ItemDeMenuDeAcciones } from './MenuDeAcciones'

function montar(items: ItemDeMenuDeAcciones[]) {
  return render(
    <MemoryRouter initialEntries={['/inicio']}>
      <button type="button">afuera</button>
      <MenuDeAcciones etiqueta="Caja" items={items} />
      <Routes>
        <Route path="*" element={<p>pantalla</p>} />
      </Routes>
    </MemoryRouter>,
  )
}

function itemsFixture(alElegir = vi.fn()): ItemDeMenuDeAcciones[] {
  return [
    { clave: 'ventas', etiqueta: 'Ventas del turno', a: '/ventas-del-turno' },
    { clave: 'retirar', etiqueta: 'Retirar efectivo', deshabilitado: true, alSeleccionar: alElegir },
    { clave: 'cerrar', etiqueta: 'Cerrar turno', alSeleccionar: alElegir },
  ]
}

describe('MenuDeAcciones', () => {
  it('arranca cerrado y el botón lo abre y lo cierra, reflejándolo en aria-expanded', async () => {
    montar(itemsFixture())
    const boton = screen.getByRole('button', { name: 'Caja' })
    expect(boton).toHaveAttribute('aria-expanded', 'false')
    expect(screen.queryByRole('button', { name: 'Cerrar turno' })).not.toBeInTheDocument()

    await userEvent.click(boton)
    expect(boton).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByRole('button', { name: 'Cerrar turno' })).toBeInTheDocument()

    await userEvent.click(boton)
    expect(boton).toHaveAttribute('aria-expanded', 'false')
  })

  it('elegir una acción la ejecuta, cierra el menú y devuelve el foco al botón', async () => {
    const alElegir = vi.fn()
    montar(itemsFixture(alElegir))
    const boton = screen.getByRole('button', { name: 'Caja' })
    await userEvent.click(boton)

    await userEvent.click(screen.getByRole('button', { name: 'Cerrar turno' }))

    expect(alElegir).toHaveBeenCalledTimes(1)
    expect(boton).toHaveAttribute('aria-expanded', 'false')
    expect(boton).toHaveFocus()
  })

  it('un ítem deshabilitado se muestra pero no ejecuta nada', async () => {
    const alElegir = vi.fn()
    montar(itemsFixture(alElegir))
    await userEvent.click(screen.getByRole('button', { name: 'Caja' }))

    const deshabilitado = screen.getByRole('button', { name: 'Retirar efectivo' })
    expect(deshabilitado).toBeDisabled()
    await userEvent.click(deshabilitado)

    expect(alElegir).not.toHaveBeenCalled()
  })

  it('elegir un enlace navega y cierra el menú', async () => {
    montar(itemsFixture())
    await userEvent.click(screen.getByRole('button', { name: 'Caja' }))

    await userEvent.click(screen.getByRole('link', { name: 'Ventas del turno' }))

    expect(screen.getByRole('button', { name: 'Caja' })).toHaveAttribute('aria-expanded', 'false')
  })

  it('Escape cierra el menú abierto y devuelve el foco al botón', async () => {
    montar(itemsFixture())
    const boton = screen.getByRole('button', { name: 'Caja' })
    await userEvent.click(boton)
    await userEvent.keyboard('{ArrowDown}')
    expect(screen.getByRole('link', { name: 'Ventas del turno' })).toHaveFocus()

    await userEvent.keyboard('{Escape}')

    expect(boton).toHaveAttribute('aria-expanded', 'false')
    expect(boton).toHaveFocus()
  })

  it('un click afuera cierra el menú', async () => {
    montar(itemsFixture())
    const boton = screen.getByRole('button', { name: 'Caja' })
    await userEvent.click(boton)

    await userEvent.click(screen.getByRole('button', { name: 'afuera' }))

    expect(boton).toHaveAttribute('aria-expanded', 'false')
  })

  it('con el teclado: ArrowDown en el botón cerrado abre y enfoca el primero; las flechas recorren saltando los deshabilitados y dan la vuelta', async () => {
    montar(itemsFixture())
    screen.getByRole('button', { name: 'Caja' }).focus()

    await userEvent.keyboard('{ArrowDown}')
    expect(screen.getByRole('link', { name: 'Ventas del turno' })).toHaveFocus()

    await userEvent.keyboard('{ArrowDown}')
    expect(screen.getByRole('button', { name: 'Cerrar turno' })).toHaveFocus()

    await userEvent.keyboard('{ArrowDown}')
    expect(screen.getByRole('link', { name: 'Ventas del turno' })).toHaveFocus()

    await userEvent.keyboard('{End}')
    expect(screen.getByRole('button', { name: 'Cerrar turno' })).toHaveFocus()
  })

  it('perder el foco hacia afuera del menú lo cierra', async () => {
    montar(itemsFixture())
    const boton = screen.getByRole('button', { name: 'Caja' })
    await userEvent.click(boton)
    await userEvent.keyboard('{ArrowDown}')

    await userEvent.tab({ shift: true })
    await userEvent.tab({ shift: true })

    expect(boton).toHaveAttribute('aria-expanded', 'false')
  })
})
