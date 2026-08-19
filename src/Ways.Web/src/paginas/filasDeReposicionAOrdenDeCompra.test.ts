import { describe, expect, it } from 'vitest'
import { itemsDeOrdenDesdeFilasDeReposicion } from './filasDeReposicionAOrdenDeCompra'
import type { FilaDeReposicion } from '../api/tipos'

function filaFixture(sobrescribir: Partial<FilaDeReposicion> = {}): FilaDeReposicion {
  return {
    idArticulo: 100,
    articulo: 'Yerba mate 1kg',
    cantidad: 3,
    minimo: 10,
    reposicion: 20,
    sugerido: 17,
    idProveedor: 1,
    proveedor: 'Proveedor Uno',
    consumoDiarioPromedio: null,
    diasDeCobertura: null,
    ...sobrescribir,
  }
}

describe('itemsDeOrdenDesdeFilasDeReposicion', () => {
  // Prueba el filtro `sugerido !== null` (mutation target #34c, parte 3): borrándolo, una fila sin
  // reposición configurada entraría a la OC con `cantidadPedida` inventada en vez de desaparecer.
  it('excluye las filas con sugerido = null, nunca las defaultea a cantidadPedida = 0', () => {
    const filaSinSugerido = filaFixture({ idArticulo: 1, sugerido: null })
    const filaConSugerido = filaFixture({ idArticulo: 2, sugerido: 17 })

    const items = itemsDeOrdenDesdeFilasDeReposicion([filaSinSugerido, filaConSugerido])

    expect(items).toHaveLength(1)
    expect(items[0].idArticulo).toBe(2)
  })

  it('un sugerido genuino de 0 SÍ se incluye — no es lo mismo que null', () => {
    const filaConSugeridoCero = filaFixture({ idArticulo: 3, sugerido: 0 })

    const items = itemsDeOrdenDesdeFilasDeReposicion([filaConSugeridoCero])

    expect(items).toHaveLength(1)
    expect(items[0].cantidadPedida).toBe(0)
  })

  it('mapea IdArticulo/Sugerido → IdArticulo/CantidadPedida, con descripción del artículo y sin costo', () => {
    const fila = filaFixture({ idArticulo: 55, articulo: 'Aceite 900ml', sugerido: 9 })

    const [item] = itemsDeOrdenDesdeFilasDeReposicion([fila])

    expect(item).toEqual({
      idArticulo: 55,
      descripcion: 'Aceite 900ml',
      cantidadPedida: 9,
      costoUnitarioEstimado: null,
    })
  })

  it('un grupo vacío produce un array vacío', () => {
    expect(itemsDeOrdenDesdeFilasDeReposicion([])).toEqual([])
  })

  it('preserva el orden original de las filas', () => {
    const filaA = filaFixture({ idArticulo: 1, sugerido: 5 })
    const filaB = filaFixture({ idArticulo: 2, sugerido: 8 })

    const items = itemsDeOrdenDesdeFilasDeReposicion([filaA, filaB])

    expect(items.map((i) => i.idArticulo)).toEqual([1, 2])
  })
})
