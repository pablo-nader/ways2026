import { describe, expect, it } from 'vitest'
import type { LineaCarrito } from './carrito'
import {
  aLineaDeCarritoDesdeEscaneo,
  aLineasDeResolucion,
  aSolicitudDeVenta,
  calcularAjustesManualesPrevia,
  calcularSubtotalPrevia,
  indexarResolucionPorLinea,
  opcionDeLote,
  previaDeLinea,
} from './ventas'
import type { ArticuloEscaneado, LoteListado, PagoDeVenta, ResultadoDeResolucion } from './tipos'

function articuloEscaneadoFixture(sobrescribir: Partial<ArticuloEscaneado> = {}): ArticuloEscaneado {
  return { idArticulo: 1, codigoInterno: 'A0001', nombre: 'Coca Cola 1L', codigoBarra: '7790001234567', cantidad: 1, acumulaEnVenta: true, ...sobrescribir }
}

/** `idLinea` por defecto = el `idArticulo` como texto: en los casos de una línea por artículo los
 * índices `{ 1: … }` siguen leyéndose igual. Los casos de dos líneas del mismo artículo pasan
 * `idLinea` explícito. */
function lineaFixture(sobrescribir: Partial<LineaCarrito> = {}): LineaCarrito {
  return {
    idLinea: String(sobrescribir.idArticulo ?? 1),
    idArticulo: 1,
    codigoInterno: 'A0001',
    nombre: 'Coca Cola 1L',
    codigoBarra: '7790001234567',
    acumulaEnVenta: true,
    cantidad: 2,
    ...sobrescribir,
  }
}

function resultadoFixture(sobrescribir: Partial<ResultadoDeResolucion> = {}): ResultadoDeResolucion {
  return { idArticulo: 1, idListaPrecio: 3, precioOriginal: 100, precioFinal: 90, descuentoUnitario: 10, aplicadas: [], ...sobrescribir }
}

describe('aLineaDeCarritoDesdeEscaneo', () => {
  it('separa la cantidad parseada del resto de los datos de identidad', () => {
    const resultado = aLineaDeCarritoDesdeEscaneo(articuloEscaneadoFixture({ cantidad: 3 }))

    expect(resultado).toEqual({
      linea: { idArticulo: 1, codigoInterno: 'A0001', nombre: 'Coca Cola 1L', codigoBarra: '7790001234567', acumulaEnVenta: true },
      cantidad: 3,
    })
  })

  it('lleva acumulaEnVenta: false del artículo escaneado', () => {
    expect(aLineaDeCarritoDesdeEscaneo(articuloEscaneadoFixture({ acumulaEnVenta: false })).linea.acumulaEnVenta).toBe(false)
  })

  it('una respuesta sin acumulaEnVenta (servidor anterior al campo) acumula', () => {
    const { acumulaEnVenta: _omitido, ...sinCampo } = articuloEscaneadoFixture()
    expect(aLineaDeCarritoDesdeEscaneo(sinCampo as ArticuloEscaneado).linea.acumulaEnVenta).toBe(true)
  })

  it('lleva la unidad de venta del escaneo a la línea del carrito', () => {
    expect(aLineaDeCarritoDesdeEscaneo(articuloEscaneadoFixture({ unidadVenta: 'Unidad' })).linea.unidadVenta).toBe('Unidad')
    expect(aLineaDeCarritoDesdeEscaneo(articuloEscaneadoFixture({ unidadVenta: 'Peso' })).linea.unidadVenta).toBe('Peso')
  })

  it('un escaneo sin unidad de venta (instantánea anterior) deja la línea sin unidad: permisivo', () => {
    expect(aLineaDeCarritoDesdeEscaneo(articuloEscaneadoFixture()).linea.unidadVenta).toBeUndefined()
  })

  it('preserva codigoBarra null cuando el escaneo resolvió por codigo_interno', () => {
    const resultado = aLineaDeCarritoDesdeEscaneo(articuloEscaneadoFixture({ codigoBarra: null }))

    expect(resultado.linea.codigoBarra).toBeNull()
  })
})

