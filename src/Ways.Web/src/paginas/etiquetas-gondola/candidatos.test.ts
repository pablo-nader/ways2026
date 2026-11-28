import { describe, expect, it } from 'vitest'
import type { FilaDeEtiqueta } from '../../api/tipos'
import { agregarPagina, alternarArticulo, filasParaImprimir, quitarPagina, SELECCION_VACIA } from './candidatos'

function fila(id: number, sobrescribir: Partial<FilaDeEtiqueta> = {}): FilaDeEtiqueta {
  return {
    idArticulo: id,
    codigoInterno: `A${id}`,
    codigoBarra: null,
    nombre: `Artículo ${id}`,
    unidadVenta: 'Unidad',
    precioOriginal: 100 * id,
    precioFinal: 100 * id,
    ofertas: [],
    promociones: [],
    ...sobrescribir,
  }
}

const oferta = { idOferta: 1, nombre: '10%', descuentoUnitario: 10 }

describe('selección', () => {
  it('alternar agrega y quita', () => {
    const agregado = alternarArticulo(SELECCION_VACIA, 1, 200)
    expect([...agregado.seleccion]).toEqual([1])
    expect([...alternarArticulo(agregado.seleccion, 1, 200).seleccion]).toEqual([])
  })

  it('no muta la selección anterior', () => {
    alternarArticulo(SELECCION_VACIA, 1, 200)
    expect(SELECCION_VACIA.size).toBe(0)
  })

  it('en el tope avisa y no agrega', () => {
    const llena = agregarPagina(SELECCION_VACIA, [1, 2], 2).seleccion
    const resultado = alternarArticulo(llena, 3, 2)
    expect(resultado.excedido).toBe(true)
    expect(resultado.seleccion.size).toBe(2)
  })

  it('quitar uno del tope siempre se puede', () => {
    const llena = agregarPagina(SELECCION_VACIA, [1, 2], 2).seleccion
    expect(alternarArticulo(llena, 2, 2).seleccion.size).toBe(1)
  })

  it('agregar la página llena hasta el tope y avisa del resto', () => {
    const resultado = agregarPagina(SELECCION_VACIA, [1, 2, 3], 2)
    expect([...resultado.seleccion]).toEqual([1, 2])
    expect(resultado.excedido).toBe(true)
  })

  it('un artículo ya elegido no cuenta dos veces al agregar la página', () => {
    const resultado = agregarPagina(new Set([1]), [1, 2], 2)
    expect([...resultado.seleccion]).toEqual([1, 2])
    expect(resultado.excedido).toBe(false)
  })

  it('la selección se conserva al cambiar de página', () => {
    const seleccion = agregarPagina(agregarPagina(SELECCION_VACIA, [1, 2], 200).seleccion, [3], 200).seleccion
    expect([...seleccion]).toEqual([1, 2, 3])
    expect([...quitarPagina(seleccion, [1, 2])]).toEqual([3])
  })
})

describe('filasParaImprimir', () => {
  const filasDelServidor = [fila(3), fila(1, { ofertas: [oferta] }), fila(2)]

  it('respeta el orden de la selección, no el de la respuesta', () => {
    expect(filasParaImprimir([1, 2, 3], filasDelServidor, false).filas.map((f) => f.idArticulo)).toEqual([1, 2, 3])
  })

  it('un elegido sin fila (sin precio) se cuenta como omitido', () => {
    const resultado = filasParaImprimir([1, 9], filasDelServidor, false)
    expect(resultado.filas).toHaveLength(1)
    expect(resultado.omitidos).toBe(1)
  })

  it('en modo Ofertas salen solo los que tienen oferta', () => {
    const resultado = filasParaImprimir([1, 2, 3], filasDelServidor, true)
    expect(resultado.filas.map((f) => f.idArticulo)).toEqual([1])
    expect(resultado.omitidos).toBe(2)
  })
})
