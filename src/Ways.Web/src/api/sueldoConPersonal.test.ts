import { describe, expect, it } from 'vitest'
import type { FormularioDePagoDeAdministracion } from './registroDePagos'
import {
  aSolicitudDePagoDePersonalDesdeAdministracion,
  aSolicitudDePagoDePersonalDesdeElPos,
  cambiosAlElegirCategoria,
  elegiblesComoPersonal,
  esPagoAPersonal,
  MOTIVO_PAGO_A_PERSONAL_SIN_CONEXION,
  ofreceElegirPersonal,
  pagoAPersonalDisponible,
} from './sueldoConPersonal'
import type { PersonalListado } from './tipos'

function formularioFixture(
  sobrescribir: Partial<FormularioDePagoDeAdministracion> = {},
): FormularioDePagoDeAdministracion {
  return {
    fecha: '2026-09-01',
    idEmpresa: 3,
    idPuntoVenta: '',
    categoria: 'Sueldos',
    idProveedor: '',
    idPersonal: 12,
    idArea: 2,
    concepto: '',
    detalle: '',
    numeroFactura: '',
    lineas: [{ clave: 1, idMedioPago: 7, importe: 1000, referencia: '' }],
    ...sobrescribir,
  }
}

describe('ofreceElegirPersonal', () => {
  it('solo la categoría Sueldos ofrece el selector de personal', () => {
    expect(ofreceElegirPersonal('Sueldos')).toBe(true)
    expect(ofreceElegirPersonal('Proveedor')).toBe(false)
    expect(ofreceElegirPersonal('Otros')).toBe(false)
  })
})

describe('esPagoAPersonal', () => {
  it('requiere Sueldos y un empleado elegido', () => {
    expect(esPagoAPersonal(formularioFixture())).toBe(true)
    expect(esPagoAPersonal(formularioFixture({ idPersonal: '' }))).toBe(false)
    expect(esPagoAPersonal(formularioFixture({ categoria: 'Otros' }))).toBe(false)
  })
})

describe('cambiosAlElegirCategoria', () => {
  it('salir de Sueldos limpia el empleado', () => {
    expect(cambiosAlElegirCategoria({ idProveedor: '', idPersonal: 12 }, 'Otros')).toEqual({
      categoria: 'Otros',
      idProveedor: '',
      idPersonal: '',
    })
  })

  it('entrar a Sueldos limpia el proveedor y conserva un empleado ya vacío', () => {
    expect(cambiosAlElegirCategoria({ idProveedor: 5, idPersonal: '' }, 'Sueldos')).toEqual({
      categoria: 'Sueldos',
      idProveedor: '',
      idPersonal: '',
    })
  })

  it('entre dos categorías que no son Sueldos conserva el proveedor', () => {
    expect(cambiosAlElegirCategoria({ idProveedor: 5, idPersonal: '' }, 'Servicios')).toEqual({
      categoria: 'Servicios',
      idProveedor: 5,
      idPersonal: '',
    })
  })
})

describe('aSolicitudDePagoDePersonalDesdeAdministracion', () => {
  it('paga de la tesorería de la empresa elegida, con una línea por medio y sin campos ajenos', () => {
    expect(aSolicitudDePagoDePersonalDesdeAdministracion(formularioFixture())).toEqual({
      fecha: '2026-09-01',
      idEmpresa: 3,
      origenFondos: 'Tesoreria',
      lineas: [{ idMedioPago: 7, importe: 1000 }],
    })
  })

  it('manda el punto de venta, el detalle recortado y la referencia de cada línea solo si hay', () => {
    const solicitud = aSolicitudDePagoDePersonalDesdeAdministracion(
      formularioFixture({
        idPuntoVenta: 9,
        detalle: '  Adelanto  ',
        lineas: [
          { clave: 1, idMedioPago: 7, importe: 600, referencia: ' Transf 1 ' },
          { clave: 2, idMedioPago: 8, importe: 400, referencia: '' },
        ],
      }),
    )
    expect(solicitud).toEqual({
      fecha: '2026-09-01',
      idEmpresa: 3,
      idPuntoVenta: 9,
      origenFondos: 'Tesoreria',
      lineas: [
        { idMedioPago: 7, importe: 600, referencia: 'Transf 1' },
        { idMedioPago: 8, importe: 400 },
      ],
      detalle: 'Adelanto',
    })
  })

  it('sin empresa elegida rechaza armar la solicitud', () => {
    expect(() => aSolicitudDePagoDePersonalDesdeAdministracion(formularioFixture({ idEmpresa: '' }))).toThrow()
  })
})

describe('elegiblesComoPersonal', () => {
  const empleado = (id: number, nombre: string): PersonalListado => ({ id, nombre }) as PersonalListado

  it('ordena por nombre sin distinguir tildes ni mayúsculas', () => {
    const lista = [empleado(1, 'Zoe'), empleado(2, 'Álvaro'), empleado(3, 'beto')]
    expect(elegiblesComoPersonal(lista).map((p) => p.nombre)).toEqual(['Álvaro', 'beto', 'Zoe'])
  })
})

describe('aSolicitudDePagoDePersonalDesdeElPos', () => {
  const lineas = [
    { clave: 1, idMedioPago: 1, importe: 500, referencia: '  ' },
    { clave: 2, idMedioPago: 3, importe: 250.5, referencia: ' cup-1 ' },
  ]

  it('sale siempre del cajón del turno, sin empresa ni descuento de cuenta corriente', () => {
    const solicitud = aSolicitudDePagoDePersonalDesdeElPos({ idPuntoVenta: 7, fecha: '2026-11-01', lineas, observaciones: '' })

    expect(solicitud).toEqual({
      fecha: '2026-11-01',
      idPuntoVenta: 7,
      origenFondos: 'CajaTurno',
      lineas: [
        { idMedioPago: 1, importe: 500 },
        { idMedioPago: 3, importe: 250.5, referencia: 'cup-1' },
      ],
    })
    expect(solicitud).not.toHaveProperty('idEmpresa')
    expect(solicitud).not.toHaveProperty('descuentoDeCuentaCorriente')
  })

  it('las observaciones recortadas viajan como detalle y en blanco se omiten', () => {
    expect(aSolicitudDePagoDePersonalDesdeElPos({ idPuntoVenta: 7, fecha: '2026-11-01', lineas, observaciones: '  adelanto ' }).detalle).toBe(
      'adelanto',
    )
    expect(aSolicitudDePagoDePersonalDesdeElPos({ idPuntoVenta: 7, fecha: '2026-11-01', lineas, observaciones: '   ' })).not.toHaveProperty(
      'detalle',
    )
  })
})

describe('pagoAPersonalDisponible', () => {
  it('sin conexión no se puede pagar a un empleado y dice por qué', () => {
    expect(pagoAPersonalDisponible({ enLinea: false })).toEqual({ habilitado: false, motivo: MOTIVO_PAGO_A_PERSONAL_SIN_CONEXION })
    expect(MOTIVO_PAGO_A_PERSONAL_SIN_CONEXION).toMatch(/conexión/)
  })

  it('con conexión queda habilitado', () => {
    expect(pagoAPersonalDisponible({ enLinea: true }).habilitado).toBe(true)
  })
})