describe('aLineasDeResolucion', () => {
  it('proyecta cada línea del carrito con el idListaPrecio/idEmpresa del lote', () => {
    const lineas = [lineaFixture({ idArticulo: 1, cantidad: 2 }), lineaFixture({ idArticulo: 2, cantidad: 5 })]

    const resultado = aLineasDeResolucion(lineas, 3, 7)

    expect(resultado).toEqual([
      { idArticulo: 1, idEmpresa: 7, idListaPrecio: 3, cantidad: 2 },
      { idArticulo: 2, idEmpresa: 7, idListaPrecio: 3, cantidad: 5 },
    ])
  })

  it('idEmpresa null viaja tal cual (artículo disponible para todas las empresas)', () => {
    const resultado = aLineasDeResolucion([lineaFixture()], 3, null)

    expect(resultado[0].idEmpresa).toBeNull()
  })

  it('un carrito vacío produce un lote vacío', () => {
    expect(aLineasDeResolucion([], 3, 7)).toEqual([])
  })
})

describe('indexarResolucionPorLinea', () => {
  it('indexa por idLinea según la posición de cada línea en el request', () => {
    const lineas = [lineaFixture({ idLinea: 'x', idArticulo: 1 }), lineaFixture({ idLinea: 'y', idArticulo: 2 })]
    const resultados = [resultadoFixture({ idArticulo: 1 }), resultadoFixture({ idArticulo: 2, precioFinal: 50 })]

    const indice = indexarResolucionPorLinea(lineas, resultados)

    expect(indice).toEqual({ x: resultados[0], y: resultados[1] })
  })

  it('dos líneas del mismo artículo conservan cada una su propio resultado', () => {
    const lineas = [lineaFixture({ idLinea: 'a', cantidad: 1 }), lineaFixture({ idLinea: 'b', cantidad: 6 })]
    const resultados = [resultadoFixture({ precioFinal: 100, descuentoUnitario: 0 }), resultadoFixture({ precioFinal: 80, descuentoUnitario: 20 })]

    const indice = indexarResolucionPorLinea(lineas, resultados)

    expect(indice.a.precioFinal).toBe(100)
    expect(indice.b.precioFinal).toBe(80)
  })

  it('una posición cuyo idArticulo no coincide con la línea queda sin precio', () => {
    const lineas = [lineaFixture({ idLinea: 'x', idArticulo: 1 }), lineaFixture({ idLinea: 'y', idArticulo: 2 })]
    const resultados = [resultadoFixture({ idArticulo: 2 }), resultadoFixture({ idArticulo: 2 })]

    expect(indexarResolucionPorLinea(lineas, resultados)).toEqual({ y: resultados[1] })
  })

  it('un lote vacío produce un índice vacío', () => {
    expect(indexarResolucionPorLinea([], [])).toEqual({})
  })
})

