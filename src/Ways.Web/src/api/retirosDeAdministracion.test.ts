import { describe, expect, it } from 'vitest'
import {
  COLUMNAS_ORDENABLES_DE_RETIROS,
  construirQueryDeRetiros,
  esColumnaOrdenableDeRetiros,
  filtrosDeRetirosVacios,
  type FiltrosDeRetiros,
} from './caja'

function query(cambios: Partial<FiltrosDeRetiros> = {}, orden: Parameters<typeof construirQueryDeRetiros>[1] = null) {
  return new URLSearchParams(construirQueryDeRetiros({ ...filtrosDeRetirosVacios(), ...cambios }, orden).slice(1))
}

describe('construirQueryDeRetiros', () => {
  it('sin filtros ni orden solo viajan la página y el tamaño', () => {
    expect(construirQueryDeRetiros(filtrosDeRetirosVacios())).toBe('?pagina=1&tamanio=25')
  })

  it.each<[Partial<FiltrosDeRetiros>, string, string]>([
    [{ desde: '2026-11-01' }, 'desde', '2026-11-01'],
    [{ hasta: '2026-11-30' }, 'hasta', '2026-11-30'],
    [{ idPuntoVenta: 7 }, 'idPuntoVenta', '7'],
    [{ idTurnoCaja: ' 55 ' }, 'idTurnoCaja', '55'],
    [{ idEmpleado: 3 }, 'idEmpleado', '3'],
    [{ importeDesde: 10.5 }, 'importeDesde', '10.5'],
    [{ importeHasta: 0 }, 'importeHasta', '0'],
    [{ motivo: ' proveedor ' }, 'motivo', 'proveedor'],
    [{ turnoCerrado: true }, 'turnoCerrado', 'true'],
    [{ conciliado: false }, 'conciliado', 'false'],
    [{ esRetiroDeCierre: true }, 'esRetiroDeCierre', 'true'],
  ])('un filtro %j envía solo %s', (cambios, clave, valor) => {
    const parametros = query(cambios)

    expect(parametros.get(clave)).toBe(valor)
    expect([...parametros.keys()].sort()).toEqual([clave, 'pagina', 'tamanio'].sort())
  })

  it('un turno que no es un entero y un motivo de solo espacios no viajan', () => {
    const parametros = query({ idTurnoCaja: '12x', motivo: '  ' })

    expect(parametros.has('idTurnoCaja')).toBe(false)
    expect(parametros.has('motivo')).toBe(false)
  })

  it('el orden viaja como orden y direccion, y sin orden no viaja ninguno', () => {
    const conOrden = query({}, { columna: 'esRetiroDeCierre', direccion: 'ascendente' })

    expect(conOrden.get('orden')).toBe('esRetiroDeCierre')
    expect(conOrden.get('direccion')).toBe('ascendente')
    expect(query().has('orden')).toBe(false)
    expect(query().has('direccion')).toBe(false)
  })
})

describe('esColumnaOrdenableDeRetiros', () => {
  it.each([...COLUMNAS_ORDENABLES_DE_RETIROS])('acepta %s', (columna) => {
    expect(esColumnaOrdenableDeRetiros(columna)).toBe(true)
  })

  it('rechaza una columna desconocida', () => {
    expect(esColumnaOrdenableDeRetiros('acciones')).toBe(false)
  })
})
