import { describe, expect, it } from 'vitest'
import {
  calcularAjusteManual,
  calcularTotalesDeLinea,
  formatearPorcentajeDeAjuste,
  mensajeDeRechazoDeAjusteManual,
  mensajeDeRechazoDeAjusteManualAlDrenar,
  rotuloDeAjusteManual,
  tipoDeAjuste,
  totalesDeAjusteManual,
  validarPorcentajeDeAjuste,
} from './ajusteManual'

describe('calcularAjusteManual', () => {
  it('sin porcentaje (null o undefined) no hay ajuste', () => {
    expect(calcularAjusteManual(100, null)).toBe(0)
    expect(calcularAjusteManual(100, undefined)).toBe(0)
  })

  it('un porcentaje negativo da un monto negativo (descuento) y uno positivo, positivo (recargo)', () => {
    expect(calcularAjusteManual(270, -10)).toBe(-27)
    expect(calcularAjusteManual(270, 15)).toBe(40.5)
  })

  it('admite el rango completo: -100 anula el neto y 100 lo duplica', () => {
    expect(calcularAjusteManual(80, -100)).toBe(-80)
    expect(calcularAjusteManual(80, 100)).toBe(80)
  })

  it('admite 2 decimales de porcentaje', () => {
    expect(calcularAjusteManual(200, 12.5)).toBe(25)
    expect(calcularAjusteManual(1000, 0.01)).toBe(0.1)
    expect(calcularAjusteManual(1000, -33.33)).toBe(-333.3)
  })

  it('redondea half-away-from-zero: un empate exacto sube en valor absoluto, con ambos signos', () => {
    // 10,05 × 10 % = 1,005 exacto
    expect(calcularAjusteManual(10.05, 10)).toBe(1.01)
    expect(calcularAjusteManual(10.05, -10)).toBe(-1.01)
    // 0,50 × 1 % = 0,005
    expect(calcularAjusteManual(0.5, 1)).toBe(0.01)
    expect(calcularAjusteManual(0.5, -1)).toBe(-0.01)
    // 33,33 × 15 % = 4,9995
    expect(calcularAjusteManual(33.33, 15)).toBe(5)
    expect(calcularAjusteManual(33.33, -15)).toBe(-5)
  })

  // Empates exactos en los que `neto * porcentaje / 100` en punto flotante cae por DEBAJO del medio
  // centavo y redondea para el otro lado que el servidor (que calcula en decimal): 4,10 × 15 % es
  // 0,615 exacto pero el producto flotante da 0,6149999999999999. Los casos de arriba no los
  // distinguen: la fórmula flotante también da 1,005, 0,005 y 4,9995 en ellos.
  it.each([
    [4.1, 0.62],
    [16.9, 2.54],
    [33.3, 5],
    [68.1, 10.22],
  ])('un empate que el punto flotante pierde: %s × 15 % da %s, y el espejo negativo su opuesto', (neto, esperado) => {
    expect(calcularAjusteManual(neto, 15)).toBe(esperado)
    expect(calcularAjusteManual(neto, -15)).toBe(-esperado)
  })

  it('lo que no llega a medio centavo redondea a 0 y nunca a -0', () => {
    const resultado = calcularAjusteManual(0.04, -10)
    expect(resultado).toBe(0)
    expect(Object.is(resultado, -0)).toBe(false)
  })

  it('un neto negativo (línea de devolución) invierte el signo del monto, no el del porcentaje', () => {
    expect(calcularAjusteManual(-100, -10)).toBe(10)
    expect(calcularAjusteManual(-100, 10)).toBe(-10)
  })

  it('un neto en 0 no genera ajuste', () => {
    expect(calcularAjusteManual(0, -10)).toBe(0)
  })
})

