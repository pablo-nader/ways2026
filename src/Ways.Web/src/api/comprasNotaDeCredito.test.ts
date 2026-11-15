import { describe, expect, it } from 'vitest'
import {
  aSolicitudDeCompra,
  esFacturaAsociable,
  esNotaDeCredito,
  lineaDeCompraVacia,
  type EncabezadoDeCompraFormulario,
} from './compras'
import type { TipoComprobanteListado } from './tipos'

function tipo(sobrescribir: Partial<TipoComprobanteListado>): TipoComprobanteListado {
  return { signo: 1, registraLibroIva: true, ...sobrescribir } as TipoComprobanteListado
}

function encabezado(sobrescribir: Partial<EncabezadoDeCompraFormulario> = {}): EncabezadoDeCompraFormulario {
  return {
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
    ...sobrescribir,
  }
}

const lineaDeArticulo = {
  ...lineaDeCompraVacia(1),
  idArticulo: 7,
  descripcion: 'Yerba',
  cantidad: '2',
  importe: 100,
  idAlicuotaIva: 1,
  actualizaCosto: true,
}

describe('esNotaDeCredito / esFacturaAsociable', () => {
  it('una nota de crédito es un tipo de signo negativo', () => {
    expect(esNotaDeCredito(tipo({ signo: -1 }))).toBe(true)
    expect(esNotaDeCredito(tipo({ signo: 1 }))).toBe(false)
    expect(esNotaDeCredito(null)).toBe(false)
  })

  it('solo una factura (signo positivo y libro IVA) es asociable', () => {
    expect(esFacturaAsociable(tipo({ signo: 1, registraLibroIva: true }))).toBe(true)
    expect(esFacturaAsociable(tipo({ signo: 1, registraLibroIva: false }))).toBe(false)
    expect(esFacturaAsociable(tipo({ signo: -1, registraLibroIva: true }))).toBe(false)
    expect(esFacturaAsociable(null)).toBe(false)
  })
})

describe('aSolicitudDeCompra con nota de crédito', () => {
  it('manda la factura asociada, sin orden de compra y sin actualizar costos', () => {
    const s = aSolicitudDeCompra(
      encabezado({ idOrdenCompra: 9, idComprobanteCompraAsociado: 44 }),
      [lineaDeArticulo],
      true,
      true,
      true,
    )
    expect(s.idComprobanteCompraAsociado).toBe(44)
    expect(s.idOrdenCompra).toBeNull()
    expect(s.items.every((i) => i.actualizaCosto === false)).toBe(true)
  })

  it('una nota sin factura asociada la manda en null', () => {
    const s = aSolicitudDeCompra(encabezado(), [lineaDeArticulo], true, true, true)
    expect(s.idComprobanteCompraAsociado).toBeNull()
  })

  it('un comprobante que no es nota no manda la asociación aunque el estado la tenga', () => {
    const s = aSolicitudDeCompra(encabezado({ idComprobanteCompraAsociado: 44 }), [lineaDeArticulo], true, true, false)
    expect('idComprobanteCompraAsociado' in s).toBe(false)
    expect(s.items[0].actualizaCosto).toBe(true)
  })
})
