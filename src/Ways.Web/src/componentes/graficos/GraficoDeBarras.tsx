import { Bar, BarChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import type { PuntoDeGrafico } from './series'

type Props = {
  data: readonly PuntoDeGrafico[]
  alto: number
}

/**
 * Único punto de entrada a `recharts` para breakdowns por dimensión (barras).
 * Mismas reglas que `GraficoDeLineas`: solo `data` + `alto`, sin props
 * crudas de `recharts` (decisión de diseño 11).
 */
export function GraficoDeBarras({ data, alto }: Props) {
  return (
    <ResponsiveContainer width="100%" height={alto}>
      <BarChart data={data as PuntoDeGrafico[]}>
        <CartesianGrid strokeDasharray="3 3" />
        <XAxis dataKey="etiqueta" />
        <YAxis />
        <Tooltip />
        <Bar dataKey="valor" fill="#0d6efd" />
      </BarChart>
    </ResponsiveContainer>
  )
}
