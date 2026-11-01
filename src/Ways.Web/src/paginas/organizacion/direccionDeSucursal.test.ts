import { describe, expect, it } from 'vitest'
import type { SucursalListado } from '../../api/tipos'
import {
  aDireccionDeLaApi,
  aDireccionDelFormulario,
  DIRECCION_VACIA,
  PROVINCIAS_ARGENTINAS,
} from './direccionDeSucursal'

const base: SucursalListado = {
  id: 1,
  idTenant: 2,
  idEmpresa: 3,
  nombre: 'Centro',
  razonSocialEmpresa: 'Sur SRL',
  cantidadPuntosVenta: 1,
}

describe('PROVINCIAS_ARGENTINAS', () => {
  it('son las 24 jurisdicciones, sin repetir, con acentos', () => {
    expect(PROVINCIAS_ARGENTINAS).toHaveLength(24)
    expect(new Set(PROVINCIAS_ARGENTINAS).size).toBe(24)
    expect(PROVINCIAS_ARGENTINAS).toContain('Córdoba')
    expect(PROVINCIAS_ARGENTINAS).toContain('Ciudad Autónoma de Buenos Aires')
  })
})

describe('aDireccionDelFormulario', () => {
  it('una sucursal sin dirección (o de una respuesta vieja) pasa a vacío', () => {
    expect(aDireccionDelFormulario(base)).toEqual(DIRECCION_VACIA)
  })

  it('las coordenadas pasan a texto y cero se conserva', () => {
    const f = aDireccionDelFormulario({ ...base, calle: 'Mitre', provincia: 'Santa Fe', latitud: -32.9468, longitud: 0 })

    expect(f.calle).toBe('Mitre')
    expect(f.provincia).toBe('Santa Fe')
    expect(f.latitud).toBe('-32.9468')
    expect(f.longitud).toBe('0')
  })
})

describe('aDireccionDeLaApi', () => {
  it('el formulario vacío viaja todo nulo', () => {
    expect(aDireccionDeLaApi(DIRECCION_VACIA)).toEqual({
      error: null,
      direccion: {
        calle: null,
        altura: null,
        ciudad: null,
        provincia: null,
        codigoPostal: null,
        latitud: null,
        longitud: null,
        referencia: null,
      },
    })
  })

  it('recorta el texto y deja nulo el que queda en blanco', () => {
    const r = aDireccionDeLaApi({ ...DIRECCION_VACIA, calle: ' Colón ', altura: '1234 bis', ciudad: '   ' })

    expect(r).toMatchObject({ error: null, direccion: { calle: 'Colón', altura: '1234 bis', ciudad: null } })
  })

  it('acepta coma o punto en las coordenadas, con signo', () => {
    const r = aDireccionDeLaApi({ ...DIRECCION_VACIA, latitud: '-31,4201', longitud: '-64.1888' })

    expect(r).toMatchObject({ error: null, direccion: { latitud: -31.4201, longitud: -64.1888 } })
  })

  it('cero es una coordenada válida, no ausente', () => {
    const r = aDireccionDeLaApi({ ...DIRECCION_VACIA, latitud: '0', longitud: '0' })

    expect(r).toMatchObject({ error: null, direccion: { latitud: 0, longitud: 0 } })
  })

  it('una sola coordenada es un error', () => {
    expect(aDireccionDeLaApi({ ...DIRECCION_VACIA, latitud: '-31' }).error).toMatch(/las dos vacías/)
    expect(aDireccionDeLaApi({ ...DIRECCION_VACIA, longitud: '-64' }).error).toMatch(/las dos vacías/)
  })

  it('rechaza lo que no es número y lo que se sale de rango', () => {
    expect(aDireccionDeLaApi({ ...DIRECCION_VACIA, latitud: 'abc', longitud: '1' }).error).toMatch(/latitud no es un número/)
    expect(aDireccionDeLaApi({ ...DIRECCION_VACIA, latitud: '90,5', longitud: '1' }).error).toMatch(/entre -90 y 90/)
    expect(aDireccionDeLaApi({ ...DIRECCION_VACIA, latitud: '1', longitud: '-180,1' }).error).toMatch(/entre -180 y 180/)
  })

  it('acepta los límites', () => {
    expect(aDireccionDeLaApi({ ...DIRECCION_VACIA, latitud: '-90', longitud: '180' }).error).toBeNull()
  })
})
