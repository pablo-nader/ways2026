import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  aLineaSolicitada,
  aSolicitudDeCompra,
  calcularTotalesDeCompra,
  construirQueryDeCompras,
  etiquetaDeEstadoCompra,
  filtrosDeComprasVacios,
  itemAFormulario,
  lineaCompletaParaEnvio,
  lineaConDescuentoInvalido,
  lineaDeCompraVacia,
  lineaFormularioACalculo,
  type LineaDeCalculo,
  type LineaDeCompraFormulario,
} from './compras'
import type { ItemDeCompra } from './tipos'

function lineaFixture(sobrescribir: Partial<LineaDeCompraFormulario> = {}): LineaDeCompraFormulario {
  return {
    clave: 1,
    idArticulo: 10,
    descripcion: 'Fideos 500g',
    unidades: '10',
    bultos: '',
    unidadesPorBulto: '',
    costoUnitario: '100',
    descuento: '50',
    idAlicuotaIva: 3,
    actualizaCosto: true,
    controlaLote: false,
    codigoLote: '',
    fechaVencimiento: '',
    ...sobrescribir,
  }
}

function itemFixture(sobrescribir: Partial<ItemDeCompra> = {}): ItemDeCompra {
  return {
    orden: 1,
    idArticulo: 10,
    descripcion: 'Fideos 500g',
    cantidad: 10,
    bultos: null,
    unidadesPorBulto: null,
    costoUnitario: 100,
    descuento: 50,
    idAlicuotaIva: 3,
    porcentajeIva: 21,
    total: 950,
    actualizaCosto: true,
    precioSugerido: 114.95,
    codigoLote: null,
    fechaVencimiento: null,
    idLote: null,
    ...sobrescribir,
  }
}

describe('etiquetaDeEstadoCompra', () => {
  it('devuelve una etiqueta legible por cada estado', () => {
    expect(etiquetaDeEstadoCompra('Borrador')).toBe('Borrador')
    expect(etiquetaDeEstadoCompra('Confirmada')).toBe('Confirmada')
    expect(etiquetaDeEstadoCompra('Anulada')).toBe('Anulada')
  })
})

describe('lineaDeCompraVacia / itemAFormulario', () => {
  it('lineaDeCompraVacia arranca sin artículo ni alícuota elegidos, actualizaCosto en true', () => {
    const linea = lineaDeCompraVacia(7)
    expect(linea).toEqual({
      clave: 7,
      idArticulo: '',
      descripcion: '',
      unidades: '',
      bultos: '',
      unidadesPorBulto: '',
      costoUnitario: '',
      descuento: '',
      idAlicuotaIva: '',
      actualizaCosto: true,
      controlaLote: false,
      codigoLote: '',
      fechaVencimiento: '',
    })
  })

  it('itemAFormulario sin bultos deriva unidades = cantidad tal cual', () => {
    const linea = itemAFormulario(1, itemFixture({ cantidad: 10, bultos: null, unidadesPorBulto: null }))
    expect(linea.unidades).toBe('10')
    expect(linea.bultos).toBe('')
    expect(linea.unidadesPorBulto).toBe('')
  })

  it('itemAFormulario con bultos reconstruye unidades restando bultos × unidadesPorBulto de la cantidad persistida', () => {
    // cantidad = unidades + bultos*unidadesPorBulto = unidades + 3*6 => unidades = cantidad - 18
    const linea = itemAFormulario(1, itemFixture({ cantidad: 20, bultos: 3, unidadesPorBulto: 6 }))
    expect(linea.unidades).toBe('2')
    expect(linea.bultos).toBe('3')
    expect(linea.unidadesPorBulto).toBe('6')
  })

  it('itemAFormulario infiere controlaLote de un dato de lote ya persistido (stage-12-lotes-vencimientos, Slice 14)', () => {
    const conLote = itemAFormulario(1, itemFixture({ codigoLote: 'L-01', fechaVencimiento: '2026-12-01' }))
    expect(conLote.controlaLote).toBe(true)
    expect(conLote.codigoLote).toBe('L-01')
    expect(conLote.fechaVencimiento).toBe('2026-12-01')

    const sinLote = itemAFormulario(1, itemFixture({ codigoLote: null, fechaVencimiento: null, idLote: null }))
    expect(sinLote.controlaLote).toBe(false)
    expect(sinLote.codigoLote).toBe('')
    expect(sinLote.fechaVencimiento).toBe('')
  })
})