describe('calcularTotalesDeLinea', () => {
  it('sin ajuste: bruto, descuento por oferta y neto, con total = neto', () => {
    expect(calcularTotalesDeLinea({ cantidad: 3, precioOriginal: 100, descuentoUnitario: 10, porcentaje: null })).toEqual({
      bruto: 300,
      descuento: 30,
      neto: 270,
      ajuste: 0,
      total: 270,
    })
  })

  it('el ajuste se aplica sobre el neto posterior a la oferta', () => {
    expect(calcularTotalesDeLinea({ cantidad: 3, precioOriginal: 100, descuentoUnitario: 10, porcentaje: -10 })).toEqual({
      bruto: 300,
      descuento: 30,
      neto: 270,
      ajuste: -27,
      total: 243,
    })
  })

  it('redondea el bruto y el descuento cada uno a 2 decimales antes de restarlos', () => {
    // 0,333 × 10,50 = 3,4965 → 3,50; 0,333 × 0,25 = 0,08325 → 0,08
    const totales = calcularTotalesDeLinea({ cantidad: 0.333, precioOriginal: 10.5, descuentoUnitario: 0.25, porcentaje: null })
    expect(totales.bruto).toBe(3.5)
    expect(totales.descuento).toBe(0.08)
    expect(totales.neto).toBe(3.42)
    expect(totales.total).toBe(3.42)
  })

  // El servidor calcula en decimal. 0,7 × 1,15 es 0,805 exacto y sube a 0,81, pero el producto
  // flotante da 0,8049999999999999 y se redondeaba a 0,80: un centavo de diferencia en el importe
  // que el cajero ve contra el que cobra el servidor.
  it('el bruto es el producto decimal exacto: un empate de medio centavo sube aunque el producto flotante quede por debajo', () => {
    expect(0.7 * 1.15).toBeLessThan(0.805)
    const totales = calcularTotalesDeLinea({ cantidad: 0.7, precioOriginal: 1.15, descuentoUnitario: 0, porcentaje: null })
    expect(totales.bruto).toBe(0.81)
    expect(totales.neto).toBe(0.81)
    expect(totales.total).toBe(0.81)
  })

  it('el descuento por oferta es el producto decimal exacto, también con más de 2 decimales por unidad', () => {
    // 0,075 × 3 = 0,225 exacto → 0,23; el producto flotante da 0,22499999999999998.
    expect(0.075 * 3).toBeLessThan(0.225)
    const totales = calcularTotalesDeLinea({ cantidad: 3, precioOriginal: 10, descuentoUnitario: 0.075, porcentaje: null })
    expect(totales.bruto).toBe(30)
    expect(totales.descuento).toBe(0.23)
    expect(totales.neto).toBe(29.77)
  })

  it('un descuento por unidad de 4 decimales se redondea una sola vez, sobre el producto de la línea', () => {
    // 0,3333 × 3 = 0,9999 → 1,00 (redondear antes el descuento unitario daría 0,33 × 3 = 0,99).
    const totales = calcularTotalesDeLinea({ cantidad: 3, precioOriginal: 10, descuentoUnitario: 0.3333, porcentaje: null })
    expect(totales.descuento).toBe(1)
    expect(totales.neto).toBe(29)
  })

  it('en una misma línea el bruto, el descuento y el ajuste son exactos: los tres empates suben un centavo', () => {
    // bruto 0,7 × 3,35 = 2,345 → 2,35; descuento 0,35 × 0,7 = 0,245 → 0,25; neto 2,10;
    // ajuste 2,10 × 15 % = 0,315 → 0,32. Con productos flotantes el bruto da 2,34 y el descuento 0,24.
    const totales = calcularTotalesDeLinea({ cantidad: 0.7, precioOriginal: 3.35, descuentoUnitario: 0.35, porcentaje: 15 })
    expect(totales).toEqual({ bruto: 2.35, descuento: 0.25, neto: 2.1, ajuste: 0.32, total: 2.42 })
  })

  it('el neto no arrastra ruido de punto flotante', () => {
    const totales = calcularTotalesDeLinea({ cantidad: 1, precioOriginal: 0.3, descuentoUnitario: 0.1, porcentaje: null })
    expect(totales.neto).toBe(0.2)
  })
})

