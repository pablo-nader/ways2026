import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  clienteDeCuentaDePersonal,
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

  it('omite el límite que está vacío', () => {
    expect(construirQueryDeCuentaDePersonal({ ...base, desde: '' })).toBe('?pagina=2&tamanio=25')
  })

  it('con histórico no manda el rango', () => {
    expect(construirQueryDeCuentaDePersonal({ ...base, hasta: '2026-10-01', historico: true })).toBe(
      '?historico=true&pagina=2&tamanio=25',
    )
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
