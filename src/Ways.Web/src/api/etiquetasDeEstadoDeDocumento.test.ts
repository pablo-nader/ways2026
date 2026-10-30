import { describe, expect, it } from 'vitest'
import {
  COLUMNAS_ORDENABLES_DE_COMPRAS,
  construirQueryDeCompras,
  etiquetaDeEstadoCompra,
  filtrosDeComprasVacios,
} from './compras'
import {
  COLUMNAS_ORDENABLES_DE_ORDENES_DE_COMPRA,
  construirQueryDeOrdenesDeCompra,
  etiquetaDeEstadoOrdenCompra,
  filtrosDeOrdenesDeCompraVacios,
} from './ordenesDeCompra'
import {
  COLUMNAS_ORDENABLES_DE_PRESUPUESTOS,
  construirQueryDePresupuestos,
  etiquetaDeEstadoPresupuesto,
  filtrosDePresupuestosVacios,
} from './presupuestos'
import {
  COLUMNAS_ORDENABLES_DE_REMITOS,
  construirQueryDeRemitos,
  etiquetaDeEstadoRemito,
  filtrosDeRemitosVacios,
} from './remitos'
import type { EstadoCompra, EstadoOrdenCompra, EstadoPresupuesto, EstadoRemito } from './tipos'

// El servidor ordena por estas mismas etiquetas (lo que se ve es lo que se ordena). Cada lista literal es idéntica a la de su
// test espejo del backend, `EtiquetasDeEstadoDeDocumentoTests` (tests/Ways.Application.Tests/Listados). Si una cambia, las dos
// tienen que cambiar.
describe('etiquetas de estado que el servidor replica para ordenar', () => {
  it('compra es la lista que fija LasEtiquetasDeCompraSonExactamenteLaListaFijada', () => {
    const estados: EstadoCompra[] = ['Borrador', 'Confirmada', 'Anulada']
    expect(estados.map((e) => [e, etiquetaDeEstadoCompra(e)])).toEqual([
      ['Borrador', 'Borrador'],
      ['Confirmada', 'Confirmada'],
      ['Anulada', 'Anulada'],
    ])
  })

  it('presupuesto es la lista que fija LasEtiquetasDePresupuestoSonExactamenteLaListaFijada', () => {
    const estados: EstadoPresupuesto[] = ['Borrador', 'Enviado', 'Convertido', 'Anulado']
    expect(estados.map((e) => [e, etiquetaDeEstadoPresupuesto(e)])).toEqual([
      ['Borrador', 'Borrador'],
      ['Enviado', 'Enviado'],
      ['Convertido', 'Convertido'],
      ['Anulado', 'Anulado'],
    ])
  })

  it('remito es la lista que fija LasEtiquetasDeRemitoSonExactamenteLaListaFijada', () => {
    const estados: EstadoRemito[] = ['Borrador', 'Emitido', 'Facturado', 'Anulado']
    expect(estados.map((e) => [e, etiquetaDeEstadoRemito(e)])).toEqual([
      ['Borrador', 'Borrador'],
      ['Emitido', 'Emitido'],
      ['Facturado', 'Facturado'],
      ['Anulado', 'Anulado'],
    ])
  })

  it('orden de compra es la lista que fija LasEtiquetasDeOrdenDeCompraSonExactamenteLaListaFijada', () => {
    const estados: EstadoOrdenCompra[] = ['Borrador', 'Enviada', 'RecibidaParcial', 'Cerrada', 'Anulada']
    expect(estados.map((e) => [e, etiquetaDeEstadoOrdenCompra(e)])).toEqual([
      ['Borrador', 'Borrador'],
      ['Enviada', 'Enviada'],
      ['RecibidaParcial', 'Recibida parcial'],
      ['Cerrada', 'Cerrada'],
      ['Anulada', 'Anulada'],
    ])
  })
})

// Las claves son las de `ClavesDeOrden` de cada servicio; los tests `LasClavesAceptadasSonExactamenteLasColumnasDeDatosDeLaPantalla`
// del backend fijan la misma lista.
describe('claves de orden de los listados de documentos', () => {
  it('cada listado acepta exactamente las claves que fija su espejo del backend', () => {
    expect([...COLUMNAS_ORDENABLES_DE_COMPRAS]).toEqual(['numero', 'proveedor', 'tipo', 'estado', 'fechaRecepcion', 'total'])
    expect([...COLUMNAS_ORDENABLES_DE_PRESUPUESTOS]).toEqual([
      'numero',
      'cliente',
      'puntoVenta',
      'estado',
      'vencimiento',
      'fechaEmision',
      'total',
    ])
    expect([...COLUMNAS_ORDENABLES_DE_REMITOS]).toEqual(['numero', 'cliente', 'puntoVenta', 'estado', 'fechaEmision', 'total'])
    expect([...COLUMNAS_ORDENABLES_DE_ORDENES_DE_COMPRA]).toEqual([
      'numero',
      'proveedor',
      'puntoVenta',
      'estado',
      'fechaEmision',
      'fechaEsperada',
    ])
  })

  it('sin orden el query no lleva orden ni direccion; con orden los lleva junto con los filtros y la página', () => {
    const orden = { columna: 'estado', direccion: 'descendente' } as const

    for (const query of [
      construirQueryDeCompras(filtrosDeComprasVacios()),
      construirQueryDePresupuestos(filtrosDePresupuestosVacios()),
      construirQueryDeRemitos(filtrosDeRemitosVacios()),
      construirQueryDeOrdenesDeCompra(filtrosDeOrdenesDeCompraVacios()),
    ]) {
      expect(query).not.toContain('orden')
      expect(query).not.toContain('direccion')
    }

    for (const query of [
      construirQueryDeCompras({ ...filtrosDeComprasVacios(), estado: 'Anulada', pagina: 3 }, orden),
      construirQueryDePresupuestos({ ...filtrosDePresupuestosVacios(), estado: 'Anulado', pagina: 3 }, orden),
      construirQueryDeRemitos({ ...filtrosDeRemitosVacios(), estado: 'Anulado', pagina: 3 }, orden),
      construirQueryDeOrdenesDeCompra({ ...filtrosDeOrdenesDeCompraVacios(), estado: 'Anulada', pagina: 3 }, orden),
    ]) {
      const parametros = new URLSearchParams(query.slice(1))
      expect(parametros.get('orden')).toBe('estado')
      expect(parametros.get('direccion')).toBe('descendente')
      expect(parametros.get('estado')).toMatch(/^Anulad[oa]$/)
      expect(parametros.get('pagina')).toBe('3')
    }
  })
})