describe('totalesDeAjusteManual', () => {
  it('sin líneas o sin ajustes ambos totales son 0', () => {
    expect(totalesDeAjusteManual([])).toEqual({ descuentoManualTotal: 0, recargoManualTotal: 0 })
    expect(
      totalesDeAjusteManual([
        { porcentaje: null, ajuste: 0 },
        { porcentaje: undefined, ajuste: 0 },
      ]),
    ).toEqual({ descuentoManualTotal: 0, recargoManualTotal: 0 })
  })

  it('el descuento suma el monto en positivo de las líneas con porcentaje negativo', () => {
    expect(
      totalesDeAjusteManual([
        { porcentaje: -10, ajuste: -20 },
        { porcentaje: -5, ajuste: -7.5 },
      ]),
    ).toEqual({ descuentoManualTotal: 27.5, recargoManualTotal: 0 })
  })

  it('un recargo no oculta un descuento: se informan los dos, no su diferencia', () => {
    expect(
      totalesDeAjusteManual([
        { porcentaje: -10, ajuste: -20 },
        { porcentaje: 15, ajuste: 15 },
      ]),
    ).toEqual({ descuentoManualTotal: 20, recargoManualTotal: 15 })
  })

  it('clasifica por el signo del porcentaje: en una línea de devolución el descuento queda negativo', () => {
    expect(totalesDeAjusteManual([{ porcentaje: -10, ajuste: 10 }])).toEqual({ descuentoManualTotal: -10, recargoManualTotal: 0 })
  })

  it('una línea con porcentaje cuyo monto redondea a 0 no suma nada ni deja -0', () => {
    const totales = totalesDeAjusteManual([{ porcentaje: -1, ajuste: 0 }])
    expect(totales.descuentoManualTotal).toBe(0)
    expect(Object.is(totales.descuentoManualTotal, -0)).toBe(false)
  })

  it('suma sin arrastrar error de punto flotante', () => {
    expect(
      totalesDeAjusteManual([
        { porcentaje: 10, ajuste: 0.1 },
        { porcentaje: 10, ajuste: 0.2 },
      ]).recargoManualTotal,
    ).toBe(0.3)
  })
})

describe('rótulos y formato del porcentaje', () => {
  it('tipoDeAjuste y rotuloDeAjusteManual salen del signo', () => {
    expect(tipoDeAjuste(-10)).toBe('descuento')
    expect(tipoDeAjuste(15)).toBe('recargo')
    expect(rotuloDeAjusteManual(-10)).toBe('Desc. manual')
    expect(rotuloDeAjusteManual(15)).toBe('Recargo')
  })

  it('formatearPorcentajeDeAjuste muestra la magnitud sin signo, con coma y sin ceros de relleno', () => {
    expect(formatearPorcentajeDeAjuste(-10)).toBe('10')
    expect(formatearPorcentajeDeAjuste(15)).toBe('15')
    expect(formatearPorcentajeDeAjuste(-12.5)).toBe('12,5')
    expect(formatearPorcentajeDeAjuste(0.25)).toBe('0,25')
    expect(formatearPorcentajeDeAjuste(100)).toBe('100')
  })
})

