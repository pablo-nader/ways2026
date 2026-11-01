import { describe, expect, it } from 'vitest'
import {
  COLUMNAS_ORDENABLES_DE_VENTAS_DE_ADMINISTRACION,
  construirQueryDeVentasDeAdministracion,
  esColumnaOrdenableDeVentasDeAdministracion,
  filtrosDeVentasDeAdministracionVacios,
  type FiltrosDeVentasDeAdministracion,
} from './ventas'

function query(cambios: Partial<FiltrosDeVentasDeAdministracion> = {}, orden: Parameters<typeof construirQueryDeVentasDeAdministracion>[1] = null) {
  return new URLSearchParams(construirQueryDeVentasDeAdministracion({ ...filtrosDeVentasDeAdministracionVacios(), ...cambios }, orden).slice(1))
}

describe('construirQueryDeVentasDeAdministracion', () => {
  it('sin filtros ni orden solo viajan la página y el tamaño', () => {
    expect(construirQueryDeVentasDeAdministracion(filtrosDeVentasDeAdministracionVacios())).toBe('?pagina=1&tamanio=25')
  })

  it.each<[Partial<FiltrosDeVentasDeAdministracion>, string, string]>([
    [{ desde: '2026-11-01' }, 'desde', '2026-11-01'],
    [{ hasta: '2026-11-30' }, 'hasta', '2026-11-30'],
    [{ numero: ' 123 ' }, 'numero', '123'],
    [{ idTipoComprobante: 6 }, 'idTipoComprobante', '6'],
    [{ idPuntoVenta: 7 }, 'idPuntoVenta', '7'],
    [{ idTurnoCaja: '55' }, 'idTurnoCaja', '55'],
    [{ idEmpleado: 3 }, 'idEmpleado', '3'],
    [{ cliente: ' Perez ' }, 'cliente', 'Perez'],
    [{ idMedioPago: 2 }, 'idMedioPago', '2'],
    [{ totalDesde: 100.5 }, 'totalDesde', '100.5'],
    [{ totalHasta: 0 }, 'totalHasta', '0'],
    [{ estado: 'Anulado' }, 'estado', 'Anulado'],
    [{ estadoFiscal: 'con_cae' }, 'estadoFiscal', 'con_cae'],
    [{ turnoCerrado: false }, 'turnoCerrado', 'false'],
    [{ conciliado: true }, 'conciliado', 'true'],
  ])('un filtro %j envía solo %s', (cambios, clave, valor) => {
    const parametros = query(cambios)

    expect(parametros.get(clave)).toBe(valor)
    expect([...parametros.keys()].sort()).toEqual([clave, 'pagina', 'tamanio'].sort())
  })

  it('un número o un turno que no es un entero no viaja', () => {
    const parametros = query({ numero: 'abc', idTurnoCaja: '1.5' })

    expect(parametros.has('numero')).toBe(false)
    expect(parametros.has('idTurnoCaja')).toBe(false)
  })

  it('un cliente de solo espacios no viaja', () => {
    expect(query({ cliente: '   ' }).has('cliente')).toBe(false)
  })

  it('el orden viaja como orden y direccion, y sin orden no viaja ninguno', () => {
    const conOrden = query({}, { columna: 'estadoTurno', direccion: 'descendente' })

    expect(conOrden.get('orden')).toBe('estadoTurno')
    expect(conOrden.get('direccion')).toBe('descendente')
    expect(query().has('orden')).toBe(false)
    expect(query().has('direccion')).toBe(false)
  })

  it('la página y el tamaño pedidos viajan tal cual', () => {
    const parametros = query({ pagina: 3, tamanio: 100 })

    expect(parametros.get('pagina')).toBe('3')
    expect(parametros.get('tamanio')).toBe('100')
  })
})

describe('esColumnaOrdenableDeVentasDeAdministracion', () => {
  it.each([...COLUMNAS_ORDENABLES_DE_VENTAS_DE_ADMINISTRACION])('acepta %s', (columna) => {
    expect(esColumnaOrdenableDeVentasDeAdministracion(columna)).toBe(true)
  })

  it('rechaza una columna desconocida', () => {
    expect(esColumnaOrdenableDeVentasDeAdministracion('acciones')).toBe(false)
  })
})
