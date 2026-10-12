import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  LARGO_MAXIMO_DE_NOMBRE,
  SALDO_INICIAL_MAXIMO,
  aAlta,
  aEdicion,
  aFormulario,
  clienteDeCuentasTesoreria,
  conSaldoInicialBloqueado,
  copiaDeBajaDeCuenta,
  errorAlGuardar,
  formularioVacio,
  nombreDeEmpresa,
  opcionesDeCuentas,
  validarFormulario,
  type CuentaTesoreriaListado,
  type Formulario,
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
  const activa = cuenta({ id: 1, nombre: 'Mercado Pago' })
  const inactiva = cuenta({ id: 2, nombre: 'Banco viejo', activo: false })

  it('ofrece solo las cuentas activas cuando el medio no tiene ninguna', () => {
    expect(opcionesDeCuentas([activa, inactiva], '', '', false)).toEqual([{ valor: '1', etiqueta: 'Mercado Pago' }])
  })

  it('conserva la cuenta actual aunque esté inactiva y la marca', () => {
    expect(opcionesDeCuentas([activa, inactiva], '2', 'Banco viejo', true)).toEqual([
      { valor: '1', etiqueta: 'Mercado Pago' },
      { valor: '2', etiqueta: 'Banco viejo (inactiva)' },
    ])
  })

  it('nombra la empresa solo cuando las cuentas son de más de una', () => {
    const deOtra = cuenta({ id: 3, idEmpresa: 8, nombreEmpresa: 'Sucursal', nombre: 'Galicia' })

    expect(opcionesDeCuentas([activa], '', '', false)[0].etiqueta).toBe('Mercado Pago')
    expect(opcionesDeCuentas([activa, deOtra], '', '', false).map((o) => o.etiqueta)).toEqual([
      'Mercado Pago · Comercio',
      'Galicia · Sucursal',
    ])
  })

  it('si la lectura falló la actual sale con el nombre que el formulario ya conoce', () => {
    expect(opcionesDeCuentas(null, '5', 'Cuenta guardada', false)).toEqual([{ valor: '5', etiqueta: 'Cuenta guardada' }])
    expect(opcionesDeCuentas(null, '5', '', false)).toEqual([{ valor: '5', etiqueta: 'Cuenta 5' }])
  })

  it('la actual que la lectura no trae (borrada) no desaparece del selector', () => {
    expect(opcionesDeCuentas([activa], '9', 'Se borró', false).map((o) => o.valor)).toEqual(['9', '1'])
  })

  it('sin cuenta actual y sin lectura no hay opciones', () => {
    expect(opcionesDeCuentas(null, '', '', false)).toEqual([])
  })
})
