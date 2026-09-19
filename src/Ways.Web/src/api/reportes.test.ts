import { afterEach, describe, expect, it, vi } from 'vitest'
import type {
  FiltrosDeBreakdown,
  FiltrosDeBreakdownConPv,
  FiltrosDeHistoricoDeCajas,
  FiltrosDeRentabilidad,
  FiltrosDeReporte,
  FiltrosDeReporteDeArticulos,
  FiltrosDeTesoreria,
  FiltrosDeTopArticulos,
} from './reportes'

const apiGetMock = vi.fn(() => Promise.resolve(undefined))

vi.mock('./cliente', () => ({
  api: { get: (...args: unknown[]) => apiGetMock(...(args as [])) },
}))

const {
  clienteDeReportes,
  construirQueryDeBreakdown,
  construirQueryDeBreakdownConPv,
  construirQueryDeHistoricoDeCajas,
  construirQueryDeReporte,
  construirQueryDeReporteDeArticulos,
  construirQueryDeTesoreria,
  filtrosDeHistoricoDeCajasVacios,
  filtrosDeReporteDeArticulosVacios,
  rangoUltimosSieteDias,
  rutasDeExportacion,
} = await import('./reportes')

function filtrosFixture(sobrescribir: Partial<FiltrosDeReporte> = {}): FiltrosDeReporte {
  return {
    idEmpresa: 1,
    idPuntoVenta: null,
    desde: '2026-08-05',
    hasta: '2026-08-11',
    granularidad: 'Dia',
    ...sobrescribir,
  }
}

function filtrosBreakdownFixture(sobrescribir: Partial<FiltrosDeBreakdown> = {}): FiltrosDeBreakdown {
  return { idEmpresa: 1, desde: '2026-08-05', hasta: '2026-08-11', ...sobrescribir }
}

function filtrosBreakdownConPvFixture(sobrescribir: Partial<FiltrosDeBreakdownConPv> = {}): FiltrosDeBreakdownConPv {
  return { idEmpresa: 1, idPuntoVenta: null, desde: '2026-08-05', hasta: '2026-08-11', ...sobrescribir }
}

function filtrosTopArticulosFixture(sobrescribir: Partial<FiltrosDeTopArticulos> = {}): FiltrosDeTopArticulos {
  return { idEmpresa: 1, idPuntoVenta: null, desde: '2026-08-05', hasta: '2026-08-11', limite: null, ...sobrescribir }
}

function filtrosRentabilidadFixture(sobrescribir: Partial<FiltrosDeRentabilidad> = {}): FiltrosDeRentabilidad {
  return { idEmpresa: 1, idPuntoVenta: null, desde: '2026-08-05', hasta: '2026-08-11', incluirEstimados: false, ...sobrescribir }
}

function filtrosHistoricoDeCajasFixture(sobrescribir: Partial<FiltrosDeHistoricoDeCajas> = {}): FiltrosDeHistoricoDeCajas {
  return { idPuntoVenta: null, desde: '2026-08-05', hasta: '2026-08-11', pagina: 1, tamanio: 25, ...sobrescribir }
}

function filtrosTesoreriaFixture(sobrescribir: Partial<FiltrosDeTesoreria> = {}): FiltrosDeTesoreria {
  return { idPuntoVenta: 7, desde: '2026-08-05', hasta: '2026-08-11', pagina: 1, tamanio: 25, ...sobrescribir }
}

function filtrosReporteDeArticulosFixture(
  sobrescribir: Partial<FiltrosDeReporteDeArticulos> = {},
): FiltrosDeReporteDeArticulos {
  return { ...filtrosDeReporteDeArticulosVacios(), ...sobrescribir }
}

// ---- construirQueryDeHistoricoDeCajas / construirQueryDeTesoreria: mismo patrón de offset
// explícito que compras.ts/cuentaCorriente.ts — /cajas y /tesoreria filtran contra un timestamptz. --

function offsetEsperado(anio: number, mes: number, dia: number): string {
  const minutos = new Date(anio, mes - 1, dia).getTimezoneOffset()
  const signo = minutos > 0 ? '-' : '+'
  const minutosAbsolutos = Math.abs(minutos)
  const horas = String(Math.floor(minutosAbsolutos / 60)).padStart(2, '0')
  const restoMinutos = String(minutosAbsolutos % 60).padStart(2, '0')
  return `${signo}${horas}:${restoMinutos}`
}

