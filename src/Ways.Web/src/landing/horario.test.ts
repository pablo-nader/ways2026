import { describe, expect, it } from 'vitest'

import { atiendeLasVeinticuatroHoras, textoDelSello } from './horario'

describe('atiendeLasVeinticuatroHoras', () => {
  it.each([
    '24 hs',
    'Abierto 24hs',
    'Todos los días 24 horas',
    'Lun a Dom: 24 H',
    'Atención 24/7',
    '24x7',
    'Las 24 hs, todo el año',
  ])('%j atiende las 24 horas', (horario) => {
    expect(atiendeLasVeinticuatroHoras(horario)).toBe(true)
  })

  it.each([
    'Lun a Vie 9 a 18',
    'Lun a Sáb 8 a 24 hs',
    'Lun a Sáb de 9 - 24 hs',
    'Hasta las 24 hs',
    'Hasta 24 hs',
    '10:24 hs',
    'Local 124 hs',
    'Abierto 24',
    '',
  ])('%j no es de 24 horas', (horario) => {
    // `Hasta las 24 hs` y `Hasta 24 hs` son cierres a medianoche; "Abierto 24" sin unidad es ambiguo y no se asume.
    expect(atiendeLasVeinticuatroHoras(horario)).toBe(false)
  })
})

describe('textoDelSello', () => {
  it('un horario de 24 horas es "Abierto 24 hs"', () => {
    expect(textoDelSello('Todos los días, 24 horas')).toBe('Abierto 24 hs')
  })

  it('cualquier otro horario sale como lo cargó el comercio, sin los espacios de los bordes', () => {
    expect(textoDelSello('  Lun a Vie 9 a 18\nSáb 9 a 13 ')).toBe('Lun a Vie 9 a 18\nSáb 9 a 13')
  })
})
