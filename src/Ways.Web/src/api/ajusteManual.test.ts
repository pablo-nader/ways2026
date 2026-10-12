import { describe, expect, it } from 'vitest'
import {
  calcularAjusteManual,
  calcularTotalesDeLinea,
  formatearPorcentajeDeAjuste,
  formatearPorcentajeDeAjusteConSigno,
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
    expect(calcularAjusteManual(270, 14)).toBe(38)
  })

  it('el monto es un peso entero: 270 × 15 % = 40,5 sube a 41', () => {
    expect(calcularAjusteManual(270, 15)).toBe(41)
    expect(calcularAjusteManual(270, -15)).toBe(-41)
  })

  it('admite el rango completo: -100 anula el neto y 100 lo duplica', () => {
    expect(calcularAjusteManual(80, -100)).toBe(-80)
    expect(calcularAjusteManual(80, 100)).toBe(80)
  })

  it('admite 2 decimales de porcentaje y redondea el monto al peso', () => {
    expect(calcularAjusteManual(200, 12.5)).toBe(25)
    expect(calcularAjusteManual(1000, 0.01)).toBe(0)
    expect(calcularAjusteManual(1000, -33.33)).toBe(-333)
    expect(calcularAjusteManual(3500, 0.05)).toBe(2)
  })

  it('redondea half-away-from-zero: un empate exacto sube en valor absoluto, con ambos signos', () => {
    // 5 × 10 % = 0,5; 15 × 10 % = 1,5; 35 × 10 % = 3,5 (el redondeo bancario daría 0, 2 y 4)
    expect(calcularAjusteManual(5, 10)).toBe(1)
    expect(calcularAjusteManual(5, -10)).toBe(-1)
    expect(calcularAjusteManual(15, 10)).toBe(2)
    expect(calcularAjusteManual(15, -10)).toBe(-2)
    expect(calcularAjusteManual(35, 10)).toBe(4)
    expect(calcularAjusteManual(35, -10)).toBe(-4)
  })

  // Empates exactos en los que `neto * porcentaje / 100` en punto flotante cae por DEBAJO del medio
  // peso y redondea para el otro lado que el servidor (que calcula en decimal): 375 × 9,2 % es
  // 34,5 exacto pero el producto flotante da 34.49999999999999. Los casos de arriba no los
  // distinguen: la fórmula flotante también da 0,5, 1,5 y 3,5 en ellos.
  it.each([
    [375, 9.2, 35],
    [375, 16.4, 62],
    [250, 64.6, 162],
  ])('un empate que el punto flotante pierde: %s × %s por ciento da %s, y el espejo negativo su opuesto', (neto, porcentaje, esperado) => {
    expect((neto * porcentaje) / 100).toBeLessThan(esperado - 0.5)
    expect(calcularAjusteManual(neto, porcentaje)).toBe(esperado)
    expect(calcularAjusteManual(neto, -porcentaje)).toBe(-esperado)
  })

  it('lo que no llega a medio peso redondea a 0 y nunca a -0', () => {
    const resultado = calcularAjusteManual(4, -10)
    expect(resultado).toBe(0)
    expect(Object.is(resultado, -0)).toBe(false)
  })

  it('un neto negativo (línea de devolución) invierte el signo del monto, no el del porcentaje', () => {
    expect(calcularAjusteManual(-100, -10)).toBe(10)
    expect(calcularAjusteManual(-100, 10)).toBe(-10)
    expect(calcularAjusteManual(-1000, 0.15)).toBe(-2)
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

  it('la oferta x3 por 3500 (1166,66 por unidad) cobra 3500 y no 3499,98', () => {
    expect(calcularTotalesDeLinea({ cantidad: 3, precioOriginal: 1500, descuentoUnitario: 333.34, porcentaje: null })).toEqual({
      bruto: 4500,
      descuento: 1000,
      neto: 3500,
      ajuste: 0,
      total: 3500,
    })
  })

  it('el neto es el neto verdadero redondeado una vez y el descuento es bruto − neto', () => {
    // 7 × 1166,66 = 8166,62 → 8167; el descuento verdadero 7 × 333,34 = 2333,38 no se redondea
    // por su cuenta: es 10500 − 8167.
    const totales = calcularTotalesDeLinea({ cantidad: 7, precioOriginal: 1500, descuentoUnitario: 333.34, porcentaje: null })
    expect(totales).toEqual({ bruto: 10500, descuento: 2333, neto: 8167, ajuste: 0, total: 8167 })
  })

  it('el medio del neto se aleja de cero aunque la resta flotante quede por debajo', () => {
    // 2,3 − 0,8 = 1,5 exacto; en flotante da 1.4999999999999998 y redondearía a 1.
    expect(2.3 - 0.8).toBeLessThan(1.5)
    const totales = calcularTotalesDeLinea({ cantidad: 1, precioOriginal: 2.3, descuentoUnitario: 0.8, porcentaje: null })
    expect(totales.neto).toBe(2)
    expect(totales.total).toBe(2)
    expect(totales.bruto).toBe(2)
    expect(totales.descuento).toBe(0)
  })

  // El servidor calcula en decimal. 0,7 × 45 es 31,5 exacto y sube a 32, pero el producto
  // flotante da 31.499999999999996 y se redondeaba a 31: un peso de diferencia en el importe que
  // el cajero ve contra el que cobra el servidor.
  it('el bruto es el producto decimal exacto: un empate de medio peso sube aunque el producto flotante quede por debajo', () => {
    expect(0.7 * 45).toBeLessThan(31.5)
    const totales = calcularTotalesDeLinea({ cantidad: 0.7, precioOriginal: 45, descuentoUnitario: 0, porcentaje: null })
    expect(totales.bruto).toBe(32)
    expect(totales.neto).toBe(32)
    expect(totales.total).toBe(32)
  })

  it('una cantidad pesada de 3 decimales redondea el neto una sola vez', () => {
    // 0,375 × 1999,99 = 749,99625 → 750; con +7,5 % el ajuste 56,25 baja a 56.
    expect(calcularTotalesDeLinea({ cantidad: 0.375, precioOriginal: 1999.99, descuentoUnitario: 0, porcentaje: 7.5 })).toEqual({
      bruto: 750,
      descuento: 0,
      neto: 750,
      ajuste: 56,
      total: 806,
    })
  })

  it('un descuento por unidad de 4 decimales se descuenta una sola vez, sobre el producto de la línea', () => {
    // 3 × (10 − 0,3333) = 29,0001 → 29; el descuento es 30 − 29 = 1.
    const totales = calcularTotalesDeLinea({ cantidad: 3, precioOriginal: 10, descuentoUnitario: 0.3333, porcentaje: null })
    expect(totales.descuento).toBe(1)
    expect(totales.neto).toBe(29)
  })

  it('una línea de devolución (cantidad negativa) redondea el medio alejándose de cero', () => {
    expect(calcularTotalesDeLinea({ cantidad: -3, precioOriginal: 100, descuentoUnitario: 10, porcentaje: null })).toEqual({
      bruto: -300,
      descuento: -30,
      neto: -270,
      ajuste: 0,
      total: -270,
    })
    expect(calcularTotalesDeLinea({ cantidad: -1, precioOriginal: 100.5, descuentoUnitario: 0, porcentaje: null }).total).toBe(-101)
  })

  // Mismas entradas y mismos resultados que CalculadorDeTotalesTests (servidor): si una tabla
  // cambia sin la otra, la vista previa y el cobro dejan de coincidir.
  it.each([
    [{ cantidad: 3, precioOriginal: 1500, descuentoUnitario: 333.34, porcentaje: -15 }, { bruto: 4500, descuento: 1000, neto: 3500, ajuste: -525, total: 2975 }],
    [{ cantidad: 1, precioOriginal: 999.5, descuentoUnitario: 0, porcentaje: 0.15 }, { bruto: 1000, descuento: 0, neto: 1000, ajuste: 2, total: 1002 }],
    [{ cantidad: -1, precioOriginal: 1000, descuentoUnitario: 0, porcentaje: 0.15 }, { bruto: -1000, descuento: 0, neto: -1000, ajuste: -2, total: -1002 }],
    [{ cantidad: 1, precioOriginal: 5, descuentoUnitario: 0, porcentaje: -10 }, { bruto: 5, descuento: 0, neto: 5, ajuste: -1, total: 4 }],
    [{ cantidad: 3, precioOriginal: 33.33, descuentoUnitario: 1, porcentaje: -100 }, { bruto: 100, descuento: 3, neto: 97, ajuste: -97, total: 0 }],
    [{ cantidad: 1, precioOriginal: 11, descuentoUnitario: 1.5, porcentaje: null }, { bruto: 11, descuento: 1, neto: 10, ajuste: 0, total: 10 }],
  ])('paridad con el servidor: %j', (entrada, esperado) => {
    expect(calcularTotalesDeLinea(entrada)).toEqual(esperado)
  })
})