describe('construirQueryDeReporte', () => {
  it('arma idEmpresa/desde/hasta/granularidad sin idPuntoVenta cuando es null', () => {
    const query = construirQueryDeReporte(filtrosFixture())

    expect(query).toBe('?idEmpresa=1&desde=2026-08-05&hasta=2026-08-11&granularidad=Dia')
  })

  it('agrega idPuntoVenta solo cuando está seteado', () => {
    const query = construirQueryDeReporte(filtrosFixture({ idPuntoVenta: 7 }))

    expect(query).toContain('idPuntoVenta=7')
  })

  it('propaga la granularidad tal cual (nombre del enum de C#, no camelCase)', () => {
    expect(construirQueryDeReporte(filtrosFixture({ granularidad: 'Semana' }))).toContain('granularidad=Semana')
    expect(construirQueryDeReporte(filtrosFixture({ granularidad: 'Mes' }))).toContain('granularidad=Mes')
  })
})

describe('construirQueryDeBreakdown', () => {
  it('arma idEmpresa/desde/hasta, sin granularidad ni idPuntoVenta (el backend no los lee en estas rutas)', () => {
    const query = construirQueryDeBreakdown(filtrosBreakdownFixture())

    expect(query).toBe('?idEmpresa=1&desde=2026-08-05&hasta=2026-08-11')
  })
})

describe('construirQueryDeBreakdownConPv', () => {
  it('omite idPuntoVenta cuando es null', () => {
    const query = construirQueryDeBreakdownConPv(filtrosBreakdownConPvFixture())

    expect(query).toBe('?idEmpresa=1&desde=2026-08-05&hasta=2026-08-11')
  })

  it('agrega idPuntoVenta solo cuando está seteado', () => {
    const query = construirQueryDeBreakdownConPv(filtrosBreakdownConPvFixture({ idPuntoVenta: 7 }))

    expect(query).toContain('idPuntoVenta=7')
  })
})

describe('clienteDeReportes.articulosTop', () => {
  it('reutiliza construirQueryDeBreakdownConPv y omite limite cuando es null', async () => {
    apiGetMock.mockClear()
    await clienteDeReportes.articulosTop(filtrosTopArticulosFixture())

    expect(apiGetMock).toHaveBeenCalledWith('/reportes/articulos/top?idEmpresa=1&desde=2026-08-05&hasta=2026-08-11')
  })

  it('agrega limite solo cuando está seteado, junto con idPuntoVenta', async () => {
    apiGetMock.mockClear()
    await clienteDeReportes.articulosTop(filtrosTopArticulosFixture({ idPuntoVenta: 7, limite: 10 }))

    expect(apiGetMock).toHaveBeenCalledWith('/reportes/articulos/top?idEmpresa=1&idPuntoVenta=7&desde=2026-08-05&hasta=2026-08-11&limite=10')
  })
})

