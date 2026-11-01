import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  LARGO_MAXIMO_DE_CONCEPTO_DE_AJUSTE,
  LARGO_MAXIMO_DE_NOMBRE,
  SALDO_INICIAL_MAXIMO,
  aAlta,
  aSolicitudDeAjuste,
  aEdicion,
  claveDeReconstruccion,
  origenesDeReconstruccion,
  clienteDeReconstruccion,
  descuentosDeAcreditacion,
  errorAlReconstruir,
  rangoDeReconstruccionPorDefecto,
  validarRangoDeReconstruccion,
  type AcreditacionDeReconstruccion,
  aFormulario,
  clienteDeCuentasTesoreria,
  conSaldoInicialBloqueado,
  copiaDeBajaDeCuenta,
  diferenciaDeVistaPrevia,
  errorAlAjustar,
  errorAlGuardar,
  esResultadoIncierto,
  formularioDeAjusteVacio,
  formularioVacio,
  importeDeVistaPrevia,
  nombreDeEmpresa,
  opcionesDeCuentas,
  saldoResultanteDeVistaPrevia,
  validarAjuste,
  validarFormulario,
  type CuentaTesoreriaListado,
  type Formulario,
  type FormularioDeAjuste,
} from './cuentasTesoreria'
import { COPIA_RESULTADO_INCIERTO } from './bajas'
import { ErrorApi } from './cliente'
import type { EmpresaListado } from './tipos'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()
const apiPutMock = vi.fn()
const apiDeleteMock = vi.fn()

vi.mock('./cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...args),
    post: (...args: unknown[]) => apiPostMock(...args),
    put: (...args: unknown[]) => apiPutMock(...args),
    delete: (...args: unknown[]) => apiDeleteMock(...args),
  },
  ErrorApi: class ErrorApiMock extends Error {
    estado: number
    codigo: string
    constructor(estado: number, codigo: string, mensaje: string) {
      super(mensaje)
      this.estado = estado
      this.codigo = codigo
    }
  },
}))

function empresa(id: number, razonSocial: string, nombreFantasia: string | null = null): EmpresaListado {
  return {
    id,
    idTenant: 1,
    razonSocial,
    nombreFantasia,
    cuit: null,
    nombreTenant: null,
    alicuotaPercepcionIibb: null,
    alicuotaPercepcionIva: null,
  }
}

function cuenta(sobrescribir: Partial<CuentaTesoreriaListado> = {}): CuentaTesoreriaListado {
  return {
    id: 4,
    idEmpresa: 7,
    nombreEmpresa: 'Comercio',
    nombre: 'Mercado Pago',
    saldoInicial: 100,
    saldoActual: 250,
    activo: true,
    saldoInicialEditable: true,
    ...sobrescribir,
  }
}

function formulario(sobrescribir: Partial<Formulario> = {}): Formulario {
  return { ...aFormulario(cuenta()), ...sobrescribir }
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiPutMock.mockReset()
  apiDeleteMock.mockReset()
})

describe('clienteDeCuentasTesoreria', () => {
  it('apunta cada operación a la ruta del servidor', async () => {
    const alta = { idEmpresa: 7, nombre: 'MP', saldoInicial: 0, activo: true }
    const edicion = { nombre: 'MP', saldoInicial: 0, activo: false }

    await clienteDeCuentasTesoreria.listar()
    await clienteDeCuentasTesoreria.crear(alta)
    await clienteDeCuentasTesoreria.actualizar(4, edicion)
    await clienteDeCuentasTesoreria.eliminar(4)

    expect(apiGetMock).toHaveBeenCalledWith('/cuentas-tesoreria')
    expect(apiPostMock).toHaveBeenCalledWith('/cuentas-tesoreria', alta)
    expect(apiPutMock).toHaveBeenCalledWith('/cuentas-tesoreria/4', edicion)
    expect(apiDeleteMock).toHaveBeenCalledWith('/cuentas-tesoreria/4')
  })
})

