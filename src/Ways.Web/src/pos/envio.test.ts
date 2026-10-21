import { describe, expect, it } from 'vitest'
import {
  importeDeEnvioAplicado,
  MENSAJE_DE_ENVIO_SIN_CLIENTE,
  MENSAJE_DE_ENVIO_SIN_DOMICILIO,
  mensajeDeRechazoDeEnvio,
  motivoDeEnvioBloqueado,
  telefonoDeContacto,
} from './envio'

describe('motivoDeEnvioBloqueado', () => {
  it('sin cliente o con Consumidor Final pide un cliente con domicilio', () => {
    expect(motivoDeEnvioBloqueado(null)).toBe(MENSAJE_DE_ENVIO_SIN_CLIENTE)
    expect(motivoDeEnvioBloqueado({ esConsumidorFinal: true, domicilio: 'Mitre 123' })).toBe(MENSAJE_DE_ENVIO_SIN_CLIENTE)
  })

  it.each([null, '', '   '])('un cliente identificado con domicilio %j no puede recibir el envío', (domicilio) => {
    expect(motivoDeEnvioBloqueado({ esConsumidorFinal: false, domicilio })).toBe(MENSAJE_DE_ENVIO_SIN_DOMICILIO)
  })

  it('un cliente identificado con domicilio no tiene motivo de bloqueo', () => {
    expect(motivoDeEnvioBloqueado({ esConsumidorFinal: false, domicilio: 'Mitre 123' })).toBeNull()
  })
})

describe('importeDeEnvioAplicado', () => {
  const base = { conEnvio: true, importe: 1500, hayLineas: true }

  it('devuelve el importe redondeado al peso cuando hay envío, importe y líneas', () => {
    expect(importeDeEnvioAplicado(base)).toBe(1500)
    expect(importeDeEnvioAplicado({ ...base, importe: 1500.4 })).toBe(1500)
  })

  it.each([
    ['sin envío', { conEnvio: false }],
    ['sin líneas', { hayLineas: false }],
    ['importe desconocido', { importe: null }],
    ['importe 0', { importe: 0 }],
    ['importe no numérico', { importe: Number.NaN }],
  ])('es 0 %s', (_, cambio) => {
    expect(importeDeEnvioAplicado({ ...base, ...cambio })).toBe(0)
  })
})

describe('mensajeDeRechazoDeEnvio', () => {
  it.each(['envio_requiere_cliente', 'envio_requiere_domicilio', 'envio_no_admitido'])('traduce %s', (codigo) => {
    expect(mensajeDeRechazoDeEnvio(codigo)).toEqual(expect.any(String))
  })

  it('devuelve null para un código ajeno', () => {
    expect(mensajeDeRechazoDeEnvio('turno_no_abierto')).toBeNull()
  })
})

describe('telefonoDeContacto', () => {
  it('usa el teléfono y, si no hay, el celular', () => {
    expect(telefonoDeContacto({ telefono: '4444-0000', celular: '11-5555' })).toBe('4444-0000')
    expect(telefonoDeContacto({ telefono: '  ', celular: '11-5555' })).toBe('11-5555')
    expect(telefonoDeContacto({ telefono: null, celular: '11-5555' })).toBe('11-5555')
  })

  it('devuelve null sin ninguno de los dos', () => {
    expect(telefonoDeContacto({ telefono: null, celular: ' ' })).toBeNull()
  })
})
