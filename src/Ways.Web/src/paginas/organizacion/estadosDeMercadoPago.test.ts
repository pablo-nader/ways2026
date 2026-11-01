import { describe, expect, it } from 'vitest'
import {
  etiquetaDeCampoFaltante,
  etiquetaDeModoDeTerminal,
  etiquetasDeCamposFaltantes,
  presentacionDeCaja,
  presentacionDeLocal,
  tieneAsignacionVieja,
} from './estadosDeMercadoPago'

describe('presentacionDeLocal', () => {
  it.each([
    ['sin_conexion', 'Sin conexión'],
    ['no_registrada', 'No registrada'],
    ['registrada', 'Registrada'],
    ['desactualizada', 'Desactualizada'],
  ])('%s se rotula %s', (estado, etiqueta) => {
    expect(presentacionDeLocal(estado).etiqueta).toBe(etiqueta)
  })

  it('registrada es verde y desactualizada advierte', () => {
    expect(presentacionDeLocal('registrada').clase).toBe('bg-success')
    expect(presentacionDeLocal('desactualizada').clase).toContain('bg-warning')
  })

  it('un estado que no conoce cae en desconocido sin romper', () => {
    expect(presentacionDeLocal('algo_nuevo')).toEqual({ etiqueta: 'Estado desconocido', clase: 'bg-secondary' })
  })
})

describe('presentacionDeCaja', () => {
  it.each([
    ['sin_conexion', 'Sin conexión'],
    ['local_no_registrado', 'Sucursal sin registrar'],
    ['no_registrada', 'No registrada'],
    ['registrada', 'Registrada'],
    ['desactualizada', 'Desactualizada'],
  ])('%s se rotula %s', (estado, etiqueta) => {
    expect(presentacionDeCaja(estado).etiqueta).toBe(etiqueta)
  })

  it('un estado que no conoce cae en desconocido', () => {
    expect(presentacionDeCaja('x').etiqueta).toBe('Estado desconocido')
  })
})

describe('etiquetas de campos faltantes', () => {
  it.each([
    ['nombre', 'Nombre'],
    ['calle', 'Calle'],
    ['altura', 'Altura (número)'],
    ['ciudad', 'Ciudad'],
    ['provincia', 'Provincia'],
    ['latitud', 'Latitud'],
    ['longitud', 'Longitud'],
  ])('%s se rotula %s', (campo, etiqueta) => {
    expect(etiquetaDeCampoFaltante(campo)).toBe(etiqueta)
  })

  it('un campo desconocido se muestra tal cual', () => {
    expect(etiquetaDeCampoFaltante('codigo_postal')).toBe('codigo_postal')
  })

  it('une los rótulos en el orden recibido', () => {
    expect(etiquetasDeCamposFaltantes(['altura', 'latitud', 'longitud'])).toBe('Altura (número), Latitud, Longitud')
  })

  it('sin campos devuelve el texto vacío', () => {
    expect(etiquetasDeCamposFaltantes([])).toBe('')
  })
})

describe('etiquetaDeModoDeTerminal', () => {
  it('rotula los dos modos conocidos', () => {
    expect(etiquetaDeModoDeTerminal('PDV')).toBe('Punto de venta (PDV)')
    expect(etiquetaDeModoDeTerminal('STANDALONE')).toBe('Independiente (STANDALONE)')
  })

  it('un modo nuevo se muestra crudo y la ausencia dice sin dato', () => {
    expect(etiquetaDeModoDeTerminal('BLOCKED')).toBe('BLOCKED')
    expect(etiquetaDeModoDeTerminal(null)).toBe('Sin dato')
    expect(etiquetaDeModoDeTerminal('')).toBe('Sin dato')
  })
})

describe('tieneAsignacionVieja', () => {
  it('terminal guardada que ya no figura en la caja es una asignación vieja', () => {
    expect(tieneAsignacionVieja({ idTerminalAsignada: 'PAX_A910__1', asignadaVigente: false })).toBe(true)
  })

  it('terminal guardada y vigente no se avisa', () => {
    expect(tieneAsignacionVieja({ idTerminalAsignada: 'PAX_A910__1', asignadaVigente: true })).toBe(false)
  })

  it('sin terminal guardada no hay nada que avisar aunque la vigencia sea falsa', () => {
    expect(tieneAsignacionVieja({ idTerminalAsignada: null, asignadaVigente: false })).toBe(false)
  })
})
