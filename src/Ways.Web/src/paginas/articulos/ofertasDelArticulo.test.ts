import { describe, expect, it } from 'vitest'
import type { OfertaListado } from '../../api/tipos'
import {
  formularioDeOfertaParaArticulo,
  formularioDeOfertaParaFamilia,
  textoDeAlcanceEnArticulo,
  textoDeCondicion,
  textoDeVigencia,
} from './ofertasDelArticulo'

function oferta(sobrescribir: Partial<OfertaListado> = {}): OfertaListado {
  return {
    id: 1,
    nombre: 'Oferta',
    idEmpresa: null,
    idArticulo: null,
    idGrupo: null,
    idCategoria: null,
    idFamilia: null,
    fechaDesde: null,
    fechaHasta: null,
    horaDesde: null,
    horaHasta: null,
    diasSemana: [],
    cantidadMinima: null,
    soloMultiplos: false,
    precioUnitario: null,
    porcentaje: 10,
    importeFijo: null,
    prioridad: 0,
    acumulable: false,
    activo: true,
    disponibleOnline: true,
    idsListas: [],
    ...sobrescribir,
  }
}

describe('textoDeAlcanceEnArticulo', () => {
  it('rotula la oferta propia del artículo', () => {
    expect(textoDeAlcanceEnArticulo(oferta({ idArticulo: 7 }), 'Sabores')).toBe('Este artículo')
  })

  it('rotula la oferta de familia con su nombre', () => {
    expect(textoDeAlcanceEnArticulo(oferta({ idFamilia: 3 }), 'Sabores')).toBe('Familia: Sabores')
  })

  it('sin el nombre de la familia la rotula por número', () => {
    expect(textoDeAlcanceEnArticulo(oferta({ idFamilia: 3 }), null)).toBe('Familia: #3')
  })
})

describe('textoDeCondicion', () => {
  it('sin cantidad mínima no hay condición', () => {
    expect(textoDeCondicion(oferta())).toBe('—')
  })

  it('muestra la cantidad mínima', () => {
    expect(textoDeCondicion(oferta({ cantidadMinima: 3 }))).toBe('Mínimo 3')
  })

  it('agrega "solo múltiplos" cuando corresponde', () => {
    expect(textoDeCondicion(oferta({ cantidadMinima: 3, soloMultiplos: true }))).toBe('Mínimo 3, solo múltiplos')
  })
})

describe('textoDeVigencia', () => {
  it('sin restricciones vale siempre', () => {
    expect(textoDeVigencia(oferta())).toBe('Siempre')
  })

  it('rango de fechas, de horas y días ordenados en una línea', () => {
    const texto = textoDeVigencia(
      oferta({
        fechaDesde: '2026-11-01',
        fechaHasta: '2026-11-30',
        horaDesde: '08:00:00',
        horaHasta: '12:30:00',
        diasSemana: [5, 1],
      }),
    )

    expect(texto).toBe('01/11/2026 al 30/11/2026 · 08:00 a 12:30 h · Lun, Vie')
  })

  it('un extremo abierto se dice "desde" o "hasta"', () => {
    expect(textoDeVigencia(oferta({ fechaDesde: '2026-11-01' }))).toBe('desde 01/11/2026')
    expect(textoDeVigencia(oferta({ fechaHasta: '2026-11-30' }))).toBe('hasta 30/11/2026')
    expect(textoDeVigencia(oferta({ horaDesde: '18:00:00' }))).toBe('desde 18:00 h')
    expect(textoDeVigencia(oferta({ horaHasta: '06:00:00' }))).toBe('hasta 06:00 h')
  })
})

describe('formularios con alcance preestablecido', () => {
  it('para el artículo fija el alcance, el id y el nombre', () => {
    const f = formularioDeOfertaParaArticulo(7, 'Cola 2L')

    expect(f).toMatchObject({ id: null, alcance: 'Articulo', idArticulo: 7, nombreArticulo: 'Cola 2L', idFamilia: '' })
  })

  it('para la familia fija el alcance y el id, sin artículo', () => {
    const f = formularioDeOfertaParaFamilia(5)

    expect(f).toMatchObject({ id: null, alcance: 'Familia', idFamilia: 5, idArticulo: '' })
  })
})
