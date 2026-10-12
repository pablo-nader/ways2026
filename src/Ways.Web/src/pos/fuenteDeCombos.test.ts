import { describe, expect, it, vi } from 'vitest'
import { ErrorApi } from '../api/cliente'
import type { ArticuloDeInstantanea, ComboCargableDeApi, ComboListado, InstantaneaDePos, UnidadVenta } from '../api/tipos'
import { elegirFuenteDeCombos, fuenteDesdeApi, fuenteDesdeInstantanea } from './fuenteDeCombos'

const LISTA = 5

function articuloDeInstantanea(
  idArticulo: number,
  nombre: string,
  extra: Partial<ArticuloDeInstantanea> = {},
  listas: number[] = [LISTA],
): ArticuloDeInstantanea {
  return {
    idArticulo,
    codigoInterno: `A${idArticulo}`,
    nombre,
    codigosBarra: [],
    idAlicuotaIva: 1,
    porcentajeIva: 21,
    unidadVenta: 'Unidad' as UnidadVenta,
    preciosPorLista: listas.map((idListaPrecio) => ({ idListaPrecio, precioOriginal: 100, precioFinal: 100, descuentoUnitario: 0, aplicadas: [] })),
    ...extra,
  }
}

function instantanea(articulos: ArticuloDeInstantanea[], combos: InstantaneaDePos['combos']): InstantaneaDePos {
  return { momento: '2026-10-11T12:00:00Z', idPuntoVenta: 1, articulos, clientes: [], mediosDePago: [], toleranciaPago: 0, combos }
}

const ARTICULOS = [
  articuloDeInstantanea(1, 'Pancho'),
  articuloDeInstantanea(2, 'Sprite', { idFamilia: 9 }),
  articuloDeInstantanea(3, 'Coca', { idFamilia: 9 }),
  articuloDeInstantanea(4, 'Pesable', { idFamilia: 9, unidadVenta: 'Peso' }),
  articuloDeInstantanea(5, 'Sin precio en la lista', { idFamilia: 9 }, [99]),
  articuloDeInstantanea(6, 'Sin unidad', { idFamilia: 9, unidadVenta: undefined }),
]

