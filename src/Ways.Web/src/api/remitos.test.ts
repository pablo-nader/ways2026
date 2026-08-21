import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  aLineaDeRemitoSolicitada,
  aSolicitudDeFacturacionDeRemitos,
  aSolicitudDeRemito,
  claseDeBadgeDeEstadoRemito,
  construirQueryDeRemitos,
  encabezadoDeRemitoVacio,
  etiquetaDeEstadoRemito,
  filtrosDeRemitosVacios,
  itemDeRemitoAFormulario,
  lineaDeRemitoCompletaParaEnvio,
  lineaDeRemitoVacia,
  reducirSeleccionDeRemitos,
  totalDeRemitosElegidos,
} from './remitos'
import type { EstadoRemito, ItemDeRemito } from './tipos'

describe('filtrosDeRemitosVacios / construirQueryDeRemitos', () => {
  it('sin filtros manda solo pagina/tamanio', () => {
    expect(construirQueryDeRemitos(filtrosDeRemitosVacios())).toBe('?pagina=1&tamanio=25')
  })

  it('idPuntoVenta, idCliente y estado viajan tal cual', () => {
    const query = construirQueryDeRemitos({ ...filtrosDeRemitosVacios(), idPuntoVenta: 7, idCliente: 3, estado: 'Emitido' })
    expect(query).toContain('idPuntoVenta=7')
    expect(query).toContain('idCliente=3')
    expect(query).toContain('estado=Emitido')
  })

  it('desde/hasta expanden a los bordes del día con el offset horario local', () => {
    const minutos = new Date(2026, 6, 1).getTimezoneOffset()
    const signo = minutos > 0 ? '-' : '+'
    const horas = String(Math.floor(Math.abs(minutos) / 60)).padStart(2, '0')
    const restoMinutos = String(Math.abs(minutos) % 60).padStart(2, '0')
    const offset = `${signo}${horas}:${restoMinutos}`

    const query = decodeURIComponent(
      construirQueryDeRemitos({ ...filtrosDeRemitosVacios(), desde: '2026-07-01', hasta: '2026-07-31' }),
    )
    expect(query).toContain(`desde=2026-07-01T00:00:00${offset}`)
    expect(query).toContain(`hasta=2026-07-31T23:59:59.999${offset}`)
  })
})

describe('construirQueryDeRemitos — offset fijo (sin espejar la fórmula de la implementación)', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('minutos=180 (UTC-3) produce el literal -03:00, nunca Z (mutation-proof-tests regla 10)', () => {
    vi.spyOn(Date.prototype, 'getTimezoneOffset').mockReturnValue(180)
    const query = decodeURIComponent(construirQueryDeRemitos({ ...filtrosDeRemitosVacios(), desde: '2026-07-01' }))
    expect(query).toContain('desde=2026-07-01T00:00:00-03:00')
  })
})

describe('etiquetaDeEstadoRemito / claseDeBadgeDeEstadoRemito', () => {
  const estados: EstadoRemito[] = ['Borrador', 'Emitido', 'Facturado', 'Anulado']

  it('cada estado tiene una etiqueta y una clase de badge propias y distintas', () => {
    const etiquetas = estados.map(etiquetaDeEstadoRemito)
    const clases = estados.map(claseDeBadgeDeEstadoRemito)
    expect(new Set(etiquetas).size).toBe(estados.length)
    expect(new Set(clases).size).toBe(estados.length)
    expect(etiquetas).toContain('Facturado')
  })
})

function itemFixture(sobrescribir: Partial<ItemDeRemito> = {}): ItemDeRemito {
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
    costoUnitario: null,
    costoEsEstimado: false,
    idLote: null,
    ...sobrescribir,
  }
}

describe('itemDeRemitoAFormulario / lineaDeRemitoVacia', () => {
  it('un item persistido se vuelca tal cual, incluido el idLote congelado', () => {
    const linea = itemDeRemitoAFormulario(1, itemFixture({ idLote: 55 }))
    expect(linea).toEqual({ clave: 1, idArticulo: 10, descripcion: 'Yerba mate 1kg', cantidad: '5', idLote: 55 })
  })

  it('lineaDeRemitoVacia arranca sin artículo, cantidad ni lote', () => {
    expect(lineaDeRemitoVacia(9)).toEqual({ clave: 9, idArticulo: '', descripcion: '', cantidad: '', idLote: null })
  })
})

describe('lineaDeRemitoCompletaParaEnvio', () => {
  it('sin artículo es incompleta', () => {
    expect(lineaDeRemitoCompletaParaEnvio(lineaDeRemitoVacia(1))).toBe(false)
  })

  it('con artículo pero sin cantidad es incompleta', () => {
    expect(lineaDeRemitoCompletaParaEnvio({ ...lineaDeRemitoVacia(1), idArticulo: 10 })).toBe(false)
  })

  it('cantidad 0 o negativa es incompleta (CHECK del servidor: cantidad > 0)', () => {
    expect(lineaDeRemitoCompletaParaEnvio({ ...lineaDeRemitoVacia(1), idArticulo: 10, cantidad: '0' })).toBe(false)
    expect(lineaDeRemitoCompletaParaEnvio({ ...lineaDeRemitoVacia(1), idArticulo: 10, cantidad: '-3' })).toBe(false)
  })

  it('artículo + cantidad positiva es completa, sin importar si hay lote elegido', () => {
    expect(lineaDeRemitoCompletaParaEnvio({ ...lineaDeRemitoVacia(1), idArticulo: 10, cantidad: '5' })).toBe(true)
    expect(lineaDeRemitoCompletaParaEnvio({ ...lineaDeRemitoVacia(1), idArticulo: 10, cantidad: '5', idLote: 3 })).toBe(true)
  })
})

