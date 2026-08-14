import { describe, expect, it } from 'vitest'
import {
  aConteoDeLotes,
  aLineasDeTransferencia,
  aSolicitudDeConteo,
  aSolicitudDeConteoPorLote,
  aSolicitudDeTransferencia,
  articulosRepetidosEnTransferencia,
  contadaValida,
  lineaDeConteoDeLoteVacia,
  lineaDeTransferenciaVacia,
  lineaTransferenciaCompleta,
  lineasDeConteoDeLoteCompletas,
  type LineaDeConteoDeLoteFormulario,
  type LineaDeTransferenciaFormulario,
} from './stock'
import type { LoteListado } from './tipos'

function lineaFixture(sobrescribir: Partial<LineaDeTransferenciaFormulario> = {}): LineaDeTransferenciaFormulario {
  return {
    clave: 1,
    idArticulo: 10,
    descripcion: 'Fideos 500g',
    cantidad: '5',
    idLote: '',
    codigoLote: '',
    controlaLote: false,
    ...sobrescribir,
  }
}

describe('lineaDeTransferenciaVacia', () => {
  it('arranca sin artículo, cantidad ni lote elegidos', () => {
    expect(lineaDeTransferenciaVacia(7)).toEqual({
      clave: 7,
      idArticulo: '',
      descripcion: '',
      cantidad: '',
      idLote: '',
      codigoLote: '',
      controlaLote: false,
    })
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
  // stage-12-lotes-vencimientos (Slice 15, judgment-day fix): el Set devuelto ahora es de
  // `clave` (identidad de la fila), no de `idArticulo` — dos líneas del mismo artículo pueden
  // coexistir sin conflicto (caso c), así que "repetido" ya no puede marcarse por artículo a secas.
  it('detecta un artículo sin control de lote (ambas líneas en Auto) que aparece en más de una línea completa', () => {
    const repetidos = articulosRepetidosEnTransferencia([
      lineaFixture({ clave: 1, idArticulo: 10 }),
      lineaFixture({ clave: 2, idArticulo: 10 }),
      lineaFixture({ clave: 3, idArticulo: 20 }),
    ])
    expect(repetidos).toEqual(new Set([1, 2]))
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

  // (a) mismo (idArticulo, idLote explícito) en dos líneas → repetido — choca contra la
  // restricción real del backend `(idArticulo, idLote)` (decisión 11).
  it('(a) dos líneas del mismo artículo con el MISMO lote explícito se marcan repetidas', () => {
    const repetidos = articulosRepetidosEnTransferencia([
      lineaFixture({ clave: 1, idArticulo: 10, idLote: 41 }),
      lineaFixture({ clave: 2, idArticulo: 10, idLote: 41 }),
    ])
    expect(repetidos).toEqual(new Set([1, 2]))
  })

  // (b) mismo artículo, ambas líneas en Auto/FEFO → repetido — el cliente no puede saber si el
  // servidor las resolvería al mismo lote; bloquear acá es honesto.
  it('(b) dos líneas del mismo artículo AMBAS en Auto (FEFO) se marcan repetidas', () => {
    const repetidos = articulosRepetidosEnTransferencia([
      lineaFixture({ clave: 1, idArticulo: 10, idLote: '' }),
      lineaFixture({ clave: 2, idArticulo: 10, idLote: '' }),
    ])
    expect(repetidos).toEqual(new Set([1, 2]))
  })

  // (c) mismo artículo con lotes explícitos DISTINTOS → PERMITIDO — operación real de depósito
  // que el picker de lote existe para habilitar (el MAJOR original: la UI bloqueaba esto).
  it('(c) dos líneas del mismo artículo con lotes explícitos DISTINTOS NO se marcan repetidas', () => {
    const repetidos = articulosRepetidosEnTransferencia([
      lineaFixture({ clave: 1, idArticulo: 10, idLote: 41 }),
      lineaFixture({ clave: 2, idArticulo: 10, idLote: 42 }),
    ])
    expect(repetidos.size).toBe(0)
  })

  // (d) mismo artículo, una línea con lote explícito y otra en Auto → PERMITIDO client-side — el
  // cliente no puede computar el pick FEFO de la línea Auto para compararlo; si el servidor
  // resuelve al mismo lote, arbitra con un 400 `articulo_repetido` (verificado en
  // Transferencias.test.tsx: el funnel de error existente lo muestra, no se traga).
  it('(d) mismo artículo, una línea con lote explícito y otra en Auto (FEFO) NO se marcan repetidas', () => {
    const repetidos = articulosRepetidosEnTransferencia([
      lineaFixture({ clave: 1, idArticulo: 10, idLote: 41 }),
      lineaFixture({ clave: 2, idArticulo: 10, idLote: '' }),
    ])
    expect(repetidos.size).toBe(0)
  })
})

describe('aLineasDeTransferencia', () => {
  it('mapea las líneas completas a número y filtra las incompletas', () => {
    const lineas = aLineasDeTransferencia([lineaFixture({ idArticulo: 10, cantidad: '5' }), lineaFixture({ clave: 2, idArticulo: '' })])
    expect(lineas).toEqual([{ idArticulo: 10, cantidad: 5, idLote: null }])
  })

  it('un idLote elegido viaja como número; sin elegir viaja como null (el servidor resuelve FEFO)', () => {
    const lineas = aLineasDeTransferencia([lineaFixture({ idArticulo: 10, cantidad: '5', idLote: 41 })])
    expect(lineas).toEqual([{ idArticulo: 10, cantidad: 5, idLote: 41 }])
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
      lineas: [{ idArticulo: 10, cantidad: 5, idLote: null }],
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

// ---- Conteo por lote (stage-12-lotes-vencimientos, Slice 15) -----------------------------------

function loteFixture(sobrescribir: Partial<LoteListado> = {}): LoteListado {
  return {
    idLote: 41,
    idArticulo: 10,
    codigo: '2026-08-20',
    fechaVencimiento: '2026-08-20',
    esSinIdentificar: false,
    cantidad: 12,
    estado: 'Vigente',
    sugerido: true,
    ...sobrescribir,
  }
}

function lineaDeLoteFixture(sobrescribir: Partial<LineaDeConteoDeLoteFormulario> = {}): LineaDeConteoDeLoteFormulario {
  return { idLote: 41, codigo: '2026-08-20', contada: '10', ...sobrescribir }
}

describe('lineaDeConteoDeLoteVacia', () => {
  it('arranca con el idLote/código del lote y sin contada tipeada', () => {
    expect(lineaDeConteoDeLoteVacia(loteFixture())).toEqual({ idLote: 41, codigo: '2026-08-20', contada: '' })
  })
})

describe('lineasDeConteoDeLoteCompletas', () => {
  it('solo cuentan las líneas con una contada válida', () => {
    const completas = lineasDeConteoDeLoteCompletas([
      lineaDeLoteFixture({ idLote: 1, contada: '10' }),
      lineaDeLoteFixture({ idLote: 2, contada: '' }),
      lineaDeLoteFixture({ idLote: 3, contada: '-1' }),
    ])
    expect(completas.map((l) => l.idLote)).toEqual([1])
  })
})

describe('aConteoDeLotes', () => {
  it('mapea las líneas completas a número y filtra las incompletas', () => {
    const lotes = aConteoDeLotes([
      lineaDeLoteFixture({ idLote: 1, contada: '10' }),
      lineaDeLoteFixture({ idLote: 2, contada: '' }),
    ])
    expect(lotes).toEqual([{ idLote: 1, contada: 10 }])
  })
})

describe('aSolicitudDeConteoPorLote', () => {
  it('arma la rama exactly-one-of por lote: contada null, lotes con las líneas completas', () => {
    const solicitud = aSolicitudDeConteoPorLote(2, 10, [lineaDeLoteFixture({ idLote: 41, contada: '15' })], '  Recuento por lote  ')
    expect(solicitud).toEqual({
      idPuntoVenta: 2,
      idArticulo: 10,
      contada: null,
      observaciones: 'Recuento por lote',
      lotes: [{ idLote: 41, contada: 15 }],
    })
  })

  it('nunca arma las dos formas a la vez — contada siempre null en la rama por lote', () => {
    const solicitud = aSolicitudDeConteoPorLote(2, 10, [lineaDeLoteFixture()], 'obs')
    expect(solicitud.contada).toBeNull()
    expect(solicitud.lotes).not.toHaveLength(0)
  })
})
