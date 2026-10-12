import { describe, expect, it } from 'vitest'

import {
  CANTIDAD_MAXIMA_DE_COMBO,
  VERSION_DEL_ALMACEN,
  articuloParaElCarrito,
  claveDeLinea,
  claveDelCarrito,
  comboParaElCarrito,
  descripcionDelComponente,
  esCantidadDeComboAceptada,
  guardarCarrito,
  leerCarrito,
  reducirCarrito,
  totalDelCarrito,
  type ArticuloParaElCarrito,
  type ItemCombo,
  type ItemDelCarrito,
} from './carrito'
import { articuloPublico, componenteDeCombo, comboPublico } from './datosDePrueba'

class AlmacenEnMemoria {
  readonly datos = new Map<string, string>()
  getItem = (clave: string) => this.datos.get(clave) ?? null
  setItem = (clave: string, valor: string) => void this.datos.set(clave, valor)
  removeItem = (clave: string) => void this.datos.delete(clave)
}

const HOST = 'sur.ways.app'

const gaseosa = componenteDeCombo(10, { nombre: 'Coca Cola 1,5 L', cantidad: 2, imagenVersion: 7 })
const pan = componenteDeCombo(11, { nombre: 'Pan', cantidad: 1 })
const combo = comboParaElCarrito(comboPublico(1, { nombre: 'Combo merienda', precio: 500, componentes: [gaseosa, pan] }))
const otroCombo = comboParaElCarrito(comboPublico(2, { nombre: 'Combo asado', precio: 1200.5 }))
const martillo: ArticuloParaElCarrito = articuloParaElCarrito(articuloPublico(1, { nombre: 'Martillo', precio: 100 }))

function lineaDeCombo(base: typeof combo, cantidad: number, extra: Partial<ItemCombo> = {}): ItemCombo {
  return { ...base, cantidad, ...extra }
}

describe('líneas de combo: el reductor', () => {
  it('agregar un combo nuevo lo suma al final con su cantidad, junto a los artículos', () => {
    const items = reducirCarrito([{ ...martillo, cantidad: 1 }], { tipo: 'agregarCombo', combo, cantidad: 2 })

    expect(items).toEqual([{ ...martillo, cantidad: 1 }, lineaDeCombo(combo, 2)])
  })

  it('agregar de nuevo el mismo combo suma cantidades en una sola línea y no pasa de 99', () => {
    const dos = reducirCarrito([lineaDeCombo(combo, 1)], { tipo: 'agregarCombo', combo, cantidad: 1 })
    const tope = reducirCarrito([lineaDeCombo(combo, 98)], { tipo: 'agregarCombo', combo, cantidad: 5 })

    expect(dos).toEqual([lineaDeCombo(combo, 2)])
    expect(tope).toEqual([lineaDeCombo(combo, CANTIDAD_MAXIMA_DE_COMBO)])
  })

  it('al sumar toma el nombre, el precio y los componentes de la última lectura y deja de estar no disponible', () => {
    const viejo = lineaDeCombo(combo, 1, { nombre: 'Viejo', precio: 1, noDisponible: true })

    const [linea] = reducirCarrito([viejo], { tipo: 'agregarCombo', combo, cantidad: 1 })

    expect(linea).toEqual(lineaDeCombo(combo, 2))
  })

  it('solo se agregan cantidades enteras de 1 a 99', () => {
    for (const cantidad of [0, -1, 1.5, 100, Number.NaN]) {
      expect(reducirCarrito([], { tipo: 'agregarCombo', combo, cantidad })).toEqual([])
    }
    expect(esCantidadDeComboAceptada(99)).toBe(true)
    expect(esCantidadDeComboAceptada(100)).toBe(false)
    expect(esCantidadDeComboAceptada(0)).toBe(false)
  })

  it('cambiar la cantidad la reemplaza si vale; si no vale o la línea no existe, no cambia nada', () => {
    const base = [lineaDeCombo(combo, 2)]

    expect(reducirCarrito(base, { tipo: 'cambiarCantidadCombo', idCombo: 1, cantidad: 5 })).toEqual([lineaDeCombo(combo, 5)])
    expect(reducirCarrito(base, { tipo: 'cambiarCantidadCombo', idCombo: 1, cantidad: 100 })).toBe(base)
    expect(reducirCarrito(base, { tipo: 'cambiarCantidadCombo', idCombo: 1, cantidad: 1.5 })).toBe(base)
    expect(reducirCarrito(base, { tipo: 'cambiarCantidadCombo', idCombo: 9, cantidad: 3 })).toBe(base)
  })

  it('un artículo y un combo con el mismo número son líneas distintas: cada acción toca solo la suya', () => {
    const items: readonly ItemDelCarrito[] = [{ ...martillo, cantidad: 3 }, lineaDeCombo(combo, 2)]

    expect(reducirCarrito(items, { tipo: 'cambiarCantidad', idArticulo: 1, cantidad: 4 })).toEqual([
      { ...martillo, cantidad: 4 },
      lineaDeCombo(combo, 2),
    ])
    expect(reducirCarrito(items, { tipo: 'cambiarCantidadCombo', idCombo: 1, cantidad: 4 })).toEqual([
      { ...martillo, cantidad: 3 },
      lineaDeCombo(combo, 4),
    ])
    expect(reducirCarrito(items, { tipo: 'quitar', idArticulo: 1 })).toEqual([lineaDeCombo(combo, 2)])
    expect(reducirCarrito(items, { tipo: 'quitarCombo', idCombo: 1 })).toEqual([{ ...martillo, cantidad: 3 }])
    expect(claveDeLinea(items[0])).not.toBe(claveDeLinea(items[1]))
  })

  it('las líneas de artículo no se confunden con un combo al agregar el mismo número', () => {
    const items = reducirCarrito([{ ...martillo, cantidad: 3 }], { tipo: 'agregarCombo', combo, cantidad: 1 })

    expect(items).toHaveLength(2)
  })
})

