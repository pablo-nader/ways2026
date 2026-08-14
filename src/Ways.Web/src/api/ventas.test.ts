import { describe, expect, it } from 'vitest'
import type { LineaCarrito } from './carrito'
import {
  aLineaDeCarritoDesdeEscaneo,
  aLineasDeResolucion,
  aSolicitudDeVenta,
  calcularSubtotalPrevia,
  indexarResolucionPorArticulo,
  opcionDeLote,
  previaDeLinea,
} from './ventas'
import type { ArticuloEscaneado, LoteListado, PagoDeVenta, ResultadoDeResolucion } from './tipos'

function articuloEscaneadoFixture(sobrescribir: Partial<ArticuloEscaneado> = {}): ArticuloEscaneado {
  return { idArticulo: 1, codigoInterno: 'A0001', nombre: 'Coca Cola 1L', codigoBarra: '7790001234567', cantidad: 1, ...sobrescribir }
}

function lineaFixture(sobrescribir: Partial<LineaCarrito> = {}): LineaCarrito {
  return { idArticulo: 1, codigoInterno: 'A0001', nombre: 'Coca Cola 1L', codigoBarra: '7790001234567', cantidad: 2, ...sobrescribir }
}

function resultadoFixture(sobrescribir: Partial<ResultadoDeResolucion> = {}): ResultadoDeResolucion {
  return { idArticulo: 1, idListaPrecio: 3, precioOriginal: 100, precioFinal: 90, descuentoUnitario: 10, aplicadas: [], ...sobrescribir }
}

describe('aLineaDeCarritoDesdeEscaneo', () => {
  it('separa la cantidad parseada del resto de los datos de identidad', () => {
    const resultado = aLineaDeCarritoDesdeEscaneo(articuloEscaneadoFixture({ cantidad: 3 }))

    expect(resultado).toEqual({
      linea: { idArticulo: 1, codigoInterno: 'A0001', nombre: 'Coca Cola 1L', codigoBarra: '7790001234567' },
      cantidad: 3,
    })
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

describe('indexarResolucionPorArticulo', () => {
  it('indexa por idArticulo para lookup O(1)', () => {
    const resultados = [resultadoFixture({ idArticulo: 1 }), resultadoFixture({ idArticulo: 2, precioFinal: 50 })]

    const indice = indexarResolucionPorArticulo(resultados)

    expect(indice[1]).toEqual(resultados[0])
    expect(indice[2].precioFinal).toBe(50)
  })

  it('un lote vacío produce un índice vacío', () => {
    expect(indexarResolucionPorArticulo([])).toEqual({})
  })
})

describe('previaDeLinea', () => {
  it('sin resultado (todavía no resolvió) devuelve todo null/0', () => {
    expect(previaDeLinea(lineaFixture(), undefined)).toEqual({ precioUnitario: null, descuentoUnitario: 0, total: null })
  })

  it('con precioFinal null (sin precio vigente) devuelve todo null/0', () => {
    const resultado = resultadoFixture({ precioOriginal: null, precioFinal: null, descuentoUnitario: 0 })

    expect(previaDeLinea(lineaFixture(), resultado)).toEqual({ precioUnitario: null, descuentoUnitario: 0, total: null })
  })

  it('el total de línea es cantidad × precioFinal (ya neto de descuento por unidad)', () => {
    const linea = lineaFixture({ cantidad: 3 })
    const resultado = resultadoFixture({ precioFinal: 90, descuentoUnitario: 10 })

    expect(previaDeLinea(linea, resultado)).toEqual({ precioUnitario: 90, descuentoUnitario: 10, total: 270 })
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
      2: resultadoFixture({ idArticulo: 2, precioFinal: 50 }),
    }

    expect(calcularSubtotalPrevia(lineas, precios)).toBe(230)
  })

  it('una línea sin precio propio dentro de un lote parcial contribuye 0, no rompe la suma', () => {
    const lineas = [lineaFixture({ idArticulo: 1, cantidad: 2 }), lineaFixture({ idArticulo: 2, cantidad: 1 })]
    const precios = { 1: resultadoFixture({ idArticulo: 1, precioFinal: 90 }) }

    expect(calcularSubtotalPrevia(lineas, precios)).toBe(180)
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

  it('una elección explícita de lote viaja en idLote de esa línea', () => {
    const resultado = aSolicitudDeVenta({
      idPuntoVenta: 7,
      idCliente: 1,
      codigoTipoComprobante: 'TX',
      idComprobanteAsociado: null,
      lineas: [lineaFixture({ idArticulo: 1 }), lineaFixture({ idArticulo: 2 })],
      lotesSeleccionados: { 1: 55 },
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
