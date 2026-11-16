import { describe, expect, it } from 'vitest'
import { ErrorApi } from '../../api/cliente'
import type { PuntoDeValoracion } from '../../api/tipos'
import {
  armarTarjetas,
  aPuntosDeGrafico,
  avisosDeCobertura,
  etiquetaDePunto,
  fraseDeValoracion,
  mensajeDeErrorDeValoracion,
  sentidoDe,
  variacion,
} from './calculos'

function punto(sobrescribir: Partial<PuntoDeValoracion> = {}): PuntoDeValoracion {
  return {
    fecha: '2026-01-31T23:59:59-03:00',
    mercaderiaCosto: 1000,
    mercaderiaVenta: 1500,
    efectivo: 200,
    cuentas: 300,
    dinero: 500,
    totalACosto: 1500,
    totalAVenta: 2000,
    articulosSinCosto: 0,
    articulosSinPrecio: 0,
    articulosConCostoEstimado: 0,
    valorCostoEstimado: 0,
    ...sobrescribir,
  }
}

describe('variacion', () => {
  it('da el monto y el porcentaje sobre el valor inicial', () => {
    expect(variacion(1000, 800)).toEqual({ monto: -200, porcentaje: -20 })
    expect(variacion(500, 1200)).toEqual({ monto: 700, porcentaje: 140 })
  })

  it('sin base (inicial en cero) no inventa un porcentaje', () => {
    expect(variacion(0, 50)).toEqual({ monto: 50, porcentaje: null })
  })

  it('el ruido binario por debajo del centavo no cuenta como cambio', () => {
    expect(sentidoDe(variacion(0.1 + 0.2, 0.3).monto)).toBe('igual')
  })
})

describe('armarTarjetas', () => {
  it('toma el valor del último punto y la variación contra el primero', () => {
    const tarjetas = armarTarjetas([punto(), punto({ mercaderiaCosto: 800, efectivo: 500, cuentas: 700, dinero: 1200, totalACosto: 2000 })])
    const porClave = Object.fromEntries(tarjetas.map((t) => [t.clave, t]))

    expect(tarjetas.map((t) => t.titulo)).toEqual([
      'Total a costo',
      'Mercadería a costo',
      'Mercadería a precio de venta',
      'Efectivo',
      'Cuentas',
    ])
    expect(porClave.totalACosto.valor).toBe(2000)
    expect(porClave.totalACosto.variacion).toEqual({ monto: 500, porcentaje: (500 / 1500) * 100 })
    expect(porClave.mercaderiaCosto.variacion.monto).toBe(-200)
    expect(porClave.efectivo.variacion.monto).toBe(300)
  })

  it('sin puntos no hay tarjetas', () => {
    expect(armarTarjetas([])).toEqual([])
  })
})

describe('fraseDeValoracion', () => {
  const inicio = punto()

  it('más dinero y menos mercadería', () => {
    expect(fraseDeValoracion([inicio, punto({ dinero: 900, mercaderiaCosto: 600 })])).toBe(
      'Tenés más dinero, pero bajó la mercadería.',
    )
  })

  it('menos dinero y más mercadería', () => {
    expect(fraseDeValoracion([inicio, punto({ dinero: 100, mercaderiaCosto: 1400 })])).toBe(
      'Hay menos dinero y más mercadería.',
    )
  })

  it('suben ambos', () => {
    expect(fraseDeValoracion([inicio, punto({ dinero: 900, mercaderiaCosto: 1400 })])).toBe(
      'Subieron tanto el dinero como la mercadería.',
    )
  })

  it('bajan ambos', () => {
    expect(fraseDeValoracion([inicio, punto({ dinero: 100, mercaderiaCosto: 600 })])).toBe(
      'Bajaron tanto el dinero como la mercadería.',
    )
  })

  it('un lado sin cambios', () => {
    expect(fraseDeValoracion([inicio, punto({ dinero: 900 })])).toBe('Tenés más dinero y la mercadería no cambió.')
    expect(fraseDeValoracion([inicio, punto({ dinero: 100 })])).toBe('Tenés menos dinero y la mercadería no cambió.')
    expect(fraseDeValoracion([inicio, punto({ mercaderiaCosto: 1400 })])).toBe('Tenés más mercadería y el dinero no cambió.')
    expect(fraseDeValoracion([inicio, punto({ mercaderiaCosto: 600 })])).toBe('Tenés menos mercadería y el dinero no cambió.')
  })

  it('ninguno cambió', () => {
    expect(fraseDeValoracion([inicio, punto()])).toBe('No hubo cambios en el dinero ni en la mercadería.')
  })

  it('compara la mercadería a costo, no a precio de venta ni el total', () => {
    const frase = fraseDeValoracion([inicio, punto({ mercaderiaVenta: 9999, totalACosto: 9999 })])

    expect(frase).toBe('No hubo cambios en el dinero ni en la mercadería.')
  })

  it('con un solo punto no hay contra qué comparar', () => {
    expect(fraseDeValoracion([inicio])).toBeNull()
    expect(fraseDeValoracion([])).toBeNull()
  })
})

