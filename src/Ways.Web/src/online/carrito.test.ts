import { describe, expect, it, vi } from 'vitest'

import {
  CANTIDAD_MAXIMA,
  claveDelCarrito,
  esCantidadAceptada,
  formatearCantidad,
  formatearPrecio,
  formatearPrecioUnitario,
  guardarCarrito,
  leerCarrito,
  normalizarCantidad,
  parsearCantidad,
  pasoDelBoton,
  reducirCarrito,
  totalDeLinea,
  totalDelCarrito,
  type ArticuloParaElCarrito,
  type ItemDelCarrito,
} from './carrito'

const martillo: ArticuloParaElCarrito = {
  idArticulo: 1,
  nombre: 'Martillo',
  precio: 1500.5,
  unidadVenta: 'Unidad',
  imagenVersion: 3,
}

const clavos: ArticuloParaElCarrito = {
  idArticulo: 2,
  nombre: 'Clavos',
  precio: 2000,
  unidadVenta: 'Peso',
  imagenVersion: null,
}

function item(base: ArticuloParaElCarrito, cantidad: number): ItemDelCarrito {
  return { ...base, cantidad }
}

class AlmacenEnMemoria {
  readonly datos = new Map<string, string>()
  getItem = (clave: string) => this.datos.get(clave) ?? null
  setItem = (clave: string, valor: string) => void this.datos.set(clave, valor)
  removeItem = (clave: string) => void this.datos.delete(clave)
}

describe('cantidades por unidad de venta', () => {
  it('Unidad acepta solo enteros positivos', () => {
    expect(esCantidadAceptada('Unidad', 1)).toBe(true)
    expect(esCantidadAceptada('Unidad', 12)).toBe(true)
    expect(esCantidadAceptada('Unidad', 1.5)).toBe(false)
    expect(esCantidadAceptada('Unidad', 0.001)).toBe(false)
    expect(esCantidadAceptada('Unidad', 0)).toBe(false)
    expect(esCantidadAceptada('Unidad', -1)).toBe(false)
  })

  it('Peso acepta hasta tres decimales y rechaza el cuarto', () => {
    expect(esCantidadAceptada('Peso', 0.001)).toBe(true)
    expect(esCantidadAceptada('Peso', 1.25)).toBe(true)
    expect(esCantidadAceptada('Peso', 0.0004)).toBe(false)
    expect(esCantidadAceptada('Peso', 1.0005)).toBe(false)
    expect(esCantidadAceptada('Peso', 0)).toBe(false)
  })

  it('el máximo por línea es 999: 999 entra y 999,001 o 1000 no', () => {
    expect(esCantidadAceptada('Unidad', CANTIDAD_MAXIMA)).toBe(true)
    expect(esCantidadAceptada('Unidad', CANTIDAD_MAXIMA + 1)).toBe(false)
    expect(esCantidadAceptada('Peso', 999.001)).toBe(false)
  })

  it('el texto tipeado acepta coma o punto y rechaza lo que no es una cantidad', () => {
    expect(parsearCantidad('Peso', '1,5')).toBe(1.5)
    expect(parsearCantidad('Peso', ' 0.250 ')).toBe(0.25)
    expect(parsearCantidad('Unidad', '3')).toBe(3)
    expect(parsearCantidad('Unidad', '2,5')).toBeNull()
    expect(parsearCantidad('Peso', '1,2345')).toBeNull()
    expect(parsearCantidad('Peso', '1e2')).toBeNull()
    expect(parsearCantidad('Peso', '-1')).toBeNull()
    expect(parsearCantidad('Peso', '')).toBeNull()
    expect(parsearCantidad('Unidad', '1000')).toBeNull()
  })

  it('el paso de los botones es uno para Unidad y medio kilo para Peso', () => {
    expect(pasoDelBoton('Unidad')).toBe(1)
    expect(pasoDelBoton('Peso')).toBe(0.5)
  })

  it('normalizar quita el ruido binario de la suma y topa en el máximo', () => {
    expect(normalizarCantidad(0.1 + 0.2)).toBe(0.3)
    expect(normalizarCantidad(998 + 5)).toBe(CANTIDAD_MAXIMA)
  })

  it('la cantidad se muestra con coma decimal', () => {
    expect(formatearCantidad(2)).toBe('2')
    expect(formatearCantidad(1.25)).toBe('1,25')
  })
})

