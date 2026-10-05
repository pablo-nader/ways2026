import { describe, expect, it } from 'vitest'
import { idLineaDestinoDeEscaneo, migrarLineasDeBorrador, nuevoIdLinea, reducirCarrito } from './carrito'
import type { LineaCarrito } from './carrito'

/** `idLinea` por defecto `l<idArticulo>`: en los casos de una línea por artículo cada línea queda
 * identificada sin repetir el id en cada fixture. */
function lineaFixture(sobrescribir: Partial<LineaCarrito> = {}): LineaCarrito {
  return {
    idLinea: `l${sobrescribir.idArticulo ?? 1}`,
    idArticulo: 1,
    codigoInterno: 'A0001',
    nombre: 'Coca Cola 1L',
    codigoBarra: '7790001234567',
    acumulaEnVenta: true,
    cantidad: 1,
    ...sobrescribir,
  }
}

describe('reducirCarrito — escanear', () => {
  it('agrega una línea nueva cuando el artículo no está en el carrito', () => {
    const resultado = reducirCarrito([], {
      tipo: 'escanear',
      linea: { idArticulo: 1, codigoInterno: 'A0001', nombre: 'Coca Cola 1L', codigoBarra: '7790001234567', acumulaEnVenta: true },
      cantidad: 1,
      idLinea: 'l1',
    })

    expect(resultado).toEqual([lineaFixture()])
  })

  it('re-escanear el mismo artículo suma la cantidad en la línea existente, no duplica', () => {
    const carritoConDos = [lineaFixture({ cantidad: 2 })]

    const resultado = reducirCarrito(carritoConDos, {
      tipo: 'escanear',
      linea: { idArticulo: 1, codigoInterno: 'A0001', nombre: 'Coca Cola 1L', codigoBarra: '7790001234567', acumulaEnVenta: true },
      cantidad: 1,
      idLinea: 'l1',
    })

    expect(resultado).toHaveLength(1)
    expect(resultado[0].cantidad).toBe(3)
  })

  it('la línea nueva conserva la unidad de venta del escaneo', () => {
    const resultado = reducirCarrito([], {
      tipo: 'escanear',
      linea: { idArticulo: 1, codigoInterno: 'A0001', nombre: 'Coca Cola 1L', codigoBarra: null, acumulaEnVenta: true, unidadVenta: 'Unidad' },
      cantidad: 1,
      idLinea: 'l1',
    })

    expect(resultado[0].unidadVenta).toBe('Unidad')
  })

  it('re-escanear completa la unidad de una línea que no la tenía (borrador anterior) y no pisa la que ya tiene', () => {
    const articulo = { idArticulo: 1, codigoInterno: 'A0001', nombre: 'Coca Cola 1L', codigoBarra: null, acumulaEnVenta: true, unidadVenta: 'Unidad' as const }
    const sinUnidad = reducirCarrito([lineaFixture()], { tipo: 'escanear', linea: articulo, cantidad: 1, idLinea: 'nueva' })
    expect(sinUnidad[0].unidadVenta).toBe('Unidad')

    const conUnidad = reducirCarrito([lineaFixture({ unidadVenta: 'Peso' })], { tipo: 'escanear', linea: articulo, cantidad: 1, idLinea: 'nueva' })
    expect(conUnidad[0].unidadVenta).toBe('Peso')
  })

  it('re-escanear con un escaneo sin unidad no inventa una unidad en la línea existente', () => {
    const resultado = reducirCarrito([lineaFixture()], {
      tipo: 'escanear',
      linea: { idArticulo: 1, codigoInterno: 'A0001', nombre: 'Coca Cola 1L', codigoBarra: null, acumulaEnVenta: true },
      cantidad: 1,
      idLinea: 'nueva',
    })

    expect('unidadVenta' in resultado[0]).toBe(false)
  })

  it('con varias líneas del mismo artículo, la unidad se completa solo en la línea destino (la última) y las demás no se tocan', () => {
    const lineas = [
      lineaFixture({ idLinea: 'a', cantidad: 1.5, acumulaEnVenta: false }),
      lineaFixture({ idLinea: 'b', cantidad: 2, acumulaEnVenta: false }),
    ]

    const resultado = reducirCarrito(lineas, {
      tipo: 'escanear',
      linea: { idArticulo: 1, codigoInterno: 'A0001', nombre: 'Coca Cola 1L', codigoBarra: null, acumulaEnVenta: true, unidadVenta: 'Unidad' },
      cantidad: 1,
      idLinea: 'nueva',
    })

    expect(resultado).toHaveLength(2)
    expect('unidadVenta' in resultado[0]).toBe(false)
    expect(resultado[1]).toMatchObject({ idLinea: 'b', cantidad: 3, unidadVenta: 'Unidad' })
  })

  it('un artículo que no acumula abre una línea nueva con su unidad y no toca la unidad de las existentes', () => {
    const resultado = reducirCarrito([lineaFixture({ idLinea: 'a', acumulaEnVenta: false })], {
      tipo: 'escanear',
      linea: { idArticulo: 1, codigoInterno: 'A0001', nombre: 'Coca Cola 1L', codigoBarra: null, acumulaEnVenta: false, unidadVenta: 'Unidad' },
      cantidad: 1,
      idLinea: 'b',
    })

    expect(resultado).toHaveLength(2)
    expect('unidadVenta' in resultado[0]).toBe(false)
    expect(resultado[1]).toMatchObject({ idLinea: 'b', unidadVenta: 'Unidad' })
  })

  it('escanear el prefijo N*codigo pasa la cantidad indicada tal cual, sin transformarla', () => {
    const resultado = reducirCarrito([], {
      tipo: 'escanear',
      linea: { idArticulo: 2, codigoInterno: 'A0002', nombre: 'Agua 500ml', codigoBarra: '7790009876543', acumulaEnVenta: true },
      cantidad: 3,
      idLinea: 'l2',
    })

    expect(resultado[0].cantidad).toBe(3)
  })

  it('escanear un segundo artículo agrega una línea nueva sin tocar la primera', () => {
    const carritoConUno = [lineaFixture()]

    const resultado = reducirCarrito(carritoConUno, {
      tipo: 'escanear',
      linea: { idArticulo: 2, codigoInterno: 'A0002', nombre: 'Agua 500ml', codigoBarra: '7790009876543', acumulaEnVenta: true },
      cantidad: 1,
      idLinea: 'l2',
    })

    expect(resultado).toHaveLength(2)
    expect(resultado[0]).toEqual(lineaFixture())
    expect(resultado[1].idArticulo).toBe(2)
  })
})

