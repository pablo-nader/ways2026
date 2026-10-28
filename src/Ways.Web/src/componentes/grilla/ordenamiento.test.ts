import { describe, expect, it } from 'vitest'
import { compararValores, ordenarFilas, siguienteOrden, type ColumnasOrdenables, type EstadoDeOrden } from './ordenamiento'

type Fila = { id: number; nombre: string | null; cantidad: number | null; fecha: Date | null; codigo: string }

const columnas: ColumnasOrdenables<Fila> = {
  nombre: { valor: (f) => f.nombre },
  cantidad: { valor: (f) => f.cantidad },
  fecha: { valor: (f) => f.fecha },
  codigo: { valor: (f) => f.codigo },
}

function fila(id: number, sobrescribir: Partial<Fila> = {}): Fila {
  return { id, nombre: `n${id}`, cantidad: id, fecha: null, codigo: `c${id}`, ...sobrescribir }
}

const ids = (filas: readonly Fila[]) => filas.map((f) => f.id)
const asc = (columna: string): EstadoDeOrden => ({ columna, direccion: 'ascendente' })
const desc = (columna: string): EstadoDeOrden => ({ columna, direccion: 'descendente' })

describe('siguienteOrden', () => {
  it('recorre ascendente, descendente y vuelve al orden original', () => {
    const primero = siguienteOrden(null, 'nombre')
    const segundo = siguienteOrden(primero, 'nombre')
    const tercero = siguienteOrden(segundo, 'nombre')

    expect(primero).toEqual(asc('nombre'))
    expect(segundo).toEqual(desc('nombre'))
    expect(tercero).toBeNull()
  })

  it('al cambiar de columna arranca ascendente, venga de donde venga', () => {
    expect(siguienteOrden(desc('nombre'), 'cantidad')).toEqual(asc('cantidad'))
    expect(siguienteOrden(asc('nombre'), 'cantidad')).toEqual(asc('cantidad'))
  })
})

describe('compararValores', () => {
  it('compara números como números y no como texto', () => {
    expect(compararValores(9, 10)).toBeLessThan(0)
  })

  it('compara fechas cronológicamente', () => {
    expect(compararValores(new Date(2026, 0, 5), new Date(2026, 0, 10))).toBeLessThan(0)
    expect(compararValores(new Date(2026, 1, 1), new Date(2025, 11, 31))).toBeGreaterThan(0)
  })

  it('compara texto sin distinguir mayúsculas ni acentos', () => {
    expect(compararValores('Árbol', 'arbol')).toBe(0)
    expect(compararValores('abeja', 'Árbol')).toBeLessThan(0)
  })

  it('compara las partes numéricas de un texto como números', () => {
    expect(compararValores('Caja 2', 'Caja 10')).toBeLessThan(0)
  })

  it('un booleano falso va antes que uno verdadero', () => {
    expect(compararValores(false, true)).toBeLessThan(0)
  })
})