describe('rutasDeExportacion', () => {
  it('ventasResumen reutiliza construirQueryDeReporte y suma formato=xlsx al final', () => {
    const ruta = rutasDeExportacion.ventasResumen(filtrosFixture())

    expect(ruta).toBe('/reportes/ventas/resumen/export?idEmpresa=1&desde=2026-08-05&hasta=2026-08-11&granularidad=Dia&formato=xlsx')
  })

  it('gastosResumen reutiliza construirQueryDeReporte y suma formato=xlsx al final', () => {
    const ruta = rutasDeExportacion.gastosResumen(filtrosFixture({ idPuntoVenta: 7 }))

    expect(ruta).toBe(
      '/reportes/gastos/resumen/export?idEmpresa=1&idPuntoVenta=7&desde=2026-08-05&hasta=2026-08-11&granularidad=Dia&formato=xlsx',
    )
  })

  it('rentabilidad omite incluirEstimados cuando es false, igual que clienteDeReportes.rentabilidad', () => {
    const ruta = rutasDeExportacion.rentabilidad(filtrosRentabilidadFixture())

    expect(ruta).toBe('/reportes/rentabilidad/export?idEmpresa=1&desde=2026-08-05&hasta=2026-08-11&formato=xlsx')
  })

  it('rentabilidad agrega incluirEstimados=true cuando está tildado', () => {
    const ruta = rutasDeExportacion.rentabilidad(filtrosRentabilidadFixture({ incluirEstimados: true }))

    expect(ruta).toBe('/reportes/rentabilidad/export?idEmpresa=1&desde=2026-08-05&hasta=2026-08-11&incluirEstimados=true&formato=xlsx')
  })

  it('historicoDeCajas reutiliza el offset de /cajas y suma formato=xlsx, sin pagina/tamanio', () => {
    const offsetDesde = offsetEsperado(2026, 8, 5)
    const offsetHasta = offsetEsperado(2026, 8, 11)
    const ruta = decodeURIComponent(rutasDeExportacion.historicoDeCajas({ idPuntoVenta: null, desde: '2026-08-05', hasta: '2026-08-11' }))

    expect(ruta).toBe(`/reportes/cajas/export?desde=2026-08-05T00:00:00${offsetDesde}&hasta=2026-08-11T23:59:59.999${offsetHasta}&formato=xlsx`)
  })

  it('historicoDeCajas agrega idPuntoVenta solo cuando está seteado', () => {
    const ruta = rutasDeExportacion.historicoDeCajas({ idPuntoVenta: 7, desde: '2026-08-05', hasta: '2026-08-11' })

    expect(ruta).toContain('idPuntoVenta=7')
  })

  it('tesoreria reutiliza el offset de /tesoreria y suma formato=xlsx, sin pagina/tamanio', () => {
    const offsetDesde = offsetEsperado(2026, 8, 5)
    const offsetHasta = offsetEsperado(2026, 8, 11)
    const ruta = decodeURIComponent(rutasDeExportacion.tesoreria({ idPuntoVenta: 7, desde: '2026-08-05', hasta: '2026-08-11' }))

    expect(ruta).toBe(
      `/reportes/tesoreria/export?idPuntoVenta=7&desde=2026-08-05T00:00:00${offsetDesde}&hasta=2026-08-11T23:59:59.999${offsetHasta}&formato=xlsx`,
    )
  })
})

describe('construirQueryDeHistoricoDeCajas', () => {
  it('omite idPuntoVenta cuando es null, agrega desde/hasta con offset y pagina/tamanio', () => {
    const offsetDesde = offsetEsperado(2026, 8, 5)
    const offsetHasta = offsetEsperado(2026, 8, 11)
    const query = decodeURIComponent(construirQueryDeHistoricoDeCajas(filtrosHistoricoDeCajasFixture()))

    expect(query).toBe(`?desde=2026-08-05T00:00:00${offsetDesde}&hasta=2026-08-11T23:59:59.999${offsetHasta}&pagina=1&tamanio=25`)
  })

  it('agrega idPuntoVenta solo cuando está seteado', () => {
    const query = construirQueryDeHistoricoDeCajas(filtrosHistoricoDeCajasFixture({ idPuntoVenta: 7 }))

    expect(query).toContain('idPuntoVenta=7')
  })

  it('filtrosDeHistoricoDeCajasVacios arranca sin idPuntoVenta, con el rango de los últimos 7 días', () => {
    const filtros = filtrosDeHistoricoDeCajasVacios()

    expect(filtros.idPuntoVenta).toBeNull()
    expect(filtros.pagina).toBe(1)
    expect(filtros.tamanio).toBe(25)
    expect(filtros.desde).not.toBe('')
    expect(filtros.hasta).not.toBe('')
  })
})

describe('construirQueryDeTesoreria', () => {
  it('idPuntoVenta viaja siempre (obligatorio), con desde/hasta y pagina/tamanio', () => {
    const offsetDesde = offsetEsperado(2026, 8, 5)
    const offsetHasta = offsetEsperado(2026, 8, 11)
    const query = decodeURIComponent(construirQueryDeTesoreria(filtrosTesoreriaFixture()))

    expect(query).toBe(`?idPuntoVenta=7&desde=2026-08-05T00:00:00${offsetDesde}&hasta=2026-08-11T23:59:59.999${offsetHasta}&pagina=1&tamanio=25`)
  })
})