describe('calcularTotalesDeLinea con unidades de combo', () => {
  it.each([
    // el combo consumió toda la línea: cobra lista menos su parte
    [{ cantidad: 1, precioOriginal: 1000, descuentoUnitario: 0, unidadesConsumidasPorCombo: 1, ahorroDeCombo: 333 }, { bruto: 1000, descuento: 333, neto: 667, ajuste: 0, total: 667 }],
    // la oferta unitaria solo alcanza a la unidad que el combo no consumió
    [{ cantidad: 3, precioOriginal: 1000, descuentoUnitario: 100, unidadesConsumidasPorCombo: 2, ahorroDeCombo: 800 }, { bruto: 3000, descuento: 900, neto: 2100, ajuste: 0, total: 2100 }],
    // cantidad fraccionaria con unidades enteras de combo
    [{ cantidad: 2.5, precioOriginal: 100.5, descuentoUnitario: 0.5, unidadesConsumidasPorCombo: 2, ahorroDeCombo: 100 }, { bruto: 251, descuento: 100, neto: 151, ajuste: 0, total: 151 }],
    // empate exacto en la parte del combo: 3 × 0,35 − 0,55 = 0,5 sube a 1
    [{ cantidad: 3, precioOriginal: 0.35, descuentoUnitario: 0, unidadesConsumidasPorCombo: 3, ahorroDeCombo: 0.55 }, { bruto: 1, descuento: 0, neto: 1, ajuste: 0, total: 1 }],
    // el ajuste manual se aplica sobre el neto con combo
    [{ cantidad: 3, precioOriginal: 1000, descuentoUnitario: 100, unidadesConsumidasPorCombo: 2, ahorroDeCombo: 800, porcentaje: -10 }, { bruto: 3000, descuento: 900, neto: 2100, ajuste: -210, total: 1890 }],
  ])('paridad con CalculadorDeTotales: %j', (entrada, esperado) => {
    expect(calcularTotalesDeLinea({ porcentaje: null, ...entrada })).toEqual(esperado)
  })

  it('sin datos de combo el resultado es el de siempre', () => {
    const base = { cantidad: 3, precioOriginal: 1500, descuentoUnitario: 333.34, porcentaje: null }
    expect(calcularTotalesDeLinea({ ...base, unidadesConsumidasPorCombo: 0, ahorroDeCombo: 0 })).toEqual(calcularTotalesDeLinea(base))
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

describe('formatearPorcentajeDeAjusteConSigno', () => {
  it('el descuento lleva el menos y el recargo no lleva más, con coma y sin ceros de relleno', () => {
    expect(formatearPorcentajeDeAjusteConSigno(-10)).toBe('-10')
    expect(formatearPorcentajeDeAjusteConSigno(15)).toBe('15')
    expect(formatearPorcentajeDeAjusteConSigno(12.5)).toBe('12,5')
    expect(formatearPorcentajeDeAjusteConSigno(-0.25)).toBe('-0,25')
    expect(formatearPorcentajeDeAjusteConSigno(-100)).toBe('-100')
  })

  it('lo que formatea vuelve a validarse como el mismo porcentaje', () => {
    for (const porcentaje of [-100, -33.33, -0.01, 0.01, 12.5, 100]) {
      expect(validarPorcentajeDeAjuste(formatearPorcentajeDeAjusteConSigno(porcentaje))).toEqual({ ok: true, porcentaje })
    }
  })
})

describe('validarPorcentajeDeAjuste', () => {
  it('sin signo o con más es un recargo y con menos es un descuento', () => {
    expect(validarPorcentajeDeAjuste('10')).toEqual({ ok: true, porcentaje: 10 })
    expect(validarPorcentajeDeAjuste('+10')).toEqual({ ok: true, porcentaje: 10 })
    expect(validarPorcentajeDeAjuste('-10')).toEqual({ ok: true, porcentaje: -10 })
  })

  it('acepta coma o punto decimal, hasta 2 decimales, y recorta los espacios', () => {
    expect(validarPorcentajeDeAjuste('12,5')).toEqual({ ok: true, porcentaje: 12.5 })
    expect(validarPorcentajeDeAjuste('-12.5')).toEqual({ ok: true, porcentaje: -12.5 })
    expect(validarPorcentajeDeAjuste('0,01')).toEqual({ ok: true, porcentaje: 0.01 })
    expect(validarPorcentajeDeAjuste('  -33,33 ')).toEqual({ ok: true, porcentaje: -33.33 })
  })

  it('el tope es 100 inclusive, con ambos signos', () => {
    expect(validarPorcentajeDeAjuste('-100')).toEqual({ ok: true, porcentaje: -100 })
    expect(validarPorcentajeDeAjuste('100,00')).toEqual({ ok: true, porcentaje: 100 })
    expect(validarPorcentajeDeAjuste('+100')).toEqual({ ok: true, porcentaje: 100 })
  })

  it.each(['', '   ', '0', '-0', '+0', '0,00', '0.0', '-0,00'])('%j quita el ajuste (vacío o cero)', (texto) => {
    expect(validarPorcentajeDeAjuste(texto)).toEqual({ ok: true, porcentaje: null })
  })

  it.each(['abc', '-', '+', '--5', '+-5', '- 5', '10,555', '10,', ',5', '1e1', '10 %', '1.000', '1,5,5'])(
    'rechaza por formato %j',
    (texto) => {
      expect(validarPorcentajeDeAjuste(texto)).toEqual({
        ok: false,
        mensaje:
          'Ingresá el porcentaje con hasta 2 decimales: negativo para descuento, positivo para recargo (por ejemplo -10 o 12,5).',
      })
    },
  )

  it.each(['101', '-101', '100,01', '-100,01', '999'])('rechaza por rango %j (como máximo 100 en valor absoluto)', (texto) => {
    expect(validarPorcentajeDeAjuste(texto)).toEqual({ ok: false, mensaje: 'El porcentaje debe estar entre -100 y 100.' })
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