describe('lineaCompletaParaEnvio', () => {
  it('una línea con artículo, alícuota, unidades y costo está completa', () => {
    expect(lineaCompletaParaEnvio(lineaFixture())).toBe(true)
  })

  it('sin artículo elegido no está completa', () => {
    expect(lineaCompletaParaEnvio(lineaFixture({ idArticulo: '' }))).toBe(false)
  })

  it('sin alícuota elegida no está completa', () => {
    expect(lineaCompletaParaEnvio(lineaFixture({ idAlicuotaIva: '' }))).toBe(false)
  })

  it('sin unidades tipeadas no está completa', () => {
    expect(lineaCompletaParaEnvio(lineaFixture({ unidades: '' }))).toBe(false)
  })

  it('sin costo unitario tipeado no está completa', () => {
    expect(lineaCompletaParaEnvio(lineaFixture({ costoUnitario: '' }))).toBe(false)
  })

  it('un artículo que controla lote sin fecha de vencimiento no está completa (espejo del 400 lote_requerido del confirm)', () => {
    expect(lineaCompletaParaEnvio(lineaFixture({ controlaLote: true, fechaVencimiento: '' }))).toBe(false)
  })

  it('un artículo que controla lote con fecha de vencimiento (sin código, se deriva) está completa', () => {
    expect(lineaCompletaParaEnvio(lineaFixture({ controlaLote: true, codigoLote: '', fechaVencimiento: '2026-12-01' }))).toBe(true)
  })

  it('un artículo que no controla lote nunca exige fecha de vencimiento', () => {
    expect(lineaCompletaParaEnvio(lineaFixture({ controlaLote: false, fechaVencimiento: '' }))).toBe(true)
  })
})

describe('aLineaSolicitada', () => {
  it('mapea los campos numéricos y recorta bultos/unidadesPorBulto vacíos a null', () => {
    expect(aLineaSolicitada(lineaFixture())).toEqual({
      idArticulo: 10,
      descripcion: 'Fideos 500g',
      unidades: 10,
      bultos: null,
      unidadesPorBulto: null,
      costoUnitario: 100,
      descuento: 50,
      idAlicuotaIva: 3,
      actualizaCosto: true,
      codigoLote: null,
      fechaVencimiento: null,
    })
  })

  it('un descuento vacío se envía como 0, nunca NaN', () => {
    expect(aLineaSolicitada(lineaFixture({ descuento: '' })).descuento).toBe(0)
  })

  it('bultos/unidadesPorBulto tipeados viajan como número', () => {
    const linea = aLineaSolicitada(lineaFixture({ bultos: '3', unidadesPorBulto: '6' }))
    expect(linea.bultos).toBe(3)
    expect(linea.unidadesPorBulto).toBe(6)
  })

  it('codigoLote/fechaVencimiento vacíos recortan a null; tipeados viajan tal cual', () => {
    expect(aLineaSolicitada(lineaFixture({ codigoLote: '', fechaVencimiento: '' })).codigoLote).toBeNull()
    expect(aLineaSolicitada(lineaFixture({ codigoLote: '', fechaVencimiento: '' })).fechaVencimiento).toBeNull()

    const linea = aLineaSolicitada(lineaFixture({ codigoLote: '  L-01  ', fechaVencimiento: '2026-12-01' }))
    expect(linea.codigoLote).toBe('L-01')
    expect(linea.fechaVencimiento).toBe('2026-12-01')
  })
})