describe('formularioVacio', () => {
  it('preselecciona la empresa cuando es la única', () => {
    expect(formularioVacio([empresa(7, 'Comercio SA')])).toEqual({
      id: null,
      idEmpresa: 7,
      nombreEmpresa: null,
      nombre: '',
      saldoInicial: 0,
      saldoInicialOriginal: null,
      saldoInicialEditable: true,
      activo: true,
    })
  })

  it.each([[[]], [[empresa(7, 'A'), empresa(8, 'B')]]])('con %j empresas obliga a elegir', (empresas) => {
    expect(formularioVacio(empresas).idEmpresa).toBeNull()
  })
})

describe('aFormulario', () => {
  it('copia lo editable y guarda el saldo inicial original', () => {
    expect(aFormulario(cuenta({ saldoInicialEditable: false, activo: false }))).toEqual({
      id: 4,
      idEmpresa: 7,
      nombreEmpresa: 'Comercio',
      nombre: 'Mercado Pago',
      saldoInicial: 100,
      saldoInicialOriginal: 100,
      saldoInicialEditable: false,
      activo: false,
    })
  })
})

describe('conSaldoInicialBloqueado', () => {
  it('vuelve al saldo guardado y deja el campo de solo lectura', () => {
    const bloqueado = conSaldoInicialBloqueado(formulario({ saldoInicial: 999 }))

    expect(bloqueado.saldoInicial).toBe(100)
    expect(bloqueado.saldoInicialEditable).toBe(false)
  })
})

describe('nombreDeEmpresa', () => {
  it('prefiere el nombre de fantasía y cae a la razón social', () => {
    expect(nombreDeEmpresa(empresa(1, 'Comercio SA', 'Comercio'))).toBe('Comercio')
    expect(nombreDeEmpresa(empresa(1, 'Comercio SA', '   '))).toBe('Comercio SA')
    expect(nombreDeEmpresa(empresa(1, 'Comercio SA'))).toBe('Comercio SA')
  })
})

describe('validarFormulario', () => {
  it('acepta una cuenta válida, también con saldo negativo o cero', () => {
    expect(validarFormulario(formulario())).toBeNull()
    expect(validarFormulario(formulario({ saldoInicial: -250.5 }))).toBeNull()
    expect(validarFormulario(formulario({ saldoInicial: 0 }))).toBeNull()
  })

  it('exige nombre y lo mide después de recortar', () => {
    expect(validarFormulario(formulario({ nombre: '   ' }))).toEqual({ campo: 'nombre', mensaje: 'Ingresá el nombre.' })
    expect(validarFormulario(formulario({ nombre: ` ${'a'.repeat(LARGO_MAXIMO_DE_NOMBRE)} ` }))).toBeNull()
    expect(validarFormulario(formulario({ nombre: 'a'.repeat(LARGO_MAXIMO_DE_NOMBRE + 1) }))?.campo).toBe('nombre')
  })

  it('exige empresa solo en el alta', () => {
    expect(validarFormulario(formulario({ id: null, idEmpresa: null }))).toEqual({
      campo: 'idEmpresa',
      mensaje: 'Elegí la empresa.',
    })
    expect(validarFormulario(formulario({ id: 4, idEmpresa: null }))).toBeNull()
  })

  it('exige saldo inicial dentro del tope solo cuando se puede editar', () => {
    expect(validarFormulario(formulario({ saldoInicial: null }))?.campo).toBe('saldoInicial')
    expect(validarFormulario(formulario({ saldoInicial: SALDO_INICIAL_MAXIMO }))).toBeNull()
    expect(validarFormulario(formulario({ saldoInicial: -SALDO_INICIAL_MAXIMO }))).toBeNull()
    expect(validarFormulario(formulario({ saldoInicial: SALDO_INICIAL_MAXIMO + 1 }))?.campo).toBe('saldoInicial')
    expect(validarFormulario(formulario({ saldoInicial: -SALDO_INICIAL_MAXIMO - 1 }))?.campo).toBe('saldoInicial')

    const bloqueado = { saldoInicialEditable: false }
    expect(validarFormulario(formulario({ ...bloqueado, saldoInicial: null }))).toBeNull()
    expect(validarFormulario(formulario({ ...bloqueado, saldoInicial: SALDO_INICIAL_MAXIMO * 10 }))).toBeNull()
  })
})