describe('reducirCarrito — editarCantidad', () => {
  it('actualiza la cantidad de la línea indicada sin tocar el resto', () => {
    const carrito = [lineaFixture({ idArticulo: 1, cantidad: 1 }), lineaFixture({ idArticulo: 2, cantidad: 5 })]

    const resultado = reducirCarrito(carrito, { tipo: 'editarCantidad', idLinea: 'l1', cantidad: 7 })

    expect(resultado.find((l) => l.idArticulo === 1)?.cantidad).toBe(7)
    expect(resultado.find((l) => l.idArticulo === 2)?.cantidad).toBe(5)
  })

  it('permite cantidad negativa — convención de signo para líneas NCX (design decisión 4)', () => {
    const carrito = [lineaFixture({ cantidad: 2 })]

    const resultado = reducirCarrito(carrito, { tipo: 'editarCantidad', idLinea: 'l1', cantidad: -2 })

    expect(resultado[0].cantidad).toBe(-2)
  })

  it('editar la cantidad de un idLinea inexistente no agrega ni modifica ninguna línea', () => {
    const carrito = [lineaFixture()]

    const resultado = reducirCarrito(carrito, { tipo: 'editarCantidad', idLinea: 'l999', cantidad: 5 })

    expect(resultado).toEqual(carrito)
  })
})

