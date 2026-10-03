import { CartesianGrid, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import type { PuntoDeGrafico } from './series'

type Props = {
  data: readonly PuntoDeGrafico[]
  alto: number
  /** Nombre accesible del gráfico (`role="img"` + `aria-label`) — el SVG de `recharts` no trae
   * texto propio para un lector de pantalla, así que cada consumidor lo declara explícito. */
  titulo: string
}

/**
 * Único punto de entrada a `recharts` para series temporales de línea. No
 * reexpone props crudas de `recharts` — solo `data` (ya mapeada por
 * `series.ts`) y `alto` explícito, para que ningún consumidor dependa de la
 * API de la librería subyacente (decisión de diseño 11).
 */
export function GraficoDeLineas({ data, alto, titulo }: Props) {
  return (
    <div role="img" aria-label={titulo}>
      <ResponsiveContainer width="100%" height={alto}>
        <LineChart data={data as PuntoDeGrafico[]}>
          <CartesianGrid strokeDasharray="3 3" stroke="var(--bs-border-color)" />
          <XAxis dataKey="etiqueta" stroke="var(--bs-border-color)" tick={{ fill: 'var(--bs-secondary-color)' }} />
          <YAxis stroke="var(--bs-border-color)" tick={{ fill: 'var(--bs-secondary-color)' }} />
          <Tooltip
            contentStyle={{
              background: 'var(--bs-body-bg)',
              borderColor: 'var(--bs-border-color)',
              color: 'var(--bs-body-color)',
            }}
            labelStyle={{ color: 'var(--bs-body-color)' }}
          />
          <Line type="monotone" dataKey="valor" stroke="#f7941d" dot={false} />
        </LineChart>
      </ResponsiveContainer>
    </div>
  )
}
