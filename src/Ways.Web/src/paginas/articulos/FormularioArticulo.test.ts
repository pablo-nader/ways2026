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

describe('FormularioArticulo — familia (doc 10 §3)', () => {
  it('un formulario vacío (artículo nuevo) no tiene familia', () => {
    expect(formularioVacio().idFamilia).toBe('')
  })

  it('aFormulario copia la familia del artículo, y un artículo sin familia queda con ""', () => {
    expect(aFormulario(articuloFixture({ idFamilia: 7 })).idFamilia).toBe(7)
    expect(aFormulario(articuloFixture({ idFamilia: null })).idFamilia).toBe('')
  })

  it('aAlta manda idFamilia: la elegida, o null si no se eligió ninguna', () => {
    expect(aAlta({ ...formularioVacio(), idFamilia: 7 }).idFamilia).toBe(7)
    expect(aAlta({ ...formularioVacio(), idFamilia: '' }).idFamilia).toBeNull()
  })

  it('aEdicion nunca manda idFamilia: la pertenencia no se cambia editando', () => {
    const formulario = { ...formularioVacio(), id: 1, idFamilia: 7 as number | '' }

    expect('idFamilia' in aEdicion(formulario)).toBe(false)
    expect('idFamilia' in aEdicion(formulario, 'Familia')).toBe(false)
  })

  it('aEdicion sin alcance no incluye la clave alcance (el servidor la lee como "sin elección")', () => {
    expect('alcance' in aEdicion({ ...formularioVacio(), id: 1 })).toBe(false)
  })

  it.each(['Familia', 'SoloEste'] as const)('aEdicion con alcance %s lo manda por nombre', (alcance) => {
    expect(aEdicion({ ...formularioVacio(), id: 1 }, alcance).alcance).toBe(alcance)
  })

  it('con alcance sigue mandando el resto de la edición intacto', () => {
    const formulario = { ...formularioVacio(), id: 1, nombre: ' Vainilla ', costoLista: '100' }

    expect(aEdicion(formulario, 'Familia')).toEqual({ ...aEdicion(formulario), alcance: 'Familia' })
  })

  it('ida y vuelta: editar un miembro conserva sus trece campos compartidos y no manda su familia', () => {
    const edicion = aEdicion(aFormulario(articuloFixture({ idFamilia: 7, costoLista: 250, idGrupo: 4 })))

    expect(edicion).toMatchObject({ costoLista: 250, idGrupo: 4, idArea: 1, idAlicuotaIva: 1, esProducto: true })
    expect('idFamilia' in edicion).toBe(false)
  })
})
