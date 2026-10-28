import { describe, expect, it } from 'vitest'
import { filtrarFilas, hayFiltrosActivos, normalizarTexto, type ColumnasFiltrables } from './filtros'

type Fila = { id: number; nombre: string; modo: string; marca: string | null }

const columnas: ColumnasFiltrables<Fila> = {
  id: { etiqueta: 'ID', valor: (f) => f.id },
  nombre: { etiqueta: 'Nombre', valor: (f) => f.nombre },
  modo: {
    etiqueta: 'Modo',
    valor: (f) => f.modo,
    opciones: [
      { valor: 'Fija', etiqueta: 'Fija' },
      { valor: 'Derivada', etiqueta: 'Derivada' },
    ],
  },
  marca: { etiqueta: 'Marca', valor: (f) => f.marca },
}

const filas: Fila[] = [
  { id: 1, nombre: 'Café molido', modo: 'Fija', marca: 'La Virginia' },
  { id: 12, nombre: 'Cafetera', modo: 'Derivada', marca: null },
  { id: 3, nombre: 'Té verde', modo: 'Fija', marca: null },
]

const ids = (resultado: readonly Fila[]) => resultado.map((f) => f.id)

describe('normalizarTexto', () => {
  it('pasa a minúsculas, quita acentos y recorta', () => {
    expect(normalizarTexto('  CAFÉ Ñandú ')).toBe('cafe nandu')
  })
})

describe('filtrarFilas', () => {
  it('sin filtros devuelve las mismas filas', () => {
    expect(filtrarFilas(filas, columnas, {})).toBe(filas)
    expect(filtrarFilas(filas, columnas, { nombre: '  ' })).toBe(filas)
  })

  it('el texto filtra por contenido sin distinguir acentos ni mayúsculas', () => {
    expect(ids(filtrarFilas(filas, columnas, { nombre: 'CAFE' }))).toEqual([1, 12])
    expect(ids(filtrarFilas(filas, columnas, { nombre: 'TE V' }))).toEqual([3])
  })

  it('un número se filtra por su texto', () => {
    expect(ids(filtrarFilas(filas, columnas, { id: '1' }))).toEqual([1, 12])
  })

  it('una columna con opciones exige el valor exacto', () => {
    expect(ids(filtrarFilas(filas, columnas, { modo: 'Fija' }))).toEqual([1, 3])
    expect(ids(filtrarFilas(filas, columnas, { modo: 'Fij' }))).toEqual([])
  })

  it('combina las columnas con Y', () => {
    expect(ids(filtrarFilas(filas, columnas, { nombre: 'cafe', modo: 'Derivada' }))).toEqual([12])
  })

  it('un valor vacío no coincide con un texto buscado', () => {
    expect(ids(filtrarFilas(filas, columnas, { marca: 'virg' }))).toEqual([1])
  })

  it('ignora filtros de columnas que no existen', () => {
    expect(filtrarFilas(filas, columnas, { inexistente: 'x' })).toBe(filas)
  })
})

describe('hayFiltrosActivos', () => {
  it('es verdadero solo si alguna columna tiene texto', () => {
    expect(hayFiltrosActivos({})).toBe(false)
    expect(hayFiltrosActivos({ nombre: ' ' })).toBe(false)
    expect(hayFiltrosActivos({ nombre: '', modo: 'Fija' })).toBe(true)
  })
})