describe('fuenteDesdeInstantanea', () => {
  it('lista los combos por código y arma los candidatos: artículo, o los miembros por unidad y con precio en la lista', async () => {
    const fuente = fuenteDesdeInstantanea(
      instantanea(ARTICULOS, [
        { idCombo: 20, codigo: 8, nombre: 'Otro', precio: 900, componentes: [{ idArticulo: 1, cantidad: 2 }, { idArticulo: 3, cantidad: 1 }] },
        { idCombo: 10, codigo: 3, nombre: 'Pancho + gaseosa', precio: 5000, componentes: [{ idArticulo: 1, cantidad: 1 }, { idFamilia: 9, cantidad: 2 }] },
      ]),
      LISTA,
    )

    const lista = await fuente.listar()
    const combo = await fuente.cargar(10)

    expect(lista.map((c) => c.codigo)).toEqual([3, 8])
    expect(lista[0]).toEqual({
      idCombo: 10,
      codigo: 3,
      nombre: 'Pancho + gaseosa',
      precio: 5000,
      resumen: 'Pancho × 1 + Coca / Sprite × 2',
      motivoNoCargable: null,
    })
    expect(combo.componentes[0].candidatos.map((c) => c.idArticulo)).toEqual([1])
    expect(combo.componentes[1].candidatos.map((c) => [c.idArticulo, c.cantidad, c.codigoBarra, c.unidadVenta])).toEqual([
      [3, 1, null, 'Unidad'],
      [2, 1, null, 'Unidad'],
    ])
  })

  it('un componente sin artículos vendibles deja el combo como no cargable, con el motivo', async () => {
    const fuente = fuenteDesdeInstantanea(
      instantanea(ARTICULOS, [
        { idCombo: 10, codigo: 3, nombre: 'Con pesable', precio: 5000, componentes: [{ idArticulo: 1, cantidad: 1 }, { idArticulo: 4, cantidad: 1 }] },
        { idCombo: 11, codigo: 4, nombre: 'Familia vacía', precio: 5000, componentes: [{ idArticulo: 1, cantidad: 1 }, { idFamilia: 77, cantidad: 1 }] },
        { idCombo: 12, codigo: 5, nombre: 'Sin precio', precio: 5000, componentes: [{ idArticulo: 1, cantidad: 1 }, { idArticulo: 5, cantidad: 1 }] },
        { idCombo: 13, codigo: 6, nombre: 'Ausente', precio: 5000, componentes: [{ idArticulo: 1, cantidad: 1 }, { idArticulo: 404, cantidad: 1 }] },
      ]),
      LISTA,
    )

    const lista = await fuente.listar()

    expect(lista.map((c) => c.motivoNoCargable)).toEqual([
      'No hay artículos disponibles para «Pesable».',
      'No hay artículos disponibles para «Familia sin artículos disponibles».',
      'No hay artículos disponibles para «Sin precio en la lista».',
      'No hay artículos disponibles para «artículo no disponible».',
    ])
  })

  it('la unidad de venta ausente se trata como Peso y queda fuera de los candidatos', async () => {
    const fuente = fuenteDesdeInstantanea(
      instantanea(ARTICULOS, [{ idCombo: 10, codigo: 3, nombre: 'C', precio: 1, componentes: [{ idArticulo: 1, cantidad: 1 }, { idFamilia: 9, cantidad: 1 }] }]),
      LISTA,
    )

    const combo = await fuente.cargar(10)

    expect(combo.componentes[1].candidatos.map((c) => c.idArticulo)).toEqual([3, 2])
  })

  it('sin lista de precios conocida no filtra por precio', async () => {
    const fuente = fuenteDesdeInstantanea(
      instantanea(ARTICULOS, [{ idCombo: 10, codigo: 3, nombre: 'C', precio: 1, componentes: [{ idArticulo: 1, cantidad: 1 }, { idArticulo: 5, cantidad: 1 }] }]),
      null,
    )

    expect((await fuente.listar())[0].motivoNoCargable).toBeNull()
  })

  it('busca por código: el combo o null', async () => {
    const fuente = fuenteDesdeInstantanea(
      instantanea(ARTICULOS, [{ idCombo: 10, codigo: 12, nombre: 'C', precio: 1, componentes: [{ idArticulo: 1, cantidad: 2 }] }]),
      LISTA,
    )

    expect((await fuente.buscarPorCodigo(12))?.idCombo).toBe(10)
    expect(await fuente.buscarPorCodigo(13)).toBeNull()
  })

  it('una instantánea sin combos lista vacío, y cargar un combo que no está rechaza', async () => {
    const fuente = fuenteDesdeInstantanea(instantanea(ARTICULOS, undefined), LISTA)

    expect(await fuente.listar()).toEqual([])
    await expect(fuente.cargar(1)).rejects.toThrow()
  })
})

const LISTADO: ComboListado[] = [
  {
    id: 2,
    codigo: 9,
    nombre: 'De otra empresa',
    precio: 1,
    idEmpresa: 77,
    activo: true,
    componentes: [{ idArticulo: 1, idFamilia: null, nombre: 'A', cantidad: 1, disponible: true }],
  },
  {
    id: 3,
    codigo: 8,
    nombre: 'Inactivo',
    precio: 1,
    idEmpresa: null,
    activo: false,
    componentes: [{ idArticulo: 1, idFamilia: null, nombre: 'A', cantidad: 1, disponible: true }],
  },
  {
    id: 4,
    codigo: 5,
    nombre: 'Colgado',
    precio: 2,
    idEmpresa: 4,
    activo: true,
    componentes: [
      { idArticulo: 1, idFamilia: null, nombre: 'Pancho', cantidad: 1, disponible: true },
      { idArticulo: null, idFamilia: 9, nombre: null, cantidad: 2, disponible: false },
    ],
  },
  {
    id: 1,
    codigo: 1,
    nombre: 'Bueno',
    precio: 5000,
    idEmpresa: null,
    activo: true,
    componentes: [
      { idArticulo: 1, idFamilia: null, nombre: 'Pancho', cantidad: 1, disponible: true },
      { idArticulo: null, idFamilia: 9, nombre: 'Gaseosas', cantidad: 2, disponible: true },
    ],
  },
]

