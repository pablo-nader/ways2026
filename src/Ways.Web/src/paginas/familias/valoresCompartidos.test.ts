import { describe, expect, it } from 'vitest'
import type { UnidadVenta, ValoresCompartidosDeLaFamilia } from '../../api/tipos'
import { CAMPOS_COMPARTIDOS } from '../articulos/familia'
import { formatearValorCompartido, mapaDeNombres, nombresDeCatalogoVacios, type NombresDeCatalogo } from './valoresCompartidos'

function valores(sobrescribir: Partial<ValoresCompartidosDeLaFamilia> = {}): ValoresCompartidosDeLaFamilia {
  return {
    idArea: 1,
    idCategoria: 2,
    idGrupo: 3,
    idProveedorHabitual: 4,
    idAlicuotaIva: 5,
    unidadVenta: 'Peso',
    unidadesPorBulto: 12,
    esProducto: true,
    controlaLote: false,
    acumulaEnVenta: true,
    costoLista: 1234.5,
    descuentoProveedor: 5,
    costoNominal: 99,
    ...sobrescribir,
  }
}

const nombres: NombresDeCatalogo = {
  areas: new Map([[1, 'Almacén']]),
  categorias: new Map([[2, 'Bebidas']]),
  grupos: new Map([[3, 'Lácteos']]),
  proveedores: new Map([[4, 'Alfa SA']]),
  alicuotas: new Map([[5, 'IVA 21%']]),
}

describe('formatearValorCompartido', () => {
  it.each([
    ['idArea', 'Almacén'],
    ['idCategoria', 'Bebidas'],
    ['idGrupo', 'Lácteos'],
    ['idProveedorHabitual', 'Alfa SA'],
    ['idAlicuotaIva', 'IVA 21%'],
    ['unidadVenta', 'Por peso'],
    ['unidadesPorBulto', '12'],
    ['esProducto', 'Sí'],
    ['controlaLote', 'No'],
    ['acumulaEnVenta', 'Sí'],
    ['costoLista', '$ 1.234,50'],
    ['descuentoProveedor', '5%'],
    ['costoNominal', '$ 99,00'],
  ] as const)('%s se muestra como "%s"', (clave, esperado) => {
    expect(formatearValorCompartido(clave, valores(), nombres)).toBe(esperado)
  })

  it('una unidad de venta que el cliente no conoce se muestra tal cual, sin romper', () => {
    expect(formatearValorCompartido('unidadVenta', valores({ unidadVenta: 'Unidad' }), nombres)).toBe('Por unidad')
    expect(formatearValorCompartido('unidadVenta', valores({ unidadVenta: 'Docena' as unknown as UnidadVenta }), nombres)).toBe('Docena')
  })

  it('los trece campos compartidos tienen formato: la prueba de arriba cubre cada uno', () => {
    expect(CAMPOS_COMPARTIDOS.map((c) => c.clave).sort()).toEqual(
      [
        'idArea',
        'idCategoria',
        'idGrupo',
        'idProveedorHabitual',
        'idAlicuotaIva',
        'unidadVenta',
        'unidadesPorBulto',
        'esProducto',
        'controlaLote',
        'acumulaEnVenta',
        'costoLista',
        'descuentoProveedor',
        'costoNominal',
      ].sort(),
    )
  })

  it('un id de catálogo que el servidor presenta como null (fila dada de baja) es "Sin asignar"', () => {
    const sinNada = valores({ idArea: null, idCategoria: null, idGrupo: null, idProveedorHabitual: null })

    expect(formatearValorCompartido('idArea', sinNada, nombres)).toBe('Sin asignar')
    expect(formatearValorCompartido('idCategoria', sinNada, nombres)).toBe('Sin asignar')
    expect(formatearValorCompartido('idGrupo', sinNada, nombres)).toBe('Sin asignar')
    expect(formatearValorCompartido('idProveedorHabitual', sinNada, nombres)).toBe('Sin asignar')
  })

  it('un id cuyo nombre no se conoce (el catálogo no cargó) se muestra con su código', () => {
    const vacios = nombresDeCatalogoVacios()

    expect(formatearValorCompartido('idArea', valores(), vacios)).toBe('#1')
    expect(formatearValorCompartido('idCategoria', valores(), vacios)).toBe('#2')
    expect(formatearValorCompartido('idGrupo', valores(), vacios)).toBe('#3')
    expect(formatearValorCompartido('idProveedorHabitual', valores(), vacios)).toBe('#4')
    expect(formatearValorCompartido('idAlicuotaIva', valores(), vacios)).toBe('#5')
  })

  it('un decimal sin valor es una raya, no un cero', () => {
    const sinValores = valores({ unidadesPorBulto: null, costoLista: null, descuentoProveedor: null, costoNominal: null })

    expect(formatearValorCompartido('unidadesPorBulto', sinValores, nombres)).toBe('—')
    expect(formatearValorCompartido('costoLista', sinValores, nombres)).toBe('—')
    expect(formatearValorCompartido('descuentoProveedor', sinValores, nombres)).toBe('—')
    expect(formatearValorCompartido('costoNominal', sinValores, nombres)).toBe('—')
  })

  it('un cero es un valor: "$ 0,00" y "0%"', () => {
    const ceros = valores({ costoLista: 0, descuentoProveedor: 0 })

    expect(formatearValorCompartido('costoLista', ceros, nombres)).toBe('$ 0,00')
    expect(formatearValorCompartido('descuentoProveedor', ceros, nombres)).toBe('0%')
  })

  it('la unidad por unidad se muestra "Por unidad"', () => {
    expect(formatearValorCompartido('unidadVenta', valores({ unidadVenta: 'Unidad' }), nombres)).toBe('Por unidad')
  })

  it('los booleanos son Sí o No, uno por uno', () => {
    const v = valores({ esProducto: false, controlaLote: true, acumulaEnVenta: false })

    expect(formatearValorCompartido('esProducto', v, nombres)).toBe('No')
    expect(formatearValorCompartido('controlaLote', v, nombres)).toBe('Sí')
    expect(formatearValorCompartido('acumulaEnVenta', v, nombres)).toBe('No')
  })
})

describe('mapaDeNombres', () => {
  it('indexa cada fila por su id con el nombre que dice la función', () => {
    const mapa = mapaDeNombres([{ id: 1, razon: 'Alfa' }, { id: 7, razon: 'Beta' }], (p) => p.razon.toUpperCase())

    expect([...mapa.entries()]).toEqual([
      [1, 'ALFA'],
      [7, 'BETA'],
    ])
  })

  it('sin filas es un mapa vacío', () => {
    expect(mapaDeNombres([], () => 'x').size).toBe(0)
  })
})

describe('nombresDeCatalogoVacios', () => {
  it('trae los cinco catálogos, cada uno vacío', () => {
    const vacios = nombresDeCatalogoVacios()

    expect(Object.keys(vacios).sort()).toEqual(['alicuotas', 'areas', 'categorias', 'grupos', 'proveedores'])
    expect(Object.values(vacios).every((m) => m.size === 0)).toBe(true)
  })
})
