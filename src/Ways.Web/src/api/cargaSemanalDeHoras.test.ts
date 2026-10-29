import { beforeEach, describe, expect, it, vi } from 'vitest'
import { clienteDeCargaSemanalDeHoras, rutaDeSemana } from './cargaSemanalDeHoras'

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

describe('rutaDeSemana', () => {
  it('sin desde no manda parámetro: el servidor responde la semana en curso', () => {
    expect(rutaDeSemana(null)).toBe('/personal/horas/semana')
  })

  it('con desde lo manda como fecha plana', () => {
    expect(rutaDeSemana('2026-10-26')).toBe('/personal/horas/semana?desde=2026-10-26')
  })
})

describe('clienteDeCargaSemanalDeHoras', () => {
  it('obtener pide la semana a la ruta del servidor', async () => {
    getMock.mockResolvedValue({ desde: '2026-10-26' })

    await expect(clienteDeCargaSemanalDeHoras.obtener('2026-10-26')).resolves.toEqual({ desde: '2026-10-26' })
    expect(getMock).toHaveBeenCalledWith('/personal/horas/semana?desde=2026-10-26')
  })

  it('guardar envía la solicitud tal cual a la misma ruta', async () => {
    postMock.mockResolvedValue({ movimientosCreados: 1, jornadasConfirmadas: 1 })
    const solicitud = { dias: [{ idPersonal: 3, fecha: '2026-10-26', horas: 7.5 }] }

    await expect(clienteDeCargaSemanalDeHoras.guardar(solicitud)).resolves.toEqual({
      movimientosCreados: 1,
      jornadasConfirmadas: 1,
    })
    expect(postMock).toHaveBeenCalledWith('/personal/horas/semana', solicitud)
  })
})
