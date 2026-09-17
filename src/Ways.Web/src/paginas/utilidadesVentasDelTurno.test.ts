import { describe, expect, it } from 'vitest'
import { ROL } from '../api/tipos'
import type { VentaDeTurnoListado } from '../api/tipos'
import { claseDeBadgeDeEstadoVenta, etiquetaDeEstadoVenta, formatearMoneda, puedeAnular, totalesDeVentas } from './utilidadesVentasDelTurno'

function ventaFixture(sobrescribir: Partial<VentaDeTurnoListado> = {}): VentaDeTurnoListado {
  return {
    id: 1,
    numero: 1,
    numeroVisible: '0007-00000001',
    estado: 'Emitido',
    fecha: '2026-09-16T12:00:00Z',
    idCliente: 1,
    nombreCliente: 'Consumidor Final',
    total: 100,
    mediosDePago: ['Efectivo'],
    ...sobrescribir,
  }
}

describe('etiquetaDeEstadoVenta / claseDeBadgeDeEstadoVenta', () => {
  it('Emitido se muestra como "Emitida" con badge de éxito', () => {
    expect(etiquetaDeEstadoVenta('Emitido')).toBe('Emitida')
    expect(claseDeBadgeDeEstadoVenta('Emitido')).toBe('bg-success')
  })

  it('Anulado se muestra como "Anulada" con badge de peligro', () => {
    expect(etiquetaDeEstadoVenta('Anulado')).toBe('Anulada')
    expect(claseDeBadgeDeEstadoVenta('Anulado')).toBe('bg-danger')
  })
})

describe('formatearMoneda', () => {
  it('formatea con separador de miles y dos decimales, es-AR', () => {
    expect(formatearMoneda(1234.5)).toBe('$1.234,50')
  })
})

describe('totalesDeVentas — regla "solo las no anuladas cuentan"', () => {
  it('excluye las anuladas del total y de la cantidad', () => {
    const ventas = [
      ventaFixture({ id: 1, total: 100, estado: 'Emitido' }),
      ventaFixture({ id: 2, total: 200, estado: 'Anulado' }),
      ventaFixture({ id: 3, total: 50, estado: 'Emitido' }),
    ]

    expect(totalesDeVentas(ventas)).toEqual({ cantidad: 2, total: 150 })
  })

  it('una lista vacía da cantidad y total en cero', () => {
    expect(totalesDeVentas([])).toEqual({ cantidad: 0, total: 0 })
  })

  it('todas anuladas da cantidad y total en cero', () => {
    const ventas = [ventaFixture({ estado: 'Anulado', total: 999 })]
    expect(totalesDeVentas(ventas)).toEqual({ cantidad: 0, total: 0 })
  })
})

describe('puedeAnular', () => {
  it('una venta Emitida puede anularse para Vendedor/Supervisor/Admin', () => {
    const venta = ventaFixture({ estado: 'Emitido' })
    expect(puedeAnular(venta, ROL.Vendedor)).toBe(true)
    expect(puedeAnular(venta, ROL.Supervisor)).toBe(true)
    expect(puedeAnular(venta, ROL.Admin)).toBe(true)
  })

  it('una venta ya Anulada nunca puede volver a anularse', () => {
    const venta = ventaFixture({ estado: 'Anulado' })
    expect(puedeAnular(venta, ROL.Vendedor)).toBe(false)
  })

  it('Root nunca opera el POS — mismo criterio que Politicas.OperacionDePos', () => {
    const venta = ventaFixture({ estado: 'Emitido' })
    expect(puedeAnular(venta, ROL.Root)).toBe(false)
  })
})
