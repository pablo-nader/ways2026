import { CartesianGrid, Legend, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import type { PuntoDeGraficoMultiple, SerieDeGrafico } from './series'

type Props = {
  data: readonly PuntoDeGraficoMultiple[]
  series: readonly SerieDeGrafico[]
  alto: number
  /** Nombre accesible del gráfico (`role="img"` + `aria-label`), mismo criterio que `GraficoDeLineas`. */
  titulo: string
  /** Formatea los valores del eje y del tooltip (p. ej. como moneda). */
  formatearValor?: (valor: number) => string
}

/**
 * Líneas con varias series sobre un mismo eje x. Como `GraficoDeLineas`, es el único punto de entrada a `recharts`
 * para este caso: el consumidor pasa `data` ya mapeada (`series.ts`) y la lista de series, nunca props crudas.
 */
export function GraficoDeLineasMultiples({ data, series, alto, titulo, formatearValor }: Props) {
  return (
    <div role="img" aria-label={titulo}>
      <ResponsiveContainer width="100%" height={alto}>
        <LineChart data={data as PuntoDeGraficoMultiple[]}>
          <CartesianGrid strokeDasharray="3 3" stroke="var(--bs-border-color)" />
          <XAxis dataKey="etiqueta" stroke="var(--bs-border-color)" tick={{ fill: 'var(--bs-secondary-color)' }} />
          <YAxis
            stroke="var(--bs-border-color)"
            tick={{ fill: 'var(--bs-secondary-color)' }}
            tickFormatter={formatearValor}
            width={90}
          />
          <Tooltip
            formatter={formatearValor === undefined ? undefined : (valor) => formatearValor(Number(valor))}
            contentStyle={{
              background: 'var(--bs-body-bg)',
              borderColor: 'var(--bs-border-color)',
              color: 'var(--bs-body-color)',
            }}
            labelStyle={{ color: 'var(--bs-body-color)' }}
          />
          <Legend />
          {series.map((serie) => (
            <Line
              key={serie.clave}
              type="monotone"
              dataKey={serie.clave}
              name={serie.nombre}
              stroke={serie.color}
              strokeDasharray={serie.punteada ? '5 4' : undefined}
              dot={false}
            />
          ))}
        </LineChart>
      </ResponsiveContainer>
    </div>
  )
}