describe('líneas de combo: los importes', () => {
  it('el total de un combo es su precio fijo por la cantidad, redondeado a centavos', () => {
    expect(totalDelCarrito([lineaDeCombo(otroCombo, 3)])).toBe(3601.5)
    expect(totalDelCarrito([lineaDeCombo(combo, 2), { ...martillo, cantidad: 2 }])).toBe(1200)
  })

  it('un combo no disponible no suma al total, pero un artículo sí', () => {
    const items: readonly ItemDelCarrito[] = [lineaDeCombo(combo, 2, { noDisponible: true }), lineaDeCombo(otroCombo, 1), { ...martillo, cantidad: 1 }]

    expect(totalDelCarrito(items)).toBe(1300.5)
  })

  it('el precio original de la oferta es solo para mostrar: el total sale del precio final', () => {
    const enOferta = articuloParaElCarrito(articuloPublico(5, { precio: 80, precioOriginal: 100, oferta: 'Liquidación' }))

    expect(totalDelCarrito([{ ...enOferta, cantidad: 2 }])).toBe(160)
  })

  it('cada componente se describe con su cantidad y su nombre', () => {
    expect(descripcionDelComponente(gaseosa)).toBe('2 × Coca Cola 1,5 L')
    expect(descripcionDelComponente({ cantidad: 1.5, nombre: 'Queso' })).toBe('1,5 × Queso')
  })
})

