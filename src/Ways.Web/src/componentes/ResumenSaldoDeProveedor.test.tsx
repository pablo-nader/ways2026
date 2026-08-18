import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { describe, expect, it } from 'vitest'
import { esSaldoAFavor, ResumenSaldoDeProveedor } from './ResumenSaldoDeProveedor'

function renderResumen(saldo: number, idProveedor = 1) {
  return render(<ResumenSaldoDeProveedor saldo={saldo} idProveedor={idProveedor} />, {
    wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter>,
  })
}

// ---- esSaldoAFavor: helper puro, sin DOM (web-descriptor-tests) -------------------------------

describe('esSaldoAFavor', () => {
  it('un saldo negativo es "a favor"', () => {
    expect(esSaldoAFavor(-1)).toBe(true)
  })

  it('cero y un saldo positivo NO son "a favor"', () => {
    expect(esSaldoAFavor(0)).toBe(false)
    expect(esSaldoAFavor(1)).toBe(false)
  })
})

// ---- ResumenSaldoDeProveedor -------------------------------------------------------------------

describe('ResumenSaldoDeProveedor', () => {
  it('un saldo positivo muestra el importe, sin el callout de saldo a favor', () => {
    renderResumen(500)
    expect(screen.getByText('$500,00')).toBeInTheDocument()
    expect(screen.queryByText('Saldo a favor.')).not.toBeInTheDocument()
  })

  // mutation target #28 (design.md, tasks.md 6.13): la rama de saldo a favor en
  // `ResumenSaldoDeProveedor.tsx` → borrarla → este test tiene que fallar.
  it('un saldo negativo muestra el importe con signo y el callout "Saldo a favor."', () => {
    renderResumen(-500)
    expect(screen.getByText('-$500,00')).toBeInTheDocument()
    expect(screen.getByText('Saldo a favor.')).toBeInTheDocument()
  })

  it('siempre linkea al estado de cuenta completo del proveedor', () => {
    renderResumen(0, 42)
    expect(screen.getByRole('link', { name: 'Ver estado de cuenta completo' })).toHaveAttribute(
      'href',
      '/proveedores/42/cuenta-corriente',
    )
  })
})
