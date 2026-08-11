import { Bar, BarChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import type { PuntoDeGrafico } from './series'

type Props = {
  data: readonly PuntoDeGrafico[]
  alto: number
  /** Nombre accesible del gráfico (`role="img"` + `aria-label`) — mismo criterio que
   * `GraficoDeLineas`: el SVG de `recharts` no trae texto propio para un lector de pantalla. */
  titulo: string
}

/**
 * Único punto de entrada a `recharts` para breakdowns por dimensión (barras).
 * Mismas reglas que `GraficoDeLineas`: solo `data` + `alto`, sin props
 * crudas de `recharts` (decisión de diseño 11).
 */
export function GraficoDeBarras({ data, alto, titulo }: Props) {
  return (
    <div role="img" aria-label={titulo}>
      <ResponsiveContainer width="100%" height={alto}>
        <BarChart data={data as PuntoDeGrafico[]}>
          <CartesianGrid strokeDasharray="3 3" />
          <XAxis dataKey="etiqueta" />
          <YAxis />
          <Tooltip />
          <Bar dataKey="valor" fill="#0d6efd" />
        </BarChart>
      </ResponsiveContainer>
    </div>
  )
}
