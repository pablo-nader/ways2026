import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  aLineaDeOrdenSolicitada,
  aSolicitudDeOrdenDeCompra,
  claseDeBadgeDeEstadoOrdenCompra,
  construirQueryDeOrdenesDeCompra,
  encabezadoDeOrdenVacio,
  etiquetaDeEstadoOrdenCompra,
  filtrosDeOrdenesDeCompraVacios,
  formatearCantidadNullable,
  formatearDesvio,
  formatearMonedaNullable,
  itemDeOrdenAFormulario,
  lineaDeOrdenCompletaParaEnvio,
  lineaDeOrdenVacia,
} from './ordenesDeCompra'
import type { EstadoOrdenCompra, ItemDeOrden } from './tipos'

describe('filtrosDeOrdenesDeCompraVacios / construirQueryDeOrdenesDeCompra', () => {
  it('sin filtros manda solo pagina/tamanio', () => {
    expect(construirQueryDeOrdenesDeCompra(filtrosDeOrdenesDeCompraVacios())).toBe('?pagina=1&tamanio=25')
  })

  it('idProveedor, idPuntoVenta y estado viajan tal cual', () => {
    const query = construirQueryDeOrdenesDeCompra({ ...filtrosDeOrdenesDeCompraVacios(), idProveedor: 8, idPuntoVenta: 3, estado: 'Enviada' })
    expect(query).toContain('idProveedor=8')
    expect(query).toContain('idPuntoVenta=3')
    expect(query).toContain('estado=Enviada')
  })

  it('desde/hasta expanden a los bordes del día con el offset horario local', () => {
    const minutos = new Date(2026, 6, 1).getTimezoneOffset()
    const signo = minutos > 0 ? '-' : '+'
    const horas = String(Math.floor(Math.abs(minutos) / 60)).padStart(2, '0')
    const restoMinutos = String(Math.abs(minutos) % 60).padStart(2, '0')
    const offset = `${signo}${horas}:${restoMinutos}`

    const query = decodeURIComponent(
      construirQueryDeOrdenesDeCompra({ ...filtrosDeOrdenesDeCompraVacios(), desde: '2026-07-01', hasta: '2026-07-31' }),
    )
    expect(query).toContain(`desde=2026-07-01T00:00:00${offset}`)
    expect(query).toContain(`hasta=2026-07-31T23:59:59.999${offset}`)
  })
})

describe('construirQueryDeOrdenesDeCompra — offset fijo (sin espejar la fórmula de la implementación)', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('minutos=180 (UTC-3) produce el literal -03:00, nunca Z (mutation-proof-tests regla 10)', () => {
    vi.spyOn(Date.prototype, 'getTimezoneOffset').mockReturnValue(180)
    const query = decodeURIComponent(construirQueryDeOrdenesDeCompra({ ...filtrosDeOrdenesDeCompraVacios(), desde: '2026-07-01' }))
    expect(query).toContain('desde=2026-07-01T00:00:00-03:00')
  })
})

describe('etiquetaDeEstadoOrdenCompra / claseDeBadgeDeEstadoOrdenCompra', () => {
  const estados: EstadoOrdenCompra[] = ['Borrador', 'Enviada', 'RecibidaParcial', 'Cerrada', 'Anulada']

  it('cada estado tiene una etiqueta y una clase de badge propias y distintas', () => {
    const etiquetas = estados.map(etiquetaDeEstadoOrdenCompra)
    const clases = estados.map(claseDeBadgeDeEstadoOrdenCompra)
    expect(new Set(etiquetas).size).toBe(estados.length)
    expect(etiquetas).toContain('Recibida parcial')
    expect(clases).toContain('text-bg-warning')
  })
})

describe('formatearCantidadNullable', () => {
  it('null renderiza —, nunca 0', () => {
    expect(formatearCantidadNullable(null)).toBe('—')
  })

  it('un cero genuino renderiza 0, nunca —', () => {
    expect(formatearCantidadNullable(0)).toBe('0')
  })

  it('formatea con separador de miles es-AR', () => {
    expect(formatearCantidadNullable(1234.5)).toBe('1.234,5')
  })
})

describe('formatearDesvio', () => {
  it('null renderiza —', () => {
    expect(formatearDesvio(null)).toBe('—')
  })

  it('positivo lleva signo +', () => {
    expect(formatearDesvio(12)).toBe('+12%')
  })

  it('negativo ya trae su propio signo, no se duplica', () => {
    expect(formatearDesvio(-5)).toBe('-5%')
  })

  it('cero genuino renderiza 0%, nunca —', () => {
    expect(formatearDesvio(0)).toBe('0%')
  })
})

describe('formatearMonedaNullable', () => {
  it('null renderiza —', () => {
    expect(formatearMonedaNullable(null)).toBe('—')
  })

  it('negativo antepone el signo antes del $', () => {
    expect(formatearMonedaNullable(-200)).toBe('-$200,00')
  })

  it('positivo formatea con dos decimales', () => {
    expect(formatearMonedaNullable(199.5)).toBe('$199,50')
  })
})

