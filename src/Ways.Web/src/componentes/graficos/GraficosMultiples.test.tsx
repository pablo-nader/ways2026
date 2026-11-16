import { render, screen } from '@testing-library/react'
import type { ReactNode } from 'react'
import { describe, expect, it, vi } from 'vitest'
import type { PuntoDeGraficoMultiple, SerieDeGrafico } from './series'

// Mismo criterio que `GraficoDeLineas.test.tsx`: `recharts` se stubea por completo y las aserciones caen sobre lo
// que el wrapper le entrega (data y series), nunca sobre el render real de la librería.
vi.mock('recharts', () => ({
  ResponsiveContainer: ({ children }: { children: ReactNode }) => <div>{children}</div>,
  LineChart: ({ data, children }: { data: PuntoDeGraficoMultiple[]; children: ReactNode }) => (
    <div data-testid="line-chart" data-serie={JSON.stringify(data)}>
      {children}
    </div>
  ),
  BarChart: ({ data, children }: { data: PuntoDeGraficoMultiple[]; children: ReactNode }) => (
    <div data-testid="bar-chart" data-serie={JSON.stringify(data)}>
      {children}
    </div>
  ),
  Line: ({ dataKey, name, strokeDasharray }: { dataKey: string; name: string; strokeDasharray?: string }) => (
    <span data-testid="linea" data-clave={dataKey} data-punteada={strokeDasharray ?? ''}>
      {name}
    </span>
  ),
  Bar: ({ dataKey, name, stackId }: { dataKey: string; name: string; stackId: string }) => (
    <span data-testid="barra" data-clave={dataKey} data-apilado={stackId}>
      {name}
    </span>
  ),
  XAxis: () => null,
  YAxis: () => null,
  Tooltip: () => null,
  Legend: () => null,
  CartesianGrid: () => null,
}))

const { GraficoDeLineasMultiples } = await import('./GraficoDeLineasMultiples')
const { GraficoDeBarrasApiladas } = await import('./GraficoDeBarrasApiladas')

const data: PuntoDeGraficoMultiple[] = [
  { etiqueta: '10/2026', a: 10, b: 5 },
  { etiqueta: '11/2026', a: 12, b: 4 },
]
const series: SerieDeGrafico[] = [
  { clave: 'a', nombre: 'Serie A', color: '#111' },
  { clave: 'b', nombre: 'Serie B', color: '#222', punteada: true },
]

describe('GraficoDeLineasMultiples', () => {
  it('entrega los puntos a recharts y dibuja una línea por serie', () => {
    render(<GraficoDeLineasMultiples data={data} series={series} alto={300} titulo="Evolución" />)

    expect(screen.getByRole('img', { name: 'Evolución' })).toBeInTheDocument()
    expect(JSON.parse(screen.getByTestId('line-chart').getAttribute('data-serie') ?? '[]')).toEqual(data)
    const lineas = screen.getAllByTestId('linea')
    expect(lineas.map((l) => l.getAttribute('data-clave'))).toEqual(['a', 'b'])
    expect(lineas.map((l) => l.textContent)).toEqual(['Serie A', 'Serie B'])
    expect(lineas[0].getAttribute('data-punteada')).toBe('')
    expect(lineas[1].getAttribute('data-punteada')).not.toBe('')
  })
})

describe('GraficoDeBarrasApiladas', () => {
  it('dibuja una barra por serie, todas en la misma pila', () => {
    render(<GraficoDeBarrasApiladas data={data} series={series} alto={300} titulo="Composición" />)

    expect(screen.getByRole('img', { name: 'Composición' })).toBeInTheDocument()
    expect(JSON.parse(screen.getByTestId('bar-chart').getAttribute('data-serie') ?? '[]')).toEqual(data)
    const barras = screen.getAllByTestId('barra')
    expect(barras.map((b) => b.getAttribute('data-clave'))).toEqual(['a', 'b'])
    expect(new Set(barras.map((b) => b.getAttribute('data-apilado'))).size).toBe(1)
  })
})