describe('reducirCarrito — quitarLinea', () => {
  it('remueve solo la línea indicada', () => {
    const carrito = [lineaFixture({ idArticulo: 1 }), lineaFixture({ idArticulo: 2 })]

    const resultado = reducirCarrito(carrito, { tipo: 'quitarLinea', idLinea: 'l1' })

    expect(resultado).toEqual([lineaFixture({ idArticulo: 2 })])
  })

  it('quitar un idLinea inexistente deja el carrito sin cambios', () => {
    const carrito = [lineaFixture()]

    const resultado = reducirCarrito(carrito, { tipo: 'quitarLinea', idLinea: 'l999' })

    expect(resultado).toEqual(carrito)
  })
})

describe('reducirCarrito — ajuste manual', () => {
  it('fijarAjusteManual pone el porcentaje en la línea indicada y no toca el resto', () => {
    const carrito = [lineaFixture({ idArticulo: 1 }), lineaFixture({ idArticulo: 2 })]

    const resultado = reducirCarrito(carrito, { tipo: 'fijarAjusteManual', idLinea: 'l2', porcentaje: -10 })

    expect(resultado[0]).toEqual(lineaFixture({ idArticulo: 1 }))
    expect(resultado[1].ajusteManualPorcentaje).toBe(-10)
    expect(resultado[0]).not.toHaveProperty('ajusteManualPorcentaje')
  })

  it('fijarAjusteManual sobre una línea que ya tiene ajuste lo reemplaza (también cambiando de signo)', () => {
    const carrito = [lineaFixture({ ajusteManualPorcentaje: -10 })]

    const resultado = reducirCarrito(carrito, { tipo: 'fijarAjusteManual', idLinea: 'l1', porcentaje: 15 })

    expect(resultado[0].ajusteManualPorcentaje).toBe(15)
  })

  it('fijarAjusteManual sobre un idLinea inexistente no agrega ni modifica ninguna línea', () => {
    const carrito = [lineaFixture()]

    expect(reducirCarrito(carrito, { tipo: 'fijarAjusteManual', idLinea: 'l999', porcentaje: 5 })).toEqual(carrito)
  })

  it('quitarAjusteManual elimina el campo de esa línea (no lo deja en null) y no toca el resto', () => {
    const carrito = [lineaFixture({ idArticulo: 1, ajusteManualPorcentaje: -10 }), lineaFixture({ idArticulo: 2, ajusteManualPorcentaje: 15 })]

    const resultado = reducirCarrito(carrito, { tipo: 'quitarAjusteManual', idLinea: 'l1' })

    expect(resultado[0]).not.toHaveProperty('ajusteManualPorcentaje')
    expect(resultado[0]).toEqual(lineaFixture({ idArticulo: 1 }))
    expect(resultado[1].ajusteManualPorcentaje).toBe(15)
  })

  it('quitarAjusteManual sobre una línea sin ajuste, o un idLinea inexistente, deja el carrito igual', () => {
    const carrito = [lineaFixture()]

    expect(reducirCarrito(carrito, { tipo: 'quitarAjusteManual', idLinea: 'l1' })).toEqual(carrito)
    expect(reducirCarrito(carrito, { tipo: 'quitarAjusteManual', idLinea: 'l999' })).toEqual(carrito)
  })

  it('las dos acciones devuelven un arreglo y una línea nuevos, y dejan el carrito anterior intacto', () => {
    const carrito = [lineaFixture({ ajusteManualPorcentaje: -10 })]
    const lineaOriginal = carrito[0]

    const fijado = reducirCarrito(carrito, { tipo: 'fijarAjusteManual', idLinea: 'l1', porcentaje: 20 })
    const quitado = reducirCarrito(carrito, { tipo: 'quitarAjusteManual', idLinea: 'l1' })

    expect(fijado).not.toBe(carrito)
    expect(quitado).not.toBe(carrito)
    expect(fijado[0]).not.toBe(lineaOriginal)
    expect(quitado[0]).not.toBe(lineaOriginal)
    expect(fijado[0].ajusteManualPorcentaje).toBe(20)
    expect(quitado[0]).not.toHaveProperty('ajusteManualPorcentaje')
    expect(carrito).toHaveLength(1)
    expect(carrito[0]).toBe(lineaOriginal)
    expect(lineaOriginal).toEqual(lineaFixture({ ajusteManualPorcentaje: -10 }))
  })

  it('re-escanear el mismo artículo suma la cantidad y conserva el ajuste', () => {
    const carrito = [lineaFixture({ cantidad: 2, ajusteManualPorcentaje: -10 })]

    const resultado = reducirCarrito(carrito, {
      tipo: 'escanear',
      linea: { idArticulo: 1, codigoInterno: 'A0001', nombre: 'Coca Cola 1L', codigoBarra: '7790001234567', acumulaEnVenta: true },
      cantidad: 1,
      idLinea: 'l1',
    })

    expect(resultado).toHaveLength(1)
    expect(resultado[0]).toMatchObject({ cantidad: 3, ajusteManualPorcentaje: -10 })
  })

  it('escanear un artículo nuevo lo agrega sin ajuste', () => {
    const carrito = [lineaFixture({ idArticulo: 1, ajusteManualPorcentaje: -10 })]

    const resultado = reducirCarrito(carrito, {
      tipo: 'escanear',
      linea: { idArticulo: 2, codigoInterno: 'A0002', nombre: 'Agua 500ml', codigoBarra: null, acumulaEnVenta: true },
      cantidad: 1,
      idLinea: 'l2',
    })

    expect(resultado[1]).not.toHaveProperty('ajusteManualPorcentaje')
  })

  it('editar la cantidad conserva el ajuste', () => {
    const carrito = [lineaFixture({ cantidad: 1, ajusteManualPorcentaje: 15 })]

    const resultado = reducirCarrito(carrito, { tipo: 'editarCantidad', idLinea: 'l1', cantidad: 4 })

    expect(resultado[0]).toMatchObject({ cantidad: 4, ajusteManualPorcentaje: 15 })
  })

  it('quitar la línea se lleva su ajuste: volver a agregar el artículo arranca sin ajuste', () => {
    const conAjuste = [lineaFixture({ ajusteManualPorcentaje: -10 })]

    const sinLinea = reducirCarrito(conAjuste, { tipo: 'quitarLinea', idLinea: 'l1' })
    const reagregada = reducirCarrito(sinLinea, {
      tipo: 'escanear',
      linea: { idArticulo: 1, codigoInterno: 'A0001', nombre: 'Coca Cola 1L', codigoBarra: '7790001234567', acumulaEnVenta: true },
      cantidad: 1,
      idLinea: 'l1',
    })

    expect(sinLinea).toEqual([])
    expect(reagregada[0]).not.toHaveProperty('ajusteManualPorcentaje')
  })

  it('vaciar descarta también los ajustes', () => {
    const carrito = [lineaFixture({ ajusteManualPorcentaje: -10 })]

    expect(reducirCarrito(carrito, { tipo: 'vaciar' })).toEqual([])
  })
})