describe('sincronizar combos', () => {
  it('pone al día nombre, precio y componentes de la línea que sigue en la lista, y conserva la cantidad', () => {
    const viejo = lineaDeCombo(combo, 3, { nombre: 'Viejo', precio: 400, componentes: [pan] })
    const nuevo = comboParaElCarrito(comboPublico(1, { nombre: 'Combo merienda XL', precio: 650, componentes: [gaseosa] }))

    const resultado = reducirCarrito([viejo], { tipo: 'sincronizarCombos', combos: [nuevo] })

    expect(resultado).toEqual([lineaDeCombo(nuevo, 3)])
  })

  it('una línea cuyo combo ya no figura se conserva marcada como no disponible, con su cantidad', () => {
    const resultado = reducirCarrito([lineaDeCombo(combo, 2), { ...martillo, cantidad: 1 }], {
      tipo: 'sincronizarCombos',
      combos: [otroCombo],
    })

    expect(resultado).toEqual([lineaDeCombo(combo, 2, { noDisponible: true }), { ...martillo, cantidad: 1 }])
  })

  it('con una lista vacía todas las líneas de combo quedan no disponibles', () => {
    const resultado = reducirCarrito([lineaDeCombo(combo, 2)], { tipo: 'sincronizarCombos', combos: [] })

    expect(resultado).toEqual([lineaDeCombo(combo, 2, { noDisponible: true })])
  })

  it('si el combo vuelve a figurar, la línea deja de estar no disponible', () => {
    const resultado = reducirCarrito([lineaDeCombo(combo, 2, { noDisponible: true })], { tipo: 'sincronizarCombos', combos: [combo] })

    expect(resultado).toEqual([lineaDeCombo(combo, 2)])
    expect(resultado[0]).not.toHaveProperty('noDisponible')
  })

  it('si nada cambia devuelve el mismo arreglo, y marcar dos veces no cambia nada más', () => {
    const base = [lineaDeCombo(combo, 2), { ...martillo, cantidad: 1 }]
    const marcado = reducirCarrito(base, { tipo: 'sincronizarCombos', combos: [] })

    expect(reducirCarrito(base, { tipo: 'sincronizarCombos', combos: [combo] })).toBe(base)
    expect(reducirCarrito(marcado, { tipo: 'sincronizarCombos', combos: [] })).toBe(marcado)
  })

  it('un cambio solo en la cantidad o la imagen de un componente también se pone al día', () => {
    const base = [lineaDeCombo(combo, 1)]
    const cambiado = comboParaElCarrito(
      comboPublico(1, { nombre: combo.nombre, precio: combo.precio, componentes: [{ ...gaseosa, cantidad: 3 }, pan] }),
    )
    const otraImagen = comboParaElCarrito(
      comboPublico(1, { nombre: combo.nombre, precio: combo.precio, componentes: [{ ...gaseosa, imagenVersion: 8 }, pan] }),
    )

    expect(reducirCarrito(base, { tipo: 'sincronizarCombos', combos: [cambiado] })).toEqual([lineaDeCombo(cambiado, 1)])
    expect(reducirCarrito(base, { tipo: 'sincronizarCombos', combos: [otraImagen] })).toEqual([lineaDeCombo(otraImagen, 1)])
  })

  it('sincronizar artículos no toca los combos, y sincronizar combos no toca los artículos', () => {
    const items: readonly ItemDelCarrito[] = [lineaDeCombo(combo, 2), { ...martillo, cantidad: 1 }]

    expect(reducirCarrito(items, { tipo: 'sincronizar', articulos: [] })).toBe(items)
    expect(reducirCarrito(items, { tipo: 'sincronizarCombos', combos: [combo] })).toBe(items)
  })

  it('un artículo con oferta pone al día su precio original y su oferta, y los pierde cuando la oferta termina', () => {
    const lista = [{ ...martillo, cantidad: 1 }]
    const enOferta = articuloParaElCarrito(articuloPublico(1, { nombre: 'Martillo', precio: 80, precioOriginal: 100, oferta: 'Liquidación' }))

    const conOferta = reducirCarrito(lista, { tipo: 'sincronizar', articulos: [enOferta] })
    const sinOferta = reducirCarrito(conOferta, { tipo: 'sincronizar', articulos: [martillo] })

    expect(conOferta).toEqual([{ ...enOferta, cantidad: 1 }])
    expect(sinOferta).toEqual([{ ...martillo, cantidad: 1 }])
    expect(sinOferta[0]).not.toHaveProperty('precioOriginal')
  })
})

