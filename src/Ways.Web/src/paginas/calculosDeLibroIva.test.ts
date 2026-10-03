import { describe, expect, it } from 'vitest'
import type { FilaDeLibroIva, LibroIva } from '../api/tipos'
import {
  alicuotaDeFila,
  CONFIGURACION_DE_PESTANA,
  etiquetaDeAdvertencia,
  etiquetaDePorcentaje,
  filasConAdvertencias,
  filasConDiferencia,
  formatearFechaDeLibro,
  periodoValido,
  porcentajesDelLibro,
} from './calculosDeLibroIva'

function fila(sobrescribir: Partial<FilaDeLibroIva> = {}): FilaDeLibroIva {
  return {
    fecha: '2026-05-10',
    tipoComprobante: 'C-FA',
    numero: '0001-00000001',
    contraparte: 'Proveedor',
    documento: null,
    alicuotas: [],
    noGravado: 0,
    exento: 0,
    percepcionIva: 0,
    percepcionIibb: 0,
    total: 0,
    diferencia: 0,
    advertencias: [],
    netoGravado: 0,
    ivaTotal: 0,
    ...sobrescribir,
  }
}

function libro(filas: FilaDeLibroIva[], porAlicuota: LibroIva['totales']['porAlicuota'] = []): LibroIva {
  return {
    desde: '2026-05-01',
    hasta: '2026-05-31',
    idEmpresa: null,
    zonaHoraria: null,
    filas,
    totales: {
      porAlicuota,
      noGravado: 0,
      exento: 0,
      percepcionIva: 0,
      percepcionIibb: 0,
      total: 0,
      diferencia: 0,
      netoGravado: 0,
      ivaTotal: 0,
    },
  }
}

describe('porcentajesDelLibro', () => {
  it('une los porcentajes de los totales y de las filas, de mayor a menor y sin repetir', () => {
    const resultado = porcentajesDelLibro(
      libro(
        [fila({ alicuotas: [{ porcentaje: 0, neto: 5, iva: 0 }, { porcentaje: 21, neto: 1, iva: 0.21 }] })],
        [{ porcentaje: 10.5, neto: 2, iva: 0.21 }, { porcentaje: 21, neto: 1, iva: 0.21 }],
      ),
    )

    expect(resultado).toEqual([21, 10.5, 0])
  })

  it('un libro sin alícuotas no tiene columnas por alícuota', () => {
    expect(porcentajesDelLibro(libro([fila()]))).toEqual([])
  })
})

describe('alicuotaDeFila', () => {
  it('devuelve la alícuota del porcentaje pedido o null si la fila no la tiene', () => {
    const f = fila({ alicuotas: [{ porcentaje: 21, neto: 100, iva: 21 }] })

    expect(alicuotaDeFila(f, 21)).toEqual({ porcentaje: 21, neto: 100, iva: 21 })
    expect(alicuotaDeFila(f, 10.5)).toBeNull()
  })
})

describe('etiquetaDePorcentaje', () => {
  it.each([
    [21, '21%'],
    [10.5, '10,5%'],
    [2.5, '2,5%'],
    [0, '0%'],
  ])('%s se muestra como %s', (porcentaje, esperado) => {
    expect(etiquetaDePorcentaje(porcentaje)).toBe(esperado)
  })
})

describe('periodoValido', () => {
  it.each([
    ['2026-05-01', '2026-05-31', true],
    ['2026-05-31', '2026-05-31', true],
    ['2026-06-01', '2026-05-31', false],
    ['', '2026-05-31', false],
    ['2026-05-01', '', false],
  ])('desde %s hasta %s → %s', (desde, hasta, esperado) => {
    expect(periodoValido(desde, hasta)).toBe(esperado)
  })
})

describe('formatearFechaDeLibro', () => {
  it('pasa de ISO a día/mes/año sin correr el día', () => {
    expect(formatearFechaDeLibro('2026-05-01')).toBe('01/05/2026')
    expect(formatearFechaDeLibro('2026-12-31')).toBe('31/12/2026')
  })
})

describe('filasConDiferencia', () => {
  it('devuelve solo las filas cuya diferencia no es cero, incluidas las negativas', () => {
    const buena = fila({ numero: 'A' })
    const positiva = fila({ numero: 'B', diferencia: 9 })
    const negativa = fila({ numero: 'C', diferencia: -0.01 })

    expect(filasConDiferencia(libro([buena, positiva, negativa])).map((f) => f.numero)).toEqual(['B', 'C'])
  })
})

describe('CONFIGURACION_DE_PESTANA', () => {
  it('compras muestra percepciones y habla de proveedor y CUIT; ventas no y habla de cliente', () => {
    expect(CONFIGURACION_DE_PESTANA.compras).toEqual({
      titulo: 'Compras',
      contraparte: 'Proveedor',
      documento: 'CUIT',
      conPercepciones: true,
    })
    expect(CONFIGURACION_DE_PESTANA.ventas).toEqual({
      titulo: 'Ventas',
      contraparte: 'Cliente',
      documento: 'Documento',
      conPercepciones: false,
    })
  })
})

describe('advertencias', () => {
  it.each([
    ['anulado_sin_nc', 'Anulado sin NC'],
    ['alicuota_sin_clasificar', 'Alícuota sin clasificar'],
    ['sin_numero_fiscal', 'Sin número fiscal de PV'],
    ['codigo_desconocido', 'codigo_desconocido'],
  ])('%s se muestra como %s', (codigo, esperado) => {
    expect(etiquetaDeAdvertencia(codigo)).toBe(esperado)
  })

  it('filasConAdvertencias devuelve solo las filas que traen alguna', () => {
    const limpia = fila({ numero: 'A' })
    const marcada = fila({ numero: 'B', advertencias: ['anulado_sin_nc'] })

    expect(filasConAdvertencias(libro([limpia, marcada])).map((f) => f.numero)).toEqual(['B'])
  })
})
