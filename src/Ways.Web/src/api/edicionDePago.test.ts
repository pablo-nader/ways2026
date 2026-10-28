import { describe, expect, it } from 'vitest'
import { ErrorApi } from './cliente'
import {
  ADVERTENCIA_TURNO_CERRADO,
  COPIA_PAGO_TURNO_CERRADO,
  advertenciasDePagoDeAdministracion,
  esPagoOriginadoEnPersonal,
  aFormularioDeEdicion,
  aSolicitudDeEdicionConLineas,
  aSolicitudDeEdicionDePago,
  copiaDeFalloDeEdicion,
  formularioDeEdicionCompleto,
  formularioDeEdicionConLineasCompleto,
  lineasIniciales,
  type FormularioDeEdicionDePago,
  type PagoEditable,
} from './registroDePagos'

function pagoFixture(sobrescribir: Partial<PagoEditable> = {}): PagoEditable {
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

function formularioFixture(sobrescribir: Partial<FormularioDeEdicionDePago> = {}): FormularioDeEdicionDePago {
  return { ...aFormularioDeEdicion(pagoFixture()), ...sobrescribir }
}

describe('esPagoOriginadoEnPersonal', () => {
  it('un pago con idMovimientoPersonal nació de un movimiento de personal y no se modifica por separado', () => {
    expect(esPagoOriginadoEnPersonal({ idMovimientoPersonal: 12 })).toBe(true)
  })

  it('un pago sin idMovimientoPersonal es común', () => {
    expect(esPagoOriginadoEnPersonal({ idMovimientoPersonal: null })).toBe(false)
  })

  it('una respuesta que no trae el campo se trata como pago común', () => {
    const respuestaSinElCampo = {} as { idMovimientoPersonal: number | null }

    expect(esPagoOriginadoEnPersonal(respuestaSinElCampo)).toBe(false)
  })
})

describe('aFormularioDeEdicion', () => {
  it('precarga cada campo y convierte los null opcionales en cadena o vacío', () => {
    expect(aFormularioDeEdicion(pagoFixture())).toEqual({
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

  it('conserva proveedor, área, detalle y factura del pago', () => {
    const f = aFormularioDeEdicion(
      pagoFixture({ categoria: 'Proveedor', idProveedor: 8, idArea: 3, detalle: 'Mes de agosto', numeroFactura: '0001-123' }),
    )
    expect(f).toMatchObject({ categoria: 'Proveedor', idProveedor: 8, idArea: 3, detalle: 'Mes de agosto', numeroFactura: '0001-123' })
  })
})

describe('aSolicitudDeEdicionDePago', () => {
  it('recorta los textos y manda null en los opcionales vacíos', () => {
    const solicitud = aSolicitudDeEdicionDePago(formularioFixture({ concepto: '  Luz  ', detalle: '   ', numeroFactura: '' }))
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
    const solicitud = aSolicitudDeEdicionDePago(
      formularioFixture({ categoria: 'Proveedor', idProveedor: 8, idArea: 3, detalle: ' d ', numeroFactura: ' 0001-1 ' }),
    )
    expect(solicitud).toMatchObject({ idProveedor: 8, idArea: 3, detalle: 'd', numeroFactura: '0001-1' })
  })

  it('no incluye origen de fondos, punto de venta ni fecha (no se editan)', () => {
    expect(Object.keys(aSolicitudDeEdicionDePago(formularioFixture())).sort()).toEqual(
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
    expect(copiaDeFalloDeEdicion(new ErrorApi(409, 'gasto_turno_cerrado', 'texto del servidor'))).toBe(COPIA_PAGO_TURNO_CERRADO)
  })

  it('otro error de la API rinde el mensaje del servidor', () => {
    expect(copiaDeFalloDeEdicion(new ErrorApi(400, 'gasto_proveedor_ligado', 'No se puede cambiar el proveedor.'))).toBe(
      'No se puede cambiar el proveedor.',
    )
  })

  it('un mensaje del servidor vacío cae al texto genérico de la acción', () => {
    expect(copiaDeFalloDeEdicion(new ErrorApi(500, 'x', '  '), 'eliminar')).toBe('No se pudo eliminar el pago.')
  })

  it('un error que no es de la API rinde el texto genérico', () => {
    expect(copiaDeFalloDeEdicion(new Error('red'))).toBe('No se pudo guardar el pago.')
  })
})

describe('advertenciasDePagoDeAdministracion', () => {
  const base = { turnoAbierto: false, idTurnoCaja: null, origenFondos: 'Tesoreria', categoria: 'Otros', idProveedor: null } as const

  it('pago de caja de un turno cerrado avisa del recálculo del arqueo', () => {
    expect(
      advertenciasDePagoDeAdministracion({ ...base, idTurnoCaja: 9, origenFondos: 'CajaTurno' }),
    ).toEqual([ADVERTENCIA_TURNO_CERRADO])
  })

  it('pago de caja de un turno abierto no avisa nada', () => {
    expect(
      advertenciasDePagoDeAdministracion({ ...base, idTurnoCaja: 9, turnoAbierto: true, origenFondos: 'CajaTurno' }),
    ).toEqual([])
  })

  it('pago de tesorería (sin turno) avisa del ajuste sobre la caja general, no del arqueo', () => {
    expect(advertenciasDePagoDeAdministracion(base)).toEqual(['Se registrará un ajuste compensatorio sobre la caja general.'])
  })

  it('pago de caja con proveedor de categoría Proveedor avisa del ajuste sobre el saldo del proveedor', () => {
    expect(
      advertenciasDePagoDeAdministracion({ ...base, origenFondos: 'CajaTurno', categoria: 'Proveedor', idProveedor: 5 }),
    ).toEqual(['Se registrará un ajuste compensatorio sobre el saldo del proveedor.'])
  })

  it('un proveedor con otra categoría no genera ajuste de proveedor', () => {
    expect(
      advertenciasDePagoDeAdministracion({ ...base, origenFondos: 'CajaTurno', categoria: 'Otros', idProveedor: 5 }),
    ).toEqual([])
  })

  it('turno cerrado + proveedor + tesorería junta los avisos en un solo ajuste', () => {
    expect(
      advertenciasDePagoDeAdministracion({ idTurnoCaja: 9, turnoAbierto: false, origenFondos: 'Tesoreria', categoria: 'Proveedor', idProveedor: 5 }),
    ).toEqual(['Se registrará un ajuste compensatorio sobre el saldo del proveedor y la caja general.'])
  })
})

describe('lineasIniciales', () => {
  it('usa el reparto que devolvió el servidor, una fila por línea', () => {
    const lineas = lineasIniciales({
      idMedioPago: 4,
      importe: 1000,
      lineas: [
        { idItem: 1, idMedioPago: 4, nombreMedioPago: 'Efectivo', importe: 600, referencia: null },
        { idItem: 2, idMedioPago: 5, nombreMedioPago: 'Transferencia', importe: 400, referencia: 'Op 9' },
      ],
    })

    expect(lineas).toEqual([
      expect.objectContaining({ clave: 1, idMedioPago: 4, importe: 600, referencia: '' }),
      expect.objectContaining({ clave: 2, idMedioPago: 5, importe: 400, referencia: 'Op 9', nombreMedioPago: 'Transferencia' }),
    ])
  })

  it('sin líneas en la respuesta arma una sola con el medio y el importe del pago', () => {
    expect(lineasIniciales({ idMedioPago: 4, importe: 1500 })).toEqual([{ clave: 1, idMedioPago: 4, importe: 1500, referencia: '' }])
    expect(lineasIniciales({ idMedioPago: 4, importe: 1500, lineas: [] })).toEqual([
      { clave: 1, idMedioPago: 4, importe: 1500, referencia: '' },
    ])
  })
})

describe('aSolicitudDeEdicionConLineas', () => {
  it('lleva los campos editables y el reparto, sin medio ni importe sueltos', () => {
    const solicitud = aSolicitudDeEdicionConLineas(formularioFixture({ concepto: '  Luz  ', detalle: '   ', numeroFactura: '' }), [
      { clave: 1, idMedioPago: 4, importe: 1000, referencia: '' },
      { clave: 2, idMedioPago: 5, importe: 500, referencia: ' R-1 ' },
    ])

    expect(solicitud).toEqual({
      categoria: 'Servicios',
      idProveedor: null,
      idArea: null,
      concepto: 'Luz',
      detalle: null,
      numeroFactura: null,
      lineas: [
        { idMedioPago: 4, importe: 1000, referencia: null },
        { idMedioPago: 5, importe: 500, referencia: 'R-1' },
      ],
    })
  })
})

describe('formularioDeEdicionConLineasCompleto', () => {
  const lineasValidas = [{ clave: 1, idMedioPago: 4, importe: 1500, referencia: '' }]

  it('acepta concepto y líneas válidas', () => {
    expect(formularioDeEdicionConLineasCompleto(formularioFixture(), lineasValidas)).toBe(true)
  })

  it('rechaza el concepto en blanco y una línea inválida', () => {
    expect(formularioDeEdicionConLineasCompleto(formularioFixture({ concepto: ' ' }), lineasValidas)).toBe(false)
    expect(formularioDeEdicionConLineasCompleto(formularioFixture(), [{ ...lineasValidas[0], importe: 0 }])).toBe(false)
  })
})
