import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import type { OfertaVigente } from '../api/ofertasVigentes'
import { MarcaDeOfertas } from './MarcaDeOfertas'

function oferta(sobrescribir: Partial<OfertaVigente> = {}): OfertaVigente {
  return {
    idOferta: 1,
    nombre: 'Gaseosas',
    alcance: 'Articulo',
    cantidadMinima: null,
    soloMultiplos: false,
    porcentaje: 10,
    precioUnitario: null,
    importeFijo: null,
    fechaHasta: null,
    acumulable: false,
    ...sobrescribir,
  }
}

describe('MarcaDeOfertas', () => {
  it('con una oferta dice "Oferta"; con varias, "Ofertas (n)"', () => {
    const { rerender } = render(<MarcaDeOfertas ofertas={[oferta()]} />)
    expect(screen.getByRole('button', { name: 'Oferta' })).toBeInTheDocument()

    rerender(<MarcaDeOfertas ofertas={[oferta(), oferta({ idOferta: 2, nombre: 'Combo' })]} />)
    expect(screen.getByRole('button', { name: 'Ofertas (2)' })).toBeInTheDocument()
  })

  it('el tooltip está oculto hasta el mouse o el foco, y lista una línea por oferta', () => {
    render(
      <MarcaDeOfertas
        ofertas={[oferta(), oferta({ idOferta: 2, nombre: 'Pack', cantidadMinima: 3, soloMultiplos: true, importeFijo: 200, porcentaje: null })]}
      />,
    )
    const boton = screen.getByRole('button')
    const tooltip = screen.getByRole('tooltip')
    expect(tooltip).toHaveAttribute('data-visible', 'false')
    expect(boton).toHaveAttribute('aria-describedby', tooltip.id)

    fireEvent.mouseEnter(boton.parentElement as HTMLElement)
    expect(tooltip).toHaveAttribute('data-visible', 'true')
    expect(tooltip).toHaveTextContent('Gaseosas — 10% de descuento')
    expect(tooltip).toHaveTextContent('Pack — $ 200,00 menos · cada 3')

    fireEvent.mouseLeave(boton.parentElement as HTMLElement)
    expect(tooltip).toHaveAttribute('data-visible', 'false')

    fireEvent.focus(boton)
    expect(tooltip).toHaveAttribute('data-visible', 'true')
    fireEvent.blur(boton)
    expect(tooltip).toHaveAttribute('data-visible', 'false')
  })

  it('Escape oculta el tooltip y no llega al keydown de document', () => {
    const enDocument = vi.fn()
    document.addEventListener('keydown', enDocument)
    render(<MarcaDeOfertas ofertas={[oferta()]} />)
    const boton = screen.getByRole('button')
    fireEvent.focus(boton)

    fireEvent.keyDown(boton, { key: 'Escape' })

    expect(screen.getByRole('tooltip')).toHaveAttribute('data-visible', 'false')
    expect(enDocument).not.toHaveBeenCalled()

    fireEvent.keyDown(boton, { key: 'Escape' })
    expect(enDocument).toHaveBeenCalledTimes(1)
    document.removeEventListener('keydown', enDocument)
  })

  it('con el mouse encima y sin foco, Escape oculta el tooltip y sigue hasta document', () => {
    const enDocument = vi.fn()
    document.addEventListener('keydown', enDocument)
    render(<MarcaDeOfertas ofertas={[oferta()]} />)
    fireEvent.mouseEnter(screen.getByRole('button').parentElement!)
    expect(screen.getByRole('tooltip')).toHaveAttribute('data-visible', 'true')

    fireEvent.keyDown(document.body, { key: 'Escape' })

    expect(screen.getByRole('tooltip')).toHaveAttribute('data-visible', 'false')
    expect(enDocument).toHaveBeenCalledTimes(1)
    document.removeEventListener('keydown', enDocument)
  })

  it('el clic no llega al contenedor', () => {
    const alClic = vi.fn()
    render(
      <div onClick={alClic}>
        <MarcaDeOfertas ofertas={[oferta()]} />
      </div>,
    )

    fireEvent.click(screen.getByRole('button'))

    expect(alClic).not.toHaveBeenCalled()
  })
})