describe('reducirCarrito — vaciar', () => {
  it('deja el carrito vacío sin importar cuántas líneas tenía', () => {
    const carrito = [lineaFixture({ idArticulo: 1 }), lineaFixture({ idArticulo: 2 })]

    expect(reducirCarrito(carrito, { tipo: 'vaciar' })).toEqual([])
  })

  it('vaciar un carrito ya vacío sigue devolviendo un arreglo vacío', () => {
    expect(reducirCarrito([], { tipo: 'vaciar' })).toEqual([])
  })
})

describe('reducirCarrito — artículo que acumula vs. que no acumula', () => {
  const coca = { idArticulo: 1, codigoInterno: 'A0001', nombre: 'Coca Cola 1L', codigoBarra: '7790001234567' }

  it('acumulaEnVenta: true suma sobre la línea existente e ignora el idLinea nuevo de la acción', () => {
    const carrito = [lineaFixture({ idLinea: 'existente', cantidad: 2 })]

    const resultado = reducirCarrito(carrito, { tipo: 'escanear', linea: { ...coca, acumulaEnVenta: true }, cantidad: 3, idLinea: 'nueva' })

    expect(resultado).toEqual([lineaFixture({ idLinea: 'existente', cantidad: 5 })])
  })

  it('acumulaEnVenta: false agrega una línea nueva con el idLinea de la acción, sin tocar la existente', () => {
    const existente = lineaFixture({ idLinea: 'existente', acumulaEnVenta: false, cantidad: 2, ajusteManualPorcentaje: -10 })

    const resultado = reducirCarrito([existente], { tipo: 'escanear', linea: { ...coca, acumulaEnVenta: false }, cantidad: 1, idLinea: 'nueva' })

    expect(resultado).toEqual([existente, lineaFixture({ idLinea: 'nueva', acumulaEnVenta: false, cantidad: 1 })])
  })

  it('escanear tres veces un artículo que no acumula deja tres líneas', () => {
    const escaneo = (idLinea: string) => ({ tipo: 'escanear' as const, linea: { ...coca, acumulaEnVenta: false }, cantidad: 1, idLinea })

    const resultado = [escaneo('a'), escaneo('b'), escaneo('c')].reduce(reducirCarrito, [] as LineaCarrito[])

    expect(resultado.map((l) => [l.idLinea, l.cantidad])).toEqual([
      ['a', 1],
      ['b', 1],
      ['c', 1],
    ])
  })

  it('un artículo que acumula suma sobre la ÚLTIMA de sus líneas, no sobre la primera', () => {
    const carrito = [
      lineaFixture({ idLinea: 'primera', cantidad: 1 }),
      lineaFixture({ idLinea: 'otro', idArticulo: 2, cantidad: 7 }),
      lineaFixture({ idLinea: 'ultima', cantidad: 4 }),
    ]

    const resultado = reducirCarrito(carrito, { tipo: 'escanear', linea: { ...coca, acumulaEnVenta: true }, cantidad: 2, idLinea: 'nueva' })

    expect(resultado.map((l) => [l.idLinea, l.cantidad])).toEqual([
      ['primera', 1],
      ['otro', 7],
      ['ultima', 6],
    ])
  })

  it('decide por el flag del escaneo entrante, no por el de la línea ya cargada', () => {
    const carrito = [lineaFixture({ idLinea: 'vieja', acumulaEnVenta: false, cantidad: 1 })]

    const resultado = reducirCarrito(carrito, { tipo: 'escanear', linea: { ...coca, acumulaEnVenta: true }, cantidad: 1, idLinea: 'nueva' })

    expect(resultado).toHaveLength(1)
    expect(resultado[0]).toMatchObject({ idLinea: 'vieja', cantidad: 2 })
  })
})

