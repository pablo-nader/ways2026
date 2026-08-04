import { describe, expect, it } from 'vitest'
import { aAltaOferta, aValoresOferta, formularioOfertaVacio, opcionesDeLista, resumenDeBeneficio } from './ofertas'
import type { ListaPrecioListado, OfertaListado } from './tipos'

function ofertaFixture(sobrescribir: Partial<OfertaListado> = {}): OfertaListado {
  return {
    id: 1,
    nombre: 'Oferta base',
    idEmpresa: null,
    idArticulo: null,
    idGrupo: null,
    idCategoria: null,
    fechaDesde: null,
    fechaHasta: null,
    horaDesde: null,
    horaHasta: null,
    diasSemana: [],
    cantidadMinima: null,
    precioUnitario: null,
    porcentaje: null,
    importeFijo: null,
    prioridad: 0,
    acumulable: false,
    activo: true,
    idsListas: [],
    ...sobrescribir,
  }
}

function listaFixture(sobrescribir: Partial<ListaPrecioListado> = {}): ListaPrecioListado {
  return {
    id: 1,
    nombre: 'Lista mayorista',
    activo: true,
    idEmpresa: null,
    esDefault: false,
    modo: 'Fija',
    idListaBase: null,
    porcentaje: null,
    ...sobrescribir,
  }
}

describe('aAltaOferta — exclusividad de alcance', () => {
  it('fuerza idGrupo e idCategoria a null cuando el alcance es Articulo', () => {
    const alta = aAltaOferta({
      ...formularioOfertaVacio(),
      nombre: 'Oferta',
      alcance: 'Articulo',
      idArticulo: 5,
      idGrupo: 9,
      idCategoria: 9,
    })

    expect(alta.idArticulo).toBe(5)
    expect(alta.idGrupo).toBeNull()
    expect(alta.idCategoria).toBeNull()
  })

  it('fuerza idArticulo e idCategoria a null cuando el alcance es Grupo', () => {
    const alta = aAltaOferta({
      ...formularioOfertaVacio(),
      nombre: 'Oferta',
      alcance: 'Grupo',
      idArticulo: 5,
      idGrupo: 9,
      idCategoria: 9,
    })

    expect(alta.idGrupo).toBe(9)
    expect(alta.idArticulo).toBeNull()
    expect(alta.idCategoria).toBeNull()
  })

  it('fuerza idArticulo e idGrupo a null cuando el alcance es Categoria', () => {
    const alta = aAltaOferta({
      ...formularioOfertaVacio(),
      nombre: 'Oferta',
      alcance: 'Categoria',
      idArticulo: 5,
      idGrupo: 9,
      idCategoria: 3,
    })

    expect(alta.idCategoria).toBe(3)
    expect(alta.idArticulo).toBeNull()
    expect(alta.idGrupo).toBeNull()
  })
})

describe('aAltaOferta — exclusividad de beneficio', () => {
  it('fuerza porcentaje e importeFijo a null cuando el beneficio es PrecioUnitario', () => {
    const alta = aAltaOferta({
      ...formularioOfertaVacio(),
      nombre: 'Oferta',
      beneficio: 'PrecioUnitario',
      precioUnitario: '100',
      porcentaje: '20',
      importeFijo: '15',
    })

    expect(alta.precioUnitario).toBe(100)
    expect(alta.porcentaje).toBeNull()
    expect(alta.importeFijo).toBeNull()
  })

  it('fuerza precioUnitario e importeFijo a null cuando el beneficio es Porcentaje', () => {
    const alta = aAltaOferta({
      ...formularioOfertaVacio(),
      nombre: 'Oferta',
      beneficio: 'Porcentaje',
      precioUnitario: '100',
      porcentaje: '20',
      importeFijo: '15',
    })

    expect(alta.porcentaje).toBe(20)
    expect(alta.precioUnitario).toBeNull()
    expect(alta.importeFijo).toBeNull()
  })

  it('fuerza precioUnitario y porcentaje a null cuando el beneficio es ImporteFijo', () => {
    const alta = aAltaOferta({
      ...formularioOfertaVacio(),
      nombre: 'Oferta',
      beneficio: 'ImporteFijo',
      precioUnitario: '100',
      porcentaje: '20',
      importeFijo: '15',
    })

    expect(alta.importeFijo).toBe(15)
    expect(alta.precioUnitario).toBeNull()
    expect(alta.porcentaje).toBeNull()
  })
})

