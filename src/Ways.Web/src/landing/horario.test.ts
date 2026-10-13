import { describe, expect, it } from 'vitest'

import { atiendeLasVeinticuatroHoras, textoDelSello } from './horario'

describe('atiendeLasVeinticuatroHoras', () => {
  it.each([
    '24 hs',
    '24hs',
    '24 h',
    '24 horas',
    'Abierto 24 hs',
    'Abierto las 24 hs',
    'Abierto 24 horas',
    'Abierto las 24 horas',
    'Las 24 horas',
    '24/7',
    '24x7',
    'Todos los días 24 hs',
    'Todos los días, 24 horas',
    'Abierto las 24 horas, todos los días',
    'Abierto las 24 horas todos los días',
    '24 hs.',
    '  24 HS  ',
    'Abierto  las   24 horas.',
  ])('%j es el horario completo de 24 horas', (horario) => {
    expect(atiendeLasVeinticuatroHoras(horario)).toBe(true)
  })

  it.each([
    'Lun a Vie 9 a 18',
    'Lun a Sáb 8 a 24 hs',
    'Lun a Sáb de 9 - 24 hs',
    '9 hs a 24 hs',
    'De 8 a las 24 hs',
    '8hs-24hs',
    'Hasta las 24 hs',
    'Hasta 24 hs',
    'Lun a Vie 8 a 18 hs. Guardia 24 hs',
    'Delivery 24 hs',
    'Lun a Sáb 24 hs, Dom cerrado',
    'Lun a Dom: 24 H',
    'Todos los días 24 hs excepto feriados',
    'Atención 24/7 por teléfono',
    'Las 24 hs, todo el año',
    '10:24 hs',
    'Local 124 hs',
    'Abierto 24',
    '24',
    '',
  ])('%j no es un horario completo de 24 horas', (horario) => {
    expect(atiendeLasVeinticuatroHoras(horario)).toBe(false)
  })
})

describe('textoDelSello', () => {
  it('un horario que es solo "24 horas" sale como "Abierto 24 hs"', () => {
    expect(textoDelSello('Todos los días, 24 horas')).toBe('Abierto 24 hs')
  })

  it('un horario mixto sale como lo cargó el comercio, sin afirmar que abre las 24 horas', () => {
    expect(textoDelSello('Lun a Vie 8 a 18 hs. Guardia 24 hs')).toBe('Lun a Vie 8 a 18 hs. Guardia 24 hs')
    expect(textoDelSello('Lun a Sáb 24 hs, Dom cerrado')).toBe('Lun a Sáb 24 hs, Dom cerrado')
  })

  it('cualquier otro horario sale como lo cargó el comercio, sin los espacios de los bordes', () => {
    expect(textoDelSello('  Lun a Vie 9 a 18\nSáb 9 a 13 ')).toBe('Lun a Vie 9 a 18\nSáb 9 a 13')
  })
})
