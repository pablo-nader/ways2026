import { describe, expect, it } from 'vitest'
import type { ArticuloEscaneado, UnidadVenta } from '../api/tipos'
import {
  componentesAElegir,
  formatearCantidadDeCombo,
  motivoDeComboNoCargable,
  parsearCodigoDeCombo,
  resolverLineasDeCombo,
  rotuloDeCodigoDeCombo,
  totalElegido,
  type ComboParaCargar,
  type ComponenteParaCargar,
} from './cargaDeCombos'

function articulo(idArticulo: number, nombre: string, unidadVenta: UnidadVenta = 'Unidad'): ArticuloEscaneado {
  return { idArticulo, codigoInterno: `A${idArticulo}`, nombre, codigoBarra: null, cantidad: 1, acumulaEnVenta: true, unidadVenta }
}

const PANCHO = articulo(1, 'Pancho')
const COCA = articulo(2, 'Coca')
const SPRITE = articulo(3, 'Sprite')
const JAMON = articulo(5, 'Jamón', 'Peso')
const QUESO = articulo(6, 'Queso', 'Peso')

function deArticulo(a: ArticuloEscaneado, cantidad: number): ComponenteParaCargar {
  return { idArticulo: a.idArticulo, idFamilia: null, etiqueta: a.nombre, cantidad, candidatos: [a] }
}

function deFamilia(idFamilia: number, etiqueta: string, cantidad: number, candidatos: ArticuloEscaneado[]): ComponenteParaCargar {
  return { idArticulo: null, idFamilia, etiqueta, cantidad, candidatos }
}

function combo(componentes: ComponenteParaCargar[]): ComboParaCargar {
  return { idCombo: 7, codigo: 12, nombre: 'Pancho + gaseosa', precio: 5000, componentes }
}

describe('parsearCodigoDeCombo', () => {
  it.each([
    ['C12', 12],
    ['c7', 7],
    ['C1', 1],
    ['C999999', 999999],
    ['C100000', 100000],
  ])('%s es el combo %i', (entrada, esperado) => {
    expect(parsearCodigoDeCombo(entrada)).toBe(esperado)
  })

  it.each(['C012', 'C001', 'c000001', 'C000012', 'C0', 'C000000', 'C1000000', 'C12X', 'C', 'C-1', 'C1.5', 'CC12', '12', 'X12', '3*C12', 'C 12', ' C12', 'C12 ', 'C１２', 'C12\n', ''])(
    '%j no es un código de combo',
    (entrada) => {
      expect(parsearCodigoDeCombo(entrada)).toBeNull()
    },
  )

  it('el rótulo es C y el número', () => {
    expect(rotuloDeCodigoDeCombo(12)).toBe('C12')
  })
})

