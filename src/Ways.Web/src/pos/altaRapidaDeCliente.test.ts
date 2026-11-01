import { describe, expect, it } from 'vitest'
import { altaRapidaDeClienteDisponible, armarAltaRapida, MOTIVO_ALTA_RAPIDA_SIN_CONEXION } from './altaRapidaDeCliente'
import { conClienteEnInstantanea } from './instantaneaOffline'
import { clienteAdmitidoOffline } from './reglasOffline'
import type { ClienteListado, InstantaneaDePos } from '../api/tipos'

describe('armarAltaRapida', () => {
  it('recorta los valores y manda null en los opcionales en blanco', () => {
    expect(armarAltaRapida('  Ana Pérez ', '   ', '')).toEqual({ nombre: 'Ana Pérez', telefono: null, domicilio: null })
  })

  it('conserva teléfono y domicilio recortados', () => {
    expect(armarAltaRapida('Ana', ' 351 555 ', ' Mitre 12 ')).toEqual({ nombre: 'Ana', telefono: '351 555', domicilio: 'Mitre 12' })
  })

  it('devuelve null si el nombre queda vacío', () => {
    expect(armarAltaRapida('   ', 'x', 'y')).toBeNull()
  })
})

describe('altaRapidaDeClienteDisponible', () => {
  it('sin conexión no está disponible y explica por qué', () => {
    expect(altaRapidaDeClienteDisponible({ enLinea: false, inerte: false })).toEqual({
      habilitado: false,
      motivo: MOTIVO_ALTA_RAPIDA_SIN_CONEXION,
    })
  })

  it('con conexión y pantalla operable está disponible', () => {
    expect(altaRapidaDeClienteDisponible({ enLinea: true, inerte: false }).habilitado).toBe(true)
  })

  it('con la pantalla inerte no está disponible aunque haya conexión', () => {
    expect(altaRapidaDeClienteDisponible({ enLinea: true, inerte: true }).habilitado).toBe(false)
  })
})

describe('conClienteEnInstantanea', () => {
  const base = {
    momento: '2026-09-20T10:00:00.000Z',
    idPuntoVenta: 7,
    articulos: [],
    clientes: [],
    mediosDePago: [],
    toleranciaPago: 0,
  } as InstantaneaDePos
  const listado: ClienteListado = {
    id: 50,
    numero: 12,
    nombre: 'Ana',
    apellido: null,
    razonSocial: null,
    tipoDocumento: null,
    numeroDocumento: null,
    idCondicionFiscal: 5,
    nacimiento: null,
    domicilio: 'Mitre 12',
    telefono: '351',
    celular: null,
    email: null,
    observaciones: null,
    idListaPrecio: 3,
    limiteCredito: 0,
    creditoIlimitado: false,
    saldo: 0,
    activo: true,
    idEmpresa: null,
    esConsumidorFinal: false,
  }

  it('agrega el cliente nuevo con su domicilio y lo hace admisible sin red', () => {
    const resultado = conClienteEnInstantanea(base, listado)
    expect(resultado.clientes).toHaveLength(1)
    expect(resultado.clientes[0]).toMatchObject({
      idCliente: 50,
      nombre: 'Ana',
      domicilio: 'Mitre 12',
      telefono: '351',
      limiteCredito: 0,
      creditoIlimitado: false,
      idListaPrecio: 3,
    })
    expect(clienteAdmitidoOffline({ id: 50, esConsumidorFinal: false }, resultado)).toBe(true)
    expect(base.clientes).toHaveLength(0)
  })

  it('reemplaza al cliente con el mismo id sin duplicarlo', () => {
    const una = conClienteEnInstantanea(base, listado)
    const dos = conClienteEnInstantanea(una, { ...listado, nombre: 'Ana María' })
    expect(dos.clientes).toHaveLength(1)
    expect(dos.clientes[0]?.nombre).toBe('Ana María')
  })

  it('mantiene el orden por número', () => {
    const una = conClienteEnInstantanea(base, { ...listado, id: 51, numero: 20 })
    const dos = conClienteEnInstantanea(una, listado)
    expect(dos.clientes.map((c) => c.numero)).toEqual([12, 20])
  })
})
