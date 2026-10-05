import { describe, expect, it } from 'vitest'
import type { CambiosDeUnArticulo, ResultadoDeAgrupacion, ValoresCompartidosDeLaFamilia } from '../../api/tipos'
import { avisoDeAgregado, avisoDeCreacion, cambiaAlgo, LIMITE_DE_ARTICULOS } from './agrupacion'

const valores: ValoresCompartidosDeLaFamilia = {
  idArea: 1,
  idCategoria: null,
  idGrupo: null,
  idProveedorHabitual: null,
  idAlicuotaIva: 1,
  unidadVenta: 'Unidad',
  unidadesPorBulto: null,
  esProducto: true,
  controlaLote: false,
  acumulaEnVenta: true,
  costoLista: null,
  descuentoProveedor: null,
  costoNominal: null,
}

function cambios(idArticulo: number, sobrescribir: Partial<CambiosDeUnArticulo> = {}): CambiosDeUnArticulo {
  return { idArticulo, campos: [], actual: valores, nuevo: valores, precios: [], ...sobrescribir }
}

const conCampo = (id: number) => cambios(id, { campos: ['costo_lista'] })
const conPrecio = (id: number) =>
  cambios(id, {
    precios: [{ idListaPrecio: 2, actual: { vigente: 1, pendiente: null }, nuevo: { vigente: 2, pendiente: null } }],
  })

function resultado(articulos: CambiosDeUnArticulo[]): ResultadoDeAgrupacion {
  return { idFamilia: 7, nombre: 'Sabores', idArticuloReferencia: 31, articulos }
}

describe('LIMITE_DE_ARTICULOS', () => {
  it('es el tope de destinos por pedido de la API', () => {
    expect(LIMITE_DE_ARTICULOS).toBe(100)
  })
})

describe('cambiaAlgo', () => {
  it('un artículo ya alineado no cambia nada', () => {
    expect(cambiaAlgo(cambios(1))).toBe(false)
  })

  it('un campo compartido que difiere es un cambio', () => {
    expect(cambiaAlgo(conCampo(1))).toBe(true)
  })

  it('un precio que difiere es un cambio, aunque ningún campo cambie', () => {
    expect(cambiaAlgo(conPrecio(1))).toBe(true)
  })
})

describe('avisoDeAgregado', () => {
  it('con un solo artículo habla en singular', () => {
    expect(avisoDeAgregado(resultado([cambios(1)]))).toBe('Se agregó 1 artículo a la familia "Sabores".')
  })

  it('con varios habla en plural', () => {
    expect(avisoDeAgregado(resultado([cambios(1), cambios(2), cambios(3)]))).toBe('Se agregaron 3 artículos a la familia "Sabores".')
  })

  it('agrega cuántos cambiaron sus valores o sus precios, contando los que cambian solo de precio', () => {
    expect(avisoDeAgregado(resultado([conCampo(1), conPrecio(2), cambios(3)]))).toBe(
      'Se agregaron 3 artículos a la familia "Sabores". 2 cambiaron sus valores o sus precios para igualar a la referencia.',
    )
  })

  it('con uno solo que cambió dice "1 cambió"', () => {
    expect(avisoDeAgregado(resultado([conCampo(1), cambios(2)]))).toContain('1 cambió sus valores o sus precios')
  })

  it('si ninguno cambió no agrega nada de cambios', () => {
    expect(avisoDeAgregado(resultado([cambios(1), cambios(2)]))).not.toContain('cambi')
  })
})

describe('avisoDeCreacion', () => {
  it('cuenta la referencia, que es miembro aunque no figure entre los artículos del resultado', () => {
    expect(avisoDeCreacion(resultado([cambios(1), cambios(2)]))).toBe('Se creó la familia "Sabores" con 3 artículos.')
  })

  it('una familia que nace con la referencia sola tiene "1 artículo"', () => {
    expect(avisoDeCreacion(resultado([]))).toBe('Se creó la familia "Sabores" con 1 artículo.')
  })
})