function itemFixture(sobrescribir: Partial<ItemDeOrden> = {}): ItemDeOrden {
  return { orden: 1, idArticulo: 10, descripcion: 'Yerba mate 1kg', cantidadPedida: 5, costoUnitarioEstimado: 120.5, ...sobrescribir }
}

describe('itemDeOrdenAFormulario / lineaDeOrdenVacia', () => {
  it('un item persistido con costo se vuelca tal cual', () => {
    const linea = itemDeOrdenAFormulario(1, itemFixture())
    expect(linea).toEqual({ clave: 1, idArticulo: 10, descripcion: 'Yerba mate 1kg', cantidadPedida: '5', costoUnitarioEstimado: '120.5' })
  })

  it('un item nunca cotizado vuelca costoUnitarioEstimado como cadena vacía, nunca "0"', () => {
    const linea = itemDeOrdenAFormulario(2, itemFixture({ costoUnitarioEstimado: null }))
    expect(linea.costoUnitarioEstimado).toBe('')
  })

  it('lineaDeOrdenVacia arranca sin artículo ni cantidad', () => {
    expect(lineaDeOrdenVacia(9)).toEqual({ clave: 9, idArticulo: '', descripcion: '', cantidadPedida: '', costoUnitarioEstimado: '' })
  })
})

describe('lineaDeOrdenCompletaParaEnvio', () => {
  it('sin artículo es incompleta', () => {
    expect(lineaDeOrdenCompletaParaEnvio(lineaDeOrdenVacia(1))).toBe(false)
  })

  it('con artículo pero sin cantidad es incompleta', () => {
    expect(lineaDeOrdenCompletaParaEnvio({ ...lineaDeOrdenVacia(1), idArticulo: 10 })).toBe(false)
  })

  it('cantidad 0 o negativa es incompleta (cantidad_pedida > 0 en el CHECK del servidor)', () => {
    expect(lineaDeOrdenCompletaParaEnvio({ ...lineaDeOrdenVacia(1), idArticulo: 10, cantidadPedida: '0' })).toBe(false)
    expect(lineaDeOrdenCompletaParaEnvio({ ...lineaDeOrdenVacia(1), idArticulo: 10, cantidadPedida: '-3' })).toBe(false)
  })

  it('artículo + cantidad positiva es completa, con o sin costo', () => {
    expect(lineaDeOrdenCompletaParaEnvio({ ...lineaDeOrdenVacia(1), idArticulo: 10, cantidadPedida: '5' })).toBe(true)
  })
})

describe('aLineaDeOrdenSolicitada', () => {
  it('mapea cantidadPedida/costoUnitarioEstimado a número, costo vacío a null', () => {
    const linea = aLineaDeOrdenSolicitada({ clave: 1, idArticulo: 10, descripcion: '  Yerba  ', cantidadPedida: '7', costoUnitarioEstimado: '' })
    expect(linea).toEqual({ idArticulo: 10, descripcion: 'Yerba', cantidadPedida: 7, costoUnitarioEstimado: null })
  })

  it('un costo seteado viaja como número, nunca string', () => {
    const linea = aLineaDeOrdenSolicitada({ clave: 1, idArticulo: 10, descripcion: 'X', cantidadPedida: '3', costoUnitarioEstimado: '99.9' })
    expect(linea.costoUnitarioEstimado).toBe(99.9)
  })
})

describe('aSolicitudDeOrdenDeCompra', () => {
  it('recorta observaciones vacías a null y fechaEsperada vacía a null', () => {
    const solicitud = aSolicitudDeOrdenDeCompra({ ...encabezadoDeOrdenVacio(), idProveedor: 1, idPuntoVenta: 2, observaciones: '  ' }, [])
    expect(solicitud.observaciones).toBeNull()
    expect(solicitud.fechaEsperada).toBeNull()
  })

  it('fechaEsperada viaja tal cual (DateOnly, sin offset horario)', () => {
    const solicitud = aSolicitudDeOrdenDeCompra(
      { ...encabezadoDeOrdenVacio(), idProveedor: 1, idPuntoVenta: 2, fechaEsperada: '2026-09-15' },
      [],
    )
    expect(solicitud.fechaEsperada).toBe('2026-09-15')
  })

  it('filtra las líneas incompletas — nunca viajan a medio llenar', () => {
    const solicitud = aSolicitudDeOrdenDeCompra({ ...encabezadoDeOrdenVacio(), idProveedor: 1, idPuntoVenta: 2 }, [
      { clave: 1, idArticulo: 10, descripcion: 'A', cantidadPedida: '5', costoUnitarioEstimado: '' },
      { clave: 2, idArticulo: '', descripcion: '', cantidadPedida: '', costoUnitarioEstimado: '' },
    ])
    expect(solicitud.items).toHaveLength(1)
  })
})
