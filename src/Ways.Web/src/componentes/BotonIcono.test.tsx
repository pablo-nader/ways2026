import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { BotonIcono, EnlaceIcono } from './BotonIcono'

afterEach(cleanup)

describe('BotonIcono', () => {
  it.each([
    ['eliminar', 'Eliminar', 'btn-outline-danger'],
    ['editar', 'Editar', 'btn-outline-primary'],
    ['agregar', 'Agregar', 'btn-primary'],
  ] as const)('el ícono %s expone nombre accesible, tooltip y variante', (icono, etiqueta, variante) => {
    render(<BotonIcono icono={icono} />)

    const boton = screen.getByRole('button', { name: etiqueta })
    expect(boton).toHaveAttribute('title', etiqueta)
    expect(boton).toHaveAttribute('type', 'button')
    expect(boton).toHaveClass('btn', 'btn-sm', 'btn-icono', variante)
    expect(boton.querySelector('svg')).toHaveAttribute('aria-hidden', 'true')
    expect(boton).toHaveTextContent('')
  })

  it('una etiqueta específica cambia el nombre accesible pero el tooltip sigue siendo la palabra corta', () => {
    render(<BotonIcono icono="eliminar" etiqueta="Eliminar Pérez" />)

    const boton = screen.getByRole('button', { name: 'Eliminar Pérez' })
    expect(boton).toHaveAttribute('title', 'Eliminar')
  })

  it('un title explícito reemplaza al tooltip por defecto sin cambiar el nombre accesible', () => {
    render(<BotonIcono icono="editar" title="No se puede editar." />)

    expect(screen.getByRole('button', { name: 'Editar' })).toHaveAttribute('title', 'No se puede editar.')
  })

  it('invoca onClick al hacer click', () => {
    const onClick = vi.fn()
    render(<BotonIcono icono="agregar" onClick={onClick} />)

    fireEvent.click(screen.getByRole('button', { name: 'Agregar' }))

    expect(onClick).toHaveBeenCalledTimes(1)
  })

  it('deshabilitado no invoca onClick', () => {
    const onClick = vi.fn()
    render(<BotonIcono icono="eliminar" disabled onClick={onClick} />)

    const boton = screen.getByRole('button', { name: 'Eliminar' })
    fireEvent.click(boton)

    expect(boton).toBeDisabled()
    expect(onClick).not.toHaveBeenCalled()
  })

  it('conserva las clases extra del llamador', () => {
    render(<BotonIcono icono="editar" className="me-1" />)

    expect(screen.getByRole('button', { name: 'Editar' })).toHaveClass('btn-icono', 'me-1')
  })
})

describe('EnlaceIcono', () => {
  it('renderiza un enlace real con href, nombre accesible, tooltip y variante', () => {
    render(
      <MemoryRouter>
        <EnlaceIcono icono="editar" to="/articulos/edit/7" />
      </MemoryRouter>,
    )

    const enlace = screen.getByRole('link', { name: 'Editar' })
    expect(enlace).toHaveAttribute('href', '/articulos/edit/7')
    expect(enlace).toHaveAttribute('title', 'Editar')
    expect(enlace).toHaveClass('btn', 'btn-icono', 'btn-outline-primary')
  })

  it('invoca onClick al hacer click', () => {
    const onClick = vi.fn((evento: { preventDefault: () => void }) => evento.preventDefault())
    render(
      <MemoryRouter>
        <EnlaceIcono icono="agregar" to="/x" onClick={onClick} />
      </MemoryRouter>,
    )

    fireEvent.click(screen.getByRole('link', { name: 'Agregar' }))

    expect(onClick).toHaveBeenCalledTimes(1)
  })
})
