import { describe, expect, it } from 'vitest'
import waysCss from './ways.css?raw'

describe('ways.css — libro de tesorería', () => {
  it('el importe negativo usa el token --ws-danger, cuyo contraste con las superficies verifica tema.test.ts en ambos temas', () => {
    const regla = /\.libro-importe--negativo\s*\{([^}]*)\}/.exec(waysCss)

    expect(regla?.[1]).toMatch(/color:\s*var\(--ws-danger\)/)
  })
})
