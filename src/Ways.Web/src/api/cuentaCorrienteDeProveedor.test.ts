import { describe, expect, it } from 'vitest'
import {
  aSolicitudDeAjusteDeProveedor,
  construirQueryEstadoDeCuentaDeProveedor,
  esSaldoAFavor,
  etiquetaDeTipoDeMovimiento,
  etiquetarAjuste,
  filtrosDeEstadoDeCuentaDeProveedorVacios,
  referenciaDeMovimiento,
} from './cuentaCorrienteDeProveedor'
import type { MovimientoDeCuentaDeProveedor } from './tipos'

function movimientoFixture(sobrescribir: Partial<MovimientoDeCuentaDeProveedor> = {}): MovimientoDeCuentaDeProveedor {
  return {
    idMovimiento: 1,
    fecha: '2026-08-17T12:00:00Z',
    tipo: 'Compra',
    importe: 1000,
    saldoResultante: 1000,
    detalle: null,
    idComprobanteCompra: 5,
    idGasto: null,
    etiqueta: null,
    ...sobrescribir,
  }
}

// ---- etiquetarAjuste: helper puro, sin DOM (web-descriptor-tests) -----------------------------

describe('etiquetarAjuste', () => {
  it('etiqueta === AnulacionContramovimiento devuelve "Contramov. de anulación"', () => {
    expect(etiquetarAjuste('AnulacionContramovimiento')).toBe('Contramov. de anulación')
  })

  it('etiqueta === Manual devuelve "Ajuste manual"', () => {
    expect(etiquetarAjuste('Manual')).toBe('Ajuste manual')
  })

  it('etiqueta null (nunca debería llegar así en un tipo Ajuste real) cae al mismo default que Manual', () => {
    expect(etiquetarAjuste(null)).toBe('Ajuste manual')
  })
})

// ---- etiquetaDeTipoDeMovimiento / referenciaDeMovimiento: el mapper de fila -------------------

describe('etiquetaDeTipoDeMovimiento', () => {
  it('Apertura, Compra y Pago se muestran tal cual', () => {
    expect(etiquetaDeTipoDeMovimiento(movimientoFixture({ tipo: 'Apertura', etiqueta: null }))).toBe('Apertura')
    expect(etiquetaDeTipoDeMovimiento(movimientoFixture({ tipo: 'Compra', etiqueta: null }))).toBe('Compra')
    expect(etiquetaDeTipoDeMovimiento(movimientoFixture({ tipo: 'Pago', etiqueta: null }))).toBe('Pago')
  })

  it('Ajuste delega en etiquetarAjuste, en ambas direcciones', () => {
    expect(etiquetaDeTipoDeMovimiento(movimientoFixture({ tipo: 'Ajuste', etiqueta: 'Manual' }))).toBe('Ajuste manual')
    expect(etiquetaDeTipoDeMovimiento(movimientoFixture({ tipo: 'Ajuste', etiqueta: 'AnulacionContramovimiento' }))).toBe(
      'Contramov. de anulación',
    )
  })
})

describe('referenciaDeMovimiento', () => {
  it('con idComprobanteCompra devuelve "Compra #N", incluso si además tuviera idGasto', () => {
    expect(referenciaDeMovimiento({ idComprobanteCompra: 5, idGasto: null })).toBe('Compra #5')
    expect(referenciaDeMovimiento({ idComprobanteCompra: 5, idGasto: 9 })).toBe('Compra #5')
  })

  it('sin idComprobanteCompra pero con idGasto devuelve "Gasto #N"', () => {
    expect(referenciaDeMovimiento({ idComprobanteCompra: null, idGasto: 9 })).toBe('Gasto #9')
  })

  it('sin ninguno de los dos (apertura, ajuste manual) devuelve "—"', () => {
    expect(referenciaDeMovimiento({ idComprobanteCompra: null, idGasto: null })).toBe('—')
  })
})

// ---- esSaldoAFavor -----------------------------------------------------------------------------

describe('esSaldoAFavor', () => {
  it('un saldo negativo es "a favor"', () => {
    expect(esSaldoAFavor(-500)).toBe(true)
  })

  it('cero y un saldo positivo NO son "a favor"', () => {
    expect(esSaldoAFavor(0)).toBe(false)
    expect(esSaldoAFavor(500)).toBe(false)
  })
})

// ---- construirQueryEstadoDeCuentaDeProveedor: el filter builder -------------------------------

describe('construirQueryEstadoDeCuentaDeProveedor', () => {
  it('historico=true omite desde/hasta pero conserva pagina/tamanio', () => {
    const query = construirQueryEstadoDeCuentaDeProveedor({
      desde: '2026-07-01',
      hasta: '2026-08-01',
      historico: true,
      pagina: 2,
      tamanio: 25,
    })
    expect(query).toContain('historico=true')
    expect(query).not.toContain('desde=')
    expect(query).not.toContain('hasta=')
    expect(query).toContain('pagina=2')
    expect(query).toContain('tamanio=25')
  })

  it('sin historico, desde/hasta viajan con el offset local, nunca Z (mutation-proof-tests regla 10)', () => {
    const query = construirQueryEstadoDeCuentaDeProveedor({
      desde: '2026-08-01',
      hasta: '2026-08-17',
      historico: false,
      pagina: 1,
      tamanio: 25,
    })
    expect(query).not.toContain('historico')
    expect(query).toMatch(/desde=2026-08-01T00%3A00%3A00[+-]\d{2}%3A\d{2}/)
    expect(query).toMatch(/hasta=2026-08-17T23%3A59%3A59\.999[+-]\d{2}%3A\d{2}/)
  })

  it('desde/hasta vacíos se omiten (el servidor aplica su propio default de último mes)', () => {
    const query = construirQueryEstadoDeCuentaDeProveedor({ desde: '', hasta: '', historico: false, pagina: 1, tamanio: 25 })
    expect(query).not.toContain('desde=')
    expect(query).not.toContain('hasta=')
    expect(query).toContain('pagina=1')
  })
})

describe('filtrosDeEstadoDeCuentaDeProveedorVacios', () => {
  it('precarga página 1, tamaño 25, histórico apagado y una ventana desde/hasta no vacía', () => {
    const filtros = filtrosDeEstadoDeCuentaDeProveedorVacios()
    expect(filtros.pagina).toBe(1)
    expect(filtros.tamanio).toBe(25)
    expect(filtros.historico).toBe(false)
    expect(filtros.desde).not.toBe('')
    expect(filtros.hasta).not.toBe('')
  })
})

// ---- aSolicitudDeAjusteDeProveedor -------------------------------------------------------------

describe('aSolicitudDeAjusteDeProveedor', () => {
  it('recorta el detalle antes de armar el cuerpo del POST', () => {
    expect(aSolicitudDeAjusteDeProveedor(7, -200, '  saldo inicial mal cargado  ')).toEqual({
      idPuntoVenta: 7,
      importe: -200,
      detalle: 'saldo inicial mal cargado',
    })
  })
})
