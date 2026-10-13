import { describe, expect, it } from 'vitest'
import type { MovimientoDeCuentaListado, TipoMovimientoCuenta, TotalesDeCuentaEnElRango } from '../../api/tipos'
import {
  avisoDeMediosSinCuenta,
  cuentaElegidaVigente,
  descuentosDelRango,
  etiquetaDeTipoMovimientoCuenta,
  formatearPorcentaje,
  nombreDelEmpleado,
  nombreDelMedio,
} from './cuentas'

function movimiento(sobrescribir: Partial<MovimientoDeCuentaListado> = {}): MovimientoDeCuentaListado {
  return {
    id: 1,
    fecha: '2026-08-05T08:00:00Z',
    tipo: 'Cobro',
    concepto: 'Cobros',
    ingreso: 100,
    egreso: 0,
    final: 100,
    idTurnoCaja: 5,
    idMedioPago: 3,
    nombreMedioPago: 'QR',
    idEmpleado: 9,
    nombreEmpleado: 'ana',
    baseBruta: null,
    porcentajeAplicado: null,
    ...sobrescribir,
  }
}

const totales: TotalesDeCuentaEnElRango = {
  cobrado: 1000,
  comision: 0.1,
  retencionIibb: 0.2,
  retencionIva: 10.5,
  retencionGanancias: 0,
  ajustes: 0,
  otros: 0,
  neto: 989.2,
}

describe('etiquetaDeTipoMovimientoCuenta', () => {
  it.each<[TipoMovimientoCuenta, string]>([
    ['Cobro', 'Cobro'],
    ['Comision', 'Comisión'],
    ['RetencionIibb', 'Retención IIBB'],
    ['RetencionIva', 'Retención IVA'],
    ['RetencionGanancias', 'Retención Ganancias'],
    ['Ajuste', 'Ajuste'],
    ['Transferencia', 'Transferencia'],
    ['Deposito', 'Depósito'],
    ['Retiro', 'Retiro'],
  ])('%s se muestra como "%s"', (tipo, etiqueta) => {
    expect(etiquetaDeTipoMovimientoCuenta(tipo)).toBe(etiqueta)
  })
})

describe('descuentosDelRango', () => {
  it('suma comisión y las tres retenciones en centavos enteros', () => {
    expect(descuentosDelRango(totales)).toBe(10.8)
  })

  it('no incluye el cobrado ni los ajustes', () => {
    expect(descuentosDelRango({ ...totales, comision: 0, retencionIibb: 0, retencionIva: 0, ajustes: 50 })).toBe(0)
  })
})

describe('nombreDelEmpleado y nombreDelMedio', () => {
  it('el empleado sin nombre cae al id', () => {
    expect(nombreDelEmpleado(movimiento())).toBe('ana')
    expect(nombreDelEmpleado(movimiento({ nombreEmpleado: null }))).toBe('Empleado #9')
  })

  it('sin medio es "—" y con el medio dado de baja es "(no disponible)", nunca lo mismo', () => {
    expect(nombreDelMedio(movimiento({ idMedioPago: null, nombreMedioPago: null }))).toBe('—')
    expect(nombreDelMedio(movimiento({ idMedioPago: 3, nombreMedioPago: null }))).toBe('(no disponible)')
    expect(nombreDelMedio(movimiento())).toBe('QR')
  })
})

describe('formatearPorcentaje', () => {
  it('usa coma decimal y no agrega ceros', () => {
    expect(formatearPorcentaje(5.5)).toBe('5,5 %')
    expect(formatearPorcentaje(21)).toBe('21 %')
    expect(formatearPorcentaje(0.125)).toBe('0,125 %')
  })
})

describe('avisoDeMediosSinCuenta', () => {
  it('sin medios no hay aviso', () => {
    expect(avisoDeMediosSinCuenta([])).toBeNull()
  })

  it('con uno o varios nombra los medios en singular o plural', () => {
    expect(avisoDeMediosSinCuenta([{ id: 1, nombre: 'QR' }])).toBe('Lo cobrado con el medio QR no entra a ninguna cuenta.')
    expect(
      avisoDeMediosSinCuenta([
        { id: 1, nombre: 'QR' },
        { id: 2, nombre: 'Transferencia' },
      ]),
    ).toBe('Lo cobrado con los medios QR, Transferencia no entra a ninguna cuenta.')
  })
})

describe('cuentaElegidaVigente', () => {
  const cuentas = { idEmpresa: 1, mediosSinCuenta: [], cuentas: [{ id: 4, nombre: 'MP', activo: true, saldoInicial: 0, saldoActual: 0, fechaUltimoMovimiento: null }] }

  it('solo vale mientras la cuenta siga en la lista', () => {
    expect(cuentaElegidaVigente(cuentas, 4)).toBe(4)
    expect(cuentaElegidaVigente(cuentas, 5)).toBeNull()
    expect(cuentaElegidaVigente(cuentas, null)).toBeNull()
    expect(cuentaElegidaVigente(null, 4)).toBeNull()
  })
})