describe('reducirCarrito — dos líneas del mismo artículo', () => {
  const dosLineas = () => [
    lineaFixture({ idLinea: 'a', acumulaEnVenta: false, cantidad: 1 }),
    lineaFixture({ idLinea: 'b', acumulaEnVenta: false, cantidad: 2 }),
  ]

  it('editarCantidad cambia solo la línea indicada', () => {
    const resultado = reducirCarrito(dosLineas(), { tipo: 'editarCantidad', idLinea: 'b', cantidad: 9 })

    expect(resultado.map((l) => [l.idLinea, l.cantidad])).toEqual([
      ['a', 1],
      ['b', 9],
    ])
  })

  it('quitarLinea quita solo la línea indicada', () => {
    const resultado = reducirCarrito(dosLineas(), { tipo: 'quitarLinea', idLinea: 'a' })

    expect(resultado).toEqual([dosLineas()[1]])
  })

  it('fijarAjusteManual pone el ajuste solo en la línea indicada', () => {
    const resultado = reducirCarrito(dosLineas(), { tipo: 'fijarAjusteManual', idLinea: 'a', porcentaje: -15 })

    expect(resultado[0].ajusteManualPorcentaje).toBe(-15)
    expect(resultado[1]).toEqual(dosLineas()[1])
  })

  it('quitarAjusteManual quita el ajuste solo de la línea indicada', () => {
    const conAjustes = [
      lineaFixture({ idLinea: 'a', acumulaEnVenta: false, ajusteManualPorcentaje: -10 }),
      lineaFixture({ idLinea: 'b', acumulaEnVenta: false, ajusteManualPorcentaje: 20 }),
    ]

    const resultado = reducirCarrito(conAjustes, { tipo: 'quitarAjusteManual', idLinea: 'b' })

    expect(resultado[0].ajusteManualPorcentaje).toBe(-10)
    expect(resultado[1]).not.toHaveProperty('ajusteManualPorcentaje')
  })
})

