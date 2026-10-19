import {
  colorDeCelda,
  DIAS_DE_LA_SEMANA,
  HORAS_DEL_DIA,
  maximoDeLaMatriz,
  rotuloDeCelda,
  totalesPorColumna,
  totalesPorFila,
} from './mapaDeCalorEscala'

type Props = {
  /** 7 filas (lunes a domingo) × 24 columnas (una por hora). */
  valores: readonly (readonly number[])[]
  /** Texto de cada celda y de los totales (puede redondear para que entren 24 columnas). */
  formatearCelda: (valor: number) => string
  /** Valor completo que se anuncia en el `title` y el `aria-label` de cada celda. */
  formatearDetalle: (valor: number) => string
  /** Nombre accesible de la tabla. */
  titulo: string
}

/**
 * Tabla día de la semana × hora con el número en cada celda sobre un fondo cuya intensidad sigue al valor
 * (relativo al máximo de las celdas, sin contar los totales). Es una `<table>` HTML y no un canvas, para que un
 * lector de pantalla recorra filas y columnas; el scroll horizontal queda adentro de `.table-responsive`.
 * Los totales por día (columna final) y por hora (fila final) suman las celdas ya mostradas.
 */
export function MapaDeCalor({ valores, formatearCelda, formatearDetalle, titulo }: Props) {
  const maximo = maximoDeLaMatriz(valores)
  const porFila = totalesPorFila(valores)
  const porColumna = totalesPorColumna(valores)
  const totalGeneral = porFila.reduce((suma, valor) => suma + valor, 0)

  return (
    <div className="table-responsive">
      <table className="table table-sm table-bordered align-middle text-center mb-0" aria-label={titulo}>
        <thead>
          <tr>
            <th scope="col" className="text-start">
              Día
            </th>
            {HORAS_DEL_DIA.map((hora) => (
              <th key={hora} scope="col">
                {String(hora).padStart(2, '0')}
              </th>
            ))}
            <th scope="col">Total</th>
          </tr>
        </thead>
        <tbody>
          {DIAS_DE_LA_SEMANA.map((dia, indiceDeDia) => (
            <tr key={dia}>
              <th scope="row" className="text-start text-nowrap">
                {dia}
              </th>
              {HORAS_DEL_DIA.map((hora) => {
                const valor = valores[indiceDeDia]?.[hora] ?? 0
                const rotulo = rotuloDeCelda(indiceDeDia, hora, formatearDetalle(valor))
                return (
                  <td
                    key={hora}
                    title={rotulo}
                    aria-label={rotulo}
                    className="small text-nowrap"
                    style={{ backgroundColor: colorDeCelda(valor, maximo) }}
                  >
                    {formatearCelda(valor)}
                  </td>
                )
              })}
              <td className="small text-nowrap fw-bold">{formatearCelda(porFila[indiceDeDia])}</td>
            </tr>
          ))}
        </tbody>
        <tfoot>
          <tr className="fw-bold">
            <th scope="row" className="text-start">
              Total
            </th>
            {porColumna.map((valor, hora) => (
              <td key={HORAS_DEL_DIA[hora]} className="small text-nowrap">
                {formatearCelda(valor)}
              </td>
            ))}
            <td className="small text-nowrap">{formatearCelda(totalGeneral)}</td>
          </tr>
        </tfoot>
      </table>
    </div>
  )
}
