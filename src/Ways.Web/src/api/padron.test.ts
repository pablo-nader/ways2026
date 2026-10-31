import { describe, expect, it } from 'vitest'
import {
  construirQueryDePadron,
  hayFiltrosEnPadron,
  ultimaPaginaDePadron,
  type ConsultaDePadron,
} from './padron'
import {
  COLUMNAS_ORDENABLES_DE_CLIENTES,
  FILTROS_DE_CLIENTES_VACIOS,
  esColumnaOrdenableDeClientes,
} from './clientes'
import {
  COLUMNAS_ORDENABLES_DE_PROVEEDORES,
  FILTROS_DE_PROVEEDORES_VACIOS,
  esColumnaOrdenableDeProveedores,
} from './proveedores'

type Filtros = { texto: string; desde: number | null; opcion: string }
type Consulta = ConsultaDePadron<Filtros, 'a' | 'b'>

const VACIOS: Filtros = { texto: '', desde: null, opcion: '' }

function consulta(sobrescribir: Partial<Consulta> = {}): Consulta {
  return { filtros: VACIOS, orden: null, pagina: 1, tamanio: 25, ...sobrescribir }
}

describe('construirQueryDePadron', () => {
  it('una consulta sin nada puesto no manda ningún parámetro', () => {
    expect(construirQueryDePadron(consulta())).toBe('')
  })

  it('omite los filtros vacíos o en blanco y recorta los textos', () => {
    expect(construirQueryDePadron(consulta({ filtros: { texto: '  ana  ', desde: null, opcion: '   ' } }))).toBe(
      '?texto=ana',
    )
  })

  it('manda un importe como número con punto decimal, y el cero también', () => {
    expect(construirQueryDePadron(consulta({ filtros: { ...VACIOS, desde: 12.5 } }))).toBe('?desde=12.5')
    expect(construirQueryDePadron(consulta({ filtros: { ...VACIOS, desde: 0 } }))).toBe('?desde=0')
  })

  it('manda orden y dirección siempre juntos, tras los filtros', () => {
    const query = construirQueryDePadron(
      consulta({ filtros: { ...VACIOS, opcion: 'true' }, orden: { columna: 'b', direccion: 'descendente' } }),
    )

    expect(query).toBe('?opcion=true&orden=b&direccion=descendente')
  })

  it('manda la página y el tamaño solo cuando no son los del servidor', () => {
    expect(construirQueryDePadron(consulta({ pagina: 2 }))).toBe('?pagina=2')
    expect(construirQueryDePadron(consulta({ tamanio: 50 }))).toBe('?tamanio=50')
    expect(construirQueryDePadron(consulta({ pagina: 3, tamanio: 100 }))).toBe('?pagina=3&tamanio=100')
  })

  it('codifica el texto', () => {
    expect(construirQueryDePadron(consulta({ filtros: { ...VACIOS, texto: 'a&b=c' } }))).toBe('?texto=a%26b%3Dc')
  })
})

describe('hayFiltrosEnPadron', () => {
  it('distingue vacío, en blanco, cero y texto', () => {
    expect(hayFiltrosEnPadron(VACIOS)).toBe(false)
    expect(hayFiltrosEnPadron({ ...VACIOS, texto: '  ' })).toBe(false)
    expect(hayFiltrosEnPadron({ ...VACIOS, desde: 0 })).toBe(true)
    expect(hayFiltrosEnPadron({ ...VACIOS, texto: 'x' })).toBe(true)
  })
})

describe('ultimaPaginaDePadron', () => {
  it.each([
    [0, 25, 1],
    [25, 25, 1],
    [26, 25, 2],
    [60, 25, 3],
    [50, 50, 1],
  ])('con %i filas de a %i hay %i página(s)', (total, tamanio, esperada) => {
    expect(ultimaPaginaDePadron(total, tamanio)).toBe(esperada)
  })
})

/** Los nombres de estas listas son los del servidor (`OrdenDeClientes.Claves`, `OrdenDeProveedores.Claves`): si cambian
 * de un lado, el otro rechazaría el pedido con 400 `orden_invalido`. */
describe('las claves que acepta el servidor', () => {
  it('clientes', () => {
    expect([...COLUMNAS_ORDENABLES_DE_CLIENTES].sort()).toEqual(
      ['documento', 'email', 'estado', 'nombre', 'numero', 'telefono'],
    )
    expect(esColumnaOrdenableDeClientes('numero')).toBe(true)
    expect(esColumnaOrdenableDeClientes('acciones')).toBe(false)
  })

  it('proveedores', () => {
    expect([...COLUMNAS_ORDENABLES_DE_PROVEEDORES].sort()).toEqual(
      ['cuit', 'email', 'estado', 'margen', 'razonSocial', 'telefono'],
    )
    expect(esColumnaOrdenableDeProveedores('razonSocial')).toBe(true)
    expect(esColumnaOrdenableDeProveedores('Margen')).toBe(false)
  })

  it('los filtros de clientes son los parámetros de GET /api/clientes', () => {
    expect(Object.keys(FILTROS_DE_CLIENTES_VACIOS).sort()).toEqual(
      ['activo', 'busqueda', 'documento', 'email', 'nombre', 'numero', 'telefono'],
    )
  })

  it('los filtros de proveedores son los parámetros de GET /api/proveedores', () => {
    expect(Object.keys(FILTROS_DE_PROVEEDORES_VACIOS).sort()).toEqual(
      ['activo', 'busqueda', 'cuitContiene', 'email', 'margenDesde', 'margenHasta', 'razonSocial', 'telefono'],
    )
  })
})
