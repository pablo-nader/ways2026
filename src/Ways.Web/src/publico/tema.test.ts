import { afterEach, describe, expect, it } from 'vitest'

import {
  ACENTO_POR_DEFECTO,
  acentoDeVitrina,
  aplicarCabeceraDelDocumento,
  aplicarTemaDeVitrina,
  temaDeVitrina,
  tintaSobreAcento,
} from './tema'

afterEach(() => {
  document.documentElement.removeAttribute('data-bs-theme')
  document.documentElement.style.removeProperty('--vitrina-acento')
  document.documentElement.style.removeProperty('--pub-tinta-sobre-acento')
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

describe('tintaSobreAcento', () => {
  it.each([
    ['#F7991C', '#121212'],
    ['#ffffff', '#121212'],
    ['#2a9d8f', '#121212'],
    ['#1b3a8a', '#ffffff'],
    ['#000080', '#ffffff'],
    ['#000000', '#ffffff'],
  ])('sobre %s va %s', (acento, esperado) => {
    expect(tintaSobreAcento(acento)).toBe(esperado)
  })

  it('un acento inválido se mide como el naranja por defecto', () => {
    expect(tintaSobreAcento('rojo')).toBe('#121212')
  })
})

describe('aplicarTemaDeVitrina', () => {
  it('deja data-bs-theme y --vitrina-acento en <html>', () => {
    aplicarTemaDeVitrina({ tema: 'claro', colorAcento: '#2a9d8f' })

    expect(document.documentElement.dataset.bsTheme).toBe('light')
    expect(document.documentElement.style.getPropertyValue('--vitrina-acento')).toBe('#2a9d8f')
  })

  it('deja la tinta del texto sobre el acento según el contraste', () => {
    aplicarTemaDeVitrina({ tema: 'oscuro', colorAcento: '#1b3a8a' })
    expect(document.documentElement.style.getPropertyValue('--pub-tinta-sobre-acento')).toBe('#ffffff')

    aplicarTemaDeVitrina({ tema: 'oscuro', colorAcento: '#F7991C' })
    expect(document.documentElement.style.getPropertyValue('--pub-tinta-sobre-acento')).toBe('#121212')
  })

  it('sin color de acento usa el naranja de Ways', () => {
    aplicarTemaDeVitrina({ tema: 'oscuro', colorAcento: null })

    expect(document.documentElement.dataset.bsTheme).toBe('dark')
    expect(document.documentElement.style.getPropertyValue('--vitrina-acento')).toBe('#F7991C')
  })
})

describe('aplicarCabeceraDelDocumento', () => {
  const metaDescripcion = () => document.head.querySelector('meta[name="description"]')

  it('título y descripción salen del nombre y la frase del comercio', () => {
    aplicarCabeceraDelDocumento({ nombre: ' Ferretería Sur ', tagline: 'Todo para tu obra', descripcion: 'Texto largo' })

    expect(document.title).toBe('Ferretería Sur')
    expect(metaDescripcion()).toHaveAttribute('content', 'Todo para tu obra')
  })

  it('con un prefijo de sección el título es "<sección> · <nombre>"', () => {
    aplicarCabeceraDelDocumento({ nombre: 'Sur', tagline: null, descripcion: null }, 'Tienda online')

    expect(document.title).toBe('Tienda online · Sur')
  })

  it('sin frase la descripción son los primeros 160 caracteres del texto, con los espacios colapsados', () => {
    aplicarCabeceraDelDocumento({ nombre: 'Sur', tagline: '  ', descripcion: `Hola

  mundo ${'x'.repeat(300)}` })

    const contenido = metaDescripcion()?.getAttribute('content') ?? ''
    expect(contenido).toHaveLength(160)
    expect(contenido.startsWith('Hola mundo xxx')).toBe(true)
  })

  it('sin vitrina no pisa el título del HTML ni crea una descripción vacía', () => {
    document.title = 'Tienda online'

    aplicarCabeceraDelDocumento(null)

    expect(document.title).toBe('Tienda online')
    expect(metaDescripcion()).toBeNull()
  })

  it('sin frase ni texto no crea la meta de descripción, y la que hay no se duplica', () => {
    aplicarCabeceraDelDocumento({ nombre: 'Sur', tagline: null, descripcion: null })
    expect(metaDescripcion()).toBeNull()

    aplicarCabeceraDelDocumento({ nombre: 'Sur', tagline: 'Una frase', descripcion: null })
    aplicarCabeceraDelDocumento({ nombre: 'Sur', tagline: 'Otra frase', descripcion: null })
    expect(document.head.querySelectorAll('meta[name="description"]')).toHaveLength(1)
    expect(metaDescripcion()).toHaveAttribute('content', 'Otra frase')
  })
})
