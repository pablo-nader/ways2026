import { CartesianGrid, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import type { PuntoDeGrafico } from './series'

type Props = {
  data: readonly PuntoDeGrafico[]
  alto: number
}

/**
 * Único punto de entrada a `recharts` para series temporales de línea. No
 * reexpone props crudas de `recharts` — solo `data` (ya mapeada por
 * `series.ts`) y `alto` explícito, para que ningún consumidor dependa de la
 * API de la librería subyacente (decisión de diseño 11).
 */
export function GraficoDeLineas({ data, alto }: Props) {
  return (
    <ResponsiveContainer width="100%" height={alto}>
      <LineChart data={data as PuntoDeGrafico[]}>
        <CartesianGrid strokeDasharray="3 3" />
        <XAxis dataKey="etiqueta" />
        <YAxis />
        <Tooltip />
        <Line type="monotone" dataKey="valor" stroke="#0d6efd" dot={false} />
      </LineChart>
    </ResponsiveContainer>
  )
}
