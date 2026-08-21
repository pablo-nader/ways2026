import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  aLineaDePresupuestoSolicitada,
  aSolicitudDeEnvio,
  aSolicitudDePresupuesto,
  aSolicitudDeVentaDesdePresupuesto,
  claseDeBadgeDeEstadoPresupuesto,
  claseDeBadgeDeVencimiento,
  construirQueryDePresupuestos,
  encabezadoDePresupuestoVacio,
  etiquetaDeEstadoPresupuesto,
  etiquetaDeVencimiento,
  filtrosDePresupuestosVacios,
  itemDePresupuestoAFormulario,
  lineaDePresupuestoCompletaParaEnvio,
  lineaDePresupuestoVacia,
  vencimientoSugerido,
} from './presupuestos'
import type { EstadoPresupuesto, ItemDePresupuesto } from './tipos'

describe('filtrosDePresupuestosVacios / construirQueryDePresupuestos', () => {
  it('sin filtros manda solo pagina/tamanio', () => {
    expect(construirQueryDePresupuestos(filtrosDePresupuestosVacios())).toBe('?pagina=1&tamanio=25')
  })

  it('idPuntoVenta, idCliente y estado viajan tal cual', () => {
    const query = construirQueryDePresupuestos({ ...filtrosDePresupuestosVacios(), idPuntoVenta: 7, idCliente: 3, estado: 'Enviado' })
    expect(query).toContain('idPuntoVenta=7')
    expect(query).toContain('idCliente=3')
    expect(query).toContain('estado=Enviado')
  })

  it('vencido viaja cuando hay idPuntoVenta (regla del guard de la 400 punto_venta_requerido)', () => {
    const query = construirQueryDePresupuestos({ ...filtrosDePresupuestosVacios(), idPuntoVenta: 7, vencido: true })
    expect(decodeURIComponent(query)).toContain('vencido=true')
  })

  it('vencido NUNCA viaja sin idPuntoVenta, aunque el filtro esté seteado (defensa en profundidad de la 400)', () => {
    const query = construirQueryDePresupuestos({ ...filtrosDePresupuestosVacios(), idPuntoVenta: null, vencido: true })
    expect(query).not.toContain('vencido')
  })

  it('desde/hasta expanden a los bordes del día con el offset horario local', () => {
    const minutos = new Date(2026, 6, 1).getTimezoneOffset()
    const signo = minutos > 0 ? '-' : '+'
    const horas = String(Math.floor(Math.abs(minutos) / 60)).padStart(2, '0')
    const restoMinutos = String(Math.abs(minutos) % 60).padStart(2, '0')
    const offset = `${signo}${horas}:${restoMinutos}`

    const query = decodeURIComponent(
      construirQueryDePresupuestos({ ...filtrosDePresupuestosVacios(), desde: '2026-07-01', hasta: '2026-07-31' }),
    )
    expect(query).toContain(`desde=2026-07-01T00:00:00${offset}`)
    expect(query).toContain(`hasta=2026-07-31T23:59:59.999${offset}`)
  })
})

describe('construirQueryDePresupuestos — offset fijo (sin espejar la fórmula de la implementación)', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('minutos=180 (UTC-3) produce el literal -03:00, nunca Z (mutation-proof-tests regla 10)', () => {
    vi.spyOn(Date.prototype, 'getTimezoneOffset').mockReturnValue(180)
    const query = decodeURIComponent(construirQueryDePresupuestos({ ...filtrosDePresupuestosVacios(), desde: '2026-07-01' }))
    expect(query).toContain('desde=2026-07-01T00:00:00-03:00')
  })
})

describe('etiquetaDeEstadoPresupuesto / claseDeBadgeDeEstadoPresupuesto', () => {
  const estados: EstadoPresupuesto[] = ['Borrador', 'Enviado', 'Convertido', 'Anulado']

  it('cada estado tiene una etiqueta y una clase de badge propias y distintas', () => {
    const etiquetas = estados.map(etiquetaDeEstadoPresupuesto)
    const clases = estados.map(claseDeBadgeDeEstadoPresupuesto)
    expect(new Set(etiquetas).size).toBe(estados.length)
    expect(new Set(clases).size).toBe(estados.length)
    expect(etiquetas).toContain('Convertido')
  })
})

describe('etiquetaDeVencimiento / claseDeBadgeDeVencimiento', () => {
  it('sin vencimiento (borrador) renderiza —, nunca una fecha inventada', () => {
    expect(etiquetaDeVencimiento(null, false)).toBe('—')
    expect(claseDeBadgeDeVencimiento(null, false)).toBe('text-bg-secondary')
  })

  it('vencido true antepone "Venció", vencido false antepone "Vence" — mismo dato crudo', () => {
    expect(etiquetaDeVencimiento('2026-09-30', true)).toBe('Venció 30/9/2026')
    expect(etiquetaDeVencimiento('2026-09-30', false)).toBe('Vence 30/9/2026')
  })

  it('la clase de badge distingue vencido de vigente', () => {
    expect(claseDeBadgeDeVencimiento('2026-09-30', true)).toBe('text-bg-danger')
    expect(claseDeBadgeDeVencimiento('2026-09-30', false)).toBe('text-bg-success')
  })
})

function itemFixture(sobrescribir: Partial<ItemDePresupuesto> = {}): ItemDePresupuesto {
  return {
    orden: 1,
    idArticulo: 10,
    descripcion: 'Yerba mate 1kg',
    cantidad: 5,
    precioUnitario: 1200.5,
    descuento: 0,
    total: 6002.5,
    idListaPrecio: 1,
    idOferta: null,
    idAlicuotaIva: 1,
    porcentajeIva: 21,
    ...sobrescribir,
  }
}

