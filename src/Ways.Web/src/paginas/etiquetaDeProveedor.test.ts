import { describe, expect, it } from 'vitest'
import { etiquetaDeProveedor } from './etiquetaDeProveedor'

describe('etiquetaDeProveedor', () => {
  it('usa el nombre de fantasía cuando existe', () => {
    expect(etiquetaDeProveedor({ razonSocial: 'Distribuidora Sur S.R.L.', nombreFantasia: 'Sur' })).toBe('Sur')
  })

  it('cae a la razón social cuando el nombre de fantasía es null', () => {
    expect(etiquetaDeProveedor({ razonSocial: 'Distribuidora Sur S.R.L.', nombreFantasia: null })).toBe(
      'Distribuidora Sur S.R.L.',
    )
  })

  it('cae a la razón social cuando el nombre de fantasía está vacío o en blanco', () => {
    expect(etiquetaDeProveedor({ razonSocial: 'Distribuidora Sur S.R.L.', nombreFantasia: '' })).toBe(
      'Distribuidora Sur S.R.L.',
    )
    expect(etiquetaDeProveedor({ razonSocial: 'Distribuidora Sur S.R.L.', nombreFantasia: '   ' })).toBe(
      'Distribuidora Sur S.R.L.',
    )
  })

  it('recorta los espacios del nombre de fantasía', () => {
    expect(etiquetaDeProveedor({ razonSocial: 'X', nombreFantasia: '  Sur ' })).toBe('Sur')
  })
})
