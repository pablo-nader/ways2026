import { render, screen } from '@testing-library/react'
import type { ReactNode } from 'react'
import { describe, expect, it, vi } from 'vitest'
import type { PuntoDeGrafico } from './series'

// `recharts` se mockea por completo: bajo jsdom `ResponsiveContainer` mide 0 y no
// renderiza nada, así que el test no puede depender de su layout real. El stub
// serializa la prop `data` en un `data-testid` para que la aserción quede sobre el
// mapeo del wrapper, nunca sobre el render de la librería (design.md, Web Composition).
vi.mock('recharts', () => ({
  ResponsiveContainer: ({ children, height }: { children: ReactNode; height: number }) => (
    <div data-testid="responsive-container" data-alto={height}>
      {children}
    </div>
  ),
  LineChart: ({ data, children }: { data: PuntoDeGrafico[]; children: ReactNode }) => (
    <div data-testid="line-chart" data-serie={JSON.stringify(data)}>
      {children}
    </div>
  ),
  Line: () => null,
  XAxis: () => null,
  YAxis: () => null,
  Tooltip: () => null,
  CartesianGrid: () => null,
}))

const { GraficoDeLineas } = await import('./GraficoDeLineas')

describe('GraficoDeLineas', () => {
  it('pasa la data mapeada al LineChart sin alterarla', () => {
    const data: PuntoDeGrafico[] = [
      { etiqueta: 'lun', valor: 100 },
      { etiqueta: 'mar', valor: 200 },
    ]

    render(<GraficoDeLineas data={data} alto={240} titulo="Ventas" />)

    const chart = screen.getByTestId('line-chart')
    expect(JSON.parse(chart.dataset.serie ?? '[]')).toEqual(data)
  })

  it('propaga el alto explícito al contenedor responsive', () => {
    render(<GraficoDeLineas data={[]} alto={180} titulo="Ventas" />)

    expect(screen.getByTestId('responsive-container')).toHaveAttribute('data-alto', '180')
  })

  it('renderiza sin datos sin crashear', () => {
    render(<GraficoDeLineas data={[]} alto={200} titulo="Ventas" />)

    expect(JSON.parse(screen.getByTestId('line-chart').dataset.serie ?? 'null')).toEqual([])
  })

  it('expone un nombre accesible para lectores de pantalla', () => {
    render(<GraficoDeLineas data={[]} alto={200} titulo="Ventas de los últimos 7 días" />)

    expect(screen.getByRole('img', { name: 'Ventas de los últimos 7 días' })).toBeInTheDocument()
  })
})
