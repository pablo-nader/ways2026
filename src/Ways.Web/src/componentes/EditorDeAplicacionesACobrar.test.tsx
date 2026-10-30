import { cleanup, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { afterEach, describe, expect, it } from 'vitest'
import type { VentaPendienteDeCobro } from '../api/tipos'
import type { ImportesPorVenta } from './cobros/aplicaciones'
import { EditorDeAplicacionesACobrar } from './EditorDeAplicacionesACobrar'

afterEach(() => {
  cleanup()
})

function venta(id: number, saldoPendiente: number, fecha: string): VentaPendienteDeCobro {
  return {
    id,
    codigoTipo: 'FA',
    letra: 'B',
    numero: id,
    numeroVisible: `0001-0000000${id}`,
    fecha,
    parteEnCuentaCorriente: saldoPendiente,
    cobrado: 0,
    saldoPendiente,
  }
}

const VENTAS = [venta(2, 200, '2026-10-02T12:00:00Z'), venta(1, 100, '2026-10-01T12:00:00Z')]

function Anfitrion({ tope, inicial = {}, disabled = false }: { tope: number; inicial?: ImportesPorVenta; disabled?: boolean }) {
  const [importes, setImportes] = useState<ImportesPorVenta>(inicial)
  return (
    <EditorDeAplicacionesACobrar
      prefijoId="t"
      ventas={VENTAS}
      importes={importes}
      onCambiar={setImportes}
      tope={tope}
      disabled={disabled}
    />
  )
}

describe('EditorDeAplicacionesACobrar', () => {
  it('lista cada venta con su número y saldo, y arranca sin nada aplicado', () => {
    render(<Anfitrion tope={150} />)

    expect(screen.getByText('0001-00000001')).toBeInTheDocument()
    expect(screen.getByText('0001-00000002')).toBeInTheDocument()
    expect(screen.getByTestId('t-aplicado')).toHaveTextContent('$ 0,00')
    expect(screen.getByTestId('t-restante')).toHaveTextContent('$ 150,00')
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('«Aplicar a las más viejas» reparte el tope desde la venta más vieja y actualiza el resumen', async () => {
    render(<Anfitrion tope={150} />)

    await userEvent.click(screen.getByRole('button', { name: 'Aplicar a las más viejas' }))

    expect(screen.getByLabelText('Importe a aplicar a la venta 0001-00000001')).toHaveValue('100,00')
    expect(screen.getByLabelText('Importe a aplicar a la venta 0001-00000002')).toHaveValue('50,00')
    expect(screen.getByTestId('t-aplicado')).toHaveTextContent('$ 150,00')
    expect(screen.getByTestId('t-restante')).toHaveTextContent('$ 0,00')
  })

  it('el resumen muestra lo que queda a favor cuando el tope supera lo aplicado', async () => {
    render(<Anfitrion tope={500} />)

    await userEvent.click(screen.getByRole('button', { name: 'Aplicar a las más viejas' }))

    expect(screen.getByTestId('t-aplicado')).toHaveTextContent('$ 300,00')
    expect(within(screen.getByTestId('t-editor')).getByText('A favor')).toBeInTheDocument()
    expect(screen.getByTestId('t-restante')).toHaveTextContent('$ 200,00')
  })

  it('un importe mayor al saldo de la venta muestra el motivo', async () => {
    render(<Anfitrion tope={1000} />)

    await userEvent.type(screen.getByLabelText('Importe a aplicar a la venta 0001-00000001'), '150')

    expect(screen.getByRole('alert')).toHaveTextContent('saldo pendiente')
  })

  it('una suma mayor al tope muestra el motivo', async () => {
    render(<Anfitrion tope={120} inicial={{ 1: 100, 2: 50 }} />)

    expect(screen.getByRole('alert')).toHaveTextContent('supera el importe disponible')
    expect(screen.getByTestId('t-restante')).toHaveTextContent('-$ 30,00')
  })

  it('deshabilitado inhabilita los importes y el botón de reparto', () => {
    render(<Anfitrion tope={150} disabled />)

    expect(screen.getByRole('button', { name: 'Aplicar a las más viejas' })).toBeDisabled()
    expect(screen.getByLabelText('Importe a aplicar a la venta 0001-00000001')).toBeDisabled()
  })

  it('sin tope el botón de reparto queda inhabilitado', () => {
    render(<Anfitrion tope={0} />)

    expect(screen.getByRole('button', { name: 'Aplicar a las más viejas' })).toBeDisabled()
  })
})