describe('aLineaDeRemitoSolicitada', () => {
  it('mapea cantidad a número y propaga el idLote elegido', () => {
    expect(aLineaDeRemitoSolicitada({ clave: 1, idArticulo: 10, descripcion: 'Yerba', cantidad: '7', idLote: 3 })).toEqual({
      idArticulo: 10,
      cantidad: 7,
      idLote: 3,
    })
  })

  it('sin lote elegido viaja null (camino feliz de FEFO automático)', () => {
    expect(aLineaDeRemitoSolicitada({ clave: 1, idArticulo: 10, descripcion: '', cantidad: '2', idLote: null }).idLote).toBeNull()
  })

  it('cantidad vacía o no numérica mapea a 0 (nunca viaja sola: el filtro de "completa" ya la descartó antes)', () => {
    expect(aLineaDeRemitoSolicitada({ clave: 1, idArticulo: 10, descripcion: '', cantidad: '', idLote: null }).cantidad).toBe(0)
  })
})

describe('aSolicitudDeRemito', () => {
  it('idCliente vacío viaja como null (Consumidor Final por defecto)', () => {
    const solicitud = aSolicitudDeRemito({ ...encabezadoDeRemitoVacio(), idPuntoVenta: 2 }, [])
    expect(solicitud.idCliente).toBeNull()
  })

  it('recorta direccionEntrega y observaciones vacías a null', () => {
    const solicitud = aSolicitudDeRemito(
      { ...encabezadoDeRemitoVacio(), idPuntoVenta: 2, direccionEntrega: '  ', observaciones: '  ' },
      [],
    )
    expect(solicitud.direccionEntrega).toBeNull()
    expect(solicitud.observaciones).toBeNull()
  })

  it('filtra las líneas incompletas — nunca viajan a medio llenar', () => {
    const solicitud = aSolicitudDeRemito({ ...encabezadoDeRemitoVacio(), idPuntoVenta: 2 }, [
      { clave: 1, idArticulo: 10, descripcion: 'A', cantidad: '5', idLote: null },
      { clave: 2, idArticulo: '', descripcion: '', cantidad: '', idLote: null },
    ])
    expect(solicitud.lineas).toHaveLength(1)
    expect(solicitud.lineas[0]).toEqual({ idArticulo: 10, cantidad: 5, idLote: null })
  })
})

describe('aSolicitudDeFacturacionDeRemitos (consolidación)', () => {
  it('arma la solicitud sin idCliente — el servidor lo deriva de los remitos (dto-contract-honesty regla 1)', () => {
    const solicitud = aSolicitudDeFacturacionDeRemitos(7, [1, 2], [{ idMedioPago: 1, importe: 500, referencia: null, vuelto: 0 }], '')
    expect(solicitud).toEqual({
      idPuntoVenta: 7,
      idsRemito: [1, 2],
      pagos: [{ idMedioPago: 1, importe: 500, referencia: null, vuelto: 0 }],
      observaciones: null,
    })
    expect(solicitud).not.toHaveProperty('idCliente')
  })

  it('recorta observaciones vacías a null', () => {
    const solicitud = aSolicitudDeFacturacionDeRemitos(7, [1], [], '   ')
    expect(solicitud.observaciones).toBeNull()
  })

  it('conserva observaciones con contenido', () => {
    const solicitud = aSolicitudDeFacturacionDeRemitos(7, [1], [], ' entrega urgente ')
    expect(solicitud.observaciones).toBe('entrega urgente')
  })
})

describe('totalDeRemitosElegidos', () => {
  it('suma los totales de los remitos elegidos', () => {
    expect(totalDeRemitosElegidos([{ total: 100 }, { total: 250.5 }, { total: 10 }])).toBe(360.5)
  })

  it('sin remitos elegidos da 0', () => {
    expect(totalDeRemitosElegidos([])).toBe(0)
  })
})

describe('reducirSeleccionDeRemitos (task 8.8, multi-select reducer)', () => {
  it('alternar agrega un id ausente', () => {
    expect(reducirSeleccionDeRemitos([1, 2], { tipo: 'alternar', id: 3 })).toEqual([1, 2, 3])
  })

  it('alternar quita un id presente', () => {
    expect(reducirSeleccionDeRemitos([1, 2, 3], { tipo: 'alternar', id: 2 })).toEqual([1, 3])
  })

  it('alternar sobre una selección vacía agrega el único id', () => {
    expect(reducirSeleccionDeRemitos([], { tipo: 'alternar', id: 5 })).toEqual([5])
  })

  it('elegirTodos reemplaza la selección completa con los ids provistos por el caller', () => {
    expect(reducirSeleccionDeRemitos([1], { tipo: 'elegirTodos', ids: [4, 5, 6] })).toEqual([4, 5, 6])
  })

  it('elegirTodos con lista vacía deselecciona todo (mismo efecto que limpiar)', () => {
    expect(reducirSeleccionDeRemitos([1, 2], { tipo: 'elegirTodos', ids: [] })).toEqual([])
  })

  it('limpiar vacía la selección sin importar el estado previo', () => {
    expect(reducirSeleccionDeRemitos([1, 2, 3], { tipo: 'limpiar' })).toEqual([])
  })

  it('el estado previo nunca se muta — cada acción retorna un array nuevo', () => {
    const previo = [1, 2]
    const siguiente = reducirSeleccionDeRemitos(previo, { tipo: 'alternar', id: 3 })
    expect(siguiente).not.toBe(previo)
    expect(previo).toEqual([1, 2])
  })
})