describe('persistencia con combos', () => {
  const lineaVieja = { idArticulo: 1, nombre: 'Martillo', precio: 100, unidadVenta: 'Unidad', cantidad: 2, imagenVersion: null }

  function guardado(items: unknown, version: unknown = VERSION_DEL_ALMACEN): AlmacenEnMemoria {
    const almacen = new AlmacenEnMemoria()
    almacen.setItem(claveDelCarrito(HOST), JSON.stringify({ version, items }))

    return almacen
  }

  it('un carrito de ida y vuelta conserva artículos con oferta y combos, también los no disponibles', () => {
    const items: readonly ItemDelCarrito[] = [
      { ...articuloParaElCarrito(articuloPublico(5, { precio: 80, precioOriginal: 100, oferta: 'Liquidación' })), cantidad: 2 },
      lineaDeCombo(combo, 3),
      lineaDeCombo(otroCombo, 1, { noDisponible: true }),
    ]
    const almacen = new AlmacenEnMemoria()

    guardarCarrito(almacen, HOST, items)

    expect(leerCarrito(almacen, HOST)).toEqual(items)
    expect(JSON.parse(almacen.getItem(claveDelCarrito(HOST)) as string)).toMatchObject({ version: VERSION_DEL_ALMACEN })
  })

  it('un carrito viejo (lista sin tipo ni versión) se migra: cada línea es un artículo', () => {
    const almacen = new AlmacenEnMemoria()
    almacen.setItem(claveDelCarrito(HOST), JSON.stringify([lineaVieja, { ...lineaVieja, idArticulo: 2, unidadVenta: 'Peso', cantidad: 1.5 }]))

    expect(leerCarrito(almacen, HOST)).toEqual([
      { ...lineaVieja, tipo: 'articulo' },
      { ...lineaVieja, tipo: 'articulo', idArticulo: 2, unidadVenta: 'Peso', cantidad: 1.5 },
    ])
  })

  it('al guardar un carrito migrado queda en el formato nuevo', () => {
    const almacen = new AlmacenEnMemoria()
    almacen.setItem(claveDelCarrito(HOST), JSON.stringify([lineaVieja]))

    guardarCarrito(almacen, HOST, leerCarrito(almacen, HOST))

    expect(JSON.parse(almacen.getItem(claveDelCarrito(HOST)) as string)).toEqual({
      version: VERSION_DEL_ALMACEN,
      items: [{ ...lineaVieja, tipo: 'articulo' }],
    })
  })

  it('en el formato nuevo una línea sin tipo, o con un tipo desconocido, se descarta', () => {
    const almacen = guardado([lineaVieja, { ...lineaVieja, tipo: 'regalo' }, { ...lineaVieja, tipo: 'articulo', idArticulo: 3 }])

    expect(leerCarrito(almacen, HOST).map(claveDeLinea)).toEqual(['a:3'])
  })

  it('una versión que no es la actual se descarta entera', () => {
    expect(leerCarrito(guardado([{ ...lineaVieja, tipo: 'articulo' }], 3), HOST)).toEqual([])
    expect(leerCarrito(guardado([{ ...lineaVieja, tipo: 'articulo' }], null), HOST)).toEqual([])
    expect(leerCarrito(guardado('no es una lista'), HOST)).toEqual([])
  })

  describe('líneas de combo mal formadas', () => {
    const buena = lineaDeCombo(combo, 2)
    const malas: [string, unknown][] = [
      ['cantidad cero', { ...buena, cantidad: 0 }],
      ['cantidad con decimales', { ...buena, cantidad: 1.5 }],
      ['cantidad sobre el máximo', { ...buena, cantidad: 100 }],
      ['id que no es entero', { ...buena, idCombo: 'uno' }],
      ['nombre que no es texto', { ...buena, nombre: 7 }],
      ['precio negativo', { ...buena, precio: -1 }],
      ['precio que no es número', { ...buena, precio: '500' }],
      ['sin componentes', { ...buena, componentes: [] }],
      ['componentes que no son una lista', { ...buena, componentes: 'pan' }],
      ['componente con cantidad cero', { ...buena, componentes: [{ ...gaseosa, cantidad: 0 }] }],
      ['componente sin nombre', { ...buena, componentes: [{ ...gaseosa, nombre: undefined }] }],
      ['componente con imagen inválida', { ...buena, componentes: [{ ...gaseosa, imagenVersion: 'x' }] }],
      ['componente que no es un objeto', { ...buena, componentes: [null] }],
      ['no disponible que no es booleano', { ...buena, noDisponible: 'si' }],
    ]

    it.each(malas)('se descarta una línea con %s y se conservan las buenas', (_, mala) => {
      const almacen = guardado([mala, lineaDeCombo(otroCombo, 1), { ...martillo, cantidad: 1 }])

      expect(leerCarrito(almacen, HOST).map(claveDeLinea)).toEqual(['c:2', 'a:1'])
    })

    it('un combo repetido se queda con la primera línea; un artículo y un combo con el mismo número conviven', () => {
      const almacen = guardado([lineaDeCombo(combo, 2), lineaDeCombo(combo, 5), { ...martillo, cantidad: 1 }])

      expect(leerCarrito(almacen, HOST)).toEqual([lineaDeCombo(combo, 2), { ...martillo, cantidad: 1 }])
    })
  })

  it('una oferta mal formada en un artículo guardado lo descarta', () => {
    const linea = { ...lineaVieja, tipo: 'articulo' }
    const almacen = guardado([
      { ...linea, precioOriginal: -5 },
      { ...linea, idArticulo: 2, oferta: 3 },
      { ...linea, idArticulo: 3, precioOriginal: 150, oferta: 'Liquidación' },
    ])

    expect(leerCarrito(almacen, HOST).map(claveDeLinea)).toEqual(['a:3'])
  })
})