describe('previaDeLinea', () => {
  it('sin resultado (todavía no resolvió) devuelve todo null/0', () => {
    expect(previaDeLinea(lineaFixture(), undefined)).toEqual({ precioUnitario: null, descuentoUnitario: 0, total: null, ajusteManual: 0, ahorroDeCombo: 0 })
  })

  it('con precioFinal null (sin precio vigente) devuelve todo null/0, aunque la línea tenga ajuste', () => {
    const resultado = resultadoFixture({ precioOriginal: null, precioFinal: null, descuentoUnitario: 0 })

    expect(previaDeLinea(lineaFixture({ ajusteManualPorcentaje: -10 }), resultado)).toEqual({
      precioUnitario: null,
      descuentoUnitario: 0,
      total: null,
      ajusteManual: 0,
      ahorroDeCombo: 0,
    })
  })

  it('el total de línea es bruto − descuento por oferta (ya neto por unidad), sin ajuste', () => {
    const linea = lineaFixture({ cantidad: 3 })
    const resultado = resultadoFixture({ precioFinal: 90, descuentoUnitario: 10 })

    expect(previaDeLinea(linea, resultado)).toEqual({ precioUnitario: 90, descuentoUnitario: 10, total: 270, ajusteManual: 0, ahorroDeCombo: 0 })
  })

  it('un descuento manual es negativo y se resta del neto', () => {
    const linea = lineaFixture({ cantidad: 3, ajusteManualPorcentaje: -10 })
    const resultado = resultadoFixture({ precioOriginal: 100, precioFinal: 90, descuentoUnitario: 10 })

    expect(previaDeLinea(linea, resultado)).toMatchObject({ total: 243, ajusteManual: -27 })
  })

  it('un recargo manual es positivo y se suma al neto', () => {
    const linea = lineaFixture({ cantidad: 3, ajusteManualPorcentaje: 15 })
    const resultado = resultadoFixture({ precioOriginal: 100, precioFinal: 90, descuentoUnitario: 10 })

    expect(previaDeLinea(linea, resultado)).toMatchObject({ total: 311, ajusteManual: 41 })
  })

  it('el ajuste se calcula sobre el neto posterior a las ofertas, no sobre el precio de lista', () => {
    const linea = lineaFixture({ cantidad: 1, ajusteManualPorcentaje: -10 })
    const resultado = resultadoFixture({ precioOriginal: 100, precioFinal: 80, descuentoUnitario: 20 })

    expect(previaDeLinea(linea, resultado)).toMatchObject({ total: 72, ajusteManual: -8 })
  })

  it('redondea el ajuste half-away-from-zero al peso, también con monto negativo (10,5 → 11, nunca 10)', () => {
    const resultado = resultadoFixture({ precioOriginal: 105, precioFinal: 105, descuentoUnitario: 0 })

    expect(previaDeLinea(lineaFixture({ cantidad: 1, ajusteManualPorcentaje: -10 }), resultado)).toMatchObject({
      ajusteManual: -11,
      total: 94,
    })
    expect(previaDeLinea(lineaFixture({ cantidad: 1, ajusteManualPorcentaje: 10 }), resultado)).toMatchObject({
      ajusteManual: 11,
      total: 116,
    })
  })

  it('con cantidad fraccionaria usa el bruto redondeado del servidor, no cantidad × precioFinal sin redondear', () => {
    const linea = lineaFixture({ cantidad: 0.333 })
    const resultado = resultadoFixture({ precioOriginal: 10.5, precioFinal: 10.5, descuentoUnitario: 0 })

    expect(previaDeLinea(linea, resultado).total).toBe(3)
  })

  it('un bruto que es empate exacto de medio peso sube como en el servidor: 0,7 × 45 = 32, no 31', () => {
    const resultado = resultadoFixture({ precioOriginal: 45, precioFinal: 45, descuentoUnitario: 0 })

    expect(previaDeLinea(lineaFixture({ cantidad: 0.7 }), resultado).total).toBe(32)
  })

  it('la oferta x3 por 3500 (1166,66 por unidad) previsualiza 3500, no 3499,98', () => {
    const resultado = resultadoFixture({ precioOriginal: 1500, precioFinal: 1166.66, descuentoUnitario: 333.34 })

    expect(previaDeLinea(lineaFixture({ cantidad: 3 }), resultado)).toMatchObject({ total: 3500, ajusteManual: 0 })
  })

  it('sin precioOriginal reconstruye el precio de lista como precioFinal + descuentoUnitario', () => {
    const linea = lineaFixture({ cantidad: 2, ajusteManualPorcentaje: -50 })
    const resultado = resultadoFixture({ precioOriginal: null, precioFinal: 90, descuentoUnitario: 10 })

    expect(previaDeLinea(linea, resultado)).toMatchObject({ total: 90, ajusteManual: -90 })
  })
})