describe('resolverLineasDeCombo', () => {
  it('un componente de artículo agrega ese artículo con la cantidad del componente, en el orden del combo', () => {
    const resultado = resolverLineasDeCombo(combo([deArticulo(COCA, 1), deArticulo(PANCHO, 2)]))

    expect(resultado).toEqual({ ok: true, lineas: [{ ...COCA, cantidad: 1 }, { ...PANCHO, cantidad: 2 }] })
  })

  it('una familia con un solo candidato se agrega directo con toda su cantidad', () => {
    const resultado = resolverLineasDeCombo(combo([deArticulo(PANCHO, 1), deFamilia(9, 'Gaseosas', 2, [COCA])]))

    expect(resultado).toEqual({ ok: true, lineas: [{ ...PANCHO, cantidad: 1 }, { ...COCA, cantidad: 2 }] })
  })

  it('una familia con varios candidatos reparte la cantidad según la elección (1 + 1)', () => {
    const c = combo([deArticulo(PANCHO, 1), deFamilia(9, 'Gaseosas', 2, [COCA, SPRITE])])

    const resultado = resolverLineasDeCombo(c, { 1: { [COCA.idArticulo]: 1, [SPRITE.idArticulo]: 1 } })

    expect(resultado).toEqual({
      ok: true,
      lineas: [
        { ...PANCHO, cantidad: 1 },
        { ...COCA, cantidad: 1 },
        { ...SPRITE, cantidad: 1 },
      ],
    })
  })

  it('toda la cantidad en un solo miembro es válida y el que quedó en cero no genera línea', () => {
    const c = combo([deFamilia(9, 'Gaseosas', 2, [COCA, SPRITE]), deArticulo(PANCHO, 1)])

    const resultado = resolverLineasDeCombo(c, { 0: { [COCA.idArticulo]: 2, [SPRITE.idArticulo]: 0 } })

    expect(resultado).toEqual({ ok: true, lineas: [{ ...COCA, cantidad: 2 }, { ...PANCHO, cantidad: 1 }] })
  })

  it('las líneas siguen el orden de los candidatos, no el de la elección', () => {
    const c = combo([deFamilia(9, 'Gaseosas', 2, [COCA, SPRITE]), deArticulo(PANCHO, 1)])

    const resultado = resolverLineasDeCombo(c, { 0: { [SPRITE.idArticulo]: 1, [COCA.idArticulo]: 1 } })

    expect(resultado.ok && resultado.lineas.map((l) => l.idArticulo)).toEqual([2, 3, 1])
  })

  it('el total elegido tiene que ser exactamente la cantidad del componente: de menos y de más se rechaza', () => {
    const c = combo([deFamilia(9, 'Gaseosas', 2, [COCA, SPRITE]), deArticulo(PANCHO, 1)])

    for (const eleccion of [{ [COCA.idArticulo]: 1 }, { [COCA.idArticulo]: 2, [SPRITE.idArticulo]: 1 }, {}]) {
      expect(resolverLineasDeCombo(c, { 0: eleccion })).toEqual({ ok: false, motivo: 'Elegí exactamente 2 para «Gaseosas».' })
    }
  })

  it('sin elección para un componente con varios candidatos se rechaza', () => {
    const c = combo([deFamilia(9, 'Gaseosas', 2, [COCA, SPRITE]), deArticulo(PANCHO, 1)])

    expect(resolverLineasDeCombo(c)).toEqual({ ok: false, motivo: 'Falta elegir los artículos de «Gaseosas».' })
  })

  it('una elección que nombra un artículo que no es candidato, o con cantidad negativa o fraccionaria en un miembro por unidad, se rechaza', () => {
    const c = combo([deFamilia(9, 'Gaseosas', 2, [COCA, SPRITE]), deArticulo(PANCHO, 1)])

    for (const eleccion of [
      { [COCA.idArticulo]: 1, [PANCHO.idArticulo]: 1 },
      { [COCA.idArticulo]: 3, [SPRITE.idArticulo]: -1 },
      { [COCA.idArticulo]: 1.5, [SPRITE.idArticulo]: 0.5 },
    ]) {
      expect(resolverLineasDeCombo(c, { 0: eleccion })).toEqual({ ok: false, motivo: 'La elección de «Gaseosas» no es válida.' })
    }
  })

  it('todo o nada: si un componente no se puede cargar no devuelve ninguna línea, ni las de los que sí', () => {
    const c = combo([deArticulo(PANCHO, 1), deFamilia(9, 'Gaseosas', 2, [])])

    expect(resolverLineasDeCombo(c)).toEqual({ ok: false, motivo: 'No hay artículos disponibles para «Gaseosas».' })
  })

  it.each([0, -1, 0.2005, Number.NaN])('una cantidad de componente no positiva o con más de tres decimales no se carga (%s)', (cantidad) => {
    expect(resolverLineasDeCombo(combo([deArticulo(JAMON, cantidad), deArticulo(COCA, 1)]))).toEqual({
      ok: false,
      motivo: 'La cantidad de «Jamón» no es válida.',
    })
  })

  // Cláusula: con un solo candidato, la cantidad tiene que ser la que admite la unidad de venta de ese artículo.
  it('una cantidad fraccionaria para un único candidato que se vende por unidad no se carga, ni las líneas de los demás', () => {
    expect(resolverLineasDeCombo(combo([deArticulo(PANCHO, 1), deArticulo(COCA, 1.5)]))).toEqual({
      ok: false,
      motivo: 'La cantidad de «Coca» no es válida.',
    })
    expect(motivoDeComboNoCargable(combo([deFamilia(9, 'Gaseosas', 0.5, [COCA]), deArticulo(PANCHO, 1)]))).toBe('La cantidad de «Gaseosas» no es válida.')
  })

  it('un único candidato sin unidad de venta se trata como pesable y se carga con una cantidad fraccionaria', () => {
    const sinUnidad: ArticuloEscaneado = { ...articulo(9, 'Suelto'), unidadVenta: undefined }

    expect(resolverLineasDeCombo(combo([deArticulo(sinUnidad, 0.25), deArticulo(PANCHO, 1)]))).toEqual({
      ok: true,
      lineas: [{ ...sinUnidad, cantidad: 0.25 }, { ...PANCHO, cantidad: 1 }],
    })
  })

  it('con varios candidatos la cantidad del componente no se juzga por la unidad del primero: la reparte la elección', () => {
    const c = combo([deFamilia(30, 'Picada', 0.5, [COCA, JAMON]), deArticulo(PANCHO, 1)])

    expect(motivoDeComboNoCargable(c)).toBeNull()
    expect(resolverLineasDeCombo(c, { 0: { [JAMON.idArticulo]: 0.5 } })).toEqual({ ok: true, lineas: [{ ...JAMON, cantidad: 0.5 }, { ...PANCHO, cantidad: 1 }] })
  })

  it('un componente pesable se carga con su cantidad en kilos: una línea por componente', () => {
    const resultado = resolverLineasDeCombo(combo([deArticulo(JAMON, 0.2), deArticulo(QUESO, 0.2)]))

    expect(resultado).toEqual({ ok: true, lineas: [{ ...JAMON, cantidad: 0.2 }, { ...QUESO, cantidad: 0.2 }] })
  })

  it('una familia pesable reparte la cantidad en kilos y el total se compara exacto (0,1 + 0,2 es 0,3)', () => {
    const c = combo([deFamilia(30, 'Fiambres', 0.3, [JAMON, QUESO]), deArticulo(COCA, 1)])

    const resultado = resolverLineasDeCombo(c, { 0: { [JAMON.idArticulo]: 0.1, [QUESO.idArticulo]: 0.2 } })

    expect(resultado).toEqual({ ok: true, lineas: [{ ...JAMON, cantidad: 0.1 }, { ...QUESO, cantidad: 0.2 }, { ...COCA, cantidad: 1 }] })
  })

  it('el total de una familia pesable que no cierra se rechaza, con la cantidad con coma', () => {
    const c = combo([deFamilia(30, 'Fiambres', 0.3, [JAMON, QUESO]), deArticulo(COCA, 1)])

    expect(resolverLineasDeCombo(c, { 0: { [JAMON.idArticulo]: 0.1, [QUESO.idArticulo]: 0.199 } })).toEqual({
      ok: false,
      motivo: 'Elegí exactamente 0,3 para «Fiambres».',
    })
  })

  it('en un miembro pesable una cantidad con más de tres decimales se rechaza', () => {
    const c = combo([deFamilia(30, 'Fiambres', 0.3, [JAMON, QUESO]), deArticulo(COCA, 1)])

    expect(resolverLineasDeCombo(c, { 0: { [JAMON.idArticulo]: 0.1005, [QUESO.idArticulo]: 0.1995 } })).toEqual({
      ok: false,
      motivo: 'La elección de «Fiambres» no es válida.',
    })
  })

  it('en una familia mixta cada miembro sigue su unidad de venta: el pesable admite fracción y el de unidad no', () => {
    const c = combo([deFamilia(30, 'Picada', 2, [JAMON, COCA]), deArticulo(PANCHO, 1)])

    expect(resolverLineasDeCombo(c, { 0: { [JAMON.idArticulo]: 1, [COCA.idArticulo]: 1 } }).ok).toBe(true)
    expect(resolverLineasDeCombo(c, { 0: { [JAMON.idArticulo]: 1.5, [COCA.idArticulo]: 0.5 } })).toEqual({
      ok: false,
      motivo: 'La elección de «Picada» no es válida.',
    })
  })

  it('un componente con un solo candidato ignora una elección que venga de más', () => {
    const c = combo([deArticulo(PANCHO, 1), deArticulo(COCA, 1)])

    expect(resolverLineasDeCombo(c, { 0: { [SPRITE.idArticulo]: 5 } })).toEqual({
      ok: true,
      lineas: [{ ...PANCHO, cantidad: 1 }, { ...COCA, cantidad: 1 }],
    })
  })

  it('no modifica los candidatos de entrada', () => {
    const c = combo([deArticulo(PANCHO, 3), deArticulo(COCA, 1)])

    resolverLineasDeCombo(c)

    expect(PANCHO.cantidad).toBe(1)
    expect(c.componentes[0].candidatos[0].cantidad).toBe(1)
  })
})