describe('idLineaDestinoDeEscaneo', () => {
  it('null para un artículo que no acumula, aunque ya esté en el carrito', () => {
    expect(idLineaDestinoDeEscaneo([lineaFixture({ idLinea: 'x' })], { idArticulo: 1, acumulaEnVenta: false })).toBeNull()
  })

  it('null para un artículo que acumula y todavía no está en el carrito', () => {
    expect(idLineaDestinoDeEscaneo([lineaFixture({ idLinea: 'x', idArticulo: 2 })], { idArticulo: 1, acumulaEnVenta: true })).toBeNull()
  })

  it('la última línea del artículo cuando acumula', () => {
    const lineas = [lineaFixture({ idLinea: 'x' }), lineaFixture({ idLinea: 'y', idArticulo: 2 }), lineaFixture({ idLinea: 'z' })]

    expect(idLineaDestinoDeEscaneo(lineas, { idArticulo: 1, acumulaEnVenta: true })).toBe('z')
  })
})

describe('nuevoIdLinea', () => {
  it('genera ids distintos en cada llamada', () => {
    const ids = new Set(Array.from({ length: 50 }, () => nuevoIdLinea()))

    expect(ids.size).toBe(50)
  })
})

describe('migrarLineasDeBorrador', () => {
  const lineaVieja = (idArticulo: number, cantidad: number) => ({
    idArticulo,
    codigoInterno: `A${idArticulo}`,
    nombre: `Art ${idArticulo}`,
    codigoBarra: null,
    cantidad,
  })
  const generadorSecuencial = () => {
    let siguiente = 0
    return () => `nuevo-${(siguiente += 1)}`
  }

  it('asigna idLinea y acumulaEnVenta: true a las líneas de un borrador anterior a los campos', () => {
    const { lineas } = migrarLineasDeBorrador([lineaVieja(1, 2), lineaVieja(2, 1)], {}, generadorSecuencial())

    expect(lineas).toEqual([
      { ...lineaVieja(1, 2), idLinea: 'nuevo-1', acumulaEnVenta: true },
      { ...lineaVieja(2, 1), idLinea: 'nuevo-2', acumulaEnVenta: true },
    ])
  })

  it('traslada la edición de cantidad indexada por idArticulo al idLinea nuevo de esa línea', () => {
    const { cantidadesEnEdicion } = migrarLineasDeBorrador([lineaVieja(1, 2), lineaVieja(2, 1)], { 2: '1.' }, generadorSecuencial())

    expect(cantidadesEnEdicion).toEqual({ 'nuevo-2': '1.' })
  })

  it('deja intactas las líneas que ya tienen idLinea y acumulaEnVenta, con sus ediciones', () => {
    const actual = lineaFixture({ idLinea: 'abc', acumulaEnVenta: false, cantidad: 3 })
    const generar = () => {
      throw new Error('no debería generar ids')
    }

    expect(migrarLineasDeBorrador([actual], { abc: '3.' }, generar)).toEqual({ lineas: [actual], cantidadesEnEdicion: { abc: '3.' } })
  })

  it('descarta una edición cuya línea ya no existe', () => {
    const { cantidadesEnEdicion } = migrarLineasDeBorrador([lineaFixture({ idLinea: 'abc' })], { 99: '5', huerfana: '2' })

    expect(cantidadesEnEdicion).toEqual({})
  })
})
