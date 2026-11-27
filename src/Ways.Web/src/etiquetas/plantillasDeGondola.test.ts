import { describe, expect, it } from 'vitest'
import { IDS_DE_MODELO } from './modelosDeGondola'
import { PLANTILLAS_DE_GONDOLA, plantillaDeGondolaPorId, PLANTILLA_POR_DEFECTO_DE_GONDOLA } from './plantillasDeGondola'

describe('PLANTILLAS_DE_GONDOLA', () => {
  it('son 10', () => {
    expect(PLANTILLAS_DE_GONDOLA).toHaveLength(10)
  })

  it('ids y nombres no se repiten', () => {
    expect(new Set(PLANTILLAS_DE_GONDOLA.map((p) => p.id)).size).toBe(10)
    expect(new Set(PLANTILLAS_DE_GONDOLA.map((p) => p.nombre)).size).toBe(10)
  })

  it('cada una tiene un diseño propio: los 10 modelos, sin repetir', () => {
    expect(PLANTILLAS_DE_GONDOLA.map((p) => p.modelo).sort()).toEqual([...IDS_DE_MODELO].sort())
  })

  it('no tienen fondo: solo colores de tinta', () => {
    for (const plantilla of PLANTILLAS_DE_GONDOLA) {
      expect(Object.keys(plantilla.colores).sort()).toEqual(['acento', 'borde', 'precio', 'texto'])
    }
  })

  it('solo usan la paleta de la marca Ways y neutros oscuros', () => {
    const marca = ['#f7941d', '#e8830c', '#9a5200', '#3a2606', '#c4c4ca']
    const neutros = ['#000000', '#1c1c1f']
    for (const plantilla of PLANTILLAS_DE_GONDOLA) {
      for (const color of Object.values(plantilla.colores)) expect([...marca, ...neutros]).toContain(color)
      expect(Object.values(plantilla.colores).some((color) => marca.includes(color))).toBe(true)
    }
  })
})

describe('plantillaDeGondolaPorId', () => {
  it('encuentra por id', () => {
    expect(plantillaDeGondolaPorId('banderin').nombre).toBe('Banderín')
  })

  it('un id desconocido cae a la plantilla por defecto', () => {
    expect(plantillaDeGondolaPorId('no-existe')).toBe(PLANTILLA_POR_DEFECTO_DE_GONDOLA)
  })
})