describe('aSolicitudDeCompra', () => {
  it('recorta numeroExterno/observaciones vacíos a null y fechaComprobante vacía a null', () => {
    const solicitud = aSolicitudDeCompra(
      {
        idProveedor: 1,
        idTipoComprobante: 5,
        idPuntoVenta: 2,
        numeroExterno: '   ',
        fechaComprobante: '',
        observaciones: '  ',
        idOrdenCompra: null,
      },
      [],
    )
    expect(solicitud.numeroExterno).toBeNull()
    expect(solicitud.fechaComprobante).toBeNull()
    expect(solicitud.observaciones).toBeNull()
  })

  it('fechaComprobante viaja tal cual (DateOnly, sin offset horario)', () => {
    const solicitud = aSolicitudDeCompra(
      {
        idProveedor: 1,
        idTipoComprobante: 5,
        idPuntoVenta: 2,
        numeroExterno: '0003-00012345',
        fechaComprobante: '2026-08-05',
        observaciones: '',
        idOrdenCompra: null,
      },
      [],
    )
    expect(solicitud.fechaComprobante).toBe('2026-08-05')
  })

  it('filtra las líneas incompletas — nunca viajan a medio llenar', () => {
    const solicitud = aSolicitudDeCompra(
      {
        idProveedor: 1,
        idTipoComprobante: 5,
        idPuntoVenta: 2,
        numeroExterno: '',
        fechaComprobante: '',
        observaciones: '',
        idOrdenCompra: null,
      },
      [lineaFixture({ clave: 1 }), lineaFixture({ clave: 2, idArticulo: '' })],
    )
    expect(solicitud.items).toHaveLength(1)
  })

  // stage-16-ordenes-de-compra, Slice 6: idOrdenCompra viaja tal cual desde el encabezado — ni
  // recortado ni defaulteado a 0/undefined, campo posicional final de SolicitudDeCompra.
  it('idOrdenCompra viaja tal cual desde el encabezado (mutation-proof-tests regla 12b)', () => {
    const solicitud = aSolicitudDeCompra(
      { idProveedor: 1, idTipoComprobante: 5, idPuntoVenta: 2, numeroExterno: '', fechaComprobante: '', observaciones: '', idOrdenCompra: 42 },
      [],
    )
    expect(solicitud.idOrdenCompra).toBe(42)
  })

  it('idOrdenCompra ausente viaja como null, nunca 0', () => {
    const solicitud = aSolicitudDeCompra(
      { idProveedor: 1, idTipoComprobante: 5, idPuntoVenta: 2, numeroExterno: '', fechaComprobante: '', observaciones: '', idOrdenCompra: null },
      [],
    )
    expect(solicitud.idOrdenCompra).toBeNull()
  })
})

describe('lineaFormularioACalculo', () => {
  it('resuelve porcentajeIva desde el catálogo de alícuotas, nunca desde el formulario', () => {
    const calculo = lineaFormularioACalculo(lineaFixture({ idAlicuotaIva: 3 }), { 3: 21 })
    expect(calculo.porcentajeIva).toBe(21)
  })

  it('una alícuota sin elegir resuelve porcentajeIva = 0', () => {
    const calculo = lineaFormularioACalculo(lineaFixture({ idAlicuotaIva: '' }), { 3: 21 })
    expect(calculo.porcentajeIva).toBe(0)
  })
})

// ---- calcularTotalesDeCompra: espejo de CalculadorDeCompra (design: "Compra Arithmetic") -----

function lineaDeCalculoFixture(sobrescribir: Partial<LineaDeCalculo> = {}): LineaDeCalculo {
  return { unidades: 10, bultos: 0, unidadesPorBulto: 0, costoUnitario: 100, descuento: 50, porcentajeIva: 21, ...sobrescribir }
}