describe('motivoDeComboNoCargable y componentesAElegir', () => {
  it('un combo completo se puede cargar y señala qué componentes piden elegir', () => {
    const c = combo([deArticulo(PANCHO, 1), deFamilia(9, 'Gaseosas', 2, [COCA, SPRITE]), deFamilia(10, 'Salsas', 1, [articulo(4, 'Mostaza')])])

    expect(motivoDeComboNoCargable(c)).toBeNull()
    expect(componentesAElegir(c)).toEqual([1])
  })

  it('el motivo es el del primer componente sin artículos', () => {
    const c = combo([deArticulo(PANCHO, 1), deFamilia(9, 'Gaseosas', 2, []), deFamilia(10, 'Salsas', 1, [])])

    expect(motivoDeComboNoCargable(c)).toBe('No hay artículos disponibles para «Gaseosas».')
  })

  it('un combo sin componentes no se puede cargar', () => {
    expect(motivoDeComboNoCargable(combo([]))).toBe('El combo no tiene componentes.')
  })

  it('totalElegido suma lo repartido en el componente y es cero sin elección', () => {
    expect(totalElegido({ 1: { 2: 1, 3: 2 } }, 1)).toBe(3)
    expect(totalElegido({}, 1)).toBe(0)
  })

  it('totalElegido es exacto a tres decimales: 0,1 + 0,2 es 0,3 y no 0,30000000000000004', () => {
    expect(totalElegido({ 0: { 5: 0.1, 6: 0.2 } }, 0)).toBe(0.3)
  })

  it('la cantidad se muestra con coma decimal y sin ceros de más', () => {
    expect([2, 0.2, 1.125].map(formatearCantidadDeCombo)).toEqual(['2', '0,2', '1,125'])
  })
})

describe('el mismo artículo como componente y como miembro de una familia', () => {
  it('entrega una línea por componente, en orden, sin fusionarlas: acumular es cosa del carrito', () => {
    const c = combo([deArticulo(PANCHO, 1), deFamilia(9, 'Panchos y gaseosas', 2, [PANCHO, COCA])])

    const resultado = resolverLineasDeCombo(c, { 1: { [PANCHO.idArticulo]: 1, [COCA.idArticulo]: 1 } })

    expect(resultado).toEqual({
      ok: true,
      lineas: [
        { ...PANCHO, cantidad: 1 },
        { ...PANCHO, cantidad: 1 },
        { ...COCA, cantidad: 1 },
      ],
    })
  })
})