describe('calcularSubtotalPrevia', () => {
  it('null mientras no hay ningún precio resuelto (primera carga)', () => {
    expect(calcularSubtotalPrevia([lineaFixture()], {})).toBeNull()
  })

  it('suma el total previsualizado de cada línea resuelta', () => {
    const lineas = [lineaFixture({ idArticulo: 1, cantidad: 2 }), lineaFixture({ idArticulo: 2, cantidad: 1 })]
    const precios = {
      1: resultadoFixture({ idArticulo: 1, precioFinal: 90 }),
      2: resultadoFixture({ idArticulo: 2, precioOriginal: 60, precioFinal: 50, descuentoUnitario: 10 }),
    }

    expect(calcularSubtotalPrevia(lineas, precios)).toBe(230)
  })

  it('incluye los ajustes manuales: es lo que el cliente tiene que pagar', () => {
    const lineas = [
      lineaFixture({ idArticulo: 1, cantidad: 2, ajusteManualPorcentaje: -10 }),
      lineaFixture({ idArticulo: 2, cantidad: 1, ajusteManualPorcentaje: 15 }),
      lineaFixture({ idArticulo: 3, cantidad: 1 }),
    ]
    const precios = {
      1: resultadoFixture({ idArticulo: 1, precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0 }),
      2: resultadoFixture({ idArticulo: 2, precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0 }),
      3: resultadoFixture({ idArticulo: 3, precioOriginal: 40, precioFinal: 40, descuentoUnitario: 0 }),
    }

    // 200 − 20 + 100 + 15 + 40
    expect(calcularSubtotalPrevia(lineas, precios)).toBe(335)
  })

  it('suma los totales ya redondeados al peso de cada línea, no la suma sin redondear (0,6 + 0,6 son 2, no 1)', () => {
    const lineas = [lineaFixture({ idArticulo: 1, cantidad: 1 }), lineaFixture({ idArticulo: 2, cantidad: 1 })]
    const precios = {
      1: resultadoFixture({ idArticulo: 1, precioOriginal: 0.6, precioFinal: 0.6, descuentoUnitario: 0 }),
      2: resultadoFixture({ idArticulo: 2, precioOriginal: 0.6, precioFinal: 0.6, descuentoUnitario: 0 }),
    }

    expect(calcularSubtotalPrevia(lineas, precios)).toBe(2)
  })

  it('lee el precio de cada línea por idLinea: dos líneas del mismo artículo suman cada una el suyo', () => {
    const lineas = [lineaFixture({ idLinea: 'a', cantidad: 1 }), lineaFixture({ idLinea: 'b', cantidad: 6 })]
    const precios = {
      a: resultadoFixture({ precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0 }),
      b: resultadoFixture({ precioOriginal: 100, precioFinal: 80, descuentoUnitario: 20 }),
    }

    // 1 × 100 + 6 × 80
    expect(calcularSubtotalPrevia(lineas, precios)).toBe(580)
  })

  it('una línea sin precio propio dentro de un lote parcial contribuye 0, no rompe la suma', () => {
    const lineas = [lineaFixture({ idArticulo: 1, cantidad: 2 }), lineaFixture({ idArticulo: 2, cantidad: 1 })]
    const precios = { 1: resultadoFixture({ idArticulo: 1, precioFinal: 90 }) }

    expect(calcularSubtotalPrevia(lineas, precios)).toBe(180)
  })
})

describe('calcularAjustesManualesPrevia', () => {
  const precios = {
    1: resultadoFixture({ idArticulo: 1, precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0 }),
    2: resultadoFixture({ idArticulo: 2, precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0 }),
  }

  it('sin ajustes ambos totales son 0', () => {
    expect(calcularAjustesManualesPrevia([lineaFixture({ idArticulo: 1 })], precios)).toEqual({
      descuentoManualTotal: 0,
      recargoManualTotal: 0,
    })
  })

  it('un recargo no oculta un descuento: cada uno se acumula por separado, ambos en positivo', () => {
    const lineas = [
      lineaFixture({ idArticulo: 1, cantidad: 2, ajusteManualPorcentaje: -10 }),
      lineaFixture({ idArticulo: 2, cantidad: 1, ajusteManualPorcentaje: 15 }),
    ]

    expect(calcularAjustesManualesPrevia(lineas, precios)).toEqual({ descuentoManualTotal: 20, recargoManualTotal: 15 })
  })

  it('acumula varias líneas del mismo signo', () => {
    const lineas = [
      lineaFixture({ idArticulo: 1, cantidad: 1, ajusteManualPorcentaje: -10 }),
      lineaFixture({ idArticulo: 2, cantidad: 1, ajusteManualPorcentaje: -5 }),
    ]

    expect(calcularAjustesManualesPrevia(lineas, precios)).toEqual({ descuentoManualTotal: 15, recargoManualTotal: 0 })
  })

  it('el ajuste de cada línea se calcula con el precio de ESA línea', () => {
    const lineas = [
      lineaFixture({ idLinea: 'a', cantidad: 1, ajusteManualPorcentaje: -10 }),
      lineaFixture({ idLinea: 'b', cantidad: 1, ajusteManualPorcentaje: -10 }),
    ]
    const porLinea = {
      a: resultadoFixture({ precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0 }),
      b: resultadoFixture({ precioOriginal: 300, precioFinal: 300, descuentoUnitario: 0 }),
    }

    expect(calcularAjustesManualesPrevia(lineas, porLinea)).toEqual({ descuentoManualTotal: 40, recargoManualTotal: 0 })
  })

  it('una línea sin precio resuelto no aporta ajuste', () => {
    const lineas = [lineaFixture({ idArticulo: 1, ajusteManualPorcentaje: -10 }), lineaFixture({ idArticulo: 9, ajusteManualPorcentaje: -50 })]

    expect(calcularAjustesManualesPrevia(lineas, precios)).toEqual({ descuentoManualTotal: 20, recargoManualTotal: 0 })
  })
})

