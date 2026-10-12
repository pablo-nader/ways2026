import { describe, expect, it } from 'vitest'
import { calcularTotalesDeLinea } from './ajusteManual'
import { calcularTotalesDeLineaConCombo, rotuloDeComboAplicado } from './combos'
import { previaDeLinea } from './ventas'
import type { LineaCarrito } from './carrito'
import type { ResultadoDeResolucion } from './tipos'

describe('calcularTotalesDeLineaConCombo', () => {
  it('sin combo es exactamente calcularTotalesDeLinea', () => {
    const base = { cantidad: 3, precioOriginal: 100, descuentoUnitario: 20, porcentaje: 15 }
    expect(calcularTotalesDeLineaConCombo(base)).toEqual(calcularTotalesDeLinea(base))
    expect(calcularTotalesDeLineaConCombo({ ...base, combo: null })).toEqual(calcularTotalesDeLinea(base))
  })

  it('el ahorro del combo se suma al descuento de oferta de las unidades NO consumidas', () => {
    // 2 panchos a 3000 con oferta de 500 por unidad; uno lo consume el combo (381,82).
    const totales = calcularTotalesDeLineaConCombo({
      cantidad: 2,
      precioOriginal: 3000,
      descuentoUnitario: 500,
      porcentaje: null,
      combo: { unidadesConsumidas: 1, ahorro: 381.82 },
    })

    expect(totales).toEqual({ bruto: 6000, descuento: 881.82, neto: 5118.18, ajuste: 0, total: 5118.18 })
  })

  it('con todas las unidades consumidas la oferta no cuenta: solo el ahorro del combo', () => {
    const totales = calcularTotalesDeLineaConCombo({
      cantidad: 2,
      precioOriginal: 3000,
      descuentoUnitario: 500,
      porcentaje: null,
      combo: { unidadesConsumidas: 2, ahorro: 545.46 },
    })

    expect(totales).toMatchObject({ descuento: 545.46, total: 5454.54 })
  })

  it('el ajuste manual se calcula sobre el neto que ya incluye el combo, con redondeo decimal exacto', () => {
    const totales = calcularTotalesDeLineaConCombo({
      cantidad: 1,
      precioOriginal: 3000,
      descuentoUnitario: 0,
      porcentaje: -10,
      combo: { unidadesConsumidas: 1, ahorro: 272.73 },
    })

    expect(totales).toMatchObject({ neto: 2727.27, ajuste: -272.73, total: 2454.54 })
  })
})

describe('rotuloDeComboAplicado', () => {
  it('lleva el sufijo × veces solo cuando se aplicó más de una vez', () => {
    expect(rotuloDeComboAplicado({ nombre: 'Pancho + Coca', veces: 1 })).toBe('Pancho + Coca')
    expect(rotuloDeComboAplicado({ nombre: 'Pancho + Coca', veces: 3 })).toBe('Pancho + Coca × 3')
  })
})

describe('previaDeLinea — combo', () => {
  const linea: LineaCarrito = { idLinea: 'l', idArticulo: 2, codigoInterno: 'A', nombre: 'Pancho', codigoBarra: null, acumulaEnVenta: true, cantidad: 1 }
  const resultado = (extra: Partial<ResultadoDeResolucion> = {}): ResultadoDeResolucion => ({
    idArticulo: 2,
    idListaPrecio: 1,
    precioOriginal: 3000,
    precioFinal: 3000,
    descuentoUnitario: 0,
    aplicadas: [],
    ...extra,
  })

  it('la línea consumida por un combo totaliza bruto menos su parte, y reporta el ahorro', () => {
    const previa = previaDeLinea(linea, resultado({ combo: { unidadesConsumidas: 1, ahorro: 272.73, idCombo: 7 } }))

    expect(previa).toMatchObject({ total: 2727.27, ahorroDeCombo: 272.73, ajusteManual: 0 })
  })

  it('sin combo el total y el ahorro son los de siempre', () => {
    expect(previaDeLinea(linea, resultado())).toMatchObject({ total: 3000, ahorroDeCombo: 0 })
  })

  it('el ajuste manual de la línea se aplica sobre el neto con combo', () => {
    const previa = previaDeLinea({ ...linea, ajusteManualPorcentaje: -10 }, resultado({ combo: { unidadesConsumidas: 1, ahorro: 272.73, idCombo: 7 } }))

    expect(previa).toMatchObject({ total: 2454.54, ajusteManual: -272.73 })
  })

  it('sin resultado no hay precio ni ahorro', () => {
    expect(previaDeLinea(linea, undefined)).toEqual({ precioUnitario: null, descuentoUnitario: 0, total: null, ajusteManual: 0, ahorroDeCombo: 0 })
  })
})