describe('fuenteDesdeApi', () => {
  it('lista los activos del tenant o de la empresa del punto de venta, por código, y marca el componente que ya no está', async () => {
    const cliente = { listar: vi.fn().mockResolvedValue(LISTADO), cargable: vi.fn() }

    const lista = await fuenteDesdeApi(4, cliente).listar()

    expect(lista.map((c) => c.codigo)).toEqual([1, 5])
    expect(lista[0].resumen).toBe('Pancho × 1 + Gaseosas × 2')
    expect(lista[0].motivoNoCargable).toBeNull()
    expect(lista[1].motivoNoCargable).toBe('Un componente del combo ya no está disponible.')
    expect(lista[1].resumen).toBe('Pancho × 1 + artículo no disponible × 2')
  })

  it('busca por código sobre esa misma lista', async () => {
    const cliente = { listar: vi.fn().mockResolvedValue(LISTADO), cargable: vi.fn() }
    const fuente = fuenteDesdeApi(4, cliente)

    expect((await fuente.buscarPorCodigo(1))?.idCombo).toBe(1)
    expect(await fuente.buscarPorCodigo(9)).toBeNull()
    expect(await fuente.buscarPorCodigo(8)).toBeNull()
  })

  it('cargar pide el detalle con la empresa del punto de venta y lo pasa al modelo común', async () => {
    const detalle: ComboCargableDeApi = {
      id: 1,
      codigo: 1,
      nombre: 'Bueno',
      precio: 5000,
      componentes: [
        { idArticulo: 1, idFamilia: null, nombre: 'Pancho', cantidad: 1, candidatos: [] },
        { idArticulo: null, idFamilia: 9, nombre: null, cantidad: 2, candidatos: [] },
      ],
    }
    const cliente = { listar: vi.fn(), cargable: vi.fn().mockResolvedValue(detalle) }

    const combo = await fuenteDesdeApi(4, cliente).cargar(1)

    expect(cliente.cargable).toHaveBeenCalledWith(1, 4)
    expect(combo.idCombo).toBe(1)
    expect(combo.componentes.map((c) => c.etiqueta)).toEqual(['Pancho', 'artículo no disponible'])
  })

  it('un 404 del detalle se propaga', async () => {
    const cliente = { listar: vi.fn(), cargable: vi.fn().mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'No existe el combo 1.')) }

    await expect(fuenteDesdeApi(4, cliente).cargar(1)).rejects.toBeInstanceOf(ErrorApi)
  })
})

describe('elegirFuenteDeCombos', () => {
  it('con una instantánea utilizable usa la instantánea, aunque haya conexión', () => {
    expect(elegirFuenteDeCombos({ instantanea: instantanea([], undefined), idListaPrecio: LISTA, idEmpresa: 1 }).origen).toBe('instantanea')
  })

  it('sin instantánea usa la API', () => {
    expect(elegirFuenteDeCombos({ instantanea: null, idListaPrecio: LISTA, idEmpresa: 1 }).origen).toBe('api')
  })
})

