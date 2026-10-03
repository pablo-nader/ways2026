import { render, screen, within } from '@testing-library/react'
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
