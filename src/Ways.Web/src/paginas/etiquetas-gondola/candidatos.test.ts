import { describe, expect, it } from 'vitest'
import type { DatosDeEtiquetas, FilaDeEtiqueta } from '../../api/tipos'
import {
  agregarPagina,
  alternarArticulo,
  candidatosDeBusqueda,
  candidatosDeOfertas,
  filasParaImprimir,
  paginaCompleta,
  quitarPagina,
  SELECCION_VACIA,
  type Candidato,
} from './candidatos'

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

function datos(filas: FilaDeEtiqueta[], excluidos: DatosDeEtiquetas['excluidos'] = []): DatosDeEtiquetas {
  return { idListaPrecio: 3, nombreDeLista: 'General', momento: '2026-11-27T10:00:00Z', filas, excluidos, truncado: false }
}

function candidato(id: number, sobrescribir: Partial<Candidato> = {}): Candidato {
  return { idArticulo: id, codigoInterno: `A${id}`, nombre: `Artículo ${id}`, precio: 100, conOferta: false, motivoDeExclusion: null, ...sobrescribir }
}

const oferta = { idOferta: 1, nombre: '10%', descuentoUnitario: 10 }

describe('candidatosDeBusqueda', () => {
  it('toma la identidad del listado y el precio de la lista elegida', () => {
    const resultado = candidatosDeBusqueda([{ id: 1, codigoInterno: 'A1', nombre: 'Uno' }], datos([fila(1)]))
    expect(resultado).toEqual([{ idArticulo: 1, codigoInterno: 'A1', nombre: 'Uno', precio: 100, conOferta: false, motivoDeExclusion: null }])
  })

  it('un artículo sin precio en la lista queda excluido con el motivo del servidor', () => {
    const resultado = candidatosDeBusqueda(
      [{ id: 2, codigoInterno: 'A2', nombre: 'Dos' }],
      datos([], [{ idArticulo: 2, codigoInterno: 'A2', nombre: 'Dos', motivo: 'sin_precio' }]),
    )
    expect(resultado[0]).toMatchObject({ precio: null, motivoDeExclusion: 'sin_precio' })
  })

  it('sin datos queda excluido con un motivo por defecto', () => {
    expect(candidatosDeBusqueda([{ id: 3, codigoInterno: 'A3', nombre: 'Tres' }], null)[0].motivoDeExclusion).toBe('Sin precio en esta lista')
  })

  it('marca la oferta o la promoción', () => {
    const promocion = { idOferta: 2, nombre: '3x', cantidad: 3, soloMultiplos: false, precioUnitarioFinal: 90 }
    const resultado = candidatosDeBusqueda(
      [{ id: 1, codigoInterno: 'A1', nombre: 'Uno' }, { id: 2, codigoInterno: 'A2', nombre: 'Dos' }],
      datos([fila(1, { ofertas: [oferta] }), fila(2, { promociones: [promocion] })]),
    )
    expect(resultado.map((c) => c.conOferta)).toEqual([true, true])
  })
})

describe('candidatosDeOfertas', () => {
  it('descarta las filas sin oferta ni promoción', () => {
    const resultado = candidatosDeOfertas(datos([fila(1), fila(2, { ofertas: [oferta] })]))
    expect(resultado.map((c) => c.idArticulo)).toEqual([2])
    expect(resultado[0].conOferta).toBe(true)
  })
})

describe('selección', () => {
  it('alternar agrega y quita', () => {
    const agregado = alternarArticulo(SELECCION_VACIA, candidato(1), 200)
    expect([...agregado.seleccion.keys()]).toEqual([1])
    expect([...alternarArticulo(agregado.seleccion, candidato(1), 200).seleccion.keys()]).toEqual([])
  })

  it('no muta la selección anterior', () => {
    alternarArticulo(SELECCION_VACIA, candidato(1), 200)
    expect(SELECCION_VACIA.size).toBe(0)
  })

  it('un excluido no se puede elegir', () => {
    const resultado = alternarArticulo(SELECCION_VACIA, candidato(1, { precio: null, motivoDeExclusion: 'sin precio' }), 200)
    expect(resultado.seleccion.size).toBe(0)
    expect(resultado.excedido).toBe(false)
  })

  it('en el tope avisa y no agrega', () => {
    const llena = agregarPagina(SELECCION_VACIA, [candidato(1), candidato(2)], 2).seleccion
    const resultado = alternarArticulo(llena, candidato(3), 2)
    expect(resultado.excedido).toBe(true)
    expect(resultado.seleccion.size).toBe(2)
  })

  it('quitar uno del tope siempre se puede', () => {
    const llena = agregarPagina(SELECCION_VACIA, [candidato(1), candidato(2)], 2).seleccion
    expect(alternarArticulo(llena, candidato(2), 2).seleccion.size).toBe(1)
  })

  it('agregar la página llena hasta el tope y avisa del resto', () => {
    const resultado = agregarPagina(SELECCION_VACIA, [candidato(1), candidato(2), candidato(3)], 2)
    expect([...resultado.seleccion.keys()]).toEqual([1, 2])
    expect(resultado.excedido).toBe(true)
  })

  it('agregar la página salta los excluidos', () => {
    const resultado = agregarPagina(SELECCION_VACIA, [candidato(1, { motivoDeExclusion: 'x', precio: null }), candidato(2)], 200)
    expect([...resultado.seleccion.keys()]).toEqual([2])
  })

  it('la selección se conserva al cambiar de página', () => {
    const paginaUno = [candidato(1), candidato(2)]
    const paginaDos = [candidato(3)]
    const seleccion = agregarPagina(agregarPagina(SELECCION_VACIA, paginaUno, 200).seleccion, paginaDos, 200).seleccion
    expect([...seleccion.keys()]).toEqual([1, 2, 3])
    expect([...quitarPagina(seleccion, paginaUno).keys()]).toEqual([3])
  })

  it('paginaCompleta exige algo seleccionable y todo elegido', () => {
    const pagina = [candidato(1), candidato(2, { motivoDeExclusion: 'x', precio: null })]
    expect(paginaCompleta(SELECCION_VACIA, pagina)).toBe(false)
    expect(paginaCompleta(agregarPagina(SELECCION_VACIA, pagina, 200).seleccion, pagina)).toBe(true)
    expect(paginaCompleta(SELECCION_VACIA, [])).toBe(false)
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
