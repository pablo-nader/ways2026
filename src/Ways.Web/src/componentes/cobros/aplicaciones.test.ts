import { describe, expect, it } from 'vitest'
import type { VentaPendienteDeCobro } from '../../api/tipos'
import {
  aAplicacionesSolicitadas,
  MAXIMO_DE_VENTAS_POR_APLICACION,
  repartirFifo,
  totalAplicado,
  validarAplicaciones,
} from './aplicaciones'

function venta(id: number, saldoPendiente: number, fecha: string, sobrescribir: Partial<VentaPendienteDeCobro> = {}): VentaPendienteDeCobro {
  return {
    id,
    codigoTipo: 'FA',
    letra: 'B',
    numero: id,
    numeroVisible: `0001-${String(id).padStart(8, '0')}`,
    fecha,
    parteEnCuentaCorriente: saldoPendiente,
    cobrado: 0,
    saldoPendiente,
    ...sobrescribir,
  }
}

// El servidor las manda la más nueva primero: el reparto no puede depender de ese orden.
const NUEVA = venta(3, 300, '2026-10-03T12:00:00Z')
const MEDIA = venta(2, 200, '2026-10-02T12:00:00Z')
const VIEJA = venta(1, 100, '2026-10-01T12:00:00Z')
const VENTAS = [NUEVA, MEDIA, VIEJA]

describe('repartirFifo', () => {
  it('con menos fondos que la venta más vieja, le aplica solo lo que hay', () => {
    expect(repartirFifo(VENTAS, 40)).toEqual({ 1: 40 })
  })

  it('con fondos que alcanzan para varias, llena la más vieja y sigue con la siguiente', () => {
    expect(repartirFifo(VENTAS, 250)).toEqual({ 1: 100, 2: 150 })
  })

  it('con fondos exactos a la primera, no toca la siguiente', () => {
    expect(repartirFifo(VENTAS, 100)).toEqual({ 1: 100 })
  })

  it('con fondos exactos a todas, las llena todas', () => {
    expect(repartirFifo(VENTAS, 600)).toEqual({ 1: 100, 2: 200, 3: 300 })
  })

  it('con más fondos que todas, aplica el saldo de cada una y deja el resto sin aplicar', () => {
    const reparto = repartirFifo(VENTAS, 1000)
    expect(reparto).toEqual({ 1: 100, 2: 200, 3: 300 })
    expect(totalAplicado(VENTAS, reparto)).toBe(600)
  })

  it('a igual fecha desempata por id', () => {
    const a = venta(10, 50, '2026-10-01T12:00:00Z')
    const b = venta(9, 50, '2026-10-01T12:00:00Z')
    expect(repartirFifo([a, b], 50)).toEqual({ 9: 50 })
  })

  it('sin fondos o con fondos no válidos no reparte nada', () => {
    expect(repartirFifo(VENTAS, 0)).toEqual({})
    expect(repartirFifo(VENTAS, -5)).toEqual({})
    expect(repartirFifo(VENTAS, Number.NaN)).toEqual({})
  })

  it('sin ventas no reparte nada', () => {
    expect(repartirFifo([], 100)).toEqual({})
  })

  it('no arrastra error de coma flotante con centavos', () => {
    const a = venta(1, 0.1, '2026-10-01T12:00:00Z')
    const b = venta(2, 0.2, '2026-10-02T12:00:00Z')
    expect(repartirFifo([a, b], 0.3)).toEqual({ 1: 0.1, 2: 0.2 })
  })

  it('no modifica la lista recibida', () => {
    const copia = [...VENTAS]
    repartirFifo(VENTAS, 250)
    expect(VENTAS).toEqual(copia)
  })
})

describe('totalAplicado', () => {
  it('suma solo lo cargado en ventas que siguen en la lista', () => {
    expect(totalAplicado(VENTAS, { 1: 10, 2: 20, 99: 500, 3: null })).toBe(30)
  })

  it('ignora los importes en cero', () => {
    expect(totalAplicado(VENTAS, { 1: 0, 2: 5 })).toBe(5)
  })
})

describe('validarAplicaciones', () => {
  it('no objeta si no se cargó nada', () => {
    expect(validarAplicaciones(VENTAS, {}, 0)).toBeNull()
    expect(validarAplicaciones(VENTAS, { 1: null, 2: 0 }, 0)).toBeNull()
  })

  it('acepta lo cargado dentro del tope y del saldo de cada venta', () => {
    expect(validarAplicaciones(VENTAS, { 1: 100, 2: 50 }, 150)).toBeNull()
  })

  it('rechaza un importe mayor al saldo pendiente de su venta', () => {
    const mensaje = validarAplicaciones(VENTAS, { 1: 100.01 }, 1000)
    expect(mensaje).toContain('0001-00000001')
    expect(mensaje).toContain('saldo pendiente')
  })

  it('rechaza una suma mayor al tope', () => {
    const mensaje = validarAplicaciones(VENTAS, { 1: 100, 2: 51 }, 150)
    expect(mensaje).toContain('supera el importe disponible')
  })

  it('compara en centavos: 0,1 + 0,2 no supera 0,3', () => {
    const a = venta(1, 0.1, '2026-10-01T12:00:00Z')
    const b = venta(2, 0.2, '2026-10-02T12:00:00Z')
    expect(validarAplicaciones([a, b], { 1: 0.1, 2: 0.2 }, 0.3)).toBeNull()
  })

  it(`rechaza más de ${MAXIMO_DE_VENTAS_POR_APLICACION} ventas`, () => {
    const muchas = Array.from({ length: MAXIMO_DE_VENTAS_POR_APLICACION + 1 }, (_, i) => venta(i + 1, 10, '2026-10-01T12:00:00Z'))
    const importes = Object.fromEntries(muchas.map((v) => [v.id, 1]))
    expect(validarAplicaciones(muchas, importes, 1000)).toContain(String(MAXIMO_DE_VENTAS_POR_APLICACION))
  })

  it(`acepta exactamente ${MAXIMO_DE_VENTAS_POR_APLICACION} ventas`, () => {
    const justas = Array.from({ length: MAXIMO_DE_VENTAS_POR_APLICACION }, (_, i) => venta(i + 1, 10, '2026-10-01T12:00:00Z'))
    const importes = Object.fromEntries(justas.map((v) => [v.id, 1]))
    expect(validarAplicaciones(justas, importes, 1000)).toBeNull()
  })
})

describe('aAplicacionesSolicitadas', () => {
  it('arma el cuerpo solo con las ventas que tienen importe, en el orden de la lista', () => {
    expect(aAplicacionesSolicitadas(VENTAS, { 1: 100, 2: null, 3: 25 })).toEqual([
      { idComprobanteVenta: 3, importe: 25 },
      { idComprobanteVenta: 1, importe: 100 },
    ])
  })

  it('devuelve vacío si no hay nada cargado', () => {
    expect(aAplicacionesSolicitadas(VENTAS, { 1: 0 })).toEqual([])
  })
})
