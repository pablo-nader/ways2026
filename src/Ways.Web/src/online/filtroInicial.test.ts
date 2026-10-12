import { describe, expect, it } from 'vitest'

import { filtroInicialDeLaUrl } from './filtroInicial'

describe('filtroInicialDeLaUrl', () => {
  it('sin parámetros no hay filtro', () => {
    expect(filtroInicialDeLaUrl('')).toEqual({ buscar: '', idCategoria: null })
    expect(filtroInicialDeLaUrl('?otro=1')).toEqual({ buscar: '', idCategoria: null })
  })

  it('?categoria=<id> fija la categoría', () => {
    expect(filtroInicialDeLaUrl('?categoria=3')).toEqual({ buscar: '', idCategoria: 3 })
  })

  it('?buscar=<texto> fija la búsqueda, decodificada y sin espacios de más', () => {
    expect(filtroInicialDeLaUrl('?buscar=%20yerba%20mate%20')).toEqual({ buscar: 'yerba mate', idCategoria: null })
  })

  it('los dos juntos', () => {
    expect(filtroInicialDeLaUrl('?categoria=3&buscar=yerba')).toEqual({ buscar: 'yerba', idCategoria: 3 })
  })

  it.each(['0', '-2', '1.5', 'abc', '', '3x', '99999999999'])('una categoría %j no es un id y se ignora', (valor) => {
    expect(filtroInicialDeLaUrl(`?categoria=${valor}`).idCategoria).toBeNull()
  })

  it('una búsqueda de puros espacios se ignora y una muy larga se corta al tope del campo', () => {
    expect(filtroInicialDeLaUrl('?buscar=%20%20').buscar).toBe('')
    expect(filtroInicialDeLaUrl(`?buscar=${'a'.repeat(150)}`).buscar).toBe('a'.repeat(100))
  })
})
