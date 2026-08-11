import { render, screen } from '@testing-library/react'
import type { ReactNode } from 'react'
import { describe, expect, it, vi } from 'vitest'
import type { PuntoDeGrafico } from './series'

// Mismo criterio que `GraficoDeLineas.test.tsx`: `recharts` se stubea por completo,
// la aserción vive sobre la prop `data` que el wrapper le pasa a `BarChart`.
vi.mock('recharts', () => ({
  ResponsiveContainer: ({ children, height }: { children: ReactNode; height: number }) => (
    <div data-testid="responsive-container" data-alto={height}>
      {children}
    </div>
  ),
  BarChart: ({ data, children }: { data: PuntoDeGrafico[]; children: ReactNode }) => (
    <div data-testid="bar-chart" data-serie={JSON.stringify(data)}>
      {children}
    </div>
  ),
  Bar: () => null,
  XAxis: () => null,
  YAxis: () => null,
  Tooltip: () => null,
  CartesianGrid: () => null,
}))

const { GraficoDeBarras } = await import('./GraficoDeBarras')

describe('GraficoDeBarras', () => {
  it('pasa la data mapeada al BarChart sin alterarla', () => {
    const data: PuntoDeGrafico[] = [
      { etiqueta: 'PV Centro', valor: 1500 },
      { etiqueta: 'PV Norte', valor: 900 },
    ]

    render(<GraficoDeBarras data={data} alto={240} titulo="Ventas por punto de venta" />)

    const chart = screen.getByTestId('bar-chart')
    expect(JSON.parse(chart.dataset.serie ?? '[]')).toEqual(data)
  })

  it('propaga el alto explícito al contenedor responsive', () => {
    render(<GraficoDeBarras data={[]} alto={160} titulo="Ventas por punto de venta" />)

    expect(screen.getByTestId('responsive-container')).toHaveAttribute('data-alto', '160')
  })

  it('renderiza sin datos sin crashear', () => {
    render(<GraficoDeBarras data={[]} alto={200} titulo="Ventas por punto de venta" />)

    expect(JSON.parse(screen.getByTestId('bar-chart').dataset.serie ?? 'null')).toEqual([])
  })

  it('expone un nombre accesible para lectores de pantalla', () => {
    render(<GraficoDeBarras data={[]} alto={200} titulo="Ventas por punto de venta" />)

    expect(screen.getByRole('img', { name: 'Ventas por punto de venta' })).toBeInTheDocument()
  })
})
