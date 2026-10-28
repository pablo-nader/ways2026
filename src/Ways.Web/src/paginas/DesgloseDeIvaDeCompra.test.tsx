import { fireEvent, render, screen, within } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { DesgloseDeIvaDeCompra } from './DesgloseDeIvaDeCompra'

const nombres = { 1: '21%', 2: 'Exento' }

describe('DesgloseDeIvaDeCompra', () => {
  it('ofrece el IVA editable en una alícuota con porcentaje pero no en una de 0%', () => {
    render(
      <DesgloseDeIvaDeCompra
        filas={[
          { idAlicuotaIva: 1, porcentaje: 21, neto: 1000, iva: 210, ivaCalculado: 210 },
          { idAlicuotaIva: 2, porcentaje: 0, neto: 300, iva: 0, ivaCalculado: 0 },
        ]}
        nombrePorAlicuota={nombres}
        onCambiarIva={vi.fn()}
      />,
    )

    expect(screen.getByLabelText('IVA impreso 21%')).toBeInTheDocument()
    expect(screen.queryByLabelText('IVA impreso Exento')).not.toBeInTheDocument()
    const fila = screen.getByText('Exento').closest('tr') as HTMLElement
    expect(within(fila).getByText('$ 0,00')).toBeInTheDocument()
  })

  it('sin permiso de costos muestra la alícuota con los importes ocultos', () => {
    render(
      <DesgloseDeIvaDeCompra filas={[{ idAlicuotaIva: 1, porcentaje: 21, neto: null, iva: null }]} nombrePorAlicuota={nombres} />,
    )

    const fila = screen.getByText('21%').closest('tr') as HTMLElement
    expect(within(fila).getAllByText('—')).toHaveLength(2)
  })
})

describe('DesgloseDeIvaDeCompra — orden por columna', () => {
  function alicuotasEnPantalla(): string[] {
    return screen
      .getAllByRole('row')
      .slice(1)
      .map((fila) => fila.querySelector('td')?.textContent ?? '')
  }

  it('el neto ordena por su valor numérico y el tercer clic vuelve al orden original', () => {
    render(
      <DesgloseDeIvaDeCompra
        filas={[
          { idAlicuotaIva: 1, porcentaje: 21, neto: 1000, iva: 210 },
          { idAlicuotaIva: 2, porcentaje: 0, neto: 300, iva: 0 },
          { idAlicuotaIva: 3, porcentaje: 10.5, neto: 20000, iva: 2100 },
        ]}
        nombrePorAlicuota={{ 1: '21%', 2: 'Exento', 3: '10,5%' }}
      />,
    )

    const neto = screen.getByRole('columnheader', { name: /Neto/ })
    fireEvent.click(within(neto).getByRole('button'))
    expect(neto).toHaveAttribute('aria-sort', 'ascending')
    expect(alicuotasEnPantalla()).toEqual(['Exento', '21%', '10,5%'])

    fireEvent.click(within(neto).getByRole('button'))
    expect(alicuotasEnPantalla()).toEqual(['10,5%', '21%', 'Exento'])

    fireEvent.click(within(neto).getByRole('button'))
    expect(alicuotasEnPantalla()).toEqual(['21%', 'Exento', '10,5%'])
  })

  it('la columna de la alícuota ordena por el porcentaje y no por el nombre mostrado', () => {
    render(
      <DesgloseDeIvaDeCompra
        filas={[
          { idAlicuotaIva: 1, porcentaje: 21, neto: 1000, iva: 210 },
          { idAlicuotaIva: 3, porcentaje: 10.5, neto: 20000, iva: 2100 },
          { idAlicuotaIva: 2, porcentaje: 0, neto: 300, iva: 0 },
        ]}
        nombrePorAlicuota={{ 1: 'IVA 21%', 2: 'Exento', 3: 'IVA 10,5%' }}
      />,
    )

    fireEvent.click(within(screen.getByRole('columnheader', { name: /Alícuota/ })).getByRole('button'))

    expect(alicuotasEnPantalla()).toEqual(['Exento', 'IVA 10,5%', 'IVA 21%'])
  })

  it('en el editor (con onCambiarIva) los encabezados no ordenan y las filas conservan el orden de carga', () => {
    render(
      <DesgloseDeIvaDeCompra
        filas={[
          { idAlicuotaIva: 1, porcentaje: 21, neto: 1000, iva: 210 },
          { idAlicuotaIva: 3, porcentaje: 10.5, neto: 20000, iva: 2100 },
        ]}
        nombrePorAlicuota={{ 1: '21%', 3: '10,5%' }}
        onCambiarIva={vi.fn()}
      />,
    )

    expect(screen.getAllByRole('columnheader').filter((th) => within(th).queryByRole('button'))).toHaveLength(0)
    expect(alicuotasEnPantalla()).toEqual(['21%', '10,5%'])
  })
})
