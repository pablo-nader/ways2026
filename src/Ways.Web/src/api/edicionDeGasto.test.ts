import { describe, expect, it } from 'vitest'
import { ErrorApi } from './cliente'
import {
  ADVERTENCIA_TURNO_CERRADO,
  COPIA_GASTO_TURNO_CERRADO,
  advertenciasDeGastoDeAdministracion,
  aFormularioDeEdicion,
  aSolicitudDeEdicionDeGasto,
  copiaDeFalloDeEdicion,
  formularioDeEdicionCompleto,
  type FormularioDeEdicionDeGasto,
  type GastoEditable,
} from './gastos'

function gastoFixture(sobrescribir: Partial<GastoEditable> = {}): GastoEditable {
  return {
    categoria: 'Servicios',
    idProveedor: null,
    idArea: null,
    concepto: 'Luz',
    detalle: null,
    idMedioPago: 4,
    numeroFactura: null,
    importe: 1500,
    ...sobrescribir,
  }
}

function formularioFixture(sobrescribir: Partial<FormularioDeEdicionDeGasto> = {}): FormularioDeEdicionDeGasto {
  return { ...aFormularioDeEdicion(gastoFixture()), ...sobrescribir }
}

describe('aFormularioDeEdicion', () => {
  it('precarga cada campo y convierte los null opcionales en cadena o vacío', () => {
    expect(aFormularioDeEdicion(gastoFixture())).toEqual({
      importe: 1500,
      idMedioPago: 4,
      categoria: 'Servicios',
      idProveedor: '',
      idArea: '',
      concepto: 'Luz',
      detalle: '',
      numeroFactura: '',
    })
  })

  it('conserva proveedor, área, detalle y factura del gasto', () => {
    const f = aFormularioDeEdicion(
      gastoFixture({ categoria: 'Proveedor', idProveedor: 8, idArea: 3, detalle: 'Mes de agosto', numeroFactura: '0001-123' }),
    )
    expect(f).toMatchObject({ categoria: 'Proveedor', idProveedor: 8, idArea: 3, detalle: 'Mes de agosto', numeroFactura: '0001-123' })
  })
})

describe('aSolicitudDeEdicionDeGasto', () => {
  it('recorta los textos y manda null en los opcionales vacíos', () => {
    const solicitud = aSolicitudDeEdicionDeGasto(formularioFixture({ concepto: '  Luz  ', detalle: '   ', numeroFactura: '' }))
    expect(solicitud).toEqual({
      categoria: 'Servicios',
      idProveedor: null,
      idArea: null,
      concepto: 'Luz',
      detalle: null,
      idMedioPago: 4,
      numeroFactura: null,
      importe: 1500,
    })
  })

  it('manda proveedor, área, detalle y factura recortados cuando vienen cargados', () => {
    const solicitud = aSolicitudDeEdicionDeGasto(
      formularioFixture({ categoria: 'Proveedor', idProveedor: 8, idArea: 3, detalle: ' d ', numeroFactura: ' 0001-1 ' }),
    )
    expect(solicitud).toMatchObject({ idProveedor: 8, idArea: 3, detalle: 'd', numeroFactura: '0001-1' })
  })

  it('no incluye origen de fondos, punto de venta ni fecha (no se editan)', () => {
    expect(Object.keys(aSolicitudDeEdicionDeGasto(formularioFixture())).sort()).toEqual(
      ['categoria', 'concepto', 'detalle', 'idArea', 'idMedioPago', 'idProveedor', 'importe', 'numeroFactura'].sort(),
    )
  })
})

describe('formularioDeEdicionCompleto', () => {
  it('acepta un formulario con medio, concepto e importe positivo', () => {
    expect(formularioDeEdicionCompleto(formularioFixture())).toBe(true)
  })

  it.each([
    ['sin medio de pago', { idMedioPago: '' as const }],
    ['concepto en blanco', { concepto: '   ' }],
    ['importe vacío', { importe: null }],
    ['importe cero', { importe: 0 }],
    ['importe negativo', { importe: -5 }],
  ])('rechaza %s', (_nombre, cambio) => {
    expect(formularioDeEdicionCompleto(formularioFixture(cambio))).toBe(false)
  })
})

describe('copiaDeFalloDeEdicion', () => {
  it('409 gasto_turno_cerrado rinde la copia propia del turno cerrado', () => {
    expect(copiaDeFalloDeEdicion(new ErrorApi(409, 'gasto_turno_cerrado', 'texto del servidor'))).toBe(COPIA_GASTO_TURNO_CERRADO)
  })

  it('otro error de la API rinde el mensaje del servidor', () => {
    expect(copiaDeFalloDeEdicion(new ErrorApi(400, 'gasto_proveedor_ligado', 'No se puede cambiar el proveedor.'))).toBe(
      'No se puede cambiar el proveedor.',
    )
  })

  it('un mensaje del servidor vacío cae al texto genérico de la acción', () => {
    expect(copiaDeFalloDeEdicion(new ErrorApi(500, 'x', '  '), 'eliminar')).toBe('No se pudo eliminar el gasto.')
  })

  it('un error que no es de la API rinde el texto genérico', () => {
    expect(copiaDeFalloDeEdicion(new Error('red'))).toBe('No se pudo guardar el gasto.')
  })
})

describe('advertenciasDeGastoDeAdministracion', () => {
  const base = { turnoAbierto: false, idTurnoCaja: null, origenFondos: 'Tesoreria', categoria: 'Otros', idProveedor: null } as const

  it('gasto de caja de un turno cerrado avisa del recálculo del arqueo', () => {
    expect(
      advertenciasDeGastoDeAdministracion({ ...base, idTurnoCaja: 9, origenFondos: 'CajaTurno' }),
    ).toEqual([ADVERTENCIA_TURNO_CERRADO])
  })

  it('gasto de caja de un turno abierto no avisa nada', () => {
    expect(
      advertenciasDeGastoDeAdministracion({ ...base, idTurnoCaja: 9, turnoAbierto: true, origenFondos: 'CajaTurno' }),
    ).toEqual([])
  })

  it('gasto de tesorería (sin turno) avisa del ajuste sobre la caja general, no del arqueo', () => {
    expect(advertenciasDeGastoDeAdministracion(base)).toEqual(['Se registrará un ajuste compensatorio sobre la caja general.'])
  })

  it('gasto de caja con proveedor de categoría Proveedor avisa del ajuste sobre el saldo del proveedor', () => {
    expect(
      advertenciasDeGastoDeAdministracion({ ...base, origenFondos: 'CajaTurno', categoria: 'Proveedor', idProveedor: 5 }),
    ).toEqual(['Se registrará un ajuste compensatorio sobre el saldo del proveedor.'])
  })

  it('un proveedor con otra categoría no genera ajuste de proveedor', () => {
    expect(
      advertenciasDeGastoDeAdministracion({ ...base, origenFondos: 'CajaTurno', categoria: 'Otros', idProveedor: 5 }),
    ).toEqual([])
  })

  it('turno cerrado + proveedor + tesorería junta los avisos en un solo ajuste', () => {
    expect(
      advertenciasDeGastoDeAdministracion({ idTurnoCaja: 9, turnoAbierto: false, origenFondos: 'Tesoreria', categoria: 'Proveedor', idProveedor: 5 }),
    ).toEqual(['Se registrará un ajuste compensatorio sobre el saldo del proveedor y la caja general.'])
  })
})