describe('construirQueryDeHistoricoDeCajas / construirQueryDeTesoreria — offset fijo (sin espejar la fórmula de la implementación)', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('minutos=180 (UTC-3) produce el literal -03:00 en ambos', () => {
    vi.spyOn(Date.prototype, 'getTimezoneOffset').mockReturnValue(180)

    expect(decodeURIComponent(construirQueryDeHistoricoDeCajas(filtrosHistoricoDeCajasFixture()))).toContain('desde=2026-08-05T00:00:00-03:00')
    expect(decodeURIComponent(construirQueryDeTesoreria(filtrosTesoreriaFixture()))).toContain('desde=2026-08-05T00:00:00-03:00')
  })
})

describe('clienteDeReportes.historicoDeCajas / clienteDeReportes.tesoreria', () => {
  it('historicoDeCajas pega contra /reportes/cajas con el query armado', async () => {
    apiGetMock.mockClear()
    await clienteDeReportes.historicoDeCajas(filtrosHistoricoDeCajasFixture())

    expect(apiGetMock).toHaveBeenCalledWith(expect.stringMatching(/^\/reportes\/cajas\?/))
  })

  it('tesoreria pega contra /reportes/tesoreria con el query armado', async () => {
    apiGetMock.mockClear()
    await clienteDeReportes.tesoreria(filtrosTesoreriaFixture())

    expect(apiGetMock).toHaveBeenCalledWith(expect.stringMatching(/^\/reportes\/tesoreria\?idPuntoVenta=7/))
  })
})

describe('construirQueryDeReporteDeArticulos', () => {
  it('sin ningún filtro seteado, solo manda pagina/tamanio', () => {
    const query = construirQueryDeReporteDeArticulos(filtrosReporteDeArticulosFixture())

    expect(query).toBe('?pagina=1&tamanio=25')
  })

  it('agrega idArea solo cuando está seteado', () => {
    const query = construirQueryDeReporteDeArticulos(filtrosReporteDeArticulosFixture({ idArea: 3 }))

    expect(query).toContain('idArea=3')
  })

  it('agrega idCategoria y omite sinCategoria (mutuamente excluyentes del lado del backend)', () => {
    const query = construirQueryDeReporteDeArticulos(filtrosReporteDeArticulosFixture({ idCategoria: 5 }))

    expect(query).toContain('idCategoria=5')
    expect(query).not.toContain('sinCategoria')
  })

  it('agrega sinCategoria=true cuando está tildado, sin idCategoria', () => {
    const query = construirQueryDeReporteDeArticulos(filtrosReporteDeArticulosFixture({ sinCategoria: true }))

    expect(query).toContain('sinCategoria=true')
    expect(query).not.toContain('idCategoria=')
  })

  it('agrega idMarca/sinMarca, idGrupo/sinGrupo e idProveedor/sinProveedor igual que categoría', () => {
    expect(construirQueryDeReporteDeArticulos(filtrosReporteDeArticulosFixture({ idMarca: 1 }))).toContain('idMarca=1')
    expect(construirQueryDeReporteDeArticulos(filtrosReporteDeArticulosFixture({ sinMarca: true }))).toContain('sinMarca=true')
    expect(construirQueryDeReporteDeArticulos(filtrosReporteDeArticulosFixture({ idGrupo: 2 }))).toContain('idGrupo=2')
    expect(construirQueryDeReporteDeArticulos(filtrosReporteDeArticulosFixture({ sinGrupo: true }))).toContain('sinGrupo=true')
    expect(construirQueryDeReporteDeArticulos(filtrosReporteDeArticulosFixture({ idProveedor: 9 }))).toContain('idProveedor=9')
    expect(construirQueryDeReporteDeArticulos(filtrosReporteDeArticulosFixture({ sinProveedor: true }))).toContain('sinProveedor=true')
  })

  it('agrega soloIncompletos solo cuando está tildado', () => {
    expect(construirQueryDeReporteDeArticulos(filtrosReporteDeArticulosFixture())).not.toContain('soloIncompletos')
    expect(construirQueryDeReporteDeArticulos(filtrosReporteDeArticulosFixture({ soloIncompletos: true }))).toContain(
      'soloIncompletos=true',
    )
  })

  it('agrega activo solo cuando no es null, con su valor real (true o false)', () => {
    expect(construirQueryDeReporteDeArticulos(filtrosReporteDeArticulosFixture())).not.toContain('activo')
    expect(construirQueryDeReporteDeArticulos(filtrosReporteDeArticulosFixture({ activo: true }))).toContain('activo=true')
    expect(construirQueryDeReporteDeArticulos(filtrosReporteDeArticulosFixture({ activo: false }))).toContain('activo=false')
  })

  it('siempre manda pagina/tamanio, incluso con filtros', () => {
    const query = construirQueryDeReporteDeArticulos(filtrosReporteDeArticulosFixture({ idArea: 3, pagina: 2, tamanio: 10 }))

    expect(query).toContain('pagina=2')
    expect(query).toContain('tamanio=10')
  })

  it('filtrosDeReporteDeArticulosVacios arranca sin ningún filtro, página 1, tamaño 25', () => {
    const filtros = filtrosDeReporteDeArticulosVacios()

    expect(filtros).toEqual({
      idArea: null,
      idCategoria: null,
      sinCategoria: false,
      idMarca: null,
      sinMarca: false,
      idGrupo: null,
      sinGrupo: false,
      idProveedor: null,
      sinProveedor: false,
      soloIncompletos: false,
      activo: null,
      pagina: 1,
      tamanio: 25,
    })
  })
})

