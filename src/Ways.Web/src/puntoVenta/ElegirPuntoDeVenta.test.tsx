import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { ElegirPuntoDeVenta } from './ElegirPuntoDeVenta'
import type { PuntoVentaListado } from '../api/tipos'

function pvFixture(sobrescribir: Partial<PuntoVentaListado> = {}): PuntoVentaListado {
  return {
    id: 100,
    idTenant: 2,
    idEmpresa: 20,
    nombre: 'Local Centro',
    domicilio: null,
    horario: null,
    whatsapp: null,
    instagram: null,
    facebook: null,
    web: null,
    nombreTenant: 'Comercio Sur',
    razonSocialEmpresa: 'Sur SRL',
    ...sobrescribir,
  }
}

const centro = pvFixture({ domicilio: 'Av. Principal 123' })
const norte = pvFixture({ id: 101, nombre: 'Local Norte' })

describe('ElegirPuntoDeVenta', () => {
  it('lista un ítem por punto de venta, cada uno con un botón alcanzable por nombre', () => {
    render(<ElegirPuntoDeVenta puntosVenta={[centro, norte]} alElegir={vi.fn()} />)

    expect(screen.getByRole('heading', { level: 1, name: 'Elegí el punto de venta' })).toBeInTheDocument()
    expect(screen.getAllByRole('listitem')).toHaveLength(2)
    expect(screen.getByRole('button', { name: /^Local Centro/ })).toHaveTextContent('Av. Principal 123')
    expect(screen.getByRole('button', { name: 'Local Norte' })).toBeInTheDocument()
  })

  it('al hacer clic informa el id del punto de venta elegido', async () => {
    const alElegir = vi.fn()
    render(<ElegirPuntoDeVenta puntosVenta={[centro, norte]} alElegir={alElegir} />)

    await userEvent.click(screen.getByRole('button', { name: 'Local Norte' }))

    expect(alElegir).toHaveBeenCalledTimes(1)
    expect(alElegir).toHaveBeenCalledWith(101)
  })

  it('marca el actual con aria-current y el sufijo "(actual)"', () => {
    render(<ElegirPuntoDeVenta puntosVenta={[centro, norte]} actual={101} alElegir={vi.fn()} />)

    expect(screen.getByRole('button', { name: 'Local Norte (actual)' })).toHaveAttribute('aria-current', 'true')
    expect(screen.getByRole('button', { name: /^Local Centro/ })).not.toHaveAttribute('aria-current')
  })

  it('sin actual ningún botón lleva aria-current', () => {
    render(<ElegirPuntoDeVenta puntosVenta={[centro, norte]} alElegir={vi.fn()} />)

    for (const boton of screen.getAllByRole('button')) {
      expect(boton).not.toHaveAttribute('aria-current')
    }
  })
})
