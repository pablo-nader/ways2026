import { describe, expect, it } from 'vitest'
import type { CategoriaListado } from '../../api/tipos'
import { armarArbol, filtrarArbol, ordenarArbol, type Nodo } from './arbolDeCategorias'

function categoria(id: number, nombre: string, idCategoriaPadre: number | null = null, orden = id): CategoriaListado {
  return { id, nombre, activo: true, idEmpresa: null, orden, idCategoriaPadre }
}

function nombres(nodos: Nodo[]): string[] {
  return nodos.map((nodo) => `${nodo.nombre}[${nombres(nodo.hijos).join(',')}]`)
}

const ARBOL = armarArbol([
  categoria(1, 'Bebidas'),
  categoria(2, 'Gaseosas', 1),
  categoria(3, 'Cola', 2),
  categoria(4, 'Jugos', 1),
  categoria(5, 'Almacén'),
  categoria(6, 'Fideos', 5),
])

describe('filtrarArbol', () => {
  it('sin texto devuelve el árbol completo', () => {
    expect(filtrarArbol(ARBOL, '  ')).toBe(ARBOL)
  })

  it('conserva la coincidencia y sus ancestros, sin los hermanos que no coinciden', () => {
    expect(nombres(filtrarArbol(ARBOL, 'cola'))).toEqual(['Bebidas[Gaseosas[Cola[]]]'])
  })

  it('una coincidencia en una raíz no arrastra a sus hijos', () => {
    expect(nombres(filtrarArbol(ARBOL, 'bebi'))).toEqual(['Bebidas[]'])
  })

  it('ignora mayúsculas y acentos', () => {
    expect(nombres(filtrarArbol(ARBOL, 'ALMACEN'))).toEqual(['Almacén[]'])
  })

  it('conserva varias ramas cuando coinciden en distintos lugares', () => {
    expect(nombres(filtrarArbol(ARBOL, 'o'))).toEqual(['Bebidas[Gaseosas[Cola[]],Jugos[]]', 'Almacén[Fideos[]]'])
  })

  it('el nivel de cada nodo es el del árbol completo', () => {
    const filtrado = filtrarArbol(ARBOL, 'cola')

    expect(filtrado[0].hijos[0].hijos[0].nivel).toBe(3)
  })

  it('sin coincidencias devuelve una lista vacía', () => {
    expect(filtrarArbol(ARBOL, 'zzz')).toEqual([])
  })
})

describe('ordenarArbol', () => {
  it('sin orden deja el árbol tal cual', () => {
    expect(ordenarArbol(ARBOL, null)).toBe(ARBOL)
  })

  it('ordena por nombre los hermanos de cada nivel, ascendente', () => {
    expect(nombres(ordenarArbol(ARBOL, { columna: 'nombre', direccion: 'ascendente' }))).toEqual([
      'Almacén[Fideos[]]',
      'Bebidas[Gaseosas[Cola[]],Jugos[]]',
    ])
  })

  it('ordena descendente sin sacar a ningún hijo de su padre', () => {
    expect(nombres(ordenarArbol(ARBOL, { columna: 'nombre', direccion: 'descendente' }))).toEqual([
      'Bebidas[Jugos[],Gaseosas[Cola[]]]',
      'Almacén[Fideos[]]',
    ])
  })

  it('no modifica el árbol de origen', () => {
    const antes = nombres(ARBOL)
    ordenarArbol(ARBOL, { columna: 'nombre', direccion: 'descendente' })

    expect(nombres(ARBOL)).toEqual(antes)
  })
})