describe('aAlta', () => {
  it('arma el cuerpo con el nombre recortado', () => {
    expect(aAlta(formulario({ id: null, nombre: '  Banco ', saldoInicial: -5, activo: false }))).toEqual({
      idEmpresa: 7,
      nombre: 'Banco',
      saldoInicial: -5,
      activo: false,
    })
  })

  it('no inventa una empresa ni un saldo', () => {
    expect(() => aAlta(formulario({ id: null, idEmpresa: null }))).toThrow('La empresa es obligatoria.')
    expect(() => aAlta(formulario({ id: null, saldoInicial: null }))).toThrow('El saldo inicial es obligatorio.')
  })
})

describe('aEdicion', () => {
  it('no manda la empresa', () => {
    expect(aEdicion(formulario({ nombre: ' MP ', saldoInicial: 300 }))).toEqual({
      nombre: 'MP',
      saldoInicial: 300,
      activo: true,
    })
  })

  it('con el saldo inicial bloqueado manda el guardado aunque el campo haya cambiado', () => {
    const cuerpo = aEdicion(formulario({ saldoInicialEditable: false, saldoInicial: 999, saldoInicialOriginal: 100 }))

    expect(cuerpo.saldoInicial).toBe(100)
  })
})

describe('errorAlGuardar', () => {
  it.each([
    ['cuenta_tesoreria_duplicada', 409, 'nombre'],
    ['saldo_inicial_con_movimientos', 409, 'saldoInicial'],
    ['nombre_requerido', 400, 'nombre'],
    ['nombre_muy_largo', 400, 'nombre'],
    ['saldo_inicial_invalido', 400, 'saldoInicial'],
    ['referencia_invalida', 400, 'idEmpresa'],
  ])('%s se muestra en el campo %s', (codigo, estado, campo) => {
    expect(errorAlGuardar(new ErrorApi(estado, codigo, 'texto del servidor')).campo).toBe(campo)
  })

  it('elige por código y nunca por mensaje', () => {
    const a = errorAlGuardar(new ErrorApi(409, 'cuenta_tesoreria_duplicada', 'uno'))
    const b = errorAlGuardar(new ErrorApi(409, 'cuenta_tesoreria_duplicada', 'otro'))

    expect(a).toEqual(b)
  })

  it('un código exótico del prototipo no resuelve a una copia propia', () => {
    expect(errorAlGuardar(new ErrorApi(400, 'constructor', 'raro'))).toEqual({ campo: 'general', mensaje: 'raro' })
  })

  it('un 404 dice que la cuenta ya no existe', () => {
    expect(errorAlGuardar(new ErrorApi(404, 'no_encontrado', 'x'))).toEqual({
      campo: 'general',
      mensaje: 'La cuenta ya no existe. Actualizá la pantalla.',
    })
  })

  it('un 5xx sin código de resultado incierto manda a verificar; con él rinde el texto del servidor', () => {
    expect(errorAlGuardar(new ErrorApi(500, 'error_interno', 'x')).mensaje).toContain(COPIA_RESULTADO_INCIERTO)
    expect(errorAlGuardar(new ErrorApi(503, 'resultado_incierto', 'Verificá el listado.')).mensaje).toBe(
      'Verificá el listado.',
    )
  })

  it('un error que no es de la API o un mensaje vacío caen en la copia genérica', () => {
    expect(errorAlGuardar(new Error('red'))).toEqual({ campo: 'general', mensaje: 'No se pudo guardar.' })
    expect(errorAlGuardar(new ErrorApi(400, 'otro', '  ')).mensaje).toBe('No se pudo guardar.')
  })
})

