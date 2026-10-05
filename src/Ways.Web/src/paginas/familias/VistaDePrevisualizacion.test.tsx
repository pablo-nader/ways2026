import { render, screen, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import type { CambiosDeUnArticulo, PrevisualizacionDeAgrupacion, ProblemaDeAgrupacion, ValoresCompartidosDeLaFamilia } from '../../api/tipos'
import { formatearFechaHora } from '../articulos/familia'
import type { ArticuloElegido } from './SelectorDeArticulos'
import { VistaDePrevisualizacion } from './VistaDePrevisualizacion'
import { nombresDeCatalogoVacios, type NombresDeCatalogo } from './valoresCompartidos'

const base: ValoresCompartidosDeLaFamilia = {
  idArea: 1,
  idCategoria: 2,
  idGrupo: null,
  idProveedorHabitual: null,
  idAlicuotaIva: 5,
  unidadVenta: 'Unidad',
  unidadesPorBulto: null,
  esProducto: true,
  controlaLote: false,
  acumulaEnVenta: true,
  costoLista: 100,
  descuentoProveedor: null,
  costoNominal: null,
}

const nombres: NombresDeCatalogo = {
  ...nombresDeCatalogoVacios(),
  areas: new Map([
    [1, 'Almacén'],
    [9, 'Fiambrería'],
  ]),
  categorias: new Map([[2, 'Bebidas']]),
  alicuotas: new Map([[5, 'IVA 21%']]),
}

const listas = new Map([[2, 'General']])

function articuloElegido(id: number, nombre: string): ArticuloElegido {
  return { id, codigoInterno: `A00${id}`, nombre }
}

const articulos = new Map([
  [32, articuloElegido(32, 'Frutilla')],
  [33, articuloElegido(33, 'Limón')],
])

function cambios(idArticulo: number, sobrescribir: Partial<CambiosDeUnArticulo> = {}): CambiosDeUnArticulo {
  return { idArticulo, campos: [], actual: base, nuevo: base, precios: [], ...sobrescribir }
}

function previsualizacion(sobrescribir: Partial<PrevisualizacionDeAgrupacion> = {}): PrevisualizacionDeAgrupacion {
  return { idArticuloReferencia: 31, idFamilia: null, articulos: [], problemas: [], ...sobrescribir }
}

function problema(codigo: string, mensaje: string, sobrescribir: Partial<ProblemaDeAgrupacion> = {}): ProblemaDeAgrupacion {
  return { codigo, mensaje, idArticulo: null, idListaPrecio: null, ...sobrescribir }
}

function montar(p: PrevisualizacionDeAgrupacion) {
  return render(<VistaDePrevisualizacion previsualizacion={p} articulos={articulos} nombres={nombres} listas={listas} />)
}

describe('VistaDePrevisualizacion — los artículos', () => {
  it('nombra cada artículo por su código y su nombre', () => {
    montar(previsualizacion({ articulos: [cambios(32), cambios(33)] }))

    expect(screen.getByRole('heading', { name: 'A0032 — Frutilla' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'A0033 — Limón' })).toBeInTheDocument()
  })

  it('un artículo que no se eligió (no está en el mapa) se nombra por su id', () => {
    montar(previsualizacion({ articulos: [cambios(77)] }))

    expect(screen.getByRole('heading', { name: 'Artículo 77' })).toBeInTheDocument()
  })

  it('un artículo ya alineado dice que no cambia nada y no muestra tablas', () => {
    montar(previsualizacion({ articulos: [cambios(32)] }))

    expect(screen.getByText('Ya está alineado con la referencia: no cambia nada.')).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
  })

  it('sin artículos ni problemas dice que no hay nada para agrupar', () => {
    montar(previsualizacion())

    expect(screen.getByText('No hay artículos para agrupar.')).toBeInTheDocument()
  })

  it('un campo que cambia se muestra con la etiqueta del campo, lo actual y a lo que pasa, con los nombres del catálogo', () => {
    montar(
      previsualizacion({
        articulos: [cambios(32, { campos: ['id_area', 'costo_lista'], actual: { ...base, idArea: 1, costoLista: 100 }, nuevo: { ...base, idArea: 9, costoLista: 250 } })],
      }),
    )

    const tabla = screen.getByRole('table')
    expect(within(tabla).getAllByRole('row').map((fila) => within(fila).queryAllByRole('cell').map((c) => c.textContent))).toEqual([
      [],
      ['Área', 'Almacén', 'Fiambrería'],
      ['Costo de lista', '$ 100,00', '$ 250,00'],
    ])
  })

  it('solo muestra los campos que cambian, en el orden en que los manda el servidor', () => {
    montar(previsualizacion({ articulos: [cambios(32, { campos: ['costo_lista', 'id_categoria'], nuevo: { ...base, costoLista: 1, idCategoria: null } })] }))

    const filas = within(screen.getByRole('table')).getAllByRole('row').slice(1)
    expect(filas.map((f) => within(f).getAllByRole('cell')[0].textContent)).toEqual(['Costo de lista', 'Categoría'])
    expect(within(filas[1]).getAllByRole('cell')[2].textContent).toBe('Sin asignar')
  })

  /** Cláusula bajo prueba: `actual === nuevo ? [] : …` de `filasDeCampos`. El servidor lista una columna porque sus ids
   * difieren; si los dos lados se muestran igual (dos filas de catálogo dadas de baja son las dos "Sin asignar") la fila
   * no dice nada. Evidencia de mutación (mutation-proof-tests): listar siempre la fila hace fallar este test y los dos
   * siguientes; revertido, vuelven a verde. */
  it('un campo cuyos dos valores se muestran igual no se lista, y los que sí cambian siguen listados', () => {
    montar(
      previsualizacion({
        articulos: [cambios(32, { campos: ['id_grupo', 'costo_lista'], actual: { ...base, idGrupo: null, costoLista: 100 }, nuevo: { ...base, idGrupo: null, costoLista: 250 } })],
      }),
    )

    const filas = within(screen.getByRole('table')).getAllByRole('row').slice(1)
    expect(filas.map((f) => within(f).getAllByRole('cell').map((c) => c.textContent))).toEqual([['Costo de lista', '$ 100,00', '$ 250,00']])
  })

  it('un importe que difiere por debajo del centavo se muestra igual en los dos lados y no se lista', () => {
    montar(
      previsualizacion({
        articulos: [cambios(32, { campos: ['costo_lista', 'id_area'], actual: { ...base, costoLista: 100.001, idArea: 1 }, nuevo: { ...base, costoLista: 100.002, idArea: 9 } })],
      }),
    )

    const filas = within(screen.getByRole('table')).getAllByRole('row').slice(1)
    expect(filas.map((f) => within(f).getAllByRole('cell')[0].textContent)).toEqual(['Área'])
  })

  it('si todo lo que el servidor lista se muestra igual en los dos lados, el artículo se ve alineado y no hay tabla', () => {
    montar(
      previsualizacion({
        articulos: [cambios(32, { campos: ['id_grupo'], actual: { ...base, idGrupo: null }, nuevo: { ...base, idGrupo: null } })],
      }),
    )

    expect(screen.getByText('Ya está alineado con la referencia: no cambia nada.')).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
  })

  /** Cláusula bajo prueba: `actual === nuevo ? [] : …` de `filasDePrecios`, la hermana de la de campos: un estado de
   * precios que se muestra igual antes y después no es un cambio para quien lo mira. Evidencia de mutación
   * (mutation-proof-tests): listar siempre la fila hace fallar este test; revertido, vuelve a verde. */
  it('un precio cuyo estado se muestra igual antes y después no se lista, y el que sí cambia sigue listado', () => {
    montar(
      previsualizacion({
        articulos: [
          cambios(32, {
            precios: [
              { idListaPrecio: 2, actual: { vigente: 100.001, pendiente: null }, nuevo: { vigente: 100.002, pendiente: null } },
              { idListaPrecio: 4, actual: { vigente: null, pendiente: null }, nuevo: { vigente: 50, pendiente: null } },
            ],
          }),
        ],
      }),
    )

    const filas = within(screen.getByRole('table')).getAllByRole('row').slice(1)
    expect(filas.map((f) => within(f).getAllByRole('cell').map((c) => c.textContent))).toEqual([['Lista 4', '—', '$ 50,00']])
  })

  /** Una columna que el cliente no conoce se lista aunque sus dos lados sean rayas: el servidor dice que cambia y acá no se
   * puede decir de qué a qué, que no es lo mismo que dos valores conocidos que se ven iguales. */
  it('una columna que el cliente no conoce se muestra por su nombre, con rayas, sin romper', () => {
    montar(previsualizacion({ articulos: [cambios(32, { campos: ['columna_nueva'] })] }))

    const celdas = within(screen.getAllByRole('row')[1]).getAllByRole('cell').map((c) => c.textContent)
    expect(celdas).toEqual(['columna_nueva', '—', '—'])
  })

  it('un precio que cambia se muestra por lista, de lo actual a lo que pasa, con el pendiente y su fecha', () => {
    const iso = '2026-12-01T10:00:00+00:00'
    montar(
      previsualizacion({
        articulos: [
          cambios(32, {
            precios: [
              { idListaPrecio: 2, actual: { vigente: 100, pendiente: null }, nuevo: { vigente: 120, pendiente: { monto: 130, vigenteDesde: iso } } },
              { idListaPrecio: 4, actual: { vigente: null, pendiente: null }, nuevo: { vigente: 50, pendiente: null } },
            ],
          }),
        ],
      }),
    )

    const filas = within(screen.getByRole('table')).getAllByRole('row').slice(1)
    expect(filas.map((f) => within(f).getAllByRole('cell').map((c) => c.textContent))).toEqual([
      ['General', '$ 100,00', `$ 120,00 · programado $ 130,00 desde ${formatearFechaHora(iso)}`],
      ['Lista 4', '—', '$ 50,00'],
    ])
  })

  it('un artículo con campos y precios que cambian muestra las dos tablas y no el aviso de alineado', () => {
    montar(
      previsualizacion({
        articulos: [
          cambios(32, {
            campos: ['costo_lista'],
            nuevo: { ...base, costoLista: 9 },
            precios: [{ idListaPrecio: 2, actual: { vigente: 1, pendiente: null }, nuevo: { vigente: 2, pendiente: null } }],
          }),
        ],
      }),
    )

    expect(screen.getAllByRole('table')).toHaveLength(2)
    expect(screen.queryByText(/Ya está alineado/)).not.toBeInTheDocument()
  })

  it('un artículo con solo un cambio de precio muestra esa tabla y no la de campos', () => {
    montar(
      previsualizacion({
        articulos: [cambios(32, { precios: [{ idListaPrecio: 2, actual: { vigente: 1, pendiente: null }, nuevo: { vigente: 2, pendiente: null } }] })],
      }),
    )

    expect(screen.getAllByRole('table')).toHaveLength(1)
    expect(screen.getByRole('columnheader', { name: 'Lista de precios' })).toBeInTheDocument()
  })
})

describe('VistaDePrevisualizacion — los problemas', () => {
  it('sin problemas no hay aviso de error', () => {
    montar(previsualizacion({ articulos: [cambios(32)] }))

    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('los lista en el orden en que llegan, cada uno con el mensaje del servidor y la ayuda de su código', () => {
    montar(
      previsualizacion({
        problemas: [
          problema('articulo_en_otra_familia', 'El artículo 33 ya está en la familia "Talles".', { idArticulo: 33 }),
          problema('familia_precio_inalineable', 'No se puede alinear el precio de "Limón" en "General".', { idArticulo: 33, idListaPrecio: 2 }),
        ],
      }),
    )

    const alerta = screen.getByRole('alert')
    expect(within(alerta).getByText(/No se puede agrupar así/)).toBeInTheDocument()
    const items = within(alerta).getAllByRole('listitem').map((li) => li.textContent)
    expect(items).toEqual([
      'El artículo 33 ya está en la familia "Talles". Agrupar no mueve a nadie de su familia: sacalo de la que tiene o dejalo fuera de la selección.',
      'No se puede alinear el precio de "Limón" en "General". Un precio nunca se quita: dejá a ese artículo fuera de la selección o corregí sus precios antes de agrupar.',
    ])
  })

  it('un problema de un código que el cliente no conoce muestra el mensaje del servidor tal cual', () => {
    montar(previsualizacion({ problemas: [problema('codigo_nuevo', 'Pasó algo nuevo.')] }))

    expect(within(screen.getByRole('alert')).getByRole('listitem')).toHaveTextContent('Pasó algo nuevo.')
  })

  it('con problemas y sin artículos no dice "no hay artículos para agrupar"', () => {
    montar(previsualizacion({ problemas: [problema('referencia_invalida', 'No existe el artículo 99.')] }))

    expect(screen.queryByText('No hay artículos para agrupar.')).not.toBeInTheDocument()
  })

  it('dos problemas con el mismo código y el mismo artículo se muestran los dos', () => {
    montar(
      previsualizacion({
        problemas: [
          problema('familia_precio_inalineable', 'Primera lista.', { idArticulo: 33, idListaPrecio: 2 }),
          problema('familia_precio_inalineable', 'Segunda lista.', { idArticulo: 33, idListaPrecio: 3 }),
        ],
      }),
    )

    expect(within(screen.getByRole('alert')).getAllByRole('listitem')).toHaveLength(2)
  })
})
