import { describe, expect, it } from 'vitest'
import { partirEnLineas, presupuestoVertical, tamanioDeFuenteDelNombre, tamanioDeFuenteDelPrecio, MAXIMO_DE_LINEAS_DEL_NOMBRE } from './ajusteDeTexto'
import { ETIQUETAS_POR_HOJA, disposicionDeHoja } from './disposicionDeHojas'

describe('partirEnLineas', () => {
  it('corta por palabras sin pasarse del ancho', () => {
    expect(partirEnLineas('Aceite de girasol Cocinero 900 ml', 14)).toEqual(['Aceite de', 'girasol', 'Cocinero 900', 'ml'])
  })

  it('una palabra más larga que la línea se parte por caracteres', () => {
    expect(partirEnLineas('Paraclorobenzoico', 6)).toEqual(['Paracl', 'oroben', 'zoico'])
  })

  it('un nombre vacío no genera líneas', () => {
    expect(partirEnLineas('   ', 10)).toEqual([])
  })
})

describe('tamanioDeFuenteDelNombre', () => {
  const celda = (porHoja: (typeof ETIQUETAS_POR_HOJA)[number]) => {
    const d = disposicionDeHoja(porHoja)
    return { anchoMm: d.celdaAnchoMm, altoMm: d.celdaAltoMm }
  }

  it('un nombre corto usa la fuente más grande', () => {
    const corto = tamanioDeFuenteDelNombre('Sal', celda(8))
    const largo = tamanioDeFuenteDelNombre('Aceite de girasol alto oleico Cocinero botella 900 ml', celda(8))
    expect(corto).toBeGreaterThan(largo)
  })

  it('la fuente nunca crece al alargar el nombre', () => {
    const base = 'Galletitas surtidas '
    let anterior = Number.POSITIVE_INFINITY
    for (let repeticiones = 1; repeticiones <= 8; repeticiones++) {
      const tamanio = tamanioDeFuenteDelNombre(base.repeat(repeticiones).trim(), celda(16))
      expect(tamanio).toBeLessThanOrEqual(anterior)
      anterior = tamanio
    }
  })

  it('el nombre elegido entra siempre en dos líneas para todos los formatos', () => {
    const nombre = 'Fideos tallarines al huevo Matarazzo paquete familiar 500 g'
    for (const porHoja of ETIQUETAS_POR_HOJA) {
      const c = celda(porHoja)
      const fuente = tamanioDeFuenteDelNombre(nombre, c)
      const lineas = partirEnLineas(nombre, (c.anchoMm * 0.88) / (fuente * 0.68))
      expect(lineas.length).toBeLessThanOrEqual(MAXIMO_DE_LINEAS_DEL_NOMBRE)
    }
  })

  it('una celda más chica reduce la fuente', () => {
    const nombre = 'Yerba mate con palo Taragüi 1 kg'
    expect(tamanioDeFuenteDelNombre(nombre, celda(16))).toBeLessThan(tamanioDeFuenteDelNombre(nombre, celda(2)))
  })

  it('un nombre imposible cae al mínimo en vez de desbordar o romper', () => {
    expect(tamanioDeFuenteDelNombre('x'.repeat(400), celda(16))).toBe(2.2)
  })
})

describe('tamanioDeFuenteDelPrecio', () => {
  const celda = { anchoMm: 90, altoMm: 90 }

  it('un precio corto usa el tope por alto', () => {
    expect(tamanioDeFuenteDelPrecio('$ 90', celda)).toBe(27)
  })

  it('un precio largo se achica para no pasarse del ancho', () => {
    expect(tamanioDeFuenteDelPrecio('$ 1.234.567,89', celda)).toBeLessThan(tamanioDeFuenteDelPrecio('$ 90', celda))
  })
})

describe('presupuestoVertical', () => {
  const nombre = 'Aceite de girasol alto oleico Cocinero botella 900 ml'

  for (const porHoja of ETIQUETAS_POR_HOJA) {
    for (const conOferta of [false, true]) {
      it(`${porHoja} por hoja ${conOferta ? 'con' : 'sin'} oferta: la suma de los bloques entra en la celda`, () => {
        const d = disposicionDeHoja(porHoja)
        const presupuesto = presupuestoVertical(nombre, '$ 12.345,67', { anchoMm: d.celdaAnchoMm, altoMm: d.celdaAltoMm }, conOferta)
        expect(presupuesto.totalMm).toBeLessThanOrEqual(d.celdaAltoMm)
      })
    }
  }

  it('con oferta el precio es más chico que sin oferta', () => {
    const celda = { anchoMm: 68, altoMm: 46 }
    expect(tamanioDeFuenteDelPrecio('$ 90', celda, true)).toBeLessThan(tamanioDeFuenteDelPrecio('$ 90', celda))
  })
})