describe('validarPorcentajeDeAjuste', () => {
  it('un descuento devuelve la magnitud en negativo y un recargo en positivo', () => {
    expect(validarPorcentajeDeAjuste('10', 'descuento')).toEqual({ ok: true, porcentaje: -10 })
    expect(validarPorcentajeDeAjuste('10', 'recargo')).toEqual({ ok: true, porcentaje: 10 })
  })

  it('acepta coma o punto decimal, hasta 2 decimales, y recorta los espacios', () => {
    expect(validarPorcentajeDeAjuste('12,5', 'recargo')).toEqual({ ok: true, porcentaje: 12.5 })
    expect(validarPorcentajeDeAjuste('12.5', 'descuento')).toEqual({ ok: true, porcentaje: -12.5 })
    expect(validarPorcentajeDeAjuste('0,01', 'recargo')).toEqual({ ok: true, porcentaje: 0.01 })
    expect(validarPorcentajeDeAjuste('  33,33 ', 'descuento')).toEqual({ ok: true, porcentaje: -33.33 })
  })

  it('el tope es 100 inclusive', () => {
    expect(validarPorcentajeDeAjuste('100', 'descuento')).toEqual({ ok: true, porcentaje: -100 })
    expect(validarPorcentajeDeAjuste('100,00', 'recargo')).toEqual({ ok: true, porcentaje: 100 })
  })

  it('vacío pide el porcentaje', () => {
    expect(validarPorcentajeDeAjuste('', 'descuento')).toEqual({ ok: false, mensaje: 'Ingresá el porcentaje del ajuste.' })
    expect(validarPorcentajeDeAjuste('   ', 'recargo')).toEqual({ ok: false, mensaje: 'Ingresá el porcentaje del ajuste.' })
  })

  it.each(['abc', '-10', '+10', '10,555', '10,', ',5', '1e1', '10 %', '1.000', '1,5,5'])(
    'rechaza por formato %j (el signo lo da el selector; hasta 2 decimales)',
    (texto) => {
      const resultado = validarPorcentajeDeAjuste(texto, 'descuento')
      expect(resultado).toEqual({
        ok: false,
        mensaje: 'Ingresá el porcentaje sin signo y con hasta 2 decimales (por ejemplo 10 o 12,5).',
      })
    },
  )

  it.each(['0', '0,00', '0.0', '101', '100,01', '999'])('rechaza por rango %j (mayor que 0 y como máximo 100)', (texto) => {
    const resultado = validarPorcentajeDeAjuste(texto, 'recargo')
    expect(resultado).toEqual({ ok: false, mensaje: 'El porcentaje debe ser mayor que 0 y como máximo 100.' })
  })
})

describe('mensajeDeRechazoDeAjusteManual', () => {
  it('traduce el código 400 ajuste_manual_invalido a un mensaje para el cajero, que lo manda a revisar el carrito', () => {
    expect(mensajeDeRechazoDeAjusteManual('ajuste_manual_invalido')).toBe(
      'El servidor rechazó un ajuste manual: el porcentaje de cada línea debe ser distinto de 0, entre -100 y 100 y con hasta 2 decimales. Revisá los ajustes del carrito.',
    )
  })

  it('un código ajeno devuelve null para que el llamador siga con su mensaje habitual', () => {
    expect(mensajeDeRechazoDeAjusteManual('turno_no_abierto')).toBeNull()
    expect(mensajeDeRechazoDeAjusteManual('')).toBeNull()
    expect(mensajeDeRechazoDeAjusteManual('constructor')).toBeNull()
  })
})

describe('mensajeDeRechazoDeAjusteManualAlDrenar', () => {
  it('informa de una venta ya cobrada y no manda a revisar un carrito que ya no existe', () => {
    const mensaje = mensajeDeRechazoDeAjusteManualAlDrenar('ajuste_manual_invalido')
    expect(mensaje).toBe(
      'el servidor rechazó el ajuste manual de alguna línea de esta venta, que ya estaba cobrada: el porcentaje de cada línea debe ser distinto de 0, entre -100 y 100 y con hasta 2 decimales.',
    )
    expect(mensaje).not.toMatch(/carrito|Revisá/)
  })

  it('un código ajeno devuelve null para que el llamador siga con su mensaje habitual', () => {
    expect(mensajeDeRechazoDeAjusteManualAlDrenar('limite_credito_excedido')).toBeNull()
    expect(mensajeDeRechazoDeAjusteManualAlDrenar('')).toBeNull()
    expect(mensajeDeRechazoDeAjusteManualAlDrenar('constructor')).toBeNull()
  })
})