describe('aSolicitudDeVenta', () => {
  it('mapea el carrito confirmado sin ningún campo de dinero por línea (design decisión 3)', () => {
    const lineas = [lineaFixture({ idArticulo: 1, cantidad: 2, codigoBarra: '7790001234567' })]
    const pagos: PagoDeVenta[] = [{ idMedioPago: 1, importe: 180, referencia: null, vuelto: 0 }]

    const resultado = aSolicitudDeVenta({
      idPuntoVenta: 7,
      idCliente: 1,
      codigoTipoComprobante: 'TX',
      idComprobanteAsociado: null,
      lineas,
      lotesSeleccionados: {},
      pagos,
      direccionEntrega: null,
      observaciones: null,
    })

    expect(resultado).toEqual({
      idPuntoVenta: 7,
      idCliente: 1,
      codigoTipoComprobante: 'TX',
      idComprobanteAsociado: null,
      lineas: [{ idArticulo: 1, cantidad: 2, codigoBarra: '7790001234567', idLote: null }],
      pagos,
      direccionEntrega: null,
      observaciones: null,
    })
    expect(resultado.lineas[0]).not.toHaveProperty('precioUnitario')
  })

  it('NCX lleva idComprobanteAsociado cuando referencia un TX original', () => {
    const resultado = aSolicitudDeVenta({
      idPuntoVenta: 7,
      idCliente: 1,
      codigoTipoComprobante: 'NCX',
      idComprobanteAsociado: 42,
      lineas: [lineaFixture({ cantidad: -2 })],
      lotesSeleccionados: {},
      pagos: [],
      direccionEntrega: null,
      observaciones: null,
    })

    expect(resultado.codigoTipoComprobante).toBe('NCX')
    expect(resultado.idComprobanteAsociado).toBe(42)
    expect(resultado.lineas[0].cantidad).toBe(-2)
  })

  it('camino feliz (design decisión 19): una línea sin selección explícita viaja con idLote null', () => {
    const resultado = aSolicitudDeVenta({
      idPuntoVenta: 7,
      idCliente: 1,
      codigoTipoComprobante: 'TX',
      idComprobanteAsociado: null,
      lineas: [lineaFixture({ idArticulo: 1 })],
      lotesSeleccionados: {},
      pagos: [],
      direccionEntrega: null,
      observaciones: null,
    })

    expect(resultado.lineas[0].idLote).toBeNull()
  })

  it('el ajuste manual viaja SOLO en la línea que lo tiene, con su signo, y es lo único de dinero', () => {
    const resultado = aSolicitudDeVenta({
      idPuntoVenta: 7,
      idCliente: 1,
      codigoTipoComprobante: 'TX',
      idComprobanteAsociado: null,
      lineas: [
        lineaFixture({ idArticulo: 1, ajusteManualPorcentaje: -10 }),
        lineaFixture({ idArticulo: 2, ajusteManualPorcentaje: 15.5 }),
        lineaFixture({ idArticulo: 3 }),
        lineaFixture({ idArticulo: 4, ajusteManualPorcentaje: null }),
      ],
      lotesSeleccionados: {},
      pagos: [],
      direccionEntrega: null,
      observaciones: null,
    })

    expect(resultado.lineas[0].ajusteManualPorcentaje).toBe(-10)
    expect(resultado.lineas[1].ajusteManualPorcentaje).toBe(15.5)
    expect(resultado.lineas[2]).not.toHaveProperty('ajusteManualPorcentaje')
    expect(resultado.lineas[3]).not.toHaveProperty('ajusteManualPorcentaje')
    expect(resultado.lineas[0]).not.toHaveProperty('precioUnitario')
    expect(resultado.lineas[0]).not.toHaveProperty('ajusteManual')
  })

  it('un carrito sin ajustes serializa idéntico al de antes de existir el campo', () => {
    const resultado = aSolicitudDeVenta({
      idPuntoVenta: 7,
      idCliente: 1,
      codigoTipoComprobante: 'TX',
      idComprobanteAsociado: null,
      lineas: [lineaFixture({ idArticulo: 1, cantidad: 2, codigoBarra: '7790001234567' })],
      lotesSeleccionados: {},
      pagos: [],
      direccionEntrega: null,
      observaciones: null,
    })

    expect(JSON.parse(JSON.stringify(resultado.lineas))).toEqual([{ idArticulo: 1, cantidad: 2, codigoBarra: '7790001234567', idLote: null }])
  })

  it('una elección explícita de lote viaja en idLote de esa línea', () => {
    const resultado = aSolicitudDeVenta({
      idPuntoVenta: 7,
      idCliente: 1,
      codigoTipoComprobante: 'TX',
      idComprobanteAsociado: null,
      lineas: [lineaFixture({ idLinea: 'a', idArticulo: 1 }), lineaFixture({ idLinea: 'b', idArticulo: 1 })],
      lotesSeleccionados: { a: 55 },
      pagos: [],
      direccionEntrega: null,
      observaciones: null,
    })

    expect(resultado.lineas[0].idLote).toBe(55)
    expect(resultado.lineas[1].idLote).toBeNull()
  })
})

