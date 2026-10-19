export const DIAS_DE_LA_SEMANA = ['Lunes', 'Martes', 'Miércoles', 'Jueves', 'Viernes', 'Sábado', 'Domingo'] as const
export const HORAS_DEL_DIA = Array.from({ length: 24 }, (_, hora) => hora)

type CeldaConPosicion = { diaDeLaSemana: number; hora: number }

/** Matriz de 7 filas (lunes a domingo) × 24 columnas. Las celdas que faltan o traen un valor no finito quedan en 0. */
export function armarMatriz<T extends CeldaConPosicion>(celdas: readonly T[], valorDe: (celda: T) => number): number[][] {
  const matriz = DIAS_DE_LA_SEMANA.map(() => HORAS_DEL_DIA.map(() => 0))
  for (const celda of celdas) {
    const fila = matriz[celda.diaDeLaSemana - 1]
    if (fila === undefined || celda.hora < 0 || celda.hora > 23) continue
    const valor = valorDe(celda)
    fila[celda.hora] = Number.isFinite(valor) ? valor : 0
  }
  return matriz
}

function redondear(valor: number): number {
  return Math.round(valor * 100) / 100
}

export function totalesPorFila(matriz: readonly (readonly number[])[]): number[] {
  return matriz.map((fila) => redondear(fila.reduce((suma, valor) => suma + valor, 0)))
}

export function totalesPorColumna(matriz: readonly (readonly number[])[]): number[] {
  return HORAS_DEL_DIA.map((hora) => redondear(matriz.reduce((suma, fila) => suma + (fila[hora] ?? 0), 0)))
}

/** Mayor valor positivo de la matriz; 0 si no hay ninguno (todo en cero o negativo). */
export function maximoDeLaMatriz(matriz: readonly (readonly number[])[]): number {
  let maximo = 0
  for (const fila of matriz) for (const valor of fila) if (valor > maximo) maximo = valor
  return maximo
}

/** Posición del valor en la escala, de 0 a 1. Cero, negativos (las notas de crédito pueden superar a las ventas) y
 * un máximo no positivo no tienen intensidad. */
export function intensidadDeCelda(valor: number, maximo: number): number {
  if (!Number.isFinite(valor) || !Number.isFinite(maximo) || valor <= 0 || maximo <= 0) return 0
  return Math.min(1, valor / maximo)
}

/** Tope del alfa: con el color de texto del tema (claro u oscuro) el contraste se mantiene legible en los dos. */
const ALFA_MINIMO = 0.1
const ALFA_MAXIMO = 0.6

/** Naranja de marca con alfa creciente sobre la superficie del tema; sin intensidad no pinta nada. */
export function colorDeCelda(valor: number, maximo: number): string {
  const intensidad = intensidadDeCelda(valor, maximo)
  if (intensidad === 0) return 'transparent'
  const alfa = ALFA_MINIMO + (ALFA_MAXIMO - ALFA_MINIMO) * intensidad
  return `rgba(247, 148, 29, ${alfa.toFixed(3)})`
}

function aHoraDelDia(hora: number): string {
  return `${String(hora % 24).padStart(2, '0')}:00`
}

/** Rótulo accesible de una celda: "Lunes 10:00–11:00: $ 200,00". */
export function rotuloDeCelda(indiceDeDia: number, hora: number, valorFormateado: string): string {
  return `${DIAS_DE_LA_SEMANA[indiceDeDia]} ${aHoraDelDia(hora)}–${aHoraDelDia(hora + 1)}: ${valorFormateado}`
}
