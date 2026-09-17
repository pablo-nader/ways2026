import { describe, expect, it } from 'vitest'
import { URL_DESCARGA_POS_POR_DEFECTO, urlDescargaPos } from './descargaPos'

describe('urlDescargaPos', () => {
  it('usa la URL por defecto cuando no hay override de entorno', () => {
    expect(urlDescargaPos({})).toBe(URL_DESCARGA_POS_POR_DEFECTO)
  })

  it('respeta VITE_URL_DESCARGA_POS cuando está definida', () => {
    expect(urlDescargaPos({ VITE_URL_DESCARGA_POS: 'https://ejemplo.test/WaysPOS-setup.exe' })).toBe(
      'https://ejemplo.test/WaysPOS-setup.exe',
    )
  })

  it('ignora un override vacío y usa la URL por defecto', () => {
    expect(urlDescargaPos({ VITE_URL_DESCARGA_POS: '' })).toBe(URL_DESCARGA_POS_POR_DEFECTO)
  })
})
