import { beforeEach, describe, expect, it, vi } from 'vitest'
import { calcularTotalesDeLinea } from './ajusteManual'
import { calcularTotalesDeLineaConCombo, clienteDeCombos, comboVigenteDeLinea, rotuloDeComboAplicado } from './combos'
import { previaDeLinea } from './ventas'
import type { LineaCarrito } from './carrito'
import { api } from './cliente'
import type { ResultadoDeResolucion } from './tipos'

vi.mock('./cliente', async (importarOriginal) => ({
  ...(await importarOriginal<typeof import('./cliente')>()),
  api: { get: vi.fn() },
}))

describe('calcularTotalesDeLineaConCombo', () => {
  it('sin combo es exactamente calcularTotalesDeLinea', () => {
    const base = { cantidad: 3, precioOriginal: 100, descuentoUnitario: 20, porcentaje: 15 }
    expect(calcularTotalesDeLineaConCombo(base)).toEqual(calcularTotalesDeLinea(base))
    expect(calcularTotalesDeLineaConCombo({ ...base, combo: null })).toEqual(calcularTotalesDeLinea(base))
  })

  it('el ahorro del combo se suma al descuento de oferta de las unidades NO consumidas', () => {
    // 2 panchos a 3000 con oferta de 500 por unidad; uno lo consume el combo (382).
    const totales = calcularTotalesDeLineaConCombo({
      cantidad: 2,
      precioOriginal: 3000,
      descuentoUnitario: 500,
      porcentaje: null,
      combo: { unidadesConsumidas: 1, ahorro: 382 },
    })

    expect(totales).toEqual({ bruto: 6000, descuento: 882, neto: 5118, ajuste: 0, total: 5118 })
  })

  it('con todas las unidades consumidas la oferta no cuenta: solo el ahorro del combo', () => {
    const totales = calcularTotalesDeLineaConCombo({
      cantidad: 2,
      precioOriginal: 3000,
      descuentoUnitario: 500,
      porcentaje: null,
      combo: { unidadesConsumidas: 2, ahorro: 546 },
    })

    expect(totales).toMatchObject({ descuento: 546, total: 5454 })
  })

  it('el ajuste manual se calcula sobre el neto que ya incluye el combo, con redondeo decimal exacto', () => {
    const totales = calcularTotalesDeLineaConCombo({
      cantidad: 1,
      precioOriginal: 3000,
      descuentoUnitario: 0,
      porcentaje: -10,
      combo: { unidadesConsumidas: 1, ahorro: 273 },
    })

    expect(totales).toMatchObject({ neto: 2727, ajuste: -273, total: 2454 })
  })
})

describe('combo viejo sobre una cantidad ya reducida (borrador restaurado o edición antes de re-resolver)', () => {
  const base = { precioOriginal: 3000, descuentoUnitario: 500, porcentaje: null }

  it('comboVigenteDeLinea: vale con unidades hasta la cantidad (incluida) y se ignora con más', () => {
    const combo = { unidadesConsumidas: 2, ahorro: 545.46 }
    expect(comboVigenteDeLinea(2, combo)).toBe(combo)
    expect(comboVigenteDeLinea(3, combo)).toBe(combo)
    expect(comboVigenteDeLinea(1, combo)).toBeNull()
    expect(comboVigenteDeLinea(2, null)).toBeNull()
    expect(comboVigenteDeLinea(2, undefined)).toBeNull()
  })

  it('con unidades consumidas mayores a la cantidad la línea se totaliza SIN combo, nunca con unidades negativas', () => {
    // Un combo de 2 unidades sobre una línea que quedó en 1: acotar a cero seguiría descontando el ahorro de
    // 545,46 pensado para 6000 de bruto; ignorarlo deja el total honesto (lista menos oferta) hasta re-resolver.
    const totales = calcularTotalesDeLineaConCombo({ ...base, cantidad: 1, combo: { unidadesConsumidas: 2, ahorro: 545.46 } })

    expect(totales).toEqual(calcularTotalesDeLinea({ ...base, cantidad: 1 }))
    expect(totales).toMatchObject({ bruto: 3000, descuento: 500, total: 2500 })
  })

  it('con unidades iguales a la cantidad el combo sigue valiendo (el borde)', () => {
    const totales = calcularTotalesDeLineaConCombo({ ...base, cantidad: 2, combo: { unidadesConsumidas: 2, ahorro: 546 } })

    expect(totales).toMatchObject({ descuento: 546, total: 5454 })
  })

  it('previaDeLinea totaliza una línea con combo viejo sin él', () => {
    const linea: LineaCarrito = { idLinea: 'l', idArticulo: 2, codigoInterno: 'A', nombre: 'Pancho', codigoBarra: null, acumulaEnVenta: true, cantidad: 1 }
    const resultado: ResultadoDeResolucion = {
      idArticulo: 2,
      idListaPrecio: 1,
      precioOriginal: 3000,
      precioFinal: 2500,
      descuentoUnitario: 500,
      aplicadas: [],
      combo: { unidadesConsumidas: 2, ahorro: 545.46, idCombo: 7 },
    }

    expect(previaDeLinea(linea, resultado).total).toBe(2500)
  })
})