describe('opcionDeLote', () => {
  function loteFixture(sobrescribir: Partial<LoteListado> = {}): LoteListado {
    return {
      idLote: 3,
      idArticulo: 1,
      codigo: '2026-11-30',
      fechaVencimiento: '2026-11-30',
      esSinIdentificar: false,
      cantidad: 12,
      estado: 'Vigente',
      sugerido: false,
      ...sobrescribir,
    }
  }

  it('arma la etiqueta con código, estado y saldo', () => {
    const opcion = opcionDeLote(loteFixture())
    expect(opcion).toEqual({ valor: '3', etiqueta: '2026-11-30 — vigente — saldo 12' })
  })

  it('un lote sugerido lo marca en la etiqueta — nunca lo resuelve de nuevo', () => {
    const opcion = opcionDeLote(loteFixture({ sugerido: true }))
    expect(opcion.etiqueta).toMatch(/— sugerido$/)
  })

  it('el lote sin identificar muestra su propio texto en vez del código reservado', () => {
    const opcion = opcionDeLote(loteFixture({ esSinIdentificar: true, codigo: 'SIN-IDENTIFICAR', fechaVencimiento: null }))
    expect(opcion.etiqueta).toBe('Sin identificar — vigente — saldo 12')
  })
})

describe('artículo por importe', () => {
  const porImporte = (sobrescribir: Partial<LineaCarrito> = {}) =>
    lineaFixture({ idLinea: 'f1', idArticulo: 500, cantidad: 1, acumulaEnVenta: false, unidadVenta: 'Importe', importe: 150.5, ...sobrescribir })

  describe('aLineaDeCarritoDesdeEscaneo', () => {
    it('lleva el importe a la línea, con cantidad 1 y sin acumular aunque el escaneo diga que acumula', () => {
      const resultado = aLineaDeCarritoDesdeEscaneo(
        articuloEscaneadoFixture({ idArticulo: 500, unidadVenta: 'Importe', importe: 150, cantidad: 1, acumulaEnVenta: true }),
      )

      expect(resultado.cantidad).toBe(1)
      expect(resultado.linea).toMatchObject({ idArticulo: 500, unidadVenta: 'Importe', importe: 150, acumulaEnVenta: false })
    })

    it('un artículo común nunca lleva importe, aunque el escaneo traiga uno', () => {
      const { linea } = aLineaDeCarritoDesdeEscaneo(articuloEscaneadoFixture({ unidadVenta: 'Unidad', importe: 150 }))

      expect(linea).not.toHaveProperty('importe')
    })

    it('un importe nulo del servidor no se convierte en importe de la línea', () => {
      const { linea } = aLineaDeCarritoDesdeEscaneo(articuloEscaneadoFixture({ unidadVenta: 'Importe', importe: null }))

      expect(linea).not.toHaveProperty('importe')
    })
  })

  describe('aLineasDeResolucion', () => {
    it('manda el importe solo en la línea por importe', () => {
      const resultado = aLineasDeResolucion([lineaFixture({ idArticulo: 1, cantidad: 2 }), porImporte()], 3, 9)

      expect(resultado[0]).not.toHaveProperty('importe')
      expect(resultado[1]).toEqual({ idArticulo: 500, idEmpresa: 9, idListaPrecio: 3, cantidad: 1, importe: 150.5 })
    })
  })

  describe('previaDeLinea', () => {
    it('el total es el importe al peso entero, sin necesitar precio de lista (150,50 cobra 151)', () => {
      const previa = previaDeLinea(porImporte(), undefined)

      expect(previa).toEqual({ precioUnitario: 150.5, descuentoUnitario: 0, total: 151, ajusteManual: 0, ahorroDeCombo: 0 })
    })

    it('ignora cualquier precio resuelto por una lista', () => {
      const previa = previaDeLinea(porImporte({ importe: 150 }), resultadoFixture({ idArticulo: 500, precioOriginal: 999, precioFinal: 999, descuentoUnitario: 0 }))

      expect(previa.precioUnitario).toBe(150)
      expect(previa.total).toBe(150)
    })

    it('el ajuste manual se aplica sobre el importe', () => {
      const previa = previaDeLinea(porImporte({ importe: 150, ajusteManualPorcentaje: -10 }), undefined)

      expect(previa).toMatchObject({ total: 135, ajusteManual: -15 })
    })

    it('el subtotal suma la línea por importe junto con las de lista', () => {
      const lineas = [lineaFixture({ idArticulo: 1, cantidad: 2 }), porImporte({ importe: 150 })]

      expect(calcularSubtotalPrevia(lineas, { 1: resultadoFixture() })).toBe(2 * 90 + 150)
    })
  })

  describe('aSolicitudDeVenta', () => {
    const armar = (lineas: LineaCarrito[]) =>
      aSolicitudDeVenta({
        idPuntoVenta: 7,
        idCliente: 1,
        codigoTipoComprobante: 'TX',
        idComprobanteAsociado: null,
        lineas,
        lotesSeleccionados: {},
        pagos: [],
        direccionEntrega: null,
        observaciones: null,
      }).lineas

    it('manda el importe solo en la línea por importe y nunca precios', () => {
      const [comun, importe] = armar([lineaFixture({ idArticulo: 1, cantidad: 2 }), porImporte({ importe: 150.5 })])

      expect(comun).not.toHaveProperty('importe')
      expect(importe).toEqual({ idArticulo: 500, cantidad: 1, codigoBarra: '7790001234567', idLote: null, importe: 150.5 })
      expect(importe).not.toHaveProperty('precioUnitario')
    })

    it('el ajuste manual viaja junto al importe', () => {
      const [importe] = armar([porImporte({ importe: 150, ajusteManualPorcentaje: -10 })])

      expect(importe).toMatchObject({ importe: 150, ajusteManualPorcentaje: -10 })
    })
  })
})

describe('calcularSubtotalPrevia — solo líneas por importe', () => {
  it('un carrito de líneas por importe tiene subtotal sin esperar ningún precio resuelto', () => {
    const lineas = [lineaFixture({ idLinea: 'f1', idArticulo: 500, cantidad: 1, importe: 150 }), lineaFixture({ idLinea: 'f2', idArticulo: 500, cantidad: 1, importe: 80 })]

    expect(calcularSubtotalPrevia(lineas, {})).toBe(230)
  })

  it('con una línea de lista sin resolver sigue sin haber subtotal', () => {
    const lineas = [lineaFixture({ idArticulo: 1 }), lineaFixture({ idLinea: 'f1', idArticulo: 500, cantidad: 1, importe: 150 })]

    expect(calcularSubtotalPrevia(lineas, {})).toBeNull()
  })
})
