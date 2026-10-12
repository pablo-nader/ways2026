import { describe, expect, it } from 'vitest'
import type { AreaListado, GastoDeTurno, MedioPagoListado, ResumenDeTurno } from '../api/tipos'
import {
  aSolicitudDeGasto,
  areaPorDefecto,
  categoriaAlElegirProveedor,
  etiquetaDeCategoriaGasto,
  efectivoDisponibleEnCaja,
  etiquetaDeOrigenFondos,
  faltanteDeEfectivo,
  formatearFechaHora,
  formatearMoneda,
  mediosValidosParaGasto,
  salidaDeEfectivoDeCaja,
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
    porcentajeFacturacion: 0,
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
    origenFondos: 'CajaTurno',
    idTurnoCaja: 55,
    turnoAbierto: true,
    idProveedor: null,
    idArea: null,
    concepto: 'Flete',
    detalle: null,
    numeroFactura: null,
    idComprobanteCompra: null,
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
    idArea: null,
    observaciones: '',
    origenFondos: 'CajaTurno',
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

describe('etiquetaDeOrigenFondos', () => {
  it('traduce cada valor de OrigenFondosGasto a su etiqueta en español', () => {
    expect(etiquetaDeOrigenFondos('CajaTurno')).toBe('Caja del turno')
    expect(etiquetaDeOrigenFondos('Tesoreria')).toBe('Caja general')
  })
})

describe('formatearMoneda / formatearFechaHora', () => {
  it('formatea el importe con símbolo', () => {
    expect(formatearMoneda(1234.5)).toBe('$ 1.234,50')
  })

  it('formatea la fecha/hora en formato es-AR de 24 horas', () => {
    expect(formatearFechaHora(new Date(2026, 8, 16, 16, 5).toISOString())).toBe('16/9/2026, 16:05:00')
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

  it('detalle/numeroFactura/idComprobanteCompra siempre viajan null — el formulario no los pide', () => {
    const solicitud = aSolicitudDeGasto(formularioFixture())
    expect(solicitud.detalle).toBeNull()
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

  it('manda idArea tal cual lo trae el formulario, o null si no hay área', () => {
    expect(aSolicitudDeGasto(formularioFixture({ idArea: 4 })).idArea).toBe(4)
    expect(aSolicitudDeGasto(formularioFixture({ idArea: null })).idArea).toBeNull()
  })

  // dto-contract-honesty: origenFondos se manda tal cual lo eligió el cajero, nunca inferido ni
  // forzado a un default silencioso dentro del mapeo.
  it('manda origenFondos tal cual lo trae el formulario, sea cual sea', () => {
    expect(aSolicitudDeGasto(formularioFixture({ origenFondos: 'CajaTurno' })).origenFondos).toBe('CajaTurno')
    expect(aSolicitudDeGasto(formularioFixture({ origenFondos: 'Tesoreria' })).origenFondos).toBe('Tesoreria')
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

function resumenFixture(sobrescribir: Partial<ResumenDeTurno> = {}): ResumenDeTurno {
  return {
    idTurnoCaja: 55,
    idMedioAncla: 1,
    medios: [],
    cantidadTickets: 0,
    primerTicket: null,
    ultimoTicket: null,
    ingresosPorArea: [],
    egresos: { porCategoria: [], porArea: [], retiros: 0 },
    ...sobrescribir,
  }
}

// Cláusula bajo prueba: la línea del medio ANCLA (y no otra) es el efectivo disponible.
describe('efectivoDisponibleEnCaja', () => {
  it('toma el importe esperado de la línea del medio ancla, ignorando las demás líneas', () => {
    const resumen = resumenFixture({
      idMedioAncla: 2,
      medios: [
        { idMedioPago: 1, importeEsperado: 9999 },
        { idMedioPago: 2, importeEsperado: 1250.5 },
        { idMedioPago: 3, importeEsperado: 7 },
      ],
    })
    expect(efectivoDisponibleEnCaja(resumen)).toBe(1250.5)
  })

  it('sin línea para el medio ancla, el disponible es 0', () => {
    expect(efectivoDisponibleEnCaja(resumenFixture({ idMedioAncla: 2, medios: [{ idMedioPago: 1, importeEsperado: 300 }] }))).toBe(0)
  })
})

// Cláusula bajo prueba: la conjunción de DOS condiciones (D3) — medio Efectivo Y origen CajaTurno.
describe('salidaDeEfectivoDeCaja', () => {
  const efectivo = medioFixture({ comportamiento: 'Efectivo' })
  const electronico = medioFixture({ id: 3, comportamiento: 'Electronico' })

  it('efectivo pagado desde la caja del turno sale del cajón', () => {
    expect(salidaDeEfectivoDeCaja(efectivo, 'CajaTurno')).toBe(true)
  })

  it('efectivo pagado desde Tesorería no sale del cajón', () => {
    expect(salidaDeEfectivoDeCaja(efectivo, 'Tesoreria')).toBe(false)
  })

  it('un medio no efectivo pagado desde la caja del turno no sale del cajón', () => {
    expect(salidaDeEfectivoDeCaja(electronico, 'CajaTurno')).toBe(false)
  })

  it('sin medio elegido no sale del cajón', () => {
    expect(salidaDeEfectivoDeCaja(undefined, 'CajaTurno')).toBe(false)
  })
})

describe('faltanteDeEfectivo', () => {
  it('es la diferencia cuando el importe supera lo disponible', () => {
    expect(faltanteDeEfectivo(1000, 400)).toBe(600)
  })

  it('es 0 cuando el importe iguala o no supera lo disponible', () => {
    expect(faltanteDeEfectivo(400, 400)).toBe(0)
    expect(faltanteDeEfectivo(100, 400)).toBe(0)
  })

  it('es 0 sin importe cargado', () => {
    expect(faltanteDeEfectivo(null, 400)).toBe(0)
  })

  it('es 0 con un importe cero o negativo, aunque el disponible sea negativo', () => {
    expect(faltanteDeEfectivo(0, -50)).toBe(0)
    expect(faltanteDeEfectivo(-10, -50)).toBe(0)
  })

  it('con efectivo disponible negativo y un importe positivo, falta el importe más el descubierto', () => {
    expect(faltanteDeEfectivo(100, -50)).toBe(150)
  })

  it('resuelve los centavos sin arrastrar error de coma flotante', () => {
    expect(faltanteDeEfectivo(0.3, 0.1)).toBe(0.2)
  })
})

function areaFixture(sobrescribir: Partial<AreaListado> = {}): AreaListado {
  return { id: 1, nombre: 'General', activo: true, idEmpresa: null, orden: 1, ...sobrescribir }
}

// Cláusula bajo prueba: el default es la primera área ACTIVA, no la primera del array.
describe('areaPorDefecto', () => {
  it('devuelve la primera área activa', () => {
    expect(areaPorDefecto([areaFixture({ id: 3 }), areaFixture({ id: 5 })])).toBe(3)
  })

  it('saltea las inactivas aunque estén primero', () => {
    expect(areaPorDefecto([areaFixture({ id: 2, activo: false }), areaFixture({ id: 5 })])).toBe(5)
  })

  it('sin áreas activas devuelve vacío', () => {
    expect(areaPorDefecto([areaFixture({ id: 2, activo: false })])).toBe('')
    expect(areaPorDefecto([])).toBe('')
  })
})
