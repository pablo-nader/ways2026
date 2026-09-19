import { describe, expect, it } from 'vitest'
import type { GastoDeTurno, MedioPagoListado } from '../api/tipos'
import {
  aSolicitudDeGasto,
  categoriaAlElegirProveedor,
  etiquetaDeCategoriaGasto,
  formatearFechaHora,
  formatearMoneda,
  mediosValidosParaGasto,
  totalDeGastos,
} from './utilidadesGastosDelTurno'
import type { FormularioDeGasto } from './utilidadesGastosDelTurno'

function medioFixture(sobrescribir: Partial<MedioPagoListado> = {}): MedioPagoListado {
  return {
    id: 1,
    nombre: 'Efectivo',
    activo: true,
    idEmpresa: null,
    orden: 1,
    comportamiento: 'Efectivo',
    admiteVuelto: true,
    requiereReferencia: false,
    recargoPorcentaje: null,
    ...sobrescribir,
  }
}

function gastoFixture(sobrescribir: Partial<GastoDeTurno> = {}): GastoDeTurno {
  return {
    id: 1,
    idPuntoVenta: 7,
    fecha: '2026-09-19T12:00:00Z',
    categoria: 'Otros',
    idMedioPago: 1,
    importe: 100,
    ...sobrescribir,
  }
}

function formularioFixture(sobrescribir: Partial<FormularioDeGasto> = {}): FormularioDeGasto {
  return {
    idPuntoVenta: 7,
    importe: 500,
    idMedioPago: 1,
    categoria: 'Otros',
    idProveedor: null,
    observaciones: '',
    ...sobrescribir,
  }
}

describe('etiquetaDeCategoriaGasto', () => {
  it('traduce cada valor de CategoriaGasto a su etiqueta en español', () => {
    expect(etiquetaDeCategoriaGasto('Proveedor')).toBe('Proveedores')
    expect(etiquetaDeCategoriaGasto('Sueldos')).toBe('Sueldos')
    expect(etiquetaDeCategoriaGasto('Otros')).toBe('Otros')
  })
})

describe('formatearMoneda / formatearFechaHora', () => {
  it('formatea el importe con símbolo', () => {
    expect(formatearMoneda(1234.5)).toBe('$ 1.234,50')
  })

  it('formatea la fecha/hora en formato es-AR', () => {
    expect(formatearFechaHora('2026-09-16T12:05:00Z')).toBe(new Date('2026-09-16T12:05:00Z').toLocaleString('es-AR'))
  })
})

// Cláusula bajo prueba: el filtro `comportamiento !== 'CuentaCorriente'` de
// `mediosValidosParaGasto` — CuentaCorriente es el crédito del CLIENTE, nunca una salida real de
// dinero de la caja (mutation-proof-tests: sacar el filtro deja pasar `medioCuentaCorriente`).
describe('mediosValidosParaGasto', () => {
  it('excluye los medios de comportamiento CuentaCorriente, conserva Efectivo y Electronico', () => {
    const efectivo = medioFixture({ id: 1, comportamiento: 'Efectivo' })
    const electronico = medioFixture({ id: 2, comportamiento: 'Electronico' })
    const cuentaCorriente = medioFixture({ id: 3, comportamiento: 'CuentaCorriente' })

    const resultado = mediosValidosParaGasto([efectivo, electronico, cuentaCorriente])

    expect(resultado.map((m) => m.id)).toEqual([1, 2])
  })

  it('con la lista vacía devuelve una lista vacía', () => {
    expect(mediosValidosParaGasto([])).toEqual([])
  })
})

// Cláusula bajo prueba: las dos ramas de `categoriaAlElegirProveedor` (decisión del pedido 1).
describe('categoriaAlElegirProveedor', () => {
  it('elegir un proveedor cambia la categoría a Proveedor, sea cual sea la categoría actual', () => {
    expect(categoriaAlElegirProveedor(5, 'Otros')).toBe('Proveedor')
    expect(categoriaAlElegirProveedor(5, 'Servicios')).toBe('Proveedor')
    expect(categoriaAlElegirProveedor(5, 'Proveedor')).toBe('Proveedor')
  })

  it('limpiar el proveedor mientras la categoría sigue en Proveedor la vuelve a Otros', () => {
    expect(categoriaAlElegirProveedor(null, 'Proveedor')).toBe('Otros')
  })

  it('limpiar el proveedor con una categoría ya distinta de Proveedor no la toca (el cajero la eligió a mano)', () => {
    expect(categoriaAlElegirProveedor(null, 'Servicios')).toBe('Servicios')
    expect(categoriaAlElegirProveedor(null, 'Otros')).toBe('Otros')
  })
})

// Cláusula bajo prueba: el fallback de `concepto` en `aSolicitudDeGasto` (decisión del pedido 2).
describe('aSolicitudDeGasto', () => {
  it('con observaciones no vacías, concepto es la observación recortada', () => {
    const solicitud = aSolicitudDeGasto(formularioFixture({ observaciones: '  Pago de flete  ' }))
    expect(solicitud.concepto).toBe('Pago de flete')
  })

  it('con observaciones vacías, concepto cae al texto fijo "Gasto del turno"', () => {
    expect(aSolicitudDeGasto(formularioFixture({ observaciones: '' })).concepto).toBe('Gasto del turno')
  })

  it('con observaciones solo de espacios, concepto también cae al texto fijo', () => {
    expect(aSolicitudDeGasto(formularioFixture({ observaciones: '   ' })).concepto).toBe('Gasto del turno')
  })

  it('detalle/idArea/numeroFactura/idComprobanteCompra siempre viajan null — el formulario no los pide', () => {
    const solicitud = aSolicitudDeGasto(formularioFixture())
    expect(solicitud.detalle).toBeNull()
    expect(solicitud.idArea).toBeNull()
    expect(solicitud.numeroFactura).toBeNull()
    expect(solicitud.idComprobanteCompra).toBeNull()
  })

  it('arma el resto del cuerpo con los valores del formulario, incluido un proveedor elegido', () => {
    const solicitud = aSolicitudDeGasto(
      formularioFixture({ idPuntoVenta: 9, importe: 250, idMedioPago: 3, categoria: 'Proveedor', idProveedor: 42 }),
    )
    expect(solicitud.idPuntoVenta).toBe(9)
    expect(solicitud.importe).toBe(250)
    expect(solicitud.idMedioPago).toBe(3)
    expect(solicitud.categoria).toBe('Proveedor')
    expect(solicitud.idProveedor).toBe(42)
  })
})

describe('totalDeGastos', () => {
  it('suma el importe de todos los gastos, sin excluir ninguno', () => {
    const total = totalDeGastos([gastoFixture({ importe: 100 }), gastoFixture({ id: 2, importe: 250 })])
    expect(total).toBe(350)
  })

  it('con la lista vacía el total es 0', () => {
    expect(totalDeGastos([])).toBe(0)
  })
})
