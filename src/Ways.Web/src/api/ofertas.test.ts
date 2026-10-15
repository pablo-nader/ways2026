import { describe, expect, it } from 'vitest'
import {
  aAltaOferta,
  aValoresOferta,
  copiaDeFalloAlGuardarOferta,
  formularioOfertaVacio,
  opcionesDeFamiliaDeOferta,
  opcionesDeLista,
  resumenDeBeneficio,
} from './ofertas'
import { ErrorApi } from './cliente'
import type { FamiliaListado, ListaPrecioListado, OfertaListado } from './tipos'

function ofertaFixture(sobrescribir: Partial<OfertaListado> = {}): OfertaListado {
  return {
    id: 1,
    nombre: 'Oferta base',
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
    precioUnitario: null,
    porcentaje: null,
    importeFijo: null,
    prioridad: 0,
    acumulable: false,
    activo: true,
    disponibleOnline: true,
    idsListas: [],
    ...sobrescribir,
  }
}

function familiaFixture(sobrescribir: Partial<FamiliaListado> = {}): FamiliaListado {
  return { id: 1, nombre: 'Sabores', activo: true, cantidadArticulos: 3, ...sobrescribir }
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

describe('aAltaOferta — separador decimal', () => {
  it('la coma y el punto se leen igual en la cantidad mínima, el porcentaje y la prioridad es entera', () => {
    const conComa = aAltaOferta({ ...formularioOfertaVacio(), nombre: 'Oferta', beneficio: 'Porcentaje', cantidadMinima: '2,5', porcentaje: '12,5' })
    const conPunto = aAltaOferta({ ...formularioOfertaVacio(), nombre: 'Oferta', beneficio: 'Porcentaje', cantidadMinima: '2.5', porcentaje: '12.5' })

    expect(conComa.cantidadMinima).toBe(2.5)
    expect(conComa.porcentaje).toBe(12.5)
    expect(conComa).toEqual(conPunto)
    expect(aAltaOferta({ ...formularioOfertaVacio(), nombre: 'Oferta', prioridad: '3' }).prioridad).toBe(3)
  })
})

describe('aAltaOferta — exclusividad de alcance', () => {
  /** Con las cuatro columnas de alcance cargadas a la vez, solo la del radio elegido sobrevive. */
  const todasCargadas = { idArticulo: 5, idGrupo: 9, idCategoria: 8, idFamilia: 7 } as const

  it.each([
    ['Articulo', { idArticulo: 5, idGrupo: null, idCategoria: null, idFamilia: null }],
    ['Grupo', { idArticulo: null, idGrupo: 9, idCategoria: null, idFamilia: null }],
    ['Categoria', { idArticulo: null, idGrupo: null, idCategoria: 8, idFamilia: null }],
    ['Familia', { idArticulo: null, idGrupo: null, idCategoria: null, idFamilia: 7 }],
  ] as const)('con alcance %s conserva solo su columna y fuerza las otras tres a null', (alcance, esperado) => {
    const alta = aAltaOferta({ ...formularioOfertaVacio(), nombre: 'Oferta', alcance, ...todasCargadas })

    expect({ idArticulo: alta.idArticulo, idGrupo: alta.idGrupo, idCategoria: alta.idCategoria, idFamilia: alta.idFamilia }).toEqual(esperado)
  })

  it('con alcance Familia sin familia elegida manda idFamilia null y no hereda otra columna', () => {
    const alta = aAltaOferta({ ...formularioOfertaVacio(), nombre: 'Oferta', alcance: 'Familia', idFamilia: '', idGrupo: 9 })

    expect(alta.idFamilia).toBeNull()
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

describe('disponibleOnline de la oferta', () => {
  it('el formulario vacío arranca disponible online', () => {
    expect(formularioOfertaVacio().disponibleOnline).toBe(true)
  })

  it('aAltaOferta lo manda siempre, marcado o no, independiente de "activo"', () => {
    const vacio = formularioOfertaVacio()

    expect(aAltaOferta(vacio).disponibleOnline).toBe(true)
    expect(aAltaOferta({ ...vacio, disponibleOnline: false }).disponibleOnline).toBe(false)
    expect(aAltaOferta({ ...vacio, disponibleOnline: false, activo: true })).toMatchObject({
      disponibleOnline: false,
      activo: true,
    })
    expect(aAltaOferta({ ...vacio, disponibleOnline: true, activo: false })).toMatchObject({
      disponibleOnline: true,
      activo: false,
    })
  })

  it('aValoresOferta lo copia de la oferta, también cuando está apagado', () => {
    expect(aValoresOferta(ofertaFixture({ disponibleOnline: true })).disponibleOnline).toBe(true)
    expect(aValoresOferta(ofertaFixture({ disponibleOnline: false })).disponibleOnline).toBe(false)
  })

  it('ida y vuelta: una oferta apagada se edita sin volver a prenderse', () => {
    const alta = aAltaOferta(aValoresOferta(ofertaFixture({ idArticulo: 7, porcentaje: 10, disponibleOnline: false })))

    expect(alta.disponibleOnline).toBe(false)
  })
})

describe('copiaDeFalloAlGuardarOferta', () => {
  it('el 400 disponible_online_requerido rinde su copia propia y no el texto del servidor', () => {
    const copia = copiaDeFalloAlGuardarOferta(new ErrorApi(400, 'disponible_online_requerido', 'texto crudo'))

    expect(copia).toBe('Falta indicar si la oferta está disponible online. Recargá la pantalla e intentá de nuevo.')
  })

  it('cualquier otro rechazo de la API rinde el mensaje del servidor', () => {
    expect(copiaDeFalloAlGuardarOferta(new ErrorApi(409, 'nombre_duplicado', 'Ya existe una oferta con ese nombre.'))).toBe(
      'Ya existe una oferta con ese nombre.',
    )
  })

  it('un error que no es de la API rinde el texto genérico', () => {
    expect(copiaDeFalloAlGuardarOferta(new Error('boom'))).toBe('No se pudo guardar la oferta.')
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

  it('deriva alcance Familia cuando idFamilia no es null', () => {
    const valores = aValoresOferta(ofertaFixture({ idFamilia: 4 }))
    expect(valores.alcance).toBe('Familia')
    expect(valores.idFamilia).toBe(4)
    expect(valores.idArticulo).toBe('')
    expect(valores.idGrupo).toBe('')
    expect(valores.idCategoria).toBe('')
  })

  it('las ofertas sin familia leen idFamilia como vacío', () => {
    expect(aValoresOferta(ofertaFixture({ idGrupo: 3 })).idFamilia).toBe('')
  })

  /** Cláusula bajo prueba: `idFamilia != null` (y no `!== null`) trata el campo ausente como "sin familia" — una API
   * desplegada antes que este cambio no manda `idFamilia`, y `undefined !== null` abriría una oferta de categoría como Familia. */
  it('un payload sin el campo idFamilia y con idCategoria deriva Categoria', () => {
    const payload: Record<string, unknown> = { ...ofertaFixture({ idCategoria: 9 }) }
    delete payload.idFamilia

    const valores = aValoresOferta(payload as unknown as OfertaListado)

    expect(valores.alcance).toBe('Categoria')
    expect(valores.idFamilia).toBe('')
  })

  it('con dos columnas de alcance cargadas a la vez gana la primera de Artículo, Grupo, Familia, Categoría', () => {
    expect(aValoresOferta(ofertaFixture({ idArticulo: 1, idGrupo: 2, idFamilia: 3, idCategoria: 4 })).alcance).toBe('Articulo')
    expect(aValoresOferta(ofertaFixture({ idGrupo: 2, idFamilia: 3, idCategoria: 4 })).alcance).toBe('Grupo')
    expect(aValoresOferta(ofertaFixture({ idFamilia: 3, idCategoria: 4 })).alcance).toBe('Familia')
  })

  it('con todas las columnas de alcance en null cae en Categoria', () => {
    expect(aValoresOferta(ofertaFixture()).alcance).toBe('Categoria')
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

describe('aValoresOferta ∘ aAltaOferta — la oferta de una familia da la vuelta entera', () => {
  it('reenvía idFamilia y deja las otras tres columnas de alcance en null', () => {
    const oferta = ofertaFixture({ idFamilia: 4, porcentaje: 15, idsListas: [2] })

    const alta = aAltaOferta(aValoresOferta(oferta))

    expect(alta.idFamilia).toBe(4)
    expect(alta.idArticulo).toBeNull()
    expect(alta.idGrupo).toBeNull()
    expect(alta.idCategoria).toBeNull()
    expect(alta.porcentaje).toBe(15)
    expect(alta.idsListas).toEqual([2])
  })
})

describe('opcionesDeFamiliaDeOferta', () => {
  it('ofrece todas las familias vivas, también las inactivas y las vacías, en el orden del servidor', () => {
    const opciones = opcionesDeFamiliaDeOferta([
      familiaFixture({ id: 1, nombre: 'Sabores' }),
      familiaFixture({ id: 2, nombre: 'Talles', activo: false }),
      familiaFixture({ id: 3, nombre: 'Colores', cantidadArticulos: 0 }),
    ])

    expect(opciones.map((o) => o.valor)).toEqual([1, 2, 3])
  })

  it('una familia activa y con artículos va sin rótulo', () => {
    expect(opcionesDeFamiliaDeOferta([familiaFixture({ id: 1, nombre: 'Sabores' })])).toEqual([{ valor: 1, etiqueta: 'Sabores' }])
  })

  it('rotula la inactiva, la vacía y la que es las dos cosas', () => {
    const opciones = opcionesDeFamiliaDeOferta([
      familiaFixture({ id: 2, nombre: 'Talles', activo: false }),
      familiaFixture({ id: 3, nombre: 'Colores', cantidadArticulos: 0 }),
      familiaFixture({ id: 4, nombre: 'Marcas', activo: false, cantidadArticulos: 0 }),
    ])

    expect(opciones.map((o) => o.etiqueta)).toEqual(['Talles (inactiva)', 'Colores (sin artículos)', 'Marcas (inactiva, sin artículos)'])
  })

  it('sin familias no ofrece nada', () => {
    expect(opcionesDeFamiliaDeOferta([])).toEqual([])
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
    expect(resumenDeBeneficio(ofertaFixture({ idArticulo: 1, importeFijo: 15 }))).toBe('$ 15,00 fijo por unidad')
  })

  it('formatea el beneficio por precio unitario', () => {
    expect(resumenDeBeneficio(ofertaFixture({ idArticulo: 1, precioUnitario: 500 }))).toBe('Precio unitario $ 500,00')
  })
})