describe('etiquetaDePunto', () => {
  it('mes: MM/aaaa; día y semana: dd/MM, con la fecha local del servidor', () => {
    expect(etiquetaDePunto('2026-10-31T23:59:59-03:00', 'Mes')).toBe('10/2026')
    expect(etiquetaDePunto('2026-10-31T23:59:59-03:00', 'Dia')).toBe('31/10')
    expect(etiquetaDePunto('2026-10-25T00:00:00-03:00', 'Semana')).toBe('25/10')
  })
})

describe('aPuntosDeGrafico', () => {
  it('mapea cada punto a sus series con la etiqueta de la granularidad', () => {
    const [primero] = aPuntosDeGrafico([punto()], 'Mes')

    expect(primero).toEqual({
      etiqueta: '01/2026',
      mercaderiaCosto: 1000,
      mercaderiaVenta: 1500,
      efectivo: 200,
      cuentas: 300,
      dinero: 500,
      totalACosto: 1500,
    })
  })
})

describe('avisosDeCobertura', () => {
  it('sin faltantes ni estimados no avisa', () => {
    expect(avisosDeCobertura([punto(), punto()])).toEqual([])
  })

  it('avisa sin costo y sin precio del último punto, con singular y plural', () => {
    const avisos = avisosDeCobertura([punto(), punto({ articulosSinCosto: 1, articulosSinPrecio: 3 })])

    expect(avisos.map((a) => a.clave)).toEqual(['sin-costo', 'sin-precio'])
    expect(avisos[0].texto).toContain('1 artículo con stock no tiene costo cargado')
    expect(avisos[1].texto).toContain('3 artículos con stock no tienen precio de venta')
  })

  it('el estimado del primer punto avisa que la variación es aproximada, con su valor', () => {
    const avisos = avisosDeCobertura([punto({ articulosConCostoEstimado: 2, valorCostoEstimado: 250 }), punto()])

    expect(avisos).toHaveLength(1)
    expect(avisos[0].clave).toBe('estimado-primero')
    expect(avisos[0].importe).toBe(250)
  })

  it('un único punto con estimado cuenta una sola vez', () => {
    const avisos = avisosDeCobertura([punto({ articulosConCostoEstimado: 2, valorCostoEstimado: 250 })])

    expect(avisos.map((a) => a.clave)).toEqual(['estimado-ultimo'])
  })
})

describe('mensajeDeErrorDeValoracion', () => {
  it('traduce los códigos del servicio', () => {
    expect(mensajeDeErrorDeValoracion(new ErrorApi(400, 'rango_en_el_futuro', 'x'))).toMatch(/después de hoy/)
    expect(mensajeDeErrorDeValoracion(new ErrorApi(400, 'demasiados_puntos', 'x'))).toMatch(/máximo 60/)
    expect(mensajeDeErrorDeValoracion(new ErrorApi(400, 'rango_demasiado_amplio', 'x'))).toMatch(/366 días/)
  })

  it('otro error de la API muestra su mensaje y uno ajeno el texto genérico', () => {
    expect(mensajeDeErrorDeValoracion(new ErrorApi(404, 'no_encontrado', 'La empresa no existe.'))).toBe('La empresa no existe.')
    expect(mensajeDeErrorDeValoracion(new Error('boom'))).toBe('No se pudo cargar el reporte de valoración.')
  })
})