describe('itemDePresupuestoAFormulario / lineaDePresupuestoVacia', () => {
  it('un item persistido se vuelca tal cual (sin ningún campo de dinero, design decisión 2)', () => {
    const linea = itemDePresupuestoAFormulario(1, itemFixture())
    expect(linea).toEqual({ clave: 1, idArticulo: 10, descripcion: 'Yerba mate 1kg', cantidad: '5' })
  })

  it('lineaDePresupuestoVacia arranca sin artículo ni cantidad', () => {
    expect(lineaDePresupuestoVacia(9)).toEqual({ clave: 9, idArticulo: '', descripcion: '', cantidad: '' })
  })
})

describe('lineaDePresupuestoCompletaParaEnvio', () => {
  it('sin artículo es incompleta', () => {
    expect(lineaDePresupuestoCompletaParaEnvio(lineaDePresupuestoVacia(1))).toBe(false)
  })

  it('con artículo pero sin cantidad es incompleta', () => {
    expect(lineaDePresupuestoCompletaParaEnvio({ ...lineaDePresupuestoVacia(1), idArticulo: 10 })).toBe(false)
  })

  it('cantidad 0 o negativa es incompleta (CHECK del servidor: cantidad > 0)', () => {
    expect(lineaDePresupuestoCompletaParaEnvio({ ...lineaDePresupuestoVacia(1), idArticulo: 10, cantidad: '0' })).toBe(false)
    expect(lineaDePresupuestoCompletaParaEnvio({ ...lineaDePresupuestoVacia(1), idArticulo: 10, cantidad: '-3' })).toBe(false)
  })

  it('artículo + cantidad positiva es completa', () => {
    expect(lineaDePresupuestoCompletaParaEnvio({ ...lineaDePresupuestoVacia(1), idArticulo: 10, cantidad: '5' })).toBe(true)
  })
})

describe('aLineaDePresupuestoSolicitada', () => {
  it('mapea cantidad a número', () => {
    expect(aLineaDePresupuestoSolicitada({ clave: 1, idArticulo: 10, descripcion: 'Yerba', cantidad: '7' })).toEqual({
      idArticulo: 10,
      cantidad: 7,
    })
  })

  it('cantidad vacía o no numérica mapea a 0 (nunca viaja sola: el filtro de "completa" ya la descartó antes)', () => {
    expect(aLineaDePresupuestoSolicitada({ clave: 1, idArticulo: 10, descripcion: '', cantidad: '' }).cantidad).toBe(0)
  })
})

describe('aSolicitudDePresupuesto', () => {
  it('idCliente vacío viaja como null (Consumidor Final por defecto)', () => {
    const solicitud = aSolicitudDePresupuesto({ ...encabezadoDePresupuestoVacio(), idPuntoVenta: 2 }, [])
    expect(solicitud.idCliente).toBeNull()
  })

  it('recorta observaciones vacías a null', () => {
    const solicitud = aSolicitudDePresupuesto({ ...encabezadoDePresupuestoVacio(), idPuntoVenta: 2, observaciones: '  ' }, [])
    expect(solicitud.observaciones).toBeNull()
  })

  it('filtra las líneas incompletas — nunca viajan a medio llenar', () => {
    const solicitud = aSolicitudDePresupuesto({ ...encabezadoDePresupuestoVacio(), idPuntoVenta: 2 }, [
      { clave: 1, idArticulo: 10, descripcion: 'A', cantidad: '5' },
      { clave: 2, idArticulo: '', descripcion: '', cantidad: '' },
    ])
    expect(solicitud.lineas).toHaveLength(1)
    expect(solicitud.lineas[0]).toEqual({ idArticulo: 10, cantidad: 5 })
  })
})

describe('aSolicitudDeEnvio', () => {
  it('vencimiento viaja tal cual, sin offset horario (DateOnly)', () => {
    expect(aSolicitudDeEnvio('2026-09-15')).toEqual({ vencimiento: '2026-09-15' })
  })
})

describe('vencimientoSugerido', () => {
  it('suma 30 días a la fecha dada, formateado YYYY-MM-DD', () => {
    expect(vencimientoSugerido(new Date(2026, 0, 1))).toBe('2026-01-31')
  })

  it('cruza el fin de mes correctamente', () => {
    expect(vencimientoSugerido(new Date(2026, 8, 15))).toBe('2026-10-15')
  })
})

describe('aSolicitudDeVentaDesdePresupuesto', () => {
  it('arma la SolicitudDeVenta de conversión sin idCliente ni lineas (dto-contract-honesty regla 1)', () => {
    const solicitud = aSolicitudDeVentaDesdePresupuesto(7, 42, [{ idMedioPago: 1, importe: 100, referencia: null, vuelto: 0 }])
    expect(solicitud).toEqual({
      idPuntoVenta: 7,
      codigoTipoComprobante: 'TX',
      idComprobanteAsociado: null,
      pagos: [{ idMedioPago: 1, importe: 100, referencia: null, vuelto: 0 }],
      direccionEntrega: null,
      observaciones: null,
      idPresupuestoOrigen: 42,
    })
    expect(solicitud).not.toHaveProperty('idCliente')
    expect(solicitud).not.toHaveProperty('lineas')
  })
})
