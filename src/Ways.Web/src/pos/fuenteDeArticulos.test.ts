import { describe, expect, it, vi } from 'vitest'
import type { ArticuloDeInstantanea, InstantaneaDePos, PaginaDe, ArticuloListado, ResultadoDeResolucion } from '../api/tipos'
import type { LineaCarrito } from '../api/carrito'
import {
  elegirFuenteDeArticulos,
  fuenteDeArticulosDesdeApi,
  fuenteDeArticulosDesdeInstantanea,
  TOPE_DE_RESULTADOS_DE_ARTICULOS,
} from './fuenteDeArticulos'
import { resolverPreciosOffline } from './instantaneaOffline'

const LISTA = 5

function articulo(idArticulo: number, nombre: string, extra: Partial<ArticuloDeInstantanea> = {}): ArticuloDeInstantanea {
  return {
    idArticulo,
    codigoInterno: `A${idArticulo}`,
    nombre,
    codigosBarra: [],
    idAlicuotaIva: 1,
    porcentajeIva: 21,
    unidadVenta: 'Unidad',
    preciosPorLista: [{ idListaPrecio: LISTA, precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0, aplicadas: [] }],
    ...extra,
  }
}

function instantanea(articulos: ArticuloDeInstantanea[]): InstantaneaDePos {
  return { momento: '2026-10-11T12:00:00Z', idPuntoVenta: 1, articulos, clientes: [], mediosDePago: [], toleranciaPago: 0 }
}

describe('fuenteDeArticulosDesdeInstantanea.buscar', () => {
  const fuente = fuenteDeArticulosDesdeInstantanea(
    instantanea([
      articulo(3, 'Ñandú pisado'),
      articulo(1, 'Fanta 1.5L', { codigosBarra: ['7790001112223'] }),
      articulo(2, 'Cola Zero', { acumulaEnVenta: false }),
      articulo(4, 'Sin unidad', { unidadVenta: undefined }),
    ]),
  )

  it('encuentra por nombre, código interno y código de barras, sin mayúsculas ni acentos', async () => {
    expect((await fuente.buscar('fAnTa')).map((a) => a.id)).toEqual([1])
    expect((await fuente.buscar('nandu')).map((a) => a.id)).toEqual([3])
    expect((await fuente.buscar('a2')).map((a) => a.id)).toEqual([2])
    expect((await fuente.buscar('1112')).map((a) => a.id)).toEqual([1])
  })

  it('no devuelve los que no coinciden y ordena por nombre', async () => {
    expect(await fuente.buscar('zzz')).toEqual([])
    expect((await fuente.buscar('a')).map((a) => a.nombre)).toEqual(['Cola Zero', 'Fanta 1.5L', 'Ñandú pisado', 'Sin unidad'])
  })

  it('entrega la identidad que usa el carrito: acumulaEnVenta, unidadVenta y costo ausente', async () => {
    const [cola] = await fuente.buscar('cola')
    expect(cola).toEqual({ id: 2, codigoInterno: 'A2', nombre: 'Cola Zero', acumulaEnVenta: false, unidadVenta: 'Unidad', costoNominal: null })
    expect((await fuente.buscar('sin unidad'))[0]).toMatchObject({ acumulaEnVenta: true, unidadVenta: 'Peso' })
  })

  it('corta en una página del servidor', async () => {
    const muchos = fuenteDeArticulosDesdeInstantanea(
      instantanea(Array.from({ length: TOPE_DE_RESULTADOS_DE_ARTICULOS + 5 }, (_, i) => articulo(i + 1, `Galletita ${String(i).padStart(2, '0')}`))),
    )
    const resultado = await muchos.buscar('galle')
    expect(resultado).toHaveLength(TOPE_DE_RESULTADOS_DE_ARTICULOS)
    expect(resultado[0].nombre).toBe('Galletita 00')
  })
})

