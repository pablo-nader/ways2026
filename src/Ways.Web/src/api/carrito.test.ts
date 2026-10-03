import { describe, expect, it } from 'vitest'
import { reducirCarrito } from './carrito'
import type { LineaCarrito } from './carrito'

function lineaFixture(sobrescribir: Partial<LineaCarrito> = {}): LineaCarrito {
  return { idArticulo: 1, codigoInterno: 'A0001', nombre: 'Coca Cola 1L', codigoBarra: '7790001234567', cantidad: 1, ...sobrescribir }
}

describe('reducirCarrito — escanear', () => {
  it('agrega una línea nueva cuando el artículo no está en el carrito', () => {
    const resultado = reducirCarrito([], {
      tipo: 'escanear',
      linea: { idArticulo: 1, codigoInterno: 'A0001', nombre: 'Coca Cola 1L', codigoBarra: '7790001234567' },
      cantidad: 1,
    })

    expect(resultado).toEqual([lineaFixture()])
  })

  it('re-escanear el mismo artículo suma la cantidad en la línea existente, no duplica', () => {
    const carritoConDos = [lineaFixture({ cantidad: 2 })]

    const resultado = reducirCarrito(carritoConDos, {
      tipo: 'escanear',
      linea: { idArticulo: 1, codigoInterno: 'A0001', nombre: 'Coca Cola 1L', codigoBarra: '7790001234567' },
      cantidad: 1,
    })

    expect(resultado).toHaveLength(1)
    expect(resultado[0].cantidad).toBe(3)
  })

  it('escanear el prefijo N*codigo pasa la cantidad indicada tal cual, sin transformarla', () => {
    const resultado = reducirCarrito([], {
      tipo: 'escanear',
      linea: { idArticulo: 2, codigoInterno: 'A0002', nombre: 'Agua 500ml', codigoBarra: '7790009876543' },
      cantidad: 3,
    })

    expect(resultado[0].cantidad).toBe(3)
  })

  it('escanear un segundo artículo agrega una línea nueva sin tocar la primera', () => {
    const carritoConUno = [lineaFixture()]

    const resultado = reducirCarrito(carritoConUno, {
      tipo: 'escanear',
      linea: { idArticulo: 2, codigoInterno: 'A0002', nombre: 'Agua 500ml', codigoBarra: '7790009876543' },
      cantidad: 1,
    })

    expect(resultado).toHaveLength(2)
    expect(resultado[0]).toEqual(lineaFixture())
    expect(resultado[1].idArticulo).toBe(2)
  })
})

describe('reducirCarrito — editarCantidad', () => {
  it('actualiza la cantidad de la línea indicada sin tocar el resto', () => {
    const carrito = [lineaFixture({ idArticulo: 1, cantidad: 1 }), lineaFixture({ idArticulo: 2, cantidad: 5 })]

    const resultado = reducirCarrito(carrito, { tipo: 'editarCantidad', idArticulo: 1, cantidad: 7 })

    expect(resultado.find((l) => l.idArticulo === 1)?.cantidad).toBe(7)
    expect(resultado.find((l) => l.idArticulo === 2)?.cantidad).toBe(5)
  })

  it('permite cantidad negativa — convención de signo para líneas NCX (design decisión 4)', () => {
    const carrito = [lineaFixture({ cantidad: 2 })]

    const resultado = reducirCarrito(carrito, { tipo: 'editarCantidad', idArticulo: 1, cantidad: -2 })

    expect(resultado[0].cantidad).toBe(-2)
  })

  it('editar la cantidad de un idArticulo inexistente no agrega ni modifica ninguna línea', () => {
    const carrito = [lineaFixture()]

    const resultado = reducirCarrito(carrito, { tipo: 'editarCantidad', idArticulo: 999, cantidad: 5 })

    expect(resultado).toEqual(carrito)
  })
})

describe('reducirCarrito — quitarLinea', () => {
  it('remueve solo la línea indicada', () => {
    const carrito = [lineaFixture({ idArticulo: 1 }), lineaFixture({ idArticulo: 2 })]

    const resultado = reducirCarrito(carrito, { tipo: 'quitarLinea', idArticulo: 1 })

    expect(resultado).toEqual([lineaFixture({ idArticulo: 2 })])
  })

  it('quitar un idArticulo inexistente deja el carrito sin cambios', () => {
    const carrito = [lineaFixture()]

    const resultado = reducirCarrito(carrito, { tipo: 'quitarLinea', idArticulo: 999 })

    expect(resultado).toEqual(carrito)
  })
})