describe('fuenteDesdeApi: lista en memoria', () => {
  const cliente = () => ({ listar: vi.fn().mockResolvedValue(LISTADO), cargable: vi.fn() })

  it('varias búsquedas por un código que está en lo guardado piden la lista una sola vez', async () => {
    const c = cliente()
    const fuente = fuenteDesdeApi(4, c)

    await fuente.buscarPorCodigo(1)
    await fuente.buscarPorCodigo(1)
    await fuente.buscarPorCodigo(5)

    expect(c.listar).toHaveBeenCalledTimes(1)
  })

  it('un código que no está en lo guardado la refresca una vez y recién entonces decide', async () => {
    const c = cliente()
    const fuente = fuenteDesdeApi(4, c)
    await fuente.buscarPorCodigo(1)

    expect(await fuente.buscarPorCodigo(77)).toBeNull()
    expect(c.listar).toHaveBeenCalledTimes(2)

    c.listar.mockResolvedValue([...LISTADO, { ...LISTADO[3], id: 9, codigo: 77, nombre: 'Nuevo' }])
    expect((await fuente.buscarPorCodigo(77))?.idCombo).toBe(9)
    expect(c.listar).toHaveBeenCalledTimes(3)
  })

  it('listar (el buscador al abrirse) siempre vuelve a pedirla', async () => {
    const c = cliente()
    const fuente = fuenteDesdeApi(4, c)

    await fuente.listar()
    await fuente.listar()

    expect(c.listar).toHaveBeenCalledTimes(2)
  })

  it('una lectura que falla no deja nada guardado', async () => {
    const c = cliente()
    c.listar.mockRejectedValueOnce(new ErrorApi(500, 'error', 'Falló.'))
    const fuente = fuenteDesdeApi(4, c)

    await expect(fuente.buscarPorCodigo(1)).rejects.toBeInstanceOf(ErrorApi)

    expect((await fuente.buscarPorCodigo(1))?.idCombo).toBe(1)
    expect(c.listar).toHaveBeenCalledTimes(2)
  })
})

describe('elegirFuenteDeCombos: instantánea y API', () => {
  const COMBO_LOCAL = { idCombo: 20, codigo: 8, nombre: 'Local', precio: 900, componentes: [{ idArticulo: 1, cantidad: 2 }] }
  const armar = (combos: InstantaneaDePos['combos'], enLinea: boolean) => {
    const cliente = {
      listar: vi.fn().mockResolvedValue(LISTADO),
      cargable: vi.fn().mockResolvedValue({ id: 1, codigo: 1, nombre: 'Bueno', precio: 5000, componentes: [] } satisfies ComboCargableDeApi),
    }
    const fuente = elegirFuenteDeCombos({
      instantanea: instantanea(ARTICULOS, combos),
      idListaPrecio: LISTA,
      idEmpresa: 4,
      enLinea,
      api: fuenteDesdeApi(4, cliente),
    })
    return { fuente, cliente }
  }

  it('con conexión y un código que la instantánea no tiene, prueba la API y lo encuentra', async () => {
    const { fuente, cliente } = armar([COMBO_LOCAL], true)

    expect((await fuente.buscarPorCodigo(1))?.idCombo).toBe(1)
    expect(cliente.listar).toHaveBeenCalledTimes(1)
  })

  it('con conexión y un código que está en la instantánea no toca la API', async () => {
    const { fuente, cliente } = armar([COMBO_LOCAL], true)

    expect((await fuente.buscarPorCodigo(8))?.idCombo).toBe(20)
    expect(cliente.listar).not.toHaveBeenCalled()
  })

  it('sin conexión responde solo con la instantánea: ni una llamada', async () => {
    const { fuente, cliente } = armar([COMBO_LOCAL], false)

    expect(await fuente.buscarPorCodigo(1)).toBeNull()
    expect((await fuente.listar()).map((c) => c.codigo)).toEqual([8])
    await fuente.cargar(20)
    expect(cliente.listar).not.toHaveBeenCalled()
    expect(cliente.cargable).not.toHaveBeenCalled()
  })

  it('una instantánea sin la clave combos lista los de la API con conexión, y nada sin conexión', async () => {
    const conRed = armar(undefined, true)
    const sinRed = armar(undefined, false)

    expect((await conRed.fuente.listar()).map((c) => c.codigo)).toEqual([1, 5])
    expect(await sinRed.fuente.listar()).toEqual([])
    expect(sinRed.cliente.listar).not.toHaveBeenCalled()
  })

  it('una instantánea con la clave combos (aunque vacía) lista los suyos y no mezcla los de la API', async () => {
    const { fuente, cliente } = armar([], true)

    expect(await fuente.listar()).toEqual([])
    expect(cliente.listar).not.toHaveBeenCalled()
  })

  it('cargar usa la instantánea para sus combos y la API para los demás', async () => {
    const { fuente, cliente } = armar([COMBO_LOCAL], true)

    await fuente.cargar(20)
    expect(cliente.cargable).not.toHaveBeenCalled()
    await fuente.cargar(1)
    expect(cliente.cargable).toHaveBeenCalledWith(1, 4)
  })
})
