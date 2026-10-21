import { describe, expect, it } from 'vitest'
import {
  importeDeEnvioAplicado,
  importeDeEnvioDeInstantanea,
  importeDeEnvioVigente,
  MENSAJE_DE_ENVIO_IMPORTE_NO_DISPONIBLE,
  MENSAJE_DE_ENVIO_SIN_CLIENTE,
  MENSAJE_DE_ENVIO_SIN_DOMICILIO,
  MENSAJE_DE_ENVIO_SIN_IMPORTE,
  MENSAJE_DE_ENVIO_SIN_IMPORTE_LOCAL,
  mensajeDeRechazoDeEnvio,
  motivoDeEnvioBloqueado,
  motivoDeEnvioPendiente,
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

describe('importeDeEnvioDeInstantanea', () => {
  it('devuelve el importe de la copia local', () => {
    expect(importeDeEnvioDeInstantanea({ importeEnvio: 1800 })).toBe(1800)
    expect(importeDeEnvioDeInstantanea({ importeEnvio: 0 })).toBe(0)
  })

  it.each([
    ['sin instantánea', null],
    ['una copia anterior al campo', {}],
    ['un importe negativo', { importeEnvio: -1 }],
    ['un importe no numérico', { importeEnvio: 'mil' as unknown as number }],
    ['un importe infinito', { importeEnvio: Number.POSITIVE_INFINITY }],
  ])('es null con %s', (_, instantanea) => {
    expect(importeDeEnvioDeInstantanea(instantanea)).toBeNull()
  })
})

describe('importeDeEnvioVigente', () => {
  it('con conexión manda el leído del servidor, aunque la copia local traiga otro', () => {
    expect(importeDeEnvioVigente({ hayConexion: true, importeEnLinea: 1500, instantanea: { importeEnvio: 1800 } })).toBe(1500)
    expect(importeDeEnvioVigente({ hayConexion: true, importeEnLinea: null, instantanea: { importeEnvio: 1800 } })).toBeNull()
  })

  it('sin conexión manda el de la copia local, aunque haya uno leído antes', () => {
    expect(importeDeEnvioVigente({ hayConexion: false, importeEnLinea: 1500, instantanea: { importeEnvio: 1800 } })).toBe(1800)
    expect(importeDeEnvioVigente({ hayConexion: false, importeEnLinea: 1500, instantanea: {} })).toBeNull()
  })
})

describe('motivoDeEnvioPendiente', () => {
  const cliente = { esConsumidorFinal: false, domicilio: 'Mitre 123' }
  const base = { conEnvio: true, cliente, hayConexion: true, importeVigente: 1500, errorAlLeerElImporte: false }

  it('sin envío o con todo en orden no hay motivo', () => {
    expect(motivoDeEnvioPendiente({ ...base, conEnvio: false, cliente: null, importeVigente: null })).toBeNull()
    expect(motivoDeEnvioPendiente(base)).toBeNull()
    expect(motivoDeEnvioPendiente({ ...base, hayConexion: false })).toBeNull()
  })

  it('el cliente se juzga antes que el importe', () => {
    expect(motivoDeEnvioPendiente({ ...base, cliente: null, importeVigente: null })).toBe(MENSAJE_DE_ENVIO_SIN_CLIENTE)
    expect(motivoDeEnvioPendiente({ ...base, cliente: { esConsumidorFinal: false, domicilio: ' ' } })).toBe(MENSAJE_DE_ENVIO_SIN_DOMICILIO)
  })

  it('sin importe: calculando, error de lectura o sin importe local según la conexión', () => {
    expect(motivoDeEnvioPendiente({ ...base, importeVigente: null })).toBe(MENSAJE_DE_ENVIO_SIN_IMPORTE)
    expect(motivoDeEnvioPendiente({ ...base, importeVigente: null, errorAlLeerElImporte: true })).toBe(MENSAJE_DE_ENVIO_IMPORTE_NO_DISPONIBLE)
    expect(motivoDeEnvioPendiente({ ...base, importeVigente: null, hayConexion: false, errorAlLeerElImporte: true })).toBe(
      MENSAJE_DE_ENVIO_SIN_IMPORTE_LOCAL,
    )
  })
})