describe('clienteDeReportes.articulos', () => {
  it('pega contra /reportes/articulos con el query armado por construirQueryDeReporteDeArticulos', async () => {
    apiGetMock.mockClear()
    await clienteDeReportes.articulos(filtrosReporteDeArticulosFixture({ idArea: 3 }))

    expect(apiGetMock).toHaveBeenCalledWith('/reportes/articulos?idArea=3&pagina=1&tamanio=25')
  })
})

describe('rutasDeExportacion.articulos — lección del proyecto: la ruta de descarga tiene que usar EXACTAMENTE los mismos filtros que el listado', () => {
  it('sin ningún filtro, la ruta queda solo con formato=xlsx', () => {
    const ruta = rutasDeExportacion.articulos(filtrosReporteDeArticulosFixture())

    expect(ruta).toBe('/reportes/articulos/export?formato=xlsx')
  })

  it('nunca manda pagina/tamanio (el export no pagina)', () => {
    const ruta = rutasDeExportacion.articulos(filtrosReporteDeArticulosFixture({ pagina: 3, tamanio: 50 }))

    expect(ruta).not.toContain('pagina')
    expect(ruta).not.toContain('tamanio')
  })

  it('propaga cada filtro seteado igual que el listado', () => {
    const filtros = filtrosReporteDeArticulosFixture({
      idArea: 3,
      sinCategoria: true,
      idMarca: 1,
      idGrupo: 2,
      sinProveedor: true,
      soloIncompletos: true,
      activo: true,
    })

    const ruta = rutasDeExportacion.articulos(filtros)

    expect(ruta).toBe(
      '/reportes/articulos/export?idArea=3&sinCategoria=true&idMarca=1&idGrupo=2&sinProveedor=true&soloIncompletos=true&activo=true&formato=xlsx',
    )
  })
})

describe('rangoUltimosSieteDias', () => {
  it('devuelve [hoy - 6 días, hoy] — 7 días inclusive', () => {
    const rango = rangoUltimosSieteDias(new Date(2026, 7, 11)) // 11 de agosto de 2026

    expect(rango).toEqual({ desde: '2026-08-05', hasta: '2026-08-11' })
  })

  it('cruza el límite de mes correctamente', () => {
    const rango = rangoUltimosSieteDias(new Date(2026, 7, 3)) // 3 de agosto de 2026

    expect(rango).toEqual({ desde: '2026-07-28', hasta: '2026-08-03' })
  })

  it('usa la fecha local, no UTC (sin desfasaje de un día)', () => {
    const rango = rangoUltimosSieteDias(new Date(2026, 0, 1, 0, 30)) // 1 de enero, 00:30 local

    expect(rango.hasta).toBe('2026-01-01')
  })
})
