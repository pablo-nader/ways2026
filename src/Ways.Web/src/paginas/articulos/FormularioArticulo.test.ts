import { describe, expect, it } from 'vitest'
import { aAlta, aEdicion, aFormulario, formularioVacio } from './FormularioArticulo'
import type { ArticuloListado } from '../../api/tipos'

function articuloFixture(sobrescribir: Partial<ArticuloListado> = {}): ArticuloListado {
  return {
    id: 1,
    codigoInterno: 'A0001',
    nombre: 'Articulo Uno',
    descripcion: null,
    idArea: 1,
    idCategoria: null,
    idMarca: null,
    idGrupo: null,
    idProveedorHabitual: null,
    idAlicuotaIva: 1,
    unidadVenta: 'Unidad',
    unidadesPorBulto: null,
    esProducto: true,
    costoLista: null,
    descuentoProveedor: null,
    costoNominal: null,
    disponibleParaTodas: true,
    idsEmpresas: [],
    activo: true,
    controlaLote: false,
    acumulaEnVenta: true,
    idFamilia: null,
    ...sobrescribir,
  }
}

describe('FormularioArticulo — acumulaEnVenta', () => {
  it('un formulario vacío (artículo nuevo) arranca acumulando', () => {
    expect(formularioVacio().acumulaEnVenta).toBe(true)
  })

  it.each([true, false])('aFormulario copia acumulaEnVenta = %s del artículo', (valor) => {
    expect(aFormulario(articuloFixture({ acumulaEnVenta: valor })).acumulaEnVenta).toBe(valor)
  })

  it.each([true, false])('aAlta y aEdicion mandan acumulaEnVenta = %s tal cual', (valor) => {
    const formulario = { ...formularioVacio(), acumulaEnVenta: valor }

    expect(aAlta(formulario).acumulaEnVenta).toBe(valor)
    expect(aEdicion(formulario).acumulaEnVenta).toBe(valor)
  })

  it('ida y vuelta: un artículo que no acumula se edita sin perder el flag', () => {
    expect(aEdicion(aFormulario(articuloFixture({ acumulaEnVenta: false }))).acumulaEnVenta).toBe(false)
  })
})
