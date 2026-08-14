import { describe, expect, it } from 'vitest'
import { agruparPorProveedor } from './agruparPorProveedor'
import type { FilaDeReposicion } from '../api/tipos'

function filaFixture(sobrescribir: Partial<FilaDeReposicion> = {}): FilaDeReposicion {
  return {
    idArticulo: 1,
    articulo: 'Artículo genérico',
    cantidad: 5,
    minimo: 10,
    reposicion: 20,
    sugerido: 15,
    idProveedor: 1,
    proveedor: 'Proveedor Uno',
    consumoDiarioPromedio: null,
    diasDeCobertura: null,
    ...sobrescribir,
  }
}

describe('agruparPorProveedor (stage-13-stock-inteligente, Slice 6 — fold sobre la lista ya ordenada)', () => {
  it('agrupa dos proveedores en el orden que ya trae el servidor', () => {
    const filaA = filaFixture({ idArticulo: 1, idProveedor: 1, proveedor: 'Proveedor Uno' })
    const filaB = filaFixture({ idArticulo: 2, idProveedor: 1, proveedor: 'Proveedor Uno' })
    const filaC = filaFixture({ idArticulo: 3, idProveedor: 2, proveedor: 'Proveedor Dos' })

    const grupos = agruparPorProveedor([filaA, filaB, filaC])

    expect(grupos).toEqual([
      { idProveedor: 1, proveedor: 'Proveedor Uno', filas: [filaA, filaB] },
      { idProveedor: 2, proveedor: 'Proveedor Dos', filas: [filaC] },
    ])
  })

  it('el bucket "Sin proveedor" (idProveedor null) aterriza último, tal como lo ordena el servidor', () => {
    const filaConProveedor = filaFixture({ idArticulo: 1, idProveedor: 1, proveedor: 'Proveedor Uno' })
    const filaSinProveedorUno = filaFixture({ idArticulo: 2, idProveedor: null, proveedor: null })
    const filaSinProveedorDos = filaFixture({ idArticulo: 3, idProveedor: null, proveedor: null })

    const grupos = agruparPorProveedor([filaConProveedor, filaSinProveedorUno, filaSinProveedorDos])

    expect(grupos).toHaveLength(2)
    expect(grupos[grupos.length - 1]).toEqual({
      idProveedor: null,
      proveedor: null,
      filas: [filaSinProveedorUno, filaSinProveedorDos],
    })
  })

  it('una única fila produce un único grupo con esa fila', () => {
    const fila = filaFixture()
    expect(agruparPorProveedor([fila])).toEqual([{ idProveedor: 1, proveedor: 'Proveedor Uno', filas: [fila] }])
  })

  it('la lista vacía produce cero grupos', () => {
    expect(agruparPorProveedor([])).toEqual([])
  })
})
