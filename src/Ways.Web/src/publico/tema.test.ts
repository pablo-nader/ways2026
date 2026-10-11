import { afterEach, describe, expect, it } from 'vitest'

import { ACENTO_POR_DEFECTO, acentoDeVitrina, aplicarCabeceraDelDocumento, aplicarTemaDeVitrina, temaDeVitrina } from './tema'

afterEach(() => {
  document.documentElement.removeAttribute('data-bs-theme')
  document.documentElement.style.removeProperty('--vitrina-acento')
  document.title = ''
  document.head.querySelector('meta[name="description"]')?.remove()
})

describe('acentoDeVitrina', () => {
  it('el naranja de Ways es el acento cuando el comercio no eligió color', () => {
    expect(ACENTO_POR_DEFECTO).toBe('#F7991C')
    expect(acentoDeVitrina(null)).toBe('#F7991C')
    expect(acentoDeVitrina(undefined)).toBe('#F7991C')
    expect(acentoDeVitrina('')).toBe('#F7991C')
  })

  it('un valor que no es #rrggbb no se usa como color', () => {
    expect(acentoDeVitrina('red')).toBe('#F7991C')
    expect(acentoDeVitrina('#fff')).toBe('#F7991C')
    expect(acentoDeVitrina('#12345g')).toBe('#F7991C')
  })

  it('un #rrggbb se respeta tal cual', () => {
    expect(acentoDeVitrina('#2a9d8f')).toBe('#2a9d8f')
  })
})

describe('temaDeVitrina', () => {
  it('solo claro es claro: lo demás es el tema oscuro por defecto', () => {
    expect(temaDeVitrina('claro')).toBe('claro')
    expect(temaDeVitrina('oscuro')).toBe('oscuro')
    expect(temaDeVitrina('verde')).toBe('oscuro')
    expect(temaDeVitrina(null)).toBe('oscuro')
  })
})

describe('aplicarTemaDeVitrina', () => {
  it('deja data-bs-theme y --vitrina-acento en <html>', () => {
    aplicarTemaDeVitrina({ tema: 'claro', colorAcento: '#2a9d8f' })

    expect(document.documentElement.dataset.bsTheme).toBe('light')
    expect(document.documentElement.style.getPropertyValue('--vitrina-acento')).toBe('#2a9d8f')
  })

  it('sin color de acento usa el naranja de Ways', () => {
    aplicarTemaDeVitrina({ tema: 'oscuro', colorAcento: null })

    expect(document.documentElement.dataset.bsTheme).toBe('dark')
    expect(document.documentElement.style.getPropertyValue('--vitrina-acento')).toBe('#F7991C')
  })
})

describe('aplicarCabeceraDelDocumento', () => {
  it('título y descripción salen del nombre y la frase del comercio', () => {
    aplicarCabeceraDelDocumento({ nombre: ' Ferretería Sur ', tagline: 'Todo para tu obra' })

    expect(document.title).toBe('Ferretería Sur')
    expect(document.head.querySelector('meta[name="description"]')).toHaveAttribute('content', 'Todo para tu obra')
  })

  it('sin vitrina el título es Ways y la descripción queda vacía; la meta no se duplica', () => {
    aplicarCabeceraDelDocumento({ nombre: 'Sur', tagline: 'Una frase' })
    aplicarCabeceraDelDocumento(null)

    expect(document.title).toBe('Ways')
    expect(document.head.querySelectorAll('meta[name="description"]')).toHaveLength(1)
    expect(document.head.querySelector('meta[name="description"]')).toHaveAttribute('content', '')
  })
})
