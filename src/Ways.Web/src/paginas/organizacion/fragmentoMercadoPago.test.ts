import { afterEach, describe, expect, it } from 'vitest'
import { limpiarFragmento, tomarRetornoDeMercadoPago } from './fragmentoMercadoPago'

afterEach(() => window.history.replaceState(null, '', '/'))

function ponerUrl(url: string) {
  window.history.replaceState(null, '', url)
}

describe('limpiarFragmento', () => {
  it('quita el fragmento y conserva la ruta y la consulta', () => {
    ponerUrl('/organizacion/empresas?a=1#mercadopago=completar&code=x')
    limpiarFragmento()

    expect(window.location.hash).toBe('')
    expect(window.location.pathname + window.location.search).toBe('/organizacion/empresas?a=1')
  })
})

describe('tomarRetornoDeMercadoPago', () => {
  it('devuelve el retorno válido y borra el fragmento', () => {
    ponerUrl('/organizacion/empresas#mercadopago=completar&empresa=10&code=TG-1&state=s')

    expect(tomarRetornoDeMercadoPago()).toEqual({ tipo: 'completar', idEmpresa: 10, code: 'TG-1', state: 's' })
    expect(window.location.hash).toBe('')
  })

  it('borra también un retorno incompleto o con empresa inválida y no devuelve nada', () => {
    ponerUrl('/organizacion/empresas#mercadopago=completar&code=solo-el-code')

    expect(tomarRetornoDeMercadoPago()).toBeNull()
    expect(window.location.hash).toBe('')

    ponerUrl('/organizacion/empresas#mercadopago=completar&empresa=abc&code=x&state=y')

    expect(tomarRetornoDeMercadoPago()).toBeNull()
    expect(window.location.hash).toBe('')
  })

  it('no toca un fragmento que no es de Mercado Pago', () => {
    ponerUrl('/organizacion/empresas#seccion-2')

    expect(tomarRetornoDeMercadoPago()).toBeNull()
    expect(window.location.hash).toBe('#seccion-2')
  })
})