describe('rotuloDeComboAplicado', () => {
  it('lleva el sufijo × veces solo cuando se aplicó más de una vez', () => {
    expect(rotuloDeComboAplicado({ nombre: 'Pancho + Coca', veces: 1 })).toBe('Pancho + Coca')
    expect(rotuloDeComboAplicado({ nombre: 'Pancho + Coca', veces: 3 })).toBe('Pancho + Coca × 3')
  })
})

describe('previaDeLinea — combo', () => {
  const linea: LineaCarrito = { idLinea: 'l', idArticulo: 2, codigoInterno: 'A', nombre: 'Pancho', codigoBarra: null, acumulaEnVenta: true, cantidad: 1 }
  const resultado = (extra: Partial<ResultadoDeResolucion> = {}): ResultadoDeResolucion => ({
    idArticulo: 2,
    idListaPrecio: 1,
    precioOriginal: 3000,
    precioFinal: 3000,
    descuentoUnitario: 0,
    aplicadas: [],
    ...extra,
  })

  it('la línea consumida por un combo totaliza bruto menos su parte, y reporta el ahorro', () => {
    const previa = previaDeLinea(linea, resultado({ combo: { unidadesConsumidas: 1, ahorro: 273, idCombo: 7 } }))

    expect(previa).toMatchObject({ total: 2727, ahorroDeCombo: 273, ajusteManual: 0 })
  })

  it('sin combo el total y el ahorro son los de siempre', () => {
    expect(previaDeLinea(linea, resultado())).toMatchObject({ total: 3000, ahorroDeCombo: 0 })
  })

  it('el ajuste manual de la línea se aplica sobre el neto con combo', () => {
    const previa = previaDeLinea({ ...linea, ajusteManualPorcentaje: -10 }, resultado({ combo: { unidadesConsumidas: 1, ahorro: 273, idCombo: 7 } }))

    expect(previa).toMatchObject({ total: 2454, ajusteManual: -273 })
  })

  it('sin resultado no hay precio ni ahorro', () => {
    expect(previaDeLinea(linea, undefined)).toEqual({ precioUnitario: null, descuentoUnitario: 0, total: null, ajusteManual: 0, ahorroDeCombo: 0 })
  })
})

describe('clienteDeCombos', () => {
  beforeEach(() => {
    vi.mocked(api.get).mockReset()
    vi.mocked(api.get).mockResolvedValue([])
  })

  it('listar pide GET /combos', async () => {
    await clienteDeCombos.listar()

    expect(api.get).toHaveBeenCalledWith('/combos')
  })

  it('cargable pide el detalle del combo con la empresa del punto de venta', async () => {
    await clienteDeCombos.cargable(70, 3)

    expect(api.get).toHaveBeenCalledWith('/combos/70/cargable?idEmpresa=3')
  })

  it('cargable sin empresa no manda el parámetro', async () => {
    await clienteDeCombos.cargable(70, null)

    expect(api.get).toHaveBeenCalledWith('/combos/70/cargable')
  })
})