describe('importes', () => {
  it('formatea el precio como $ 1.234,56', () => {
    expect(formatearPrecio(1234.56)).toBe('$ 1.234,56')
    expect(formatearPrecio(0)).toBe('$ 0,00')
  })

  it('el precio de un artículo por peso lleva "por kg"', () => {
    expect(formatearPrecioUnitario(2000, 'Peso')).toBe('$ 2.000,00 por kg')
    expect(formatearPrecioUnitario(2000, 'Unidad')).toBe('$ 2.000,00')
  })

  it('el total de la línea es precio × cantidad redondeado a centavos con decimal exacto', () => {
    expect(totalDeLinea(item(martillo, 3))).toBe(4501.5)
    expect(totalDeLinea({ precio: 0.7, cantidad: 1.15 })).toBe(0.81)
    expect(totalDeLinea(item(clavos, 0.25))).toBe(500)
  })

  it('el total del carrito suma las líneas ya redondeadas', () => {
    expect(totalDelCarrito([])).toBe(0)
    expect(totalDelCarrito([item(martillo, 2), item(clavos, 1.5)])).toBe(6001)
    expect(totalDelCarrito([{ ...martillo, precio: 0.1, cantidad: 1 }, { ...clavos, precio: 0.2, cantidad: 1 }])).toBe(0.3)
  })
})

describe('reducirCarrito', () => {
  it('agregar un artículo nuevo lo suma al final con su cantidad', () => {
    const items = reducirCarrito([item(martillo, 1)], { tipo: 'agregar', articulo: clavos, cantidad: 0.5 })

    expect(items).toEqual([item(martillo, 1), item(clavos, 0.5)])
  })

  it('agregar de nuevo el mismo artículo suma cantidades en una sola línea', () => {
    const una = reducirCarrito([], { tipo: 'agregar', articulo: martillo, cantidad: 1 })
    const dos = reducirCarrito(una, { tipo: 'agregar', articulo: martillo, cantidad: 2 })

    expect(dos).toEqual([item(martillo, 3)])
  })

  it('al sumar toma el precio y el nombre de la última lectura del catálogo', () => {
    const items = reducirCarrito([item(martillo, 1)], {
      tipo: 'agregar',
      articulo: { ...martillo, nombre: 'Martillo carpintero', precio: 1800 },
      cantidad: 1,
    })

    expect(items).toEqual([item({ ...martillo, nombre: 'Martillo carpintero', precio: 1800 }, 2)])
  })

  it('la suma de decimales no arrastra ruido y no pasa de 999', () => {
    const base = [item(clavos, 0.1)]

    expect(reducirCarrito(base, { tipo: 'agregar', articulo: clavos, cantidad: 0.2 })).toEqual([item(clavos, 0.3)])
    expect(reducirCarrito([item(martillo, 998)], { tipo: 'agregar', articulo: martillo, cantidad: 5 })).toEqual([
      item(martillo, CANTIDAD_MAXIMA),
    ])
  })

  it('agregar una cantidad que la unidad no admite deja el carrito igual', () => {
    const items = [item(martillo, 1)]

    expect(reducirCarrito(items, { tipo: 'agregar', articulo: martillo, cantidad: 1.5 })).toBe(items)
    expect(reducirCarrito(items, { tipo: 'agregar', articulo: clavos, cantidad: 0 })).toBe(items)
  })

  it('cambiar la cantidad la reemplaza si es válida para la unidad de esa línea', () => {
    const items = [item(martillo, 1), item(clavos, 1)]

    expect(reducirCarrito(items, { tipo: 'cambiarCantidad', idArticulo: 2, cantidad: 0.75 })).toEqual([
      item(martillo, 1),
      item(clavos, 0.75),
    ])
    expect(reducirCarrito(items, { tipo: 'cambiarCantidad', idArticulo: 1, cantidad: 4 })[0].cantidad).toBe(4)
  })

  it('cambiar a una cantidad inválida, a otra unidad o de una línea que no existe no cambia nada', () => {
    const items = [item(martillo, 1), item(clavos, 1)]

    expect(reducirCarrito(items, { tipo: 'cambiarCantidad', idArticulo: 1, cantidad: 1.5 })).toBe(items)
    expect(reducirCarrito(items, { tipo: 'cambiarCantidad', idArticulo: 1, cantidad: 1000 })).toBe(items)
    expect(reducirCarrito(items, { tipo: 'cambiarCantidad', idArticulo: 2, cantidad: 0 })).toBe(items)
    expect(reducirCarrito(items, { tipo: 'cambiarCantidad', idArticulo: 99, cantidad: 2 })).toBe(items)
  })

  it('quitar saca solo esa línea y vaciar deja el carrito sin nada', () => {
    const items = [item(martillo, 1), item(clavos, 1)]

    expect(reducirCarrito(items, { tipo: 'quitar', idArticulo: 1 })).toEqual([item(clavos, 1)])
    expect(reducirCarrito(items, { tipo: 'vaciar' })).toEqual([])
  })

  it('no muta el arreglo que recibe', () => {
    const items = Object.freeze([Object.freeze(item(martillo, 1))])

    expect(() => reducirCarrito(items, { tipo: 'agregar', articulo: martillo, cantidad: 1 })).not.toThrow()
    expect(() => reducirCarrito(items, { tipo: 'cambiarCantidad', idArticulo: 1, cantidad: 3 })).not.toThrow()
    expect(() => reducirCarrito(items, { tipo: 'quitar', idArticulo: 1 })).not.toThrow()
  })
})