describe('copiaDeBajaDeCuenta', () => {
  it('cuenta_tesoreria_en_uso rinde el mensaje del servidor primero y sugiere desactivar', () => {
    const copia = copiaDeBajaDeCuenta(
      new ErrorApi(409, 'cuenta_tesoreria_en_uso', 'No se puede dar de baja la cuenta porque tiene medios de pago.'),
      'MP',
    )

    expect(copia).toBe(
      'No se puede dar de baja la cuenta porque tiene medios de pago. Desactivala para que no se ofrezca más.',
    )
  })

  it('cuenta_tesoreria_en_uso sin mensaje usa el nombre de la cuenta', () => {
    expect(copiaDeBajaDeCuenta(new ErrorApi(409, 'cuenta_tesoreria_en_uso', ' '), 'MP')).toContain('"MP"')
  })

  it('un 404 no anexa el mensaje del servidor', () => {
    expect(copiaDeBajaDeCuenta(new ErrorApi(404, 'no_encontrado', 'existe en otro tenant'), 'MP')).toBe(
      'No se pudo dar de baja la cuenta "MP". Ya no existe o no está a tu alcance. Actualizá el listado.',
    )
  })

  it('un 5xx y un error de red mandan a verificar antes de reintentar', () => {
    expect(copiaDeBajaDeCuenta(new ErrorApi(500, 'error_interno', 'x'), 'MP')).toContain(COPIA_RESULTADO_INCIERTO)
    expect(copiaDeBajaDeCuenta(new Error('red'), 'MP')).toContain(COPIA_RESULTADO_INCIERTO)
    expect(copiaDeBajaDeCuenta(new ErrorApi(503, 'resultado_incierto', 'Verificá el listado.'), 'MP')).toContain(
      'Verificá el listado.',
    )
  })

  it('otro 4xx rinde el mensaje del servidor', () => {
    expect(copiaDeBajaDeCuenta(new ErrorApi(400, 'otro', 'Algo pasó.'), 'MP')).toBe('Algo pasó.')
  })
})

describe('opcionesDeCuentas', () => {
  const activa = cuenta({ id: 1, nombre: 'Mercado Pago', idEmpresa: 7 })
  const inactiva = cuenta({ id: 2, nombre: 'Banco viejo', idEmpresa: 7, activo: false })
  const deOtra = cuenta({ id: 3, nombre: 'Galicia', idEmpresa: 8, nombreEmpresa: 'Sucursal' })
  const unaEmpresa = [{ id: 7 }]
  const dosEmpresas = [{ id: 7 }, { id: 8 }]

  it('un medio de una empresa solo ofrece cuentas activas de esa empresa', () => {
    expect(opcionesDeCuentas([activa, inactiva, deOtra], dosEmpresas, '7', '', '', false)).toEqual([
      { valor: '1', etiqueta: 'Mercado Pago' },
    ])
    expect(opcionesDeCuentas([activa, inactiva, deOtra], dosEmpresas, '8', '', '', false)).toEqual([
      { valor: '3', etiqueta: 'Galicia' },
    ])
  })

  it('un medio de todo el tenant ofrece las cuentas de la única empresa y ninguna si hay varias', () => {
    expect(opcionesDeCuentas([activa, deOtra], unaEmpresa, '', '', '', false)).toEqual([
      { valor: '1', etiqueta: 'Mercado Pago' },
    ])
    expect(opcionesDeCuentas([activa, deOtra], dosEmpresas, '', '', '', false)).toEqual([])
  })

  it('conserva la cuenta actual aunque esté inactiva y la marca', () => {
    expect(opcionesDeCuentas([activa, inactiva], unaEmpresa, '7', '2', 'Banco viejo', true)).toEqual([
      { valor: '1', etiqueta: 'Mercado Pago' },
      { valor: '2', etiqueta: 'Banco viejo (inactiva)' },
    ])
  })

  it('la actual se conserva aunque sea de otra empresa (para que el servidor la rechace, no el formulario)', () => {
    expect(opcionesDeCuentas([activa, deOtra], dosEmpresas, '7', '3', 'Galicia', false).map((o) => o.valor)).toEqual(['3', '1'])
  })

  it('si la lectura falló la actual sale con el nombre que el formulario ya conoce', () => {
    expect(opcionesDeCuentas(null, null, '', '5', 'Cuenta guardada', false)).toEqual([{ valor: '5', etiqueta: 'Cuenta guardada' }])
    expect(opcionesDeCuentas(null, null, '', '5', '', false)).toEqual([{ valor: '5', etiqueta: 'Cuenta 5' }])
  })

  it('la actual que la lectura no trae (borrada) no desaparece del selector', () => {
    expect(opcionesDeCuentas([activa], unaEmpresa, '', '9', 'Se borró', false).map((o) => o.valor)).toEqual(['9', '1'])
  })

  it('sin cuenta actual y sin lectura no hay opciones', () => {
    expect(opcionesDeCuentas(null, null, '', '', '', false)).toEqual([])
  })
})

