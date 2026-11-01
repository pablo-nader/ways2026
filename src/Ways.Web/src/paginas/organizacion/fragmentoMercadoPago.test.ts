import { afterEach, describe, expect, it } from 'vitest'
import { descartarRetornoDeMercadoPago, limpiarFragmento } from './fragmentoMercadoPago'

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

describe('descartarRetornoDeMercadoPago', () => {
  it('borra un fragmento de retorno de Mercado Pago', () => {
    ponerUrl('/organizacion/empresas#mercadopago=completar&empresa=10&code=TG-1&state=s')
    descartarRetornoDeMercadoPago()

    expect(window.location.hash).toBe('')
  })

  it('borra también un retorno incompleto: el code no debe quedar en la URL', () => {
    ponerUrl('/organizacion/empresas#mercadopago=completar&code=solo-el-code')
    descartarRetornoDeMercadoPago()

    expect(window.location.hash).toBe('')
  })

  it('no toca un fragmento que no es de Mercado Pago', () => {
    ponerUrl('/organizacion/empresas#seccion-2')
    descartarRetornoDeMercadoPago()

    expect(window.location.hash).toBe('#seccion-2')
  })
})
