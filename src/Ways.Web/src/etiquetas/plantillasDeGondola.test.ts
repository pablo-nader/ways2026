import { describe, expect, it } from 'vitest'
import { PLANTILLAS_DE_GONDOLA, plantillaDeGondolaPorId, PLANTILLA_POR_DEFECTO_DE_GONDOLA, rutaDelLogoDeGondola } from './plantillasDeGondola'

describe('PLANTILLAS_DE_GONDOLA', () => {
  it('son 10: 5 claras y 5 oscuras', () => {
    expect(PLANTILLAS_DE_GONDOLA).toHaveLength(10)
    expect(PLANTILLAS_DE_GONDOLA.filter((p) => p.tono === 'claro')).toHaveLength(5)
    expect(PLANTILLAS_DE_GONDOLA.filter((p) => p.tono === 'oscuro')).toHaveLength(5)
  })

  it('ids y nombres no se repiten', () => {
    expect(new Set(PLANTILLAS_DE_GONDOLA.map((p) => p.id)).size).toBe(10)
    expect(new Set(PLANTILLAS_DE_GONDOLA.map((p) => p.nombre)).size).toBe(10)
  })

  it('ninguna repite la combinación de fondo y precio de otra', () => {
    expect(new Set(PLANTILLAS_DE_GONDOLA.map((p) => `${p.colores.fondo}|${p.colores.precio}|${p.decoracion}`)).size).toBe(10)
  })

  it('solo usan la paleta de la marca Ways y neutros', () => {
    const marca = ['#f7941d', '#e8830c', '#fdebd3', '#9a5200', '#3a2606', '#f9ab4a', '#c4c4ca']
    const neutros = ['#ffffff', '#000000', '#f4f4f6', '#1c1c1f', '#111113', '#232326', '#2c2c31', '#3a3a40']
    for (const plantilla of PLANTILLAS_DE_GONDOLA) {
      for (const color of Object.values(plantilla.colores)) expect([...marca, ...neutros]).toContain(color)
      expect(Object.values(plantilla.colores).some((color) => marca.includes(color))).toBe(true)
    }
  })

  it('todos los colores son hexadecimales', () => {
    for (const plantilla of PLANTILLAS_DE_GONDOLA) {
      for (const color of Object.values(plantilla.colores)) expect(color).toMatch(/^#[0-9a-f]{6}$/i)
    }
  })
})

describe('plantillaDeGondolaPorId', () => {
  it('encuentra por id', () => {
    expect(plantillaDeGondolaPorId('ambar').nombre).toBe('Ámbar')
  })

  it('un id desconocido cae a la plantilla por defecto', () => {
    expect(plantillaDeGondolaPorId('no-existe')).toBe(PLANTILLA_POR_DEFECTO_DE_GONDOLA)
  })
})

describe('rutaDelLogoDeGondola', () => {
  it('trazo negro sobre fondos claros y blanco sobre oscuros', () => {
    expect(rutaDelLogoDeGondola('claro')).toBe('/marca/ways-logo-claro.png')
    expect(rutaDelLogoDeGondola('oscuro')).toBe('/marca/ways-logo-oscuro.png')
  })
})
