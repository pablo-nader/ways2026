import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  ampliarRangoParaFecha,
  clienteDeCuentaDePersonal,
  conElRangoEfectivo,
  construirQueryDeCuentaDePersonal,
  filtrosDeCuentaDePersonalIniciales,
} from './cuentaDePersonal'
import type { SolicitudDeAjusteDePersonal, SolicitudDeCargaDeHoras, SolicitudDePagoDePersonal } from './tipos'

const getMock = vi.fn()
const postMock = vi.fn()

vi.mock('./cliente', () => ({
  api: {
    get: (...args: unknown[]) => getMock(...args),
    post: (...args: unknown[]) => postMock(...args),
  },
}))

beforeEach(() => {
  getMock.mockReset()
  postMock.mockReset()
})

describe('filtrosDeCuentaDePersonalIniciales', () => {
  it('arranca un mes atrás, sin tope superior y en la primera página', () => {
    expect(filtrosDeCuentaDePersonalIniciales(new Date(2026, 9, 12))).toEqual({
      desde: '2026-09-12',
      hasta: '',
      historico: false,
      pagina: 1,
      tamanio: 25,
    })
  })
})

describe('construirQueryDeCuentaDePersonal', () => {
  const base = { desde: '2026-09-12', hasta: '', historico: false, pagina: 2, tamanio: 25 }

  it('manda el rango como fechas planas y la paginación', () => {
    expect(construirQueryDeCuentaDePersonal({ ...base, hasta: '2026-10-01' })).toBe(
      '?desde=2026-09-12&hasta=2026-10-01&pagina=2&tamanio=25',
    )
  })

  it('no manda el límite que está vacío (sin "desde" el servidor aplica su default de un mes)', () => {
    expect(construirQueryDeCuentaDePersonal({ ...base, desde: '' })).toBe('?pagina=2&tamanio=25')
  })

  it('con histórico no manda el rango', () => {
    expect(construirQueryDeCuentaDePersonal({ ...base, hasta: '2026-10-01', historico: true })).toBe(
      '?historico=true&pagina=2&tamanio=25',
    )
  })
})

describe('conElRangoEfectivo', () => {
  const filtros = { desde: '', hasta: '', historico: false, pagina: 3, tamanio: 25 }

  it('un "desde" vacío pasa a ser la ventana que el servidor aplicó, sin tocar la página', () => {
    expect(conElRangoEfectivo(filtros, { historico: false, desde: '2026-09-12', hasta: null })).toEqual({
      ...filtros,
      desde: '2026-09-12',
      hasta: '',
    })
  })

  it('toma también el "hasta" que aplicó el servidor', () => {
    expect(conElRangoEfectivo(filtros, { historico: false, desde: '2026-08-31', hasta: '2026-09-30' })).toMatchObject({
      desde: '2026-08-31',
      hasta: '2026-09-30',
    })
  })

  it('con histórico el servidor no devuelve rango y los filtros quedan como están', () => {
    const conRango = { ...filtros, desde: '2026-09-12', hasta: '2026-10-01', historico: true }

    expect(conElRangoEfectivo(conRango, { historico: true, desde: null, hasta: null })).toBe(conRango)
  })
})

describe('ampliarRangoParaFecha', () => {
  const filtros = { desde: '2026-09-12', hasta: '', historico: false, pagina: 1, tamanio: 25 }

  it('una fecha anterior al "desde" lo adelanta a esa fecha', () => {
    expect(ampliarRangoParaFecha(filtros, '2026-09-01')).toEqual({ filtros: { ...filtros, desde: '2026-09-01' }, ampliado: true })
  })

  it('una fecha posterior al "hasta" lo extiende a esa fecha', () => {
    const acotados = { ...filtros, hasta: '2026-10-01' }

    expect(ampliarRangoParaFecha(acotados, '2026-10-05')).toEqual({ filtros: { ...acotados, hasta: '2026-10-05' }, ampliado: true })
  })

  it('una fecha dentro del rango, o justo en un borde, no lo cambia', () => {
    const acotados = { ...filtros, hasta: '2026-10-01' }

    for (const fecha of ['2026-09-12', '2026-09-20', '2026-10-01']) {
      expect(ampliarRangoParaFecha(acotados, fecha)).toEqual({ filtros: acotados, ampliado: false })
    }
  })

  it('sin tope superior una fecha futura no hace falta ampliar nada', () => {
    expect(ampliarRangoParaFecha(filtros, '2027-01-01')).toEqual({ filtros, ampliado: false })
  })

  it('con histórico o con un "desde" vacío no hay rango que ensanchar', () => {
    const historico = { ...filtros, historico: true }
    const sinDesde = { ...filtros, desde: '' }

    expect(ampliarRangoParaFecha(historico, '2020-01-01')).toEqual({ filtros: historico, ampliado: false })
    expect(ampliarRangoParaFecha(sinDesde, '2020-01-01')).toEqual({ filtros: sinDesde, ampliado: false })
  })
})

describe('clienteDeCuentaDePersonal', () => {
  it('obtener pide el estado de cuenta con el query', () => {
    clienteDeCuentaDePersonal.obtener(7, { desde: '', hasta: '', historico: true, pagina: 1, tamanio: 25 })

    expect(getMock).toHaveBeenCalledWith('/personal/7/cuenta?historico=true&pagina=1&tamanio=25')
  })

  it('obtenerPersonal lee un empleado por id', () => {
    clienteDeCuentaDePersonal.obtenerPersonal(7)

    expect(getMock).toHaveBeenCalledWith('/personal/7')
  })

  it('cargarHoras, registrarAjuste y pagar postean a su ruta con el cuerpo tal cual', () => {
    const horas: SolicitudDeCargaDeHoras = { fecha: '2026-10-12', horas: 8, valorHora: 100 }
    const ajuste: SolicitudDeAjusteDePersonal = { fecha: '2026-10-12', importe: -50, detalle: 'Corrección' }
    const pago: SolicitudDePagoDePersonal = { fecha: '2026-10-12', descuentoDeCuentaCorriente: 10 }

    clienteDeCuentaDePersonal.cargarHoras(7, horas)
    clienteDeCuentaDePersonal.registrarAjuste(7, ajuste)
    clienteDeCuentaDePersonal.pagar(7, pago)

    expect(postMock).toHaveBeenNthCalledWith(1, '/personal/7/horas', horas)
    expect(postMock).toHaveBeenNthCalledWith(2, '/personal/7/ajustes', ajuste)
    expect(postMock).toHaveBeenNthCalledWith(3, '/personal/7/pagos', pago)
  })
})
