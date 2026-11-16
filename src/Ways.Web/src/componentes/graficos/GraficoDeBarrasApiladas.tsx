import { Bar, BarChart, CartesianGrid, Legend, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import type { PuntoDeGraficoMultiple, SerieDeGrafico } from './series'

type Props = {
  data: readonly PuntoDeGraficoMultiple[]
  series: readonly SerieDeGrafico[]
  alto: number
  /** Nombre accesible del gráfico (`role="img"` + `aria-label`), mismo criterio que `GraficoDeBarras`. */
  titulo: string
  /** Formatea los valores del eje y del tooltip (p. ej. como moneda). */
  formatearValor?: (valor: number) => string
}

/** Barras apiladas: cada serie es un tramo de la barra de su punto. Mismas reglas que `GraficoDeBarras`. */
export function GraficoDeBarrasApiladas({ data, series, alto, titulo, formatearValor }: Props) {
  return (
    <div role="img" aria-label={titulo}>
      <ResponsiveContainer width="100%" height={alto}>
        <BarChart data={data as PuntoDeGraficoMultiple[]}>
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
            <Bar key={serie.clave} dataKey={serie.clave} name={serie.nombre} stackId="apilado" fill={serie.color} />
          ))}
        </BarChart>
      </ResponsiveContainer>
    </div>
  )
}
