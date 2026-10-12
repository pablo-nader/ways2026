import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { ChipDeSaldo } from './ChipDeSaldo'

function chip() {
  return screen.getByTestId('chip-de-saldo')
}

function barra() {
  return chip().querySelector('.progress-bar') as HTMLElement | null
}

describe('ChipDeSaldo', () => {
  it('muestra lo usado sobre el límite, con la barra llena en saldo / límite', () => {
    render(<ChipDeSaldo cliente={{ saldo: 20000, limiteCredito: 80000, creditoIlimitado: false }} />)
    expect(chip()).toHaveTextContent('$ 20.000,00 / $ 80.000,00')
    expect(barra()).toHaveStyle({ width: '25%' })
    expect(barra()).not.toHaveClass('bg-danger')
    expect(chip()).not.toHaveClass('text-danger')
  })

  it('el disponible va en el tooltip y en el texto accesible', () => {
    render(<ChipDeSaldo cliente={{ saldo: 5000, limiteCredito: 80000, creditoIlimitado: false }} />)
    expect(chip()).toHaveAttribute('title', 'Disponible $ 75.000,00')
    expect(chip()).toHaveTextContent('Saldo $ 5.000,00 · Disponible $ 75.000,00')
  })

  it('con crédito ilimitado muestra el saldo y "sin límite", sin barra', () => {
    render(<ChipDeSaldo cliente={{ saldo: 5000, limiteCredito: 0, creditoIlimitado: true }} />)
    expect(chip()).toHaveTextContent('$ 5.000,00 · sin límite')
    expect(chip()).toHaveTextContent('Saldo $ 5.000,00 · Disponible sin límite')
    expect(chip()).toHaveAttribute('title', 'Disponible: sin límite')
    expect(barra()).not.toBeInTheDocument()
    expect(chip().querySelector('.progress')).not.toBeInTheDocument()
  })

  it('por encima del límite se ve en color de peligro con la barra llena', () => {
    render(<ChipDeSaldo cliente={{ saldo: 90000, limiteCredito: 80000, creditoIlimitado: false }} />)
    expect(chip()).toHaveClass('text-danger')
    expect(barra()).toHaveClass('bg-danger')
    expect(barra()).toHaveStyle({ width: '100%' })
    expect(chip()).toHaveTextContent('$ 90.000,00 / $ 80.000,00')
  })

  it('justo en el límite la barra está llena pero todavía no es de peligro', () => {
    render(<ChipDeSaldo cliente={{ saldo: 80000, limiteCredito: 80000, creditoIlimitado: false }} />)
    expect(barra()).toHaveStyle({ width: '100%' })
    expect(barra()).not.toHaveClass('bg-danger')
    expect(chip()).not.toHaveClass('text-danger')
  })

  it('con saldo a favor deja la barra vacía y muestra el saldo tal cual', () => {
    render(<ChipDeSaldo cliente={{ saldo: -2000, limiteCredito: 80000, creditoIlimitado: false }} />)
    expect(barra()).toHaveStyle({ width: '0%' })
    expect(chip()).toHaveTextContent('-$ 2.000,00 / $ 80.000,00')
    expect(chip()).not.toHaveClass('text-danger')
  })
})
