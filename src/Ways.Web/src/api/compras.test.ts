import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  aSolicitudDeCompra,
  calcularTotalesDeCompra,
  costoDeLineaIncluyeIva,
  construirQueryDeCompras,
  claseDeBadgeDeEstadoPagoDeLista,
  construirQueryDeDuplicados,
  etiquetaDeEstadoPagoDeLista,
  esBorradorCargadoPorElAsistente,
  etiquetaDeEstadoCompra,
  filtrosDeComprasVacios,
  lineaDeCompraVacia,
  lineaFormularioACalculo,
  type LineaDeCalculo,
  type LineaDeCompraFormulario,
} from './compras'

function lineaFixture(sobrescribir: Partial<LineaDeCompraFormulario> = {}): LineaDeCompraFormulario {
  return {
    ...lineaDeCompraVacia(1),
    idArticulo: 10,
    descripcion: 'Fideos 500g',
    cantidad: '10',
    importe: 100,
    descuentoPorcentaje: 5,
    idAlicuotaIva: 3,
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

describe('costoDeLineaIncluyeIva', () => {
  it.each([
    ['discrimina IVA y los precios no son finales: el costo es neto', true, false, false],
    ['discrimina IVA y los precios son finales: el costo trae el IVA', true, true, true],
    ['no discrimina IVA: el costo es el final', false, false, true],
  ])('%s', (_nombre, discriminaIva, preciosFinales, esperado) => {
    expect(costoDeLineaIncluyeIva(discriminaIva, preciosFinales)).toBe(esperado)
  })

  it('coincide con cómo calcula el costo efectivo de la línea: solo suma el IVA cuando el costo es neto', () => {
    const linea = lineaFixture({ cantidad: '1', importe: 100, descuentoPorcentaje: null, idAlicuotaIva: 3 })
    const costoEfectivo = (discriminaIva: boolean, preciosFinales: boolean) =>
      calcularTotalesDeCompra([lineaFormularioACalculo(linea, { 3: 21 })], discriminaIva, {}, preciosFinales).items[0].costoEfectivo

    expect(costoEfectivo(true, false)).toBe(121)
    expect(costoDeLineaIncluyeIva(true, false)).toBe(false)
    expect(costoEfectivo(true, true)).toBe(100)
    expect(costoDeLineaIncluyeIva(true, true)).toBe(true)
    expect(costoEfectivo(false, false)).toBe(100)
    expect(costoDeLineaIncluyeIva(false, false)).toBe(true)
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
        discriminaIva: null,
        ivaImpreso: {},
        preciosIncluyenIva: false,
        percepciones: [],
        percepcionesDescartadas: [],
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
        discriminaIva: null,
        ivaImpreso: {},
        preciosIncluyenIva: false,
        percepciones: [],
        percepcionesDescartadas: [],
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
        discriminaIva: null,
        ivaImpreso: {},
        preciosIncluyenIva: false,
        percepciones: [],
        percepcionesDescartadas: [],
      },
      [lineaFixture({ clave: 1 }), lineaFixture({ clave: 2, importe: null }), lineaDeCompraVacia(3)],
    )
    expect(solicitud.items).toHaveLength(1)
  })

  // stage-16-ordenes-de-compra, Slice 6: idOrdenCompra viaja tal cual desde el encabezado — ni
  // recortado ni defaulteado a 0/undefined, campo posicional final de SolicitudDeCompra.
  it('idOrdenCompra viaja tal cual desde el encabezado (mutation-proof-tests regla 12b)', () => {
    const solicitud = aSolicitudDeCompra(
      { idProveedor: 1, idTipoComprobante: 5, idPuntoVenta: 2, numeroExterno: '', fechaComprobante: '', observaciones: '', idOrdenCompra: 42, discriminaIva: null, ivaImpreso: {}, preciosIncluyenIva: false, percepciones: [], percepcionesDescartadas: [] },
      [],
    )
    expect(solicitud.idOrdenCompra).toBe(42)
  })

  it('idOrdenCompra ausente viaja como null, nunca 0', () => {
    const solicitud = aSolicitudDeCompra(
      { idProveedor: 1, idTipoComprobante: 5, idPuntoVenta: 2, numeroExterno: '', fechaComprobante: '', observaciones: '', idOrdenCompra: null, discriminaIva: null, ivaImpreso: {}, preciosIncluyenIva: false, percepciones: [], percepcionesDescartadas: [] },
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

  it('el descuento en porcentaje llega al cálculo como monto sobre el bruto de la línea', () => {
    const calculo = lineaFormularioACalculo(lineaFixture({ cantidad: '10', importe: 100, descuentoPorcentaje: 5 }), { 3: 21 })
    expect(calculo).toMatchObject({ unidades: 10, bultos: 0, unidadesPorBulto: 0, costoUnitario: 100, descuento: 50 })
  })

  it('una línea por bulto llega al cálculo con bultos, unidades por bulto y costo por unidad', () => {
    const calculo = lineaFormularioACalculo(
      lineaFixture({ cantidad: '2', unidadDeMedida: 'Bulto', unidadesPorBulto: 6, importe: 60, descuentoPorcentaje: null }),
      { 3: 21 },
    )
    expect(calculo).toMatchObject({ unidades: 0, bultos: 2, unidadesPorBulto: 6, costoUnitario: 10, descuento: 0 })
    expect(calcularTotalesDeCompra([calculo], false).items[0]).toMatchObject({ cantidad: 12, bruto: 120, total: 120 })
  })
})

// ---- calcularTotalesDeCompra: espejo de CalculadorDeCompra (design: "Compra Arithmetic") -----

function lineaDeCalculoFixture(sobrescribir: Partial<LineaDeCalculo> = {}): LineaDeCalculo {
  return { idAlicuotaIva: 1, unidades: 10, bultos: 0, unidadesPorBulto: 0, costoUnitario: 100, descuento: 50, porcentajeIva: 21, impuestoInterno: 0, ...sobrescribir }
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
    expect(totales).toEqual({
      items: [],
      subtotal: 0,
      descuentoTotal: 0,
      ivaTotal: null,
      total: 0,
      alicuotas: [],
      percepcionesTotal: 0,
      impuestosInternos: 0,
    })
  })

  it('cantidad 0 no divide por cero — costoEfectivo es null', () => {
    const totales = calcularTotalesDeCompra([lineaDeCalculoFixture({ unidades: 0, bultos: 0, unidadesPorBulto: 0 })], true)
    expect(totales.items[0].costoEfectivo).toBeNull()
  })

  it('dos líneas suman sus brutos/descuentos/iva independientemente', () => {
    const totales = calcularTotalesDeCompra(
      [lineaDeCalculoFixture(), lineaDeCalculoFixture({ idAlicuotaIva: 2, unidades: 5, costoUnitario: 40, descuento: 0, porcentajeIva: 10.5 })],
      true,
    )
    // línea 2: cantidad 5, bruto 200, total 200, iva round(200×10.5/100,2)=21
    expect(totales.subtotal).toBe(1200) // 1000 + 200
    expect(totales.descuentoTotal).toBe(50) // 50 + 0
    expect(totales.ivaTotal).toBe(220.5) // 199.5 + 21
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

  it('origen viaja tal cual y sin filtro no se manda', () => {
    expect(construirQueryDeCompras({ ...filtrosDeComprasVacios(), origen: 'Asistente' })).toContain('origen=Asistente')
    expect(construirQueryDeCompras(filtrosDeComprasVacios())).not.toContain('origen')
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

describe('esBorradorCargadoPorElAsistente', () => {
  it('solo un borrador del asistente pide revisión', () => {
    expect(esBorradorCargadoPorElAsistente({ estado: 'Borrador', origen: 'Asistente' })).toBe(true)
    expect(esBorradorCargadoPorElAsistente({ estado: 'Borrador', origen: 'Manual' })).toBe(false)
    expect(esBorradorCargadoPorElAsistente({ estado: 'Confirmada', origen: 'Asistente' })).toBe(false)
    expect(esBorradorCargadoPorElAsistente({ estado: 'Anulada', origen: 'Asistente' })).toBe(false)
  })

  it('sin compra cargada no hay aviso', () => {
    expect(esBorradorCargadoPorElAsistente(null)).toBe(false)
  })
})

describe('construirQueryDeCompras — filtros de la grilla', () => {
  it('sin filtros nuevos no manda ninguno de sus parámetros', () => {
    const query = construirQueryDeCompras(filtrosDeComprasVacios())
    for (const clave of ['numero', 'idTipoComprobante', 'estadoPago', 'fechaComprobante']) expect(query).not.toContain(clave)
  })

  it('numero, tipo y estado de pago viajan tal cual; el número se recorta', () => {
    const params = new URLSearchParams(
      construirQueryDeCompras({ ...filtrosDeComprasVacios(), numero: '  0003-1 ', idTipoComprobante: 5, estadoPago: 'pendiente' }),
    )
    expect(params.get('numero')).toBe('0003-1')
    expect(params.get('idTipoComprobante')).toBe('5')
    expect(params.get('estadoPago')).toBe('pendiente')
  })

  it('un número en blanco no se manda', () => {
    expect(construirQueryDeCompras({ ...filtrosDeComprasVacios(), numero: '   ' })).not.toContain('numero')
  })

  it('la fecha del comprobante viaja como fecha de calendario, sin offset horario', () => {
    const params = new URLSearchParams(
      construirQueryDeCompras({ ...filtrosDeComprasVacios(), fechaComprobanteDesde: '2026-07-01', fechaComprobanteHasta: '2026-07-31' }),
    )
    expect(params.get('fechaComprobanteDesde')).toBe('2026-07-01')
    expect(params.get('fechaComprobanteHasta')).toBe('2026-07-31')
    expect(params.has('desde')).toBe(false)
  })
})

describe('etiquetas del estado de pago de la lista', () => {
  it('Pagada se muestra Pagado en verde y Pendiente en ámbar, con textos distintos', () => {
    expect(etiquetaDeEstadoPagoDeLista('Pagada')).toBe('Pagado')
    expect(etiquetaDeEstadoPagoDeLista('Pendiente')).toBe('Pendiente')
    expect(claseDeBadgeDeEstadoPagoDeLista('Pagada')).toBe('text-bg-success')
    expect(claseDeBadgeDeEstadoPagoDeLista('Pendiente')).toBe('text-bg-warning')
  })

  it('una compra sin estado de pago no tiene etiqueta', () => {
    expect(etiquetaDeEstadoPagoDeLista(null)).toBeNull()
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

describe('construirQueryDeDuplicados', () => {
  it('lleva proveedor, tipo y número, y excluirId solo al editar una compra existente', () => {
    const base = { idProveedor: 4, idTipoComprobante: 5, numeroExterno: '00003-00012345' }

    const alCrear = new URLSearchParams(construirQueryDeDuplicados({ ...base, excluirId: null }))
    expect(alCrear.get('idProveedor')).toBe('4')
    expect(alCrear.get('idTipoComprobante')).toBe('5')
    expect(alCrear.get('numeroExterno')).toBe('00003-00012345')
    expect(alCrear.has('excluirId')).toBe(false)

    const alEditar = new URLSearchParams(construirQueryDeDuplicados({ ...base, excluirId: 9 }))
    expect(alEditar.get('excluirId')).toBe('9')
  })

  it('codifica el número para que un texto libre con espacios o barras llegue intacto', () => {
    const query = construirQueryDeDuplicados({ idProveedor: 1, idTipoComprobante: 2, numeroExterno: 'Rem 3/12&5', excluirId: null })

    expect(query.startsWith('?')).toBe(true)
    expect(new URLSearchParams(query).get('numeroExterno')).toBe('Rem 3/12&5')
  })
})