describe('calcularTotalesDeCompra', () => {
  it('cantidad = unidades + bultos × unidadesPorBulto', () => {
    const { items } = calcularTotalesDeCompra([lineaDeCalculoFixture({ unidades: 2, bultos: 3, unidadesPorBulto: 6 })], false)
    expect(items[0].cantidad).toBe(20)
  })

  it('discrimina_iva = true: iva_total por línea, total = subtotal − descuento + iva, costoEfectivo incluye IVA', () => {
    const totales = calcularTotalesDeCompra([lineaDeCalculoFixture()], true)
    expect(totales.subtotal).toBe(1000) // 10 × 100
    expect(totales.descuentoTotal).toBe(50)
    expect(totales.items[0].total).toBe(950) // 1000 − 50
    expect(totales.ivaTotal).toBe(199.5) // round(950 × 21/100, 2)
    expect(totales.total).toBe(1149.5) // 1000 − 50 + 199.5
    expect(totales.items[0].costoEfectivo).toBe(114.95) // round(950 × 1.21 / 10, 2)
  })

  it('discrimina_iva = false: ivaTotal es NULL, total no suma IVA, costoEfectivo no lo incluye', () => {
    const totales = calcularTotalesDeCompra([lineaDeCalculoFixture()], false)
    expect(totales.ivaTotal).toBeNull()
    expect(totales.total).toBe(950) // 1000 − 50
    expect(totales.items[0].costoEfectivo).toBe(95) // round(950 / 10, 2)
  })

  it('una línea de bonificación (costo 0) no aporta bruto ni costoEfectivo negativo', () => {
    const totales = calcularTotalesDeCompra([lineaDeCalculoFixture({ costoUnitario: 0, descuento: 0 })], false)
    expect(totales.items[0].bruto).toBe(0)
    expect(totales.items[0].costoEfectivo).toBe(0)
  })

  it('un set vacío de líneas da totales en cero, iva NULL cuando no discrimina', () => {
    const totales = calcularTotalesDeCompra([], false)
    expect(totales).toEqual({ items: [], subtotal: 0, descuentoTotal: 0, ivaTotal: null, total: 0 })
  })

  it('cantidad 0 no divide por cero — costoEfectivo es null', () => {
    const totales = calcularTotalesDeCompra([lineaDeCalculoFixture({ unidades: 0, bultos: 0, unidadesPorBulto: 0 })], true)
    expect(totales.items[0].costoEfectivo).toBeNull()
  })

  it('dos líneas suman sus brutos/descuentos/iva independientemente', () => {
    const totales = calcularTotalesDeCompra(
      [lineaDeCalculoFixture(), lineaDeCalculoFixture({ unidades: 5, costoUnitario: 40, descuento: 0, porcentajeIva: 10.5 })],
      true,
    )
    // línea 2: cantidad 5, bruto 200, total 200, iva round(200×10.5/100,2)=21
    expect(totales.subtotal).toBe(1200) // 1000 + 200
    expect(totales.descuentoTotal).toBe(50) // 50 + 0
    expect(totales.ivaTotal).toBe(220.5) // 199.5 + 21
  })
})

describe('lineaConDescuentoInvalido', () => {
  it('un descuento mayor al bruto es inválido', () => {
    expect(lineaConDescuentoInvalido(lineaDeCalculoFixture({ descuento: 1500 }))).toBe(true)
  })

  it('un descuento igual o menor al bruto es válido', () => {
    expect(lineaConDescuentoInvalido(lineaDeCalculoFixture({ descuento: 1000 }))).toBe(false)
    expect(lineaConDescuentoInvalido(lineaDeCalculoFixture({ descuento: 50 }))).toBe(false)
  })
})

// ---- construirQueryDeCompras: mismo patrón de offset explícito que cuentaCorriente.ts --------

function offsetEsperado(anio: number, mes: number, dia: number): string {
  const minutos = new Date(anio, mes - 1, dia).getTimezoneOffset()
  const signo = minutos > 0 ? '-' : '+'
  const minutosAbsolutos = Math.abs(minutos)
  const horas = String(Math.floor(minutosAbsolutos / 60)).padStart(2, '0')
  const restoMinutos = String(minutosAbsolutos % 60).padStart(2, '0')
  return `${signo}${horas}:${restoMinutos}`
}

describe('construirQueryDeCompras', () => {
  it('sin filtros manda solo pagina/tamanio', () => {
    expect(construirQueryDeCompras(filtrosDeComprasVacios())).toBe('?pagina=1&tamanio=25')
  })

  it('idProveedor y estado viajan tal cual', () => {
    const query = construirQueryDeCompras({ ...filtrosDeComprasVacios(), idProveedor: 8, estado: 'Confirmada' })
    expect(query).toContain('idProveedor=8')
    expect(query).toContain('estado=Confirmada')
  })

  it('desde/hasta expanden a los bordes del día con el offset horario local', () => {
    const offsetDesde = offsetEsperado(2026, 7, 1)
    const offsetHasta = offsetEsperado(2026, 7, 31)
    const query = decodeURIComponent(
      construirQueryDeCompras({ ...filtrosDeComprasVacios(), desde: '2026-07-01', hasta: '2026-07-31' }),
    )
    expect(query).toContain(`desde=2026-07-01T00:00:00${offsetDesde}`)
    expect(query).toContain(`hasta=2026-07-31T23:59:59.999${offsetHasta}`)
  })
})

describe('construirQueryDeCompras — offset fijo (sin espejar la fórmula de la implementación)', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('minutos=180 (UTC-3) produce el literal -03:00', () => {
    vi.spyOn(Date.prototype, 'getTimezoneOffset').mockReturnValue(180)
    const query = decodeURIComponent(construirQueryDeCompras({ ...filtrosDeComprasVacios(), desde: '2026-07-01' }))
    expect(query).toContain('desde=2026-07-01T00:00:00-03:00')
  })
})
