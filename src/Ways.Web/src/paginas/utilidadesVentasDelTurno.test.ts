import { describe, expect, it } from 'vitest'
import { ROL } from '../api/tipos'
import type { MedioPagoListado, VentaDeTurnoListado } from '../api/tipos'
import {
  FILTROS_VACIOS,
  claseDeBadgeDeEstadoVenta,
  etiquetaDeEstadoVenta,
  filtrarVentas,
  formatearFechaHora,
  formatearMoneda,
  hayFiltrosActivos,
  mediosDisponibles,
  nombreDeMedio,
  puedeAnular,
  puedeReimprimir,
  totalesDeVentas,
  totalesPorMedioDeVentas,
} from './utilidadesVentasDelTurno'
import type { FiltrosDeVentasDelTurno } from './utilidadesVentasDelTurno'

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
    mediosDePago: [{ idMedioPago: 1, nombre: 'Efectivo', importe: 100 }],
    ...sobrescribir,
  }
}

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
    expect(formatearMoneda(1234.5)).toBe('$ 1.234,50')
  })
})

describe('formatearFechaHora', () => {
  it('delega en toLocaleString("es-AR") — mismo criterio que Caja.test.tsx (independiente de la zona horaria del entorno)', () => {
    const iso = '2026-09-16T12:05:00Z'
    expect(formatearFechaHora(iso)).toBe(new Date(iso).toLocaleString('es-AR'))
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

describe('totalesPorMedioDeVentas — totales arriba de la tabla, uno por medio', () => {
  it('suma el importe (ya neto) por medio, agrupando entre ventas distintas', () => {
    const ventas = [
      ventaFixture({
        id: 1,
        estado: 'Emitido',
        mediosDePago: [
          { idMedioPago: 1, nombre: 'Efectivo', importe: 100 },
          { idMedioPago: 2, nombre: 'Tarjeta', importe: 50 },
        ],
      }),
      ventaFixture({ id: 2, estado: 'Emitido', mediosDePago: [{ idMedioPago: 1, nombre: 'Efectivo', importe: 30 }] }),
    ]

    expect(totalesPorMedioDeVentas(ventas)).toEqual([
      { idMedioPago: 1, nombre: 'Efectivo', total: 130 },
      { idMedioPago: 2, nombre: 'Tarjeta', total: 50 },
    ])
  })

  it('excluye las anuladas — mutation-proof-tests: la clausula bajo prueba es el filter, no la suma', () => {
    const ventas = [
      ventaFixture({ id: 1, estado: 'Emitido', mediosDePago: [{ idMedioPago: 1, nombre: 'Efectivo', importe: 100 }] }),
      ventaFixture({ id: 2, estado: 'Anulado', mediosDePago: [{ idMedioPago: 1, nombre: 'Efectivo', importe: 900 }] }),
    ]

    expect(totalesPorMedioDeVentas(ventas)).toEqual([{ idMedioPago: 1, nombre: 'Efectivo', total: 100 }])
  })

  it('una lista vacía (o sin ventas vigentes) da un arreglo vacío', () => {
    expect(totalesPorMedioDeVentas([])).toEqual([])
  })

  it('orden alfabético (es) por nombre, sin importar el orden de llegada', () => {
    const ventas = [
      ventaFixture({ id: 1, mediosDePago: [{ idMedioPago: 2, nombre: 'Transferencia', importe: 10 }] }),
      ventaFixture({ id: 2, mediosDePago: [{ idMedioPago: 1, nombre: 'Efectivo', importe: 10 }] }),
    ]
    expect(totalesPorMedioDeVentas(ventas).map((t) => t.nombre)).toEqual(['Efectivo', 'Transferencia'])
  })
})

describe('nombreDeMedio', () => {
  it('resuelve el nombre contra el catálogo', () => {
    expect(nombreDeMedio(1, [medioFixture({ id: 1, nombre: 'Efectivo' })])).toBe('Efectivo')
  })

  it('un medio ausente del catálogo nunca se asume — cae a "Medio #id"', () => {
    expect(nombreDeMedio(99, [medioFixture({ id: 1 })])).toBe('Medio #99')
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

describe('puedeReimprimir', () => {
  it('una venta Emitida puede reimprimirse', () => {
    expect(puedeReimprimir(ventaFixture({ estado: 'Emitido' }))).toBe(true)
  })

  it('una venta Anulada nunca puede reimprimirse', () => {
    expect(puedeReimprimir(ventaFixture({ estado: 'Anulado' }))).toBe(false)
  })
})

describe('filtrarVentas — una clausula por columna, todas en AND', () => {
  const numero1 = ventaFixture({ id: 1, numeroVisible: '0007-00000001', nombreCliente: 'Consumidor Final', total: 100 })
  const numero2 = ventaFixture({
    id: 2,
    numeroVisible: '0007-00000002',
    nombreCliente: 'Juan Pérez',
    total: 250,
    estado: 'Anulado',
    mediosDePago: [{ idMedioPago: 2, nombre: 'Tarjeta', importe: 250 }],
  })

  it('sin filtros (FILTROS_VACIOS) devuelve todo tal cual', () => {
    expect(filtrarVentas([numero1, numero2], FILTROS_VACIOS)).toEqual([numero1, numero2])
  })

  it('número: contains case/accent-insensitive sobre numeroVisible', () => {
    const filtros: FiltrosDeVentasDelTurno = { ...FILTROS_VACIOS, numero: '00000002' }
    expect(filtrarVentas([numero1, numero2], filtros)).toEqual([numero2])
  })

  it('cliente: contains case-insensitive', () => {
    const filtros: FiltrosDeVentasDelTurno = { ...FILTROS_VACIOS, cliente: 'juan' }
    expect(filtrarVentas([numero1, numero2], filtros)).toEqual([numero2])
  })

  it('cliente: contains accent-insensitive ("Perez" matchea "Pérez")', () => {
    const filtros: FiltrosDeVentasDelTurno = { ...FILTROS_VACIOS, cliente: 'Perez' }
    expect(filtrarVentas([numero1, numero2], filtros)).toEqual([numero2])
  })

  it('fecha: contains sobre el texto formateado (formatearFechaHora), no sobre el ISO crudo', () => {
    const conFecha = ventaFixture({ id: 3, fecha: '2026-01-15T10:00:00Z' })
    const filtros: FiltrosDeVentasDelTurno = { ...FILTROS_VACIOS, fecha: formatearFechaHora(conFecha.fecha) }
    expect(filtrarVentas([numero1, conFecha], filtros)).toEqual([conFecha])
  })

  it('total mínimo: excluye ventas por debajo', () => {
    const filtros: FiltrosDeVentasDelTurno = { ...FILTROS_VACIOS, totalMinimo: 200 }
    expect(filtrarVentas([numero1, numero2], filtros)).toEqual([numero2])
  })

  it('total máximo: excluye ventas por encima', () => {
    const filtros: FiltrosDeVentasDelTurno = { ...FILTROS_VACIOS, totalMaximo: 200 }
    expect(filtrarVentas([numero1, numero2], filtros)).toEqual([numero1])
  })

  it('total mínimo y máximo combinados acotan un rango', () => {
    const numero3 = ventaFixture({ id: 3, total: 175 })
    const filtros: FiltrosDeVentasDelTurno = { ...FILTROS_VACIOS, totalMinimo: 150, totalMaximo: 200 }
    expect(filtrarVentas([numero1, numero2, numero3], filtros)).toEqual([numero3])
  })

  it('medio de pago: la venta matchea si USÓ ese medio', () => {
    const filtros: FiltrosDeVentasDelTurno = { ...FILTROS_VACIOS, idMedioPago: 2 }
    expect(filtrarVentas([numero1, numero2], filtros)).toEqual([numero2])
  })

  it('estado: Todas no filtra nada', () => {
    expect(filtrarVentas([numero1, numero2], { ...FILTROS_VACIOS, estado: 'Todas' })).toEqual([numero1, numero2])
  })

  it('estado: Emitido excluye las anuladas', () => {
    expect(filtrarVentas([numero1, numero2], { ...FILTROS_VACIOS, estado: 'Emitido' })).toEqual([numero1])
  })

  it('estado: Anulado excluye las emitidas', () => {
    expect(filtrarVentas([numero1, numero2], { ...FILTROS_VACIOS, estado: 'Anulado' })).toEqual([numero2])
  })

  it('varios filtros combinados son AND, no OR', () => {
    const filtros: FiltrosDeVentasDelTurno = { ...FILTROS_VACIOS, cliente: 'Juan', estado: 'Emitido' }
    // numero2 matchea cliente pero está Anulado — el AND lo excluye igual.
    expect(filtrarVentas([numero1, numero2], filtros)).toEqual([])
  })

  it('un filtro que no matchea ninguna fila da un arreglo vacío (nunca lanza)', () => {
    expect(filtrarVentas([numero1, numero2], { ...FILTROS_VACIOS, numero: 'no existe' })).toEqual([])
  })
})

describe('hayFiltrosActivos', () => {
  it('false con FILTROS_VACIOS', () => {
    expect(hayFiltrosActivos(FILTROS_VACIOS)).toBe(false)
  })

  it('true si cualquier campo de texto está seteado', () => {
    expect(hayFiltrosActivos({ ...FILTROS_VACIOS, numero: 'a' })).toBe(true)
    expect(hayFiltrosActivos({ ...FILTROS_VACIOS, fecha: 'a' })).toBe(true)
    expect(hayFiltrosActivos({ ...FILTROS_VACIOS, cliente: 'a' })).toBe(true)
  })

  it('true si hay total mínimo, máximo, medio o estado distinto de Todas', () => {
    expect(hayFiltrosActivos({ ...FILTROS_VACIOS, totalMinimo: 1 })).toBe(true)
    expect(hayFiltrosActivos({ ...FILTROS_VACIOS, totalMaximo: 1 })).toBe(true)
    expect(hayFiltrosActivos({ ...FILTROS_VACIOS, idMedioPago: 1 })).toBe(true)
    expect(hayFiltrosActivos({ ...FILTROS_VACIOS, estado: 'Emitido' })).toBe(true)
  })
})

describe('mediosDisponibles', () => {
  it('deduplica por id y ordena alfabéticamente (es)', () => {
    const ventas = [
      ventaFixture({
        id: 1,
        mediosDePago: [
          { idMedioPago: 2, nombre: 'Transferencia', importe: 10 },
          { idMedioPago: 1, nombre: 'Efectivo', importe: 20 },
        ],
      }),
      ventaFixture({ id: 2, mediosDePago: [{ idMedioPago: 1, nombre: 'Efectivo', importe: 30 }] }),
    ]

    expect(mediosDisponibles(ventas)).toEqual([
      { idMedioPago: 1, nombre: 'Efectivo' },
      { idMedioPago: 2, nombre: 'Transferencia' },
    ])
  })

  it('una lista vacía da un arreglo vacío', () => {
    expect(mediosDisponibles([])).toEqual([])
  })
})