describe('reducirCarrito — ajuste manual', () => {
  it('fijarAjusteManual pone el porcentaje en la línea indicada y no toca el resto', () => {
    const carrito = [lineaFixture({ idArticulo: 1 }), lineaFixture({ idArticulo: 2 })]

    const resultado = reducirCarrito(carrito, { tipo: 'fijarAjusteManual', idArticulo: 2, porcentaje: -10 })

    expect(resultado[0]).toEqual(lineaFixture({ idArticulo: 1 }))
    expect(resultado[1].ajusteManualPorcentaje).toBe(-10)
    expect(resultado[0]).not.toHaveProperty('ajusteManualPorcentaje')
  })

  it('fijarAjusteManual sobre una línea que ya tiene ajuste lo reemplaza (también cambiando de signo)', () => {
    const carrito = [lineaFixture({ ajusteManualPorcentaje: -10 })]

    const resultado = reducirCarrito(carrito, { tipo: 'fijarAjusteManual', idArticulo: 1, porcentaje: 15 })

    expect(resultado[0].ajusteManualPorcentaje).toBe(15)
  })

  it('fijarAjusteManual sobre un idArticulo inexistente no agrega ni modifica ninguna línea', () => {
    const carrito = [lineaFixture()]

    expect(reducirCarrito(carrito, { tipo: 'fijarAjusteManual', idArticulo: 999, porcentaje: 5 })).toEqual(carrito)
  })

  it('quitarAjusteManual elimina el campo de esa línea (no lo deja en null) y no toca el resto', () => {
    const carrito = [lineaFixture({ idArticulo: 1, ajusteManualPorcentaje: -10 }), lineaFixture({ idArticulo: 2, ajusteManualPorcentaje: 15 })]

    const resultado = reducirCarrito(carrito, { tipo: 'quitarAjusteManual', idArticulo: 1 })

    expect(resultado[0]).not.toHaveProperty('ajusteManualPorcentaje')
    expect(resultado[0]).toEqual(lineaFixture({ idArticulo: 1 }))
    expect(resultado[1].ajusteManualPorcentaje).toBe(15)
  })

  it('quitarAjusteManual sobre una línea sin ajuste, o un idArticulo inexistente, deja el carrito igual', () => {
    const carrito = [lineaFixture()]

    expect(reducirCarrito(carrito, { tipo: 'quitarAjusteManual', idArticulo: 1 })).toEqual(carrito)
    expect(reducirCarrito(carrito, { tipo: 'quitarAjusteManual', idArticulo: 999 })).toEqual(carrito)
  })

  it('las dos acciones devuelven un carrito nuevo sin mutar el anterior', () => {
    const carrito = [lineaFixture({ ajusteManualPorcentaje: -10 })]

    reducirCarrito(carrito, { tipo: 'fijarAjusteManual', idArticulo: 1, porcentaje: 20 })
    reducirCarrito(carrito, { tipo: 'quitarAjusteManual', idArticulo: 1 })

    expect(carrito[0].ajusteManualPorcentaje).toBe(-10)
  })

  it('re-escanear el mismo artículo suma la cantidad y conserva el ajuste', () => {
    const carrito = [lineaFixture({ cantidad: 2, ajusteManualPorcentaje: -10 })]

    const resultado = reducirCarrito(carrito, {
      tipo: 'escanear',
      linea: { idArticulo: 1, codigoInterno: 'A0001', nombre: 'Coca Cola 1L', codigoBarra: '7790001234567' },
      cantidad: 1,
    })

    expect(resultado).toHaveLength(1)
    expect(resultado[0]).toMatchObject({ cantidad: 3, ajusteManualPorcentaje: -10 })
  })

  it('escanear un artículo nuevo lo agrega sin ajuste', () => {
    const carrito = [lineaFixture({ idArticulo: 1, ajusteManualPorcentaje: -10 })]

    const resultado = reducirCarrito(carrito, {
      tipo: 'escanear',
      linea: { idArticulo: 2, codigoInterno: 'A0002', nombre: 'Agua 500ml', codigoBarra: null },
      cantidad: 1,
    })

    expect(resultado[1]).not.toHaveProperty('ajusteManualPorcentaje')
  })

  it('editar la cantidad conserva el ajuste', () => {
    const carrito = [lineaFixture({ cantidad: 1, ajusteManualPorcentaje: 15 })]

    const resultado = reducirCarrito(carrito, { tipo: 'editarCantidad', idArticulo: 1, cantidad: 4 })

    expect(resultado[0]).toMatchObject({ cantidad: 4, ajusteManualPorcentaje: 15 })
  })

  it('quitar la línea se lleva su ajuste: volver a agregar el artículo arranca sin ajuste', () => {
    const conAjuste = [lineaFixture({ ajusteManualPorcentaje: -10 })]

    const sinLinea = reducirCarrito(conAjuste, { tipo: 'quitarLinea', idArticulo: 1 })
    const reagregada = reducirCarrito(sinLinea, {
      tipo: 'escanear',
      linea: { idArticulo: 1, codigoInterno: 'A0001', nombre: 'Coca Cola 1L', codigoBarra: '7790001234567' },
      cantidad: 1,
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
