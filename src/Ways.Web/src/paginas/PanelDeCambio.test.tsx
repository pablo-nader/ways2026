import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { formatearValor, PanelDeCambio } from './PanelDeCambio'

// ---- formatearValor: helper puro, sin DOM (web-descriptor-tests) ------------------------------

describe('formatearValor', () => {
  it('undefined se muestra como —', () => {
    expect(formatearValor(undefined)).toBe('—')
  })

  it('null se muestra como el string "null" (distinto de "clave ausente")', () => {
    expect(formatearValor(null)).toBe('null')
  })

  it('un objeto/array se serializa con JSON.stringify', () => {
    expect(formatearValor({ id: 1 })).toBe('{"id":1}')
    expect(formatearValor([1, 2, 3])).toBe('[1,2,3]')
  })

  it('un valor primitivo (number/string/boolean) se convierte con String()', () => {
    expect(formatearValor(150)).toBe('150')
    expect(formatearValor('activo')).toBe('activo')
    expect(formatearValor(true)).toBe('true')
  })
})

// ---- PanelDeCambio: componente, judgment-day ronda 1 finding 5 (0 tests previos) --------------

describe('PanelDeCambio', () => {
  it('una clave agregada (valorAnterior null) muestra — del lado anterior y el valor real del lado nuevo', () => {
    render(<PanelDeCambio valorAnterior={null} valorNuevo={{ nombre: 'Artículo nuevo' }} />)

    expect(screen.getByTestId('panel-cambio-anterior-nombre')).toHaveTextContent('—')
    expect(screen.getByTestId('panel-cambio-nuevo-nombre')).toHaveTextContent('Artículo nuevo')
  })

  it('una clave modificada muestra AMBOS valores reales y distintos, nunca el mismo lado repetido', () => {
    render(<PanelDeCambio valorAnterior={{ monto: 100 }} valorNuevo={{ monto: 150 }} />)

    const celdaAnterior = screen.getByTestId('panel-cambio-anterior-monto')
    const celdaNuevo = screen.getByTestId('panel-cambio-nuevo-monto')

    expect(celdaAnterior).toHaveTextContent('100')
    expect(celdaNuevo).toHaveTextContent('150')
    expect(celdaAnterior.textContent).not.toBe(celdaNuevo.textContent)
  })

  it('una clave quitada (valor undefined del lado nuevo) muestra — del lado nuevo, valor real del lado anterior', () => {
    render(<PanelDeCambio valorAnterior={{ campo: 'valor viejo' }} valorNuevo={{ campo: undefined }} />)

    expect(screen.getByTestId('panel-cambio-anterior-campo')).toHaveTextContent('valor viejo')
    expect(screen.getByTestId('panel-cambio-nuevo-campo')).toHaveTextContent('—')
  })

  it('una clave sin cambios muestra el mismo valor en ambos lados', () => {
    render(<PanelDeCambio valorAnterior={{ estado: 'activo' }} valorNuevo={{ estado: 'activo' }} />)

    expect(screen.getByTestId('panel-cambio-anterior-estado')).toHaveTextContent('activo')
    expect(screen.getByTestId('panel-cambio-nuevo-estado')).toHaveTextContent('activo')
  })
})