describe('persistencia del carrito', () => {
  it('la clave incluye el host, así dos comercios en un navegador no comparten carrito', () => {
    expect(claveDelCarrito('sur.ways.app')).not.toBe(claveDelCarrito('norte.ways.app'))
    expect(claveDelCarrito('sur.ways.app')).toContain('sur.ways.app')
  })

  it('lo guardado se lee igual y cada host ve el suyo', () => {
    const almacen = new AlmacenEnMemoria()
    guardarCarrito(almacen, 'sur.ways.app', [item(martillo, 2), item(clavos, 0.5)])
    guardarCarrito(almacen, 'norte.ways.app', [item(clavos, 3)])

    expect(leerCarrito(almacen, 'sur.ways.app')).toEqual([item(martillo, 2), item(clavos, 0.5)])
    expect(leerCarrito(almacen, 'norte.ways.app')).toEqual([item(clavos, 3)])
    expect(leerCarrito(almacen, 'otro.ways.app')).toEqual([])
  })

  it('un carrito vacío borra la clave en lugar de guardar un arreglo vacío', () => {
    const almacen = new AlmacenEnMemoria()
    guardarCarrito(almacen, 'sur.ways.app', [item(martillo, 1)])

    guardarCarrito(almacen, 'sur.ways.app', [])

    expect(almacen.datos.size).toBe(0)
  })

  it('un almacén corrupto o con otra forma se descarta sin romper', () => {
    const almacen = new AlmacenEnMemoria()
    const clave = claveDelCarrito('sur.ways.app')

    for (const crudo of ['{no es json', '{"a":1}', 'null', '"texto"', '[1,2]']) {
      almacen.datos.set(clave, crudo)
      expect(leerCarrito(almacen, 'sur.ways.app')).toEqual([])
    }
  })

  it('conserva las líneas buenas y descarta las inválidas o repetidas', () => {
    const almacen = new AlmacenEnMemoria()
    almacen.datos.set(
      claveDelCarrito('sur.ways.app'),
      JSON.stringify([
        item(martillo, 2),
        { ...item(clavos, 1.5), unidadVenta: 'Docena' },
        item(clavos, 0),
        { ...item(martillo, 1.5), idArticulo: 7 },
        item(martillo, 5),
        { ...item(clavos, 1), precio: -3 },
        { ...item(clavos, 1), nombre: 12 },
        { ...item(clavos, 1), imagenVersion: 'x' },
        item(clavos, 1000),
      ]),
    )

    expect(leerCarrito(almacen, 'sur.ways.app')).toEqual([item(martillo, 2)])
  })

  it('sin almacén o con un almacén que falla, ni lee ni guarda pero no rompe', () => {
    const roto = {
      getItem: vi.fn(() => {
        throw new Error('bloqueado')
      }),
      setItem: vi.fn(() => {
        throw new Error('lleno')
      }),
      removeItem: vi.fn(() => {
        throw new Error('bloqueado')
      }),
    }

    expect(leerCarrito(null, 'sur.ways.app')).toEqual([])
    expect(leerCarrito(roto, 'sur.ways.app')).toEqual([])
    expect(() => guardarCarrito(roto, 'sur.ways.app', [item(martillo, 1)])).not.toThrow()
    expect(() => guardarCarrito(roto, 'sur.ways.app', [])).not.toThrow()
    expect(() => guardarCarrito(null, 'sur.ways.app', [item(martillo, 1)])).not.toThrow()
  })
})
