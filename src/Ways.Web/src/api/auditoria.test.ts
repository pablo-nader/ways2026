import { describe, expect, it } from 'vitest'
import type { FiltrosDeConsultaDeAuditoria, FiltrosDeExportacionDeAuditoria } from './auditoria'

const { rutasDeExportacion, construirQueryDeConsultaDeAuditoria, puedeExportarAuditoria } = await import('./auditoria')

function offsetEsperado(anio: number, mes: number, dia: number): string {
  const minutos = new Date(anio, mes - 1, dia).getTimezoneOffset()
  const signo = minutos > 0 ? '-' : '+'
  const minutosAbsolutos = Math.abs(minutos)
  const horas = String(Math.floor(minutosAbsolutos / 60)).padStart(2, '0')
  const restoMinutos = String(minutosAbsolutos % 60).padStart(2, '0')
  return `${signo}${horas}:${restoMinutos}`
}

function filtrosFixture(sobrescribir: Partial<FiltrosDeExportacionDeAuditoria> = {}): FiltrosDeExportacionDeAuditoria {
  return {
    desde: '2026-08-05',
    hasta: '2026-08-11',
    accion: null,
    idActor: null,
    entidad: null,
    idEntidad: null,
    idPuntoVenta: null,
    ...sobrescribir,
  }
}

describe('rutasDeExportacion.auditoria', () => {
  it('aplica el mismo offset local que /cajas y /tesoreria, y suma formato=xlsx al final', () => {
    const offsetDesde = offsetEsperado(2026, 8, 5)
    const offsetHasta = offsetEsperado(2026, 8, 11)

    const ruta = decodeURIComponent(rutasDeExportacion.auditoria(filtrosFixture()))

    expect(ruta).toBe(
      `/auditoria/export?desde=2026-08-05T00:00:00${offsetDesde}&hasta=2026-08-11T23:59:59.999${offsetHasta}&formato=xlsx`,
    )
  })

  it('omite los 5 filtros opcionales cuando son null', () => {
    const ruta = rutasDeExportacion.auditoria(filtrosFixture())

    expect(ruta).not.toContain('accion=')
    expect(ruta).not.toContain('idActor=')
    expect(ruta).not.toContain('entidad=')
    expect(ruta).not.toContain('idEntidad=')
    expect(ruta).not.toContain('idPuntoVenta=')
  })

  it('agrega los 5 filtros opcionales solo cuando están seteados', () => {
    const ruta = rutasDeExportacion.auditoria(
      filtrosFixture({ accion: 'precio.cambio', idActor: 3, entidad: 'articulo', idEntidad: 41, idPuntoVenta: 7 }),
    )

    expect(ruta).toContain('accion=precio.cambio')
    expect(ruta).toContain('idActor=3')
    expect(ruta).toContain('entidad=articulo')
    expect(ruta).toContain('idEntidad=41')
    expect(ruta).toContain('idPuntoVenta=7')
  })

  // judgment-day ronda 1, finding 1: dos builders divergentes dejaban `desde`/`hasta` vacíos
  // guardados en la consulta JSON pero NO en el export (fechaIsoConOffset sobre un string vacío
  // ⇒ `T00:00:00+NaN:NaN`, un DateTimeOffset malformado que el servidor rechaza con 400). Con el
  // builder unificado (`construirQueryDeAlcanceDeAuditoria`) el caso vacío queda guardado en
  // AMBOS lados por construcción, no por disciplina.
  it('con desde/hasta vacíos, la URL de export NO contiene NaN (guardada por construcción, no por disciplina)', () => {
    const ruta = rutasDeExportacion.auditoria(filtrosFixture({ desde: '', hasta: '' }))

    expect(ruta).not.toContain('NaN')
    expect(ruta).not.toContain('desde=')
    expect(ruta).not.toContain('hasta=')
    expect(ruta).toBe('/auditoria/export?&formato=xlsx')
  })
})

describe('puedeExportarAuditoria', () => {
  it('es false si desde o hasta están vacíos', () => {
    expect(puedeExportarAuditoria({ desde: '', hasta: '2026-08-11' })).toBe(false)
    expect(puedeExportarAuditoria({ desde: '2026-08-05', hasta: '' })).toBe(false)
    expect(puedeExportarAuditoria({ desde: '', hasta: '' })).toBe(false)
  })

  it('es true cuando ambas fechas están seteadas', () => {
    expect(puedeExportarAuditoria({ desde: '2026-08-05', hasta: '2026-08-11' })).toBe(true)
  })
})

// judgment-day ronda 1, finding 2/4: la paridad JSON↔export solo estaba asertada para `accion` —
// borrar `idPuntoVenta`, `idActor`, `entidad` o `idEntidad` de un solo builder sobrevivía. Con el
// builder unificado esto es casi estructural, pero se asertan igual las DOS URLs completas: si
// alguien vuelve a divergir los builders, este test lo detecta sin depender de cuál filtro se
// borró.
describe('paridad JSON↔export — construirQueryDeConsultaDeAuditoria vs. rutasDeExportacion.auditoria', () => {
  function filtrosConsultaFixture(sobrescribir: Partial<FiltrosDeConsultaDeAuditoria> = {}): FiltrosDeConsultaDeAuditoria {
    return {
      desde: '2026-08-05',
      hasta: '2026-08-11',
      accion: null,
      idActor: null,
      entidad: null,
      idEntidad: null,
      idPuntoVenta: null,
      pagina: 1,
      tamanio: 25,
      ...sobrescribir,
    }
  }

  it('con los 7 filtros de alcance seteados, la consulta JSON y la URL de export llevan EXACTAMENTE los mismos filtros', () => {
    const filtrosCompletos = filtrosConsultaFixture({
      accion: 'precio.cambio',
      idActor: 3,
      entidad: 'articulo',
      idEntidad: 41,
      idPuntoVenta: 7,
    })

    const queryJson = construirQueryDeConsultaDeAuditoria(filtrosCompletos)
    const alcanceDesdeJson = queryJson.replace(/&pagina=\d+&tamanio=\d+$/, '').slice(1)

    const urlExport = rutasDeExportacion.auditoria(filtrosCompletos)
    const alcanceDesdeExport = urlExport.replace('/auditoria/export?', '').replace(/&formato=xlsx$/, '')

    expect(alcanceDesdeExport).toBe(alcanceDesdeJson)
    // Sanity check — que la comparación no esté vacía por un regex que no matcheó nada.
    expect(alcanceDesdeJson).toContain('idPuntoVenta=7')
    expect(alcanceDesdeJson).toContain('idActor=3')
    expect(alcanceDesdeJson).toContain('entidad=articulo')
    expect(alcanceDesdeJson).toContain('idEntidad=41')
  })
})
