import { describe, expect, it } from 'vitest'
import {
  aLineasDeTransferencia,
  aSolicitudDeConteo,
  aSolicitudDeTransferencia,
  articulosRepetidosEnTransferencia,
  contadaValida,
  lineaDeTransferenciaVacia,
  lineaTransferenciaCompleta,
  type LineaDeTransferenciaFormulario,
} from './stock'

function lineaFixture(sobrescribir: Partial<LineaDeTransferenciaFormulario> = {}): LineaDeTransferenciaFormulario {
  return { clave: 1, idArticulo: 10, descripcion: 'Fideos 500g', cantidad: '5', ...sobrescribir }
}

describe('lineaDeTransferenciaVacia', () => {
  it('arranca sin artículo ni cantidad tipeados', () => {
    expect(lineaDeTransferenciaVacia(7)).toEqual({ clave: 7, idArticulo: '', descripcion: '', cantidad: '' })
  })
})

describe('lineaTransferenciaCompleta', () => {
  it('una línea con artículo y cantidad positiva está completa', () => {
    expect(lineaTransferenciaCompleta(lineaFixture())).toBe(true)
  })

  it('sin artículo elegido no está completa', () => {
    expect(lineaTransferenciaCompleta(lineaFixture({ idArticulo: '' }))).toBe(false)
  })

  it('sin cantidad tipeada no está completa', () => {
    expect(lineaTransferenciaCompleta(lineaFixture({ cantidad: '' }))).toBe(false)
  })

  it('una cantidad cero o negativa no está completa', () => {
    expect(lineaTransferenciaCompleta(lineaFixture({ cantidad: '0' }))).toBe(false)
    expect(lineaTransferenciaCompleta(lineaFixture({ cantidad: '-1' }))).toBe(false)
  })
})

describe('articulosRepetidosEnTransferencia', () => {
  it('detecta un artículo que aparece en más de una línea completa', () => {
    const repetidos = articulosRepetidosEnTransferencia([
      lineaFixture({ clave: 1, idArticulo: 10 }),
      lineaFixture({ clave: 2, idArticulo: 10 }),
      lineaFixture({ clave: 3, idArticulo: 20 }),
    ])
    expect(repetidos).toEqual(new Set([10]))
  })

  it('ignora las líneas incompletas al detectar repetidos', () => {
    const repetidos = articulosRepetidosEnTransferencia([
      lineaFixture({ clave: 1, idArticulo: 10, cantidad: '' }),
      lineaFixture({ clave: 2, idArticulo: 10, cantidad: '' }),
    ])
    expect(repetidos.size).toBe(0)
  })

  it('sin repetidos devuelve un set vacío', () => {
    const repetidos = articulosRepetidosEnTransferencia([lineaFixture({ idArticulo: 10 }), lineaFixture({ clave: 2, idArticulo: 20 })])
    expect(repetidos.size).toBe(0)
  })
})

describe('aLineasDeTransferencia', () => {
  it('mapea las líneas completas a número y filtra las incompletas', () => {
    const lineas = aLineasDeTransferencia([lineaFixture({ idArticulo: 10, cantidad: '5' }), lineaFixture({ clave: 2, idArticulo: '' })])
    expect(lineas).toEqual([{ idArticulo: 10, cantidad: 5 }])
  })
})

describe('aSolicitudDeTransferencia', () => {
  it('recorta observaciones y filtra líneas incompletas', () => {
    const solicitud = aSolicitudDeTransferencia(1, 2, '  Reposición de sucursal  ', [
      lineaFixture({ idArticulo: 10, cantidad: '5' }),
      lineaFixture({ clave: 2, idArticulo: '' }),
    ])
    expect(solicitud).toEqual({
      idPuntoVentaOrigen: 1,
      idPuntoVentaDestino: 2,
      observaciones: 'Reposición de sucursal',
      lineas: [{ idArticulo: 10, cantidad: 5 }],
    })
  })

  it('un origen/destino sin elegir viaja como 0 — el servidor lo rechaza igual que el mirror', () => {
    const solicitud = aSolicitudDeTransferencia('', '', '', [])
    expect(solicitud.idPuntoVentaOrigen).toBe(0)
    expect(solicitud.idPuntoVentaDestino).toBe(0)
  })
})

describe('contadaValida', () => {
  it('un número positivo o cero es válido', () => {
    expect(contadaValida('45')).toBe(true)
    expect(contadaValida('0')).toBe(true)
  })

  it('vacío, negativo o no numérico es inválido', () => {
    expect(contadaValida('')).toBe(false)
    expect(contadaValida('-1')).toBe(false)
    expect(contadaValida('abc')).toBe(false)
  })
})

describe('aSolicitudDeConteo', () => {
  it('recorta observaciones y convierte contada a número', () => {
    const solicitud = aSolicitudDeConteo(2, 10, '45', '  Recuento mensual  ')
    expect(solicitud).toEqual({ idPuntoVenta: 2, idArticulo: 10, contada: 45, observaciones: 'Recuento mensual' })
  })

  it('idPuntoVenta/idArticulo sin elegir viajan como 0', () => {
    const solicitud = aSolicitudDeConteo('', '', '10', 'obs')
    expect(solicitud.idPuntoVenta).toBe(0)
    expect(solicitud.idArticulo).toBe(0)
  })
})
