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
          <Bar dataKey="valor" fill="#f7941d" />
        </BarChart>
      </ResponsiveContainer>
    </div>
  )
}