describe('ajuste manual del saldo — reglas puras', () => {
  const completo = (sobrescribir: Partial<FormularioDeAjuste> = {}): FormularioDeAjuste => ({
    modo: 'importe',
    saldoObjetivo: null,
    importe: 10,
    concepto: 'Conciliación',
    ...sobrescribir,
  })

  it('arranca en el modo "tengo el saldo real", vacío', () => {
    expect(formularioDeAjusteVacio()).toEqual({ modo: 'saldoObjetivo', saldoObjetivo: null, importe: null, concepto: '' })
  })

  it('la vista previa de la diferencia se calcula en centavos, sin arrastrar el error del punto flotante', () => {
    expect(diferenciaDeVistaPrevia(999.9, 1000.1)).toBe(0.2)
    expect(diferenciaDeVistaPrevia(1000, 940.5)).toBe(-59.5)
    expect(diferenciaDeVistaPrevia(100, 100)).toBe(0)
    expect(diferenciaDeVistaPrevia(100, null)).toBeNull()
  })

  it('el saldo resultante de la vista previa suma en centavos enteros', () => {
    expect(saldoResultanteDeVistaPrevia(0.1, 0.2)).toBe(0.3)
    expect(saldoResultanteDeVistaPrevia(100, -250.55)).toBe(-150.55)
  })

  it('el importe de la vista previa sale del modo elegido', () => {
    expect(importeDeVistaPrevia(completo({ modo: 'importe', importe: -7 }), 100)).toBe(-7)
    expect(importeDeVistaPrevia(completo({ modo: 'saldoObjetivo', saldoObjetivo: 130 }), 100)).toBe(30)
    expect(importeDeVistaPrevia(completo({ modo: 'saldoObjetivo', saldoObjetivo: null }), 100)).toBeNull()
  })

  it('un ajuste válido en cada modo no tiene error', () => {
    expect(validarAjuste(completo())).toBeNull()
    expect(validarAjuste(completo({ modo: 'saldoObjetivo', saldoObjetivo: 50, importe: null }))).toBeNull()
  })

  it('el concepto es obligatorio y tiene tope, medido después de recortar', () => {
    expect(validarAjuste(completo({ concepto: '   ' }))).toEqual({
      campo: 'concepto',
      mensaje: 'Ingresá el concepto del ajuste.',
    })
    expect(validarAjuste(completo({ concepto: ` ${'a'.repeat(LARGO_MAXIMO_DE_CONCEPTO_DE_AJUSTE)} ` }))).toBeNull()
    expect(validarAjuste(completo({ concepto: 'a'.repeat(LARGO_MAXIMO_DE_CONCEPTO_DE_AJUSTE + 1) }))?.campo).toBe('concepto')
  })

  it('el importe no puede faltar, ser cero ni estar fuera de rango; el saldo resultante lo decide el servidor', () => {
    expect(validarAjuste(completo({ importe: null }))?.campo).toBe('importe')
    expect(validarAjuste(completo({ importe: 0 }))?.mensaje).toBe('El importe no puede ser cero.')
    expect(validarAjuste(completo({ importe: SALDO_INICIAL_MAXIMO + 1 }))?.campo).toBe('importe')
    expect(validarAjuste(completo({ importe: 5 }))).toBeNull()
  })

  it('el saldo real no puede faltar ni estar fuera de rango', () => {
    const modo = { modo: 'saldoObjetivo' as const, importe: null }
    expect(validarAjuste(completo({ ...modo, saldoObjetivo: null }))?.campo).toBe('saldoObjetivo')
    expect(validarAjuste(completo({ ...modo, saldoObjetivo: SALDO_INICIAL_MAXIMO + 1 }))?.campo).toBe('saldoObjetivo')
  })

  it('un saldo real igual al que muestra la pantalla NO se rechaza localmente: el saldo cacheado puede estar viejo', () => {
    expect(validarAjuste(completo({ modo: 'saldoObjetivo', saldoObjetivo: 100, importe: null }))).toBeNull()
  })

  it('un campo del otro modo no cuenta: con modo importe se ignora un saldo real inválido y al revés', () => {
    expect(validarAjuste(completo({ modo: 'importe', saldoObjetivo: SALDO_INICIAL_MAXIMO + 1 }))).toBeNull()
    expect(validarAjuste(completo({ modo: 'saldoObjetivo', importe: 0, saldoObjetivo: 5 }))).toBeNull()
  })

  it('la solicitud lleva SOLO el campo del modo elegido y el concepto recortado', () => {
    const porImporte = aSolicitudDeAjuste(completo({ modo: 'importe', importe: -4.5, saldoObjetivo: 99, concepto: ' Ajuste ' }))
    expect(porImporte).toEqual({ concepto: 'Ajuste', importe: -4.5 })
    expect('saldoObjetivo' in porImporte).toBe(false)

    const porSaldo = aSolicitudDeAjuste(completo({ modo: 'saldoObjetivo', saldoObjetivo: 250, importe: 7 }))
    expect(porSaldo).toEqual({ concepto: 'Conciliación', saldoObjetivo: 250 })
    expect('importe' in porSaldo).toBe(false)
  })

  it('armar la solicitud de un formulario sin el valor del modo es un error de programación', () => {
    expect(() => aSolicitudDeAjuste(completo({ modo: 'importe', importe: null }))).toThrow()
    expect(() => aSolicitudDeAjuste(completo({ modo: 'saldoObjetivo', saldoObjetivo: null }))).toThrow()
  })

  it('el cliente postea a /cuentas-tesoreria/{id}/ajustes', async () => {
    apiPostMock.mockResolvedValue({ saldoActual: 1 })

    await clienteDeCuentasTesoreria.ajustar(7, { concepto: 'x', importe: 1 })

    expect(apiPostMock).toHaveBeenCalledWith('/cuentas-tesoreria/7/ajustes', { concepto: 'x', importe: 1 })
  })

  it.each([
    ['concepto_requerido', 'concepto'],
    ['concepto_muy_largo', 'concepto'],
    ['importe_invalido', 'importe'],
    ['saldo_objetivo_invalido', 'saldoObjetivo'],
    ['ajuste_sin_diferencia', 'saldoObjetivo'],
    ['saldo_resultante_fuera_de_rango', 'general'],
    ['importe_o_saldo_objetivo_requerido', 'general'],
    ['importe_y_saldo_objetivo_excluyentes', 'general'],
  ])('el código %s se ubica en el campo %s', (codigo, campo) => {
    expect(errorAlAjustar(new ErrorApi(codigo === 'ajuste_sin_diferencia' ? 409 : 400, codigo, 'texto del servidor')).campo).toBe(campo)
  })

  it('un código desconocido cae al mensaje del servidor; el estado 404 y el 403 tienen copia propia', () => {
    expect(errorAlAjustar(new ErrorApi(400, 'otro', 'Algo salió mal'))).toEqual({ campo: 'general', mensaje: 'Algo salió mal' })
    expect(errorAlAjustar(new ErrorApi(404, 'no_encontrado', 'x')).mensaje).toBe('La cuenta ya no existe. Actualizá la pantalla.')
    expect(errorAlAjustar(new ErrorApi(403, 'prohibido', 'x')).mensaje).toBe(
      'Solo un administrador puede ajustar el saldo de una cuenta.',
    )
  })

  it('un código heredado del prototipo no se confunde con uno conocido', () => {
    expect(errorAlAjustar(new ErrorApi(400, 'constructor', 'Rechazo')).mensaje).toBe('Rechazo')
  })

  it('un 5xx rinde el texto del servidor si es resultado_incierto y la guía genérica si no; un error de red, la guía', () => {
    expect(errorAlAjustar(new ErrorApi(503, 'resultado_incierto', 'Verificá el libro antes de reintentar.')).mensaje).toBe(
      'Verificá el libro antes de reintentar.',
    )
    expect(errorAlAjustar(new ErrorApi(500, 'error', '')).mensaje).toBe(`No se pudo registrar el ajuste. ${COPIA_RESULTADO_INCIERTO}`)
    expect(errorAlAjustar(new Error('red')).mensaje).toBe(`No se pudo registrar el ajuste. ${COPIA_RESULTADO_INCIERTO}`)
  })

  it('solo un 5xx o un error sin respuesta es un resultado incierto: un 4xx es un rechazo cierto', () => {
    expect(esResultadoIncierto(new ErrorApi(503, 'resultado_incierto', ''))).toBe(true)
    expect(esResultadoIncierto(new Error('red'))).toBe(true)
    expect(esResultadoIncierto(new ErrorApi(400, 'importe_invalido', ''))).toBe(false)
    expect(esResultadoIncierto(new ErrorApi(409, 'ajuste_sin_diferencia', ''))).toBe(false)
  })
})