describe('aAltaOferta — coerción y strings vacíos', () => {
  it('convierte cantidadMinima vacía a null y no vacía a número', () => {
    const vacia = aAltaOferta({ ...formularioOfertaVacio(), nombre: 'A', cantidadMinima: '' })
    const cargada = aAltaOferta({ ...formularioOfertaVacio(), nombre: 'A', cantidadMinima: '3.5' })

    expect(vacia.cantidadMinima).toBeNull()
    expect(cargada.cantidadMinima).toBe(3.5)
  })

  it('convierte fechaDesde/fechaHasta vacías a null', () => {
    const alta = aAltaOferta({ ...formularioOfertaVacio(), nombre: 'A', fechaDesde: '', fechaHasta: '2026-08-01' })

    expect(alta.fechaDesde).toBeNull()
    expect(alta.fechaHasta).toBe('2026-08-01')
  })

  it('completa los segundos de horaDesde/horaHasta al enviar y deja null si están vacías', () => {
    const alta = aAltaOferta({ ...formularioOfertaVacio(), nombre: 'A', horaDesde: '10:00', horaHasta: '' })

    expect(alta.horaDesde).toBe('10:00:00')
    expect(alta.horaHasta).toBeNull()
  })

  it('convierte un diasSemana vacío a null y uno cargado queda ordenado', () => {
    const vacio = aAltaOferta({ ...formularioOfertaVacio(), nombre: 'A', diasSemana: [] })
    const cargado = aAltaOferta({ ...formularioOfertaVacio(), nombre: 'A', diasSemana: [7, 1, 3] })

    expect(vacio.diasSemana).toBeNull()
    expect(cargado.diasSemana).toEqual([1, 3, 7])
  })

  it('convierte idEmpresa vacío a null y prioridad vacía a 0', () => {
    const alta = aAltaOferta({ ...formularioOfertaVacio(), nombre: 'A', idEmpresa: '', prioridad: '' })

    expect(alta.idEmpresa).toBeNull()
    expect(alta.prioridad).toBe(0)
  })

  it('recorta el nombre y pasa acumulable/idsListas/activo sin transformar', () => {
    const alta = aAltaOferta({
      ...formularioOfertaVacio(),
      nombre: '  2x1 Verano  ',
      acumulable: true,
      idsListas: [2, 4],
      activo: false,
    })

    expect(alta.nombre).toBe('2x1 Verano')
    expect(alta.acumulable).toBe(true)
    expect(alta.idsListas).toEqual([2, 4])
    expect(alta.activo).toBe(false)
  })
})

describe('aValoresOferta — deriva alcance y beneficio', () => {
  it('deriva alcance Articulo cuando idArticulo no es null', () => {
    const valores = aValoresOferta(ofertaFixture({ idArticulo: 7 }))
    expect(valores.alcance).toBe('Articulo')
    expect(valores.idArticulo).toBe(7)
  })

  it('deriva alcance Grupo cuando idGrupo no es null', () => {
    const valores = aValoresOferta(ofertaFixture({ idGrupo: 3 }))
    expect(valores.alcance).toBe('Grupo')
  })

  it('deriva alcance Categoria cuando idCategoria no es null', () => {
    const valores = aValoresOferta(ofertaFixture({ idCategoria: 9 }))
    expect(valores.alcance).toBe('Categoria')
  })

  it('deriva beneficio PrecioUnitario cuando precioUnitario no es null', () => {
    const valores = aValoresOferta(ofertaFixture({ idArticulo: 1, precioUnitario: 50 }))
    expect(valores.beneficio).toBe('PrecioUnitario')
    expect(valores.precioUnitario).toBe('50')
  })

  it('deriva beneficio Porcentaje cuando porcentaje no es null', () => {
    const valores = aValoresOferta(ofertaFixture({ idArticulo: 1, porcentaje: 15.5 }))
    expect(valores.beneficio).toBe('Porcentaje')
    expect(valores.porcentaje).toBe('15.5')
  })

  it('deriva beneficio ImporteFijo cuando importeFijo no es null', () => {
    const valores = aValoresOferta(ofertaFixture({ idArticulo: 1, importeFijo: 20 }))
    expect(valores.beneficio).toBe('ImporteFijo')
    expect(valores.importeFijo).toBe('20')
  })

  it('mapea campos numéricos null a string vacío', () => {
    const valores = aValoresOferta(ofertaFixture({ idArticulo: 1, porcentaje: 10, cantidadMinima: null }))
    expect(valores.cantidadMinima).toBe('')
  })

  it('recorta los segundos de horaDesde/horaHasta al leer', () => {
    const valores = aValoresOferta(ofertaFixture({ idArticulo: 1, porcentaje: 10, horaDesde: '10:00:00', horaHasta: '14:30:00' }))
    expect(valores.horaDesde).toBe('10:00')
    expect(valores.horaHasta).toBe('14:30')
  })

  it('mapea idEmpresa null a string vacío', () => {
    const valores = aValoresOferta(ofertaFixture({ idArticulo: 1, porcentaje: 10, idEmpresa: null }))
    expect(valores.idEmpresa).toBe('')
  })
})

describe('opcionesDeLista', () => {
  it('filtra las listas inactivas', () => {
    const listas = [
      listaFixture({ id: 1, nombre: 'Activa', activo: true }),
      listaFixture({ id: 2, nombre: 'Inactiva', activo: false }),
    ]

    const opciones = opcionesDeLista(listas)

    expect(opciones).toEqual([{ valor: '1', etiqueta: 'Activa' }])
  })

  it('mapea cada opción a { valor: String(id), etiqueta: nombre }', () => {
    const listas = [listaFixture({ id: 3, nombre: 'Mayorista', activo: true })]

    expect(opcionesDeLista(listas)).toEqual([{ valor: '3', etiqueta: 'Mayorista' }])
  })
})

describe('resumenDeBeneficio', () => {
  it('formatea el beneficio por porcentaje', () => {
    expect(resumenDeBeneficio(ofertaFixture({ idArticulo: 1, porcentaje: 20 }))).toBe('20% de descuento')
  })

  it('formatea el beneficio por importe fijo', () => {
    expect(resumenDeBeneficio(ofertaFixture({ idArticulo: 1, importeFijo: 15 }))).toBe('$15 fijo por unidad')
  })

  it('formatea el beneficio por precio unitario', () => {
    expect(resumenDeBeneficio(ofertaFixture({ idArticulo: 1, precioUnitario: 500 }))).toBe('Precio unitario $500')
  })
})
