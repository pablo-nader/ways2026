import { describe, expect, it } from 'vitest'
import { costoDeListaDeAltaRapida } from './prefillAltaRapidaArticulo'

describe('costoDeListaDeAltaRapida', () => {
  it('redondea a 2 decimales el costo de la línea, que admite 4', () => {
    expect(costoDeListaDeAltaRapida(12.3456)).toBe(12.35)
    expect(costoDeListaDeAltaRapida(12.344)).toBe(12.34)
  })

  it('deja intacto un costo ya de 2 decimales y respeta la ausencia de costo', () => {
    expect(costoDeListaDeAltaRapida(100.5)).toBe(100.5)
    expect(costoDeListaDeAltaRapida(null)).toBeNull()
  })
})