describe('fuenteDeArticulosDesdeInstantanea.resolverPrecios', () => {
  it('el precio es el que resuelve el carrito offline para una línea de cantidad 1 en la misma lista', async () => {
    const conEscalon = articulo(1, 'Con oferta', {
      preciosPorLista: [
        {
          idListaPrecio: LISTA,
          precioOriginal: 200,
          precioFinal: 180,
          descuentoUnitario: 20,
          aplicadas: [{ idOferta: 7, nombre: 'Diez por ciento', descuentoUnitario: 20 }],
          escalones: [{ cantidadDesde: 3, precioFinal: 150, descuentoUnitario: 50, aplicadas: [] }],
        },
      ],
    })
    const snapshot = instantanea([conEscalon, articulo(2, 'Plano')])
    const fuente = fuenteDeArticulosDesdeInstantanea(snapshot)

    const vista = await fuente.resolverPrecios([1, 2], LISTA, 9)

    for (const r of vista) {
      const linea: LineaCarrito = { idLinea: 'l', idArticulo: r.idArticulo, codigoInterno: '', nombre: '', codigoBarra: null, acumulaEnVenta: true, cantidad: 1 }
      expect(r).toEqual(resolverPreciosOffline([linea], snapshot, LISTA).l)
    }
    expect(vista.map((r) => r.precioFinal)).toEqual([180, 100])
  })

  it('un artículo sin precio en la lista queda sin precio, y las filas no se suman entre sí', async () => {
    const fuente = fuenteDeArticulosDesdeInstantanea(instantanea([articulo(1, 'Otra lista', { preciosPorLista: [] })]))

    expect(await fuente.resolverPrecios([1, 99], LISTA, null)).toEqual([
      { idArticulo: 1, idListaPrecio: LISTA, precioOriginal: null, precioFinal: null, descuentoUnitario: 0, aplicadas: [] },
      { idArticulo: 99, idListaPrecio: LISTA, precioOriginal: null, precioFinal: null, descuentoUnitario: 0, aplicadas: [] },
    ])
  })

  it('no ofrece ofertas vigentes: la instantánea no las trae', () => {
    expect(fuenteDeArticulosDesdeInstantanea(instantanea([])).ofertasVigentes).toBeUndefined()
  })
})

describe('fuenteDeArticulosDesdeApi', () => {
  it('busca vendibles con el cliente de artículos y resuelve a cantidad 1 por línea con el cliente de ofertas', async () => {
    const items = [{ id: 9, codigoInterno: 'A9', nombre: 'Fanta' }] as ArticuloListado[]
    const listar = vi.fn().mockResolvedValue({ items, total: 1, pagina: 1, tamanio: 25 } satisfies PaginaDe<ArticuloListado>)
    const resolver = vi.fn().mockResolvedValue([] as ResultadoDeResolucion[])
    const vigentes = vi.fn().mockResolvedValue([])
    const fuente = fuenteDeArticulosDesdeApi({ listar }, { resolver }, vigentes)

    expect(fuente.origen).toBe('api')
    expect(await fuente.buscar('fa')).toBe(items)
    expect(listar).toHaveBeenCalledWith('fa', false, undefined, { soloVendibles: true })

    await fuente.resolverPrecios([9], 3, 7)
    expect(resolver).toHaveBeenCalledWith([{ idArticulo: 9, idEmpresa: 7, idListaPrecio: 3, cantidad: 1 }], 'PorLinea')

    await fuente.ofertasVigentes?.([9], 7, 3)
    expect(vigentes).toHaveBeenCalledWith({ idsArticulos: [9], idEmpresa: 7, idListaPrecio: 3 })
  })
})

describe('elegirFuenteDeArticulos', () => {
  it('con instantánea usa la local; sin ella, la API', () => {
    expect(elegirFuenteDeArticulos(instantanea([])).origen).toBe('instantanea')
    expect(elegirFuenteDeArticulos(null).origen).toBe('api')
  })
})