describe('ordenarFilas', () => {
  it('sin orden devuelve las filas tal cual', () => {
    const filas = [fila(2), fila(1)]

    expect(ordenarFilas(filas, columnas, null)).toBe(filas)
  })

  it('una columna desconocida no reordena', () => {
    const filas = [fila(2), fila(1)]

    expect(ordenarFilas(filas, columnas, asc('inexistente'))).toBe(filas)
  })

  it('no modifica el arreglo de origen', () => {
    const filas = [fila(2), fila(1)]

    ordenarFilas(filas, columnas, asc('cantidad'))

    expect(ids(filas)).toEqual([2, 1])
  })

  it('ordena números numéricamente en los dos sentidos', () => {
    const filas = [fila(1, { cantidad: 10 }), fila(2, { cantidad: 9 }), fila(3, { cantidad: 100 })]

    expect(ids(ordenarFilas(filas, columnas, asc('cantidad')))).toEqual([2, 1, 3])
    expect(ids(ordenarFilas(filas, columnas, desc('cantidad')))).toEqual([3, 1, 2])
  })

  it('ordena números guardados como texto con colación numérica', () => {
    const filas = [fila(1, { codigo: '10' }), fila(2, { codigo: '9' }), fila(3, { codigo: '100' })]

    expect(ids(ordenarFilas(filas, columnas, asc('codigo')))).toEqual([2, 1, 3])
  })

  it('ordena fechas cronológicamente', () => {
    const filas = [
      fila(1, { fecha: new Date(2026, 5, 1) }),
      fila(2, { fecha: new Date(2025, 11, 31) }),
      fila(3, { fecha: new Date(2026, 0, 15) }),
    ]

    expect(ids(ordenarFilas(filas, columnas, asc('fecha')))).toEqual([2, 3, 1])
    expect(ids(ordenarFilas(filas, columnas, desc('fecha')))).toEqual([1, 3, 2])
  })

  it('ordena texto ignorando acentos y mayúsculas', () => {
    const filas = [fila(1, { nombre: 'zorro' }), fila(2, { nombre: 'Árbol' }), fila(3, { nombre: 'abeja' })]

    expect(ids(ordenarFilas(filas, columnas, asc('nombre')))).toEqual([3, 2, 1])
  })

  it('los vacíos van siempre al final, ascendente o descendente', () => {
    const filas = [
      fila(1, { nombre: null }),
      fila(2, { nombre: 'b' }),
      fila(3, { nombre: '   ' }),
      fila(4, { nombre: 'a' }),
    ]

    expect(ids(ordenarFilas(filas, columnas, asc('nombre')))).toEqual([4, 2, 1, 3])
    expect(ids(ordenarFilas(filas, columnas, desc('nombre')))).toEqual([2, 4, 1, 3])
  })

  it('el cero no es un vacío y NaN sí', () => {
    const filas = [fila(1, { cantidad: Number.NaN }), fila(2, { cantidad: 0 }), fila(3, { cantidad: -1 })]

    expect(ids(ordenarFilas(filas, columnas, asc('cantidad')))).toEqual([3, 2, 1])
  })

  it('es estable: las filas que empatan conservan el orden original en los dos sentidos', () => {
    const filas = [fila(1, { nombre: 'a' }), fila(2, { nombre: 'A' }), fila(3, { nombre: 'a' }), fila(4, { nombre: 'b' })]

    expect(ids(ordenarFilas(filas, columnas, asc('nombre')))).toEqual([1, 2, 3, 4])
    expect(ids(ordenarFilas(filas, columnas, desc('nombre')))).toEqual([4, 1, 2, 3])
  })

  it('un comparador propio reemplaza al del valor, respeta la dirección y deja los vacíos al final', () => {
    const porLongitud: ColumnasOrdenables<Fila> = {
      nombre: { valor: (f) => f.nombre, comparar: (a, b) => (a.nombre?.length ?? 0) - (b.nombre?.length ?? 0) },
    }
    const filas = [fila(1, { nombre: 'ccc' }), fila(2, { nombre: null }), fila(3, { nombre: 'a' }), fila(4, { nombre: 'bb' })]

    expect(ids(ordenarFilas(filas, porLongitud, asc('nombre')))).toEqual([3, 4, 1, 2])
    expect(ids(ordenarFilas(filas, porLongitud, desc('nombre')))).toEqual([1, 4, 3, 2])
  })

  it('sirve para ordenar dentro de cada grupo sin mezclarlos', () => {
    const grupos = [
      [fila(1, { nombre: 'b' }), fila(2, { nombre: 'a' })],
      [fila(3, { nombre: 'd' }), fila(4, { nombre: 'c' })],
    ]

    const resultado = grupos.map((grupo) => ids(ordenarFilas(grupo, columnas, asc('nombre'))))

    expect(resultado).toEqual([
      [2, 1],
      [4, 3],
    ])
  })
})
