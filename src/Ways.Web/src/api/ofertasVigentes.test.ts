import { beforeEach, describe, expect, it, vi } from 'vitest'
import { obtenerOfertasVigentes, textoDeOfertaVigente, type OfertaVigente } from './ofertasVigentes'

const postMock = vi.fn()
vi.mock('./cliente', () => ({
  api: { post: (...args: unknown[]) => postMock(...args) },
}))

function oferta(sobrescribir: Partial<OfertaVigente> = {}): OfertaVigente {
  return {
    idOferta: 1,
    nombre: 'Gaseosas',
    alcance: 'Articulo',
    cantidadMinima: null,
    soloMultiplos: false,
    porcentaje: null,
    precioUnitario: null,
    importeFijo: null,
    fechaHasta: null,
    acumulable: false,
    ...sobrescribir,
  }
}

describe('textoDeOfertaVigente', () => {
  it('porcentaje y cantidad con decimales llevan coma', () => {
    expect(textoDeOfertaVigente(oferta({ porcentaje: 12.5, cantidadMinima: 0.5 }))).toBe(
      'Gaseosas — 12,5% de descuento · Llevando 0,5',
    )
  })

  it('una oferta de familia aclara que la cantidad es de la familia', () => {
    expect(textoDeOfertaVigente(oferta({ alcance: 'Familia', porcentaje: 10, cantidadMinima: 3 }))).toBe(
      'Gaseosas — 10% de descuento · Llevando 3 de la familia',
    )
    expect(
      textoDeOfertaVigente(oferta({ alcance: 'Familia', porcentaje: 10, cantidadMinima: 3, soloMultiplos: true })),
    ).toBe('Gaseosas — 10% de descuento · cada 3 de la familia')
  })

  it('porcentaje', () => {
    expect(textoDeOfertaVigente(oferta({ porcentaje: 10 }))).toBe('Gaseosas — 10% de descuento')
  })

  it('precio unitario', () => {
    expect(textoDeOfertaVigente(oferta({ precioUnitario: 1500 }))).toBe('Gaseosas — $ 1.500,00 c/u')
  })

  it('importe fijo', () => {
    expect(textoDeOfertaVigente(oferta({ importeFijo: 200 }))).toBe('Gaseosas — $ 200,00 menos')
  })

  it('cantidad mínima sin múltiplos', () => {
    expect(textoDeOfertaVigente(oferta({ porcentaje: 10, cantidadMinima: 3 }))).toBe('Gaseosas — 10% de descuento · Llevando 3')
  })

  it('cantidad mínima con solo múltiplos', () => {
    expect(textoDeOfertaVigente(oferta({ porcentaje: 10, cantidadMinima: 3, soloMultiplos: true }))).toBe(
      'Gaseosas — 10% de descuento · cada 3',
    )
  })

  it('solo múltiplos sin cantidad mínima no agrega condición', () => {
    expect(textoDeOfertaVigente(oferta({ porcentaje: 10, soloMultiplos: true }))).toBe('Gaseosas — 10% de descuento')
  })

  it('fecha hasta como dd/mm', () => {
    expect(textoDeOfertaVigente(oferta({ porcentaje: 10, fechaHasta: '2026-11-05' }))).toBe('Gaseosas — 10% de descuento · hasta 05/11')
  })

  it('fecha con formato inesperado se omite', () => {
    expect(textoDeOfertaVigente(oferta({ porcentaje: 10, fechaHasta: 'mañana' }))).toBe('Gaseosas — 10% de descuento')
  })

  it('todo junto', () => {
    expect(textoDeOfertaVigente(oferta({ importeFijo: 200, cantidadMinima: 2, fechaHasta: '2026-12-31' }))).toBe(
      'Gaseosas — $ 200,00 menos · Llevando 2 · hasta 31/12',
    )
  })

  it('sin beneficio ni condición queda solo el nombre', () => {
    expect(textoDeOfertaVigente(oferta())).toBe('Gaseosas')
  })
})

describe('obtenerOfertasVigentes', () => {
  beforeEach(() => postMock.mockReset())

  it('hace POST a /ofertas/vigentes con la solicitud tal cual', async () => {
    postMock.mockResolvedValue([])
    const solicitud = { idsArticulos: [1, 2], idEmpresa: 7, idListaPrecio: 3 }

    await obtenerOfertasVigentes(solicitud)

    expect(postMock).toHaveBeenCalledWith('/ofertas/vigentes', solicitud)
  })
})
