import { describe, expect, it } from 'vitest'
import type { FilaDeEtiqueta, PromocionDeEtiqueta } from '../api/tipos'
import {
  contenidoDeGondola,
  elegirPromocion,
  formatearPrecioDeGondola,
  partirPrecio,
  porcentajeDeDescuento,
  textoDePromocion,
  tieneOferta,
} from './contenidoDeGondola'

function promocion(sobrescribir: Partial<PromocionDeEtiqueta> = {}): PromocionDeEtiqueta {
  return { idOferta: 1, nombre: '3x', cantidad: 3, soloMultiplos: false, precioUnitarioFinal: 900, ...sobrescribir }
}

function fila(sobrescribir: Partial<FilaDeEtiqueta> = {}): FilaDeEtiqueta {
  return {
    idArticulo: 1,
    codigoInterno: 'A1',
    codigoBarra: null,
    nombre: 'Coca Cola 1L',
    unidadVenta: 'Unidad',
    precioOriginal: 1000,
    precioFinal: 1000,
    ofertas: [],
    promociones: [],
    ...sobrescribir,
  }
}

const descuento = { idOferta: 9, nombre: '15%', descuentoUnitario: 150 }

describe('formatearPrecioDeGondola', () => {
  it('pesos enteros sin decimales', () => {
    expect(formatearPrecioDeGondola(2700)).toBe('$ 2.700')
  })

  it('con centavos, dos decimales', () => {
    expect(formatearPrecioDeGondola(1234.5)).toBe('$ 1.234,50')
  })
})

describe('textoDePromocion', () => {
  it('cantidad por el total del lote', () => {
    expect(textoDePromocion(promocion())).toBe('3x $ 2.700')
  })

  it('la cantidad con decimales usa coma, no punto', () => {
    expect(textoDePromocion(promocion({ cantidad: 1.5, precioUnitarioFinal: 1000 }))).toBe('1,5x $ 1.500')
  })

  it('no arrastra ruido de coma flotante', () => {
    expect(textoDePromocion(promocion({ cantidad: 3, precioUnitarioFinal: 0.1 }))).toBe('3x $ 0,30')
  })
})

describe('elegirPromocion', () => {
  it('sin promociones no elige ninguna', () => {
    expect(elegirPromocion([])).toBeNull()
  })

  it('elige la de menor cantidad', () => {
    expect(elegirPromocion([promocion({ cantidad: 6 }), promocion({ idOferta: 2, cantidad: 2 })])?.idOferta).toBe(2)
  })

  it('a igual cantidad, la de menor precio unitario', () => {
    expect(elegirPromocion([promocion({ idOferta: 1, precioUnitarioFinal: 900 }), promocion({ idOferta: 2, precioUnitarioFinal: 800 })])?.idOferta).toBe(2)
  })
})

describe('porcentajeDeDescuento', () => {
  it('redondea al entero', () => {
    expect(porcentajeDeDescuento(1000, 850)).toBe('-15%')
    expect(porcentajeDeDescuento(300, 199)).toBe('-34%')
  })

  it('sin baja de precio no hay porcentaje', () => {
    expect(porcentajeDeDescuento(1000, 1000)).toBeNull()
    expect(porcentajeDeDescuento(1000, 1200)).toBeNull()
    expect(porcentajeDeDescuento(0, 0)).toBeNull()
  })

  it('una baja que redondea a 0% no se anuncia', () => {
    expect(porcentajeDeDescuento(1000, 996)).toBeNull()
  })
})

describe('tieneOferta', () => {
  it('oferta o promoción', () => {
    expect(tieneOferta(fila())).toBe(false)
    expect(tieneOferta(fila({ ofertas: [descuento] }))).toBe(true)
    expect(tieneOferta(fila({ promociones: [promocion()] }))).toBe(true)
  })
})

describe('contenidoDeGondola', () => {
  it('sin Ofertas ignora todo y muestra el precio de la lista', () => {
    const conOferta = fila({ precioFinal: 850, ofertas: [descuento], promociones: [promocion()] })
    expect(contenidoDeGondola(conOferta, false)).toEqual({ precio: 1000, precioTachado: null, porcentaje: null, promocion: null })
  })

  it('con Ofertas y descuento: precio final grande, original tachado y porcentaje', () => {
    expect(contenidoDeGondola(fila({ precioFinal: 850, ofertas: [descuento] }), true)).toEqual({
      precio: 850,
      precioTachado: 1000,
      porcentaje: '-15%',
      promocion: null,
    })
  })

  it('con Ofertas y promoción por cantidad: el precio de lista y el texto 3x', () => {
    expect(contenidoDeGondola(fila({ promociones: [promocion()] }), true)).toEqual({
      precio: 1000,
      precioTachado: null,
      porcentaje: null,
      promocion: '3x $ 2.700',
    })
  })

  it('con descuento y promoción a la vez muestra los dos', () => {
    const contenido = contenidoDeGondola(fila({ precioFinal: 850, ofertas: [descuento], promociones: [promocion()] }), true)
    expect(contenido.precio).toBe(850)
    expect(contenido.promocion).toBe('3x $ 2.700')
  })

  it('una oferta que no baja el precio no tacha nada', () => {
    expect(contenidoDeGondola(fila({ ofertas: [descuento] }), true).precioTachado).toBeNull()
  })
})

describe('partirPrecio', () => {
  it('separa signo, entero y centavos', () => {
    expect(partirPrecio('$ 12.345,67')).toEqual({ prefijo: '$', entero: '12.345', centavos: '67' })
  })

  it('un precio sin decimales no tiene centavos', () => {
    expect(partirPrecio(formatearPrecioDeGondola(2700))).toEqual({ prefijo: '$', entero: '2.700', centavos: null })
  })

  it('la promoción conserva la cantidad en el prefijo', () => {
    expect(partirPrecio('3x $ 2.700,50')).toEqual({ prefijo: '3x $', entero: '2.700', centavos: '50' })
  })

  it('un texto sin signo pesos queda entero', () => {
    expect(partirPrecio('1.000')).toEqual({ prefijo: '', entero: '1.000', centavos: null })
  })
})