describe('reconstrucción histórica', () => {
  const solicitud = { idEmpresa: 3, desde: '2026-10-01', hasta: '2026-10-31' }

  it('origenesDeReconstruccion nombra turnos y pagos con su plural y, sin pagos, solo los turnos', () => {
    expect(origenesDeReconstruccion(1, 0)).toBe('1 turno')
    expect(origenesDeReconstruccion(2, 0)).toBe('2 turnos')
    expect(origenesDeReconstruccion(0, 1)).toBe('1 pago')
    expect(origenesDeReconstruccion(0, 3)).toBe('3 pagos')
    expect(origenesDeReconstruccion(1, 1)).toBe('1 turno y 1 pago')
    expect(origenesDeReconstruccion(2, 5)).toBe('2 turnos y 5 pagos')
    expect(origenesDeReconstruccion(0, 0)).toBe('0 turnos')
  })

  it('la vista previa y la aplicación postean la solicitud tal cual a su ruta y devuelven el plan', async () => {
    const plan = {
      idEmpresa: 3,
      desde: '2026-10-01',
      hasta: '2026-10-31',
      turnos: 0,
      pagos: 0,
      filas: 0,
      acreditaciones: [],
      debitos: [],
      cuentas: [],
      debitosCongelados: [],
    }
    apiPostMock.mockResolvedValue(plan)

    await expect(clienteDeReconstruccion.vistaPrevia(solicitud)).resolves.toBe(plan)
    expect(apiPostMock).toHaveBeenLastCalledWith('/cuentas-tesoreria/reconstruccion/vista-previa', solicitud)

    await expect(clienteDeReconstruccion.aplicar(solicitud)).resolves.toBe(plan)
    expect(apiPostMock).toHaveBeenLastCalledWith('/cuentas-tesoreria/reconstruccion', solicitud)
  })

  it('el rango por defecto va del primer día del mes a hoy', () => {
    expect(rangoDeReconstruccionPorDefecto(new Date(2026, 9, 31))).toEqual({ desde: '2026-10-01', hasta: '2026-10-31' })
    expect(rangoDeReconstruccionPorDefecto(new Date(2026, 0, 5))).toEqual({ desde: '2026-01-01', hasta: '2026-01-05' })
  })

  it('valida empresa, fechas, orden y tope de 366 días', () => {
    expect(validarRangoDeReconstruccion('', '2026-10-01', '2026-10-31')).toBe('Elegí la empresa.')
    expect(validarRangoDeReconstruccion('3', '', '2026-10-31')).toBe('Indicá las fechas desde y hasta.')
    expect(validarRangoDeReconstruccion('3', '2026-11-01', '2026-10-31')).toBe('La fecha "desde" no puede ser posterior a "hasta".')
    expect(validarRangoDeReconstruccion('3', '2026-10-01', '2026-10-01')).toBeNull()
    expect(validarRangoDeReconstruccion('3', '2025-10-01', '2026-10-02')).toBeNull()
    expect(validarRangoDeReconstruccion('3', '2025-10-01', '2026-10-03')).toBe('El rango no puede superar los 366 días.')
  })

  it('la clave cambia con cualquiera de los tres campos', () => {
    const base = claveDeReconstruccion('3', '2026-10-01', '2026-10-31')
    expect(claveDeReconstruccion('4', '2026-10-01', '2026-10-31')).not.toBe(base)
    expect(claveDeReconstruccion('3', '2026-10-02', '2026-10-31')).not.toBe(base)
    expect(claveDeReconstruccion('3', '2026-10-01', '2026-10-30')).not.toBe(base)
  })

  it('separa comisión y retenciones de los componentes de la acreditación', () => {
    const acreditacion: AcreditacionDeReconstruccion = {
      idTurnoCaja: 1,
      fechaCierre: '2026-10-05T20:00:00Z',
      idMedioPago: 2,
      nombreMedio: 'Mercado Pago',
      idCuentaTesoreria: 3,
      nombreCuenta: 'MP',
      baseBruta: 1000,
      neto: 940.1,
      componentes: [
        { tipo: 'Cobro', ingreso: 1000, egreso: 0, porcentajeAplicado: null },
        { tipo: 'Comision', ingreso: 0, egreso: 40.1, porcentajeAplicado: 4.01 },
        { tipo: 'RetencionIibb', ingreso: 0, egreso: 10.2, porcentajeAplicado: 1 },
        { tipo: 'RetencionIva', ingreso: 0, egreso: 9.7, porcentajeAplicado: 1 },
      ],
    }

    expect(descuentosDeAcreditacion(acreditacion)).toEqual({ comision: 40.1, retenciones: 19.9 })
  })

  it('traduce los códigos del servidor y distingue lectura de escritura ante un fallo incierto', () => {
    expect(errorAlReconstruir(new ErrorApi(400, 'rango_invalido', ''), false)).toBe('La fecha "desde" no puede ser posterior a "hasta".')
    expect(errorAlReconstruir(new ErrorApi(400, 'rango_demasiado_amplio', ''), false)).toBe('El rango no puede superar los 366 días.')
    expect(errorAlReconstruir(new ErrorApi(409, 'reconstruccion_concurrente', ''), true)).toBe(
      'Otra reconstrucción está en curso. Esperá unos segundos y generá la vista previa de nuevo.',
    )
    expect(errorAlReconstruir(new ErrorApi(400, 'solicitud_invalida', 'Indicá la empresa.'), false)).toBe('Indicá la empresa.')
    expect(errorAlReconstruir(new ErrorApi(404, 'no_encontrado', ''), false)).toBe('La empresa ya no existe. Actualizá la pantalla.')
    expect(errorAlReconstruir(new ErrorApi(403, 'prohibido', ''), true)).toBe(
      'Solo un administrador puede reconstruir el historial de las cuentas.',
    )
    expect(errorAlReconstruir(new ErrorApi(500, 'x', ''), true)).toBe(`No se pudo reconstruir el historial. ${COPIA_RESULTADO_INCIERTO}`)
    expect(errorAlReconstruir(new ErrorApi(500, 'x', ''), false)).toBe('No se pudo generar la vista previa.')
    expect(errorAlReconstruir(new Error('red'), true)).toBe(`No se pudo reconstruir el historial. ${COPIA_RESULTADO_INCIERTO}`)
  })
})
