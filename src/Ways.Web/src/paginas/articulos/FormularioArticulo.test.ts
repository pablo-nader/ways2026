import { describe, expect, it } from 'vitest'
import { CAMPOS_COMPARTIDOS } from './familia'
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
    costoListaPor: 'Unidad',
    costoListaIncluyeIva: true,
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

describe('FormularioArticulo — descuento por defecto 0', () => {
  it('un formulario vacío (artículo nuevo) arranca con descuento 0', () => {
    expect(formularioVacio().descuentoProveedor).toBe('0')
  })

  it('aFormulario con costos visibles lee un descuento guardado nulo como 0', () => {
    expect(aFormulario(articuloFixture({ costoLista: 100, costoListaPor: 'Unidad', descuentoProveedor: null })).descuentoProveedor).toBe('0')
  })

  it('aFormulario con costos visibles conserva el descuento guardado', () => {
    expect(aFormulario(articuloFixture({ costoListaPor: 'Unidad', descuentoProveedor: 7.5 })).descuentoProveedor).toBe('7.5')
    expect(aFormulario(articuloFixture({ costoListaPor: 'Unidad', descuentoProveedor: 0 })).descuentoProveedor).toBe('0')
  })

  it('aFormulario con costos ocultos deja el descuento vacío', () => {
    const formulario = aFormulario(
      articuloFixture({ costoLista: null, costoListaPor: null, costoListaIncluyeIva: null, descuentoProveedor: null, costoNominal: null }),
    )

    expect(formulario.descuentoProveedor).toBe('')
    expect(formulario.costoGuardado?.descuentoProveedor).toBe('')
  })

  it.each([
    ['vacío', '', 0],
    ['solo espacios', '  ', 0],
    ['cero', '0', 0],
    ['un valor tipeado', '5', 5],
    ['un valor con decimales', '12.5', 12.5],
  ])('aAlta y aEdicion mandan el descuento %s como %s', (_nombre, texto, esperado) => {
    const formulario = { ...formularioVacio(), id: 1, descuentoProveedor: texto }

    expect(aAlta(formulario).descuentoProveedor).toBe(esperado)
    expect(aEdicion(formulario).descuentoProveedor).toBe(esperado)
  })

  it('un alta sin tocar el descuento manda 0, no null', () => {
    expect(aAlta(formularioVacio()).descuentoProveedor).toBe(0)
  })

  it('aEdicion de un artículo con costos ocultos manda null en el descuento, como el resto de los campos de costo que no ve', () => {
    const edicion = aEdicion(
      aFormulario(articuloFixture({ costoLista: null, costoListaPor: null, costoListaIncluyeIva: null, descuentoProveedor: null, costoNominal: null })),
    )

    expect(edicion.descuentoProveedor).toBeNull()
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

  /** Los ids y los números son distintos entre sí, así que una columna cruzada con otra se nota. Los tres booleanos no
   * pueden serlo (solo hay dos valores): en cada asignación uno difiere de los otros dos y, entre las dos asignaciones,
   * cada par de booleanos difiere en alguna. */
  it.each<Pick<ArticuloListado, 'esProducto' | 'controlaLote' | 'acumulaEnVenta'>>([
    { esProducto: false, controlaLote: true, acumulaEnVenta: false },
    { esProducto: true, controlaLote: false, acumulaEnVenta: false },
  ])('ida y vuelta: editar un miembro conserva sus quince campos compartidos y no manda su familia (booleanos %j)', (booleanos) => {
    const compartidos = {
      idArea: 11,
      idCategoria: 12,
      idGrupo: 13,
      idProveedorHabitual: 14,
      idAlicuotaIva: 15,
      unidadVenta: 'Peso',
      unidadesPorBulto: 6,
      ...booleanos,
      costoLista: 250,
      costoListaPor: 'Bulto',
      costoListaIncluyeIva: false,
      descuentoProveedor: 7,
      costoNominal: 200,
    } satisfies Partial<ArticuloListado>
    const { costoNominal, ...enviados } = compartidos

    const formulario = aFormulario(articuloFixture({ idFamilia: 7, ...compartidos }))
    const edicion = aEdicion(formulario)

    expect(Object.keys(compartidos).sort()).toEqual(CAMPOS_COMPARTIDOS.map((campo) => campo.clave).sort())
    expect(edicion).toMatchObject(enviados)
    expect(formulario.costoNominal).toBe(String(costoNominal))
    expect('costoNominal' in edicion).toBe(false)
    expect('idFamilia' in edicion).toBe(false)
  })
})

describe('FormularioArticulo — costos (doc 10 §3)', () => {
  it('un formulario vacío arranca con el costo por unidad y con IVA, sin costo nominal ni entradas guardadas', () => {
    expect(formularioVacio()).toMatchObject({
      costoListaPor: 'Unidad',
      costoListaIncluyeIva: true,
      costoNominal: '',
      costoGuardado: null,
      costosOcultos: false,
    })
  })

  it('aFormulario copia la base, el IVA y el nominal guardado, y deja las entradas con las que se cargó', () => {
    const formulario = aFormulario(
      articuloFixture({
        costoLista: 300,
        costoListaPor: 'Bulto',
        costoListaIncluyeIva: false,
        unidadesPorBulto: 12,
        descuentoProveedor: 5,
        costoNominal: 27.5,
        idAlicuotaIva: 3,
      }),
    )

    expect(formulario).toMatchObject({
      costoLista: '300',
      costoListaPor: 'Bulto',
      costoListaIncluyeIva: false,
      costoNominal: '27.5',
      costosOcultos: false,
      costoGuardado: {
        costoLista: '300',
        costoListaPor: 'Bulto',
        costoListaIncluyeIva: false,
        unidadesPorBulto: '12',
        descuentoProveedor: '5',
        idAlicuotaIva: 3,
      },
    })
  })

  it('un artículo que llega sin costos queda marcado y con los valores por defecto', () => {
    const formulario = aFormulario(
      articuloFixture({ costoLista: null, costoListaPor: null, costoListaIncluyeIva: null, descuentoProveedor: null, costoNominal: null }),
    )

    expect(formulario).toMatchObject({ costosOcultos: true, costoListaPor: 'Unidad', costoListaIncluyeIva: true, costoNominal: '' })
  })

  it('aAlta manda la base y el IVA tal cual y nunca el costo nominal', () => {
    const alta = aAlta({ ...formularioVacio(), costoLista: '300', costoListaPor: 'Bulto', costoListaIncluyeIva: false, costoNominal: '99' })

    expect(alta).toMatchObject({ costoLista: 300, costoListaPor: 'Bulto', costoListaIncluyeIva: false })
    expect('costoNominal' in alta).toBe(false)
  })

  it('aAlta sin costo manda los valores por defecto explícitos: Unidad y con IVA', () => {
    expect(aAlta(formularioVacio())).toMatchObject({ costoLista: null, costoListaPor: 'Unidad', costoListaIncluyeIva: true })
  })

  it('aEdicion manda la base y el IVA explícitos, aun sin tocarlos, y nunca el costo nominal', () => {
    const edicion = aEdicion(
      aFormulario(articuloFixture({ costoLista: 100, costoListaPor: 'Unidad', costoListaIncluyeIva: true, costoNominal: 73.5 })),
    )

    expect(edicion).toMatchObject({ costoLista: 100, costoListaPor: 'Unidad', costoListaIncluyeIva: true })
    expect('costoNominal' in edicion).toBe(false)
  })

  it('aEdicion de un artículo sin costos manda null en la base y el IVA: el servidor conserva lo guardado', () => {
    const edicion = aEdicion(
      aFormulario(articuloFixture({ costoLista: null, costoListaPor: null, costoListaIncluyeIva: null, costoNominal: null })),
    )

    expect(edicion).toMatchObject({ costoLista: null, costoListaPor: null, costoListaIncluyeIva: null })
  })

  it('aEdicion con alcance conserva la base y el IVA', () => {
    const formulario = { ...formularioVacio(), id: 1, costoListaPor: 'Bulto' as const, costoListaIncluyeIva: false }

    expect(aEdicion(formulario, 'Familia')).toMatchObject({ costoListaPor: 'Bulto', costoListaIncluyeIva: false, alcance: 'Familia' })
  })
})

describe('FormularioArticulo — familia nueva en el alta', () => {
  it('un formulario vacío no pide ninguna familia nueva, y aFormulario tampoco', () => {
    expect(formularioVacio().nombreFamiliaNueva).toBe('')
    expect(aFormulario(articuloFixture({ idFamilia: 7 })).nombreFamiliaNueva).toBe('')
  })

  it('aAlta manda nombreFamiliaNueva recortado, con idFamilia: null', () => {
    const alta = aAlta({ ...formularioVacio(), nombreFamiliaNueva: '  Talles  ' })

    expect(alta.nombreFamiliaNueva).toBe('Talles')
    expect(alta.idFamilia).toBeNull()
  })

  it('aAlta con familia nueva conserva la base y el IVA del costo de lista', () => {
    const alta = aAlta({ ...formularioVacio(), costoListaPor: 'Bulto', costoListaIncluyeIva: false, nombreFamiliaNueva: 'Talles' })

    expect(alta).toMatchObject({ costoListaPor: 'Bulto', costoListaIncluyeIva: false, nombreFamiliaNueva: 'Talles', idFamilia: null })
  })

  it('aAlta no manda la clave nombreFamiliaNueva cuando no se pidió familia nueva', () => {
    expect('nombreFamiliaNueva' in aAlta(formularioVacio())).toBe(false)
    expect('nombreFamiliaNueva' in aAlta({ ...formularioVacio(), nombreFamiliaNueva: '   ' })).toBe(false)
  })

  /** Cláusula bajo prueba: la exclusión entre `idFamilia` y `nombreFamiliaNueva` (el servidor responde 400
   * `familia_existente_y_nueva` si llegan los dos).
   * Evidencia de mutación (mutation-proof-tests): quitar la cláusula hace fallar este test; revertido, vuelve a verde. */
  it('aAlta nunca manda la familia nueva junto con una familia elegida', () => {
    const alta = aAlta({ ...formularioVacio(), idFamilia: 7, nombreFamiliaNueva: 'Talles' })

    expect(alta.idFamilia).toBe(7)
    expect('nombreFamiliaNueva' in alta).toBe(false)
  })

  it('aEdicion nunca manda nombreFamiliaNueva', () => {
    expect('nombreFamiliaNueva' in aEdicion({ ...formularioVacio(), id: 1, nombreFamiliaNueva: 'Talles' })).toBe(false)
  })
})
