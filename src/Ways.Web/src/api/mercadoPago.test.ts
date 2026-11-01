import { beforeEach, describe, expect, it, vi } from 'vitest'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()
const apiDeleteMock = vi.fn()

vi.mock('./cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...args),
    post: (...args: unknown[]) => apiPostMock(...args),
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

import { ErrorApi } from './cliente'
import {
  clienteDeCajasMercadoPago,
  clienteDeLocalesMercadoPago,
  clienteDeMercadoPago,
  copiaDeFalloDeMercadoPago,
  esUrlDeAutorizacionNavegable,
  leerRetornoDeMercadoPago,
} from './mercadoPago'

describe('leerRetornoDeMercadoPago', () => {
  it('lee el retorno que debe completarse, con el code y el state decodificados', () => {
    expect(leerRetornoDeMercadoPago('#mercadopago=completar&empresa=7&code=TG-a%2Bb%2F1&state=abc%3D%3D')).toEqual({
      tipo: 'completar',
      idEmpresa: 7,
      code: 'TG-a+b/1',
      state: 'abc==',
    })
  })

  it('lee la cancelación sin exigir code ni state', () => {
    expect(leerRetornoDeMercadoPago('#mercadopago=cancelado&empresa=12')).toEqual({ tipo: 'cancelado', idEmpresa: 12 })
  })

  it('acepta el hash sin el # inicial', () => {
    expect(leerRetornoDeMercadoPago('mercadopago=cancelado&empresa=3')).toEqual({ tipo: 'cancelado', idEmpresa: 3 })
  })

  it.each([
    ['vacío', ''],
    ['solo #', '#'],
    ['otro fragmento', '#seccion-precios'],
    ['resultado desconocido', '#mercadopago=conectado&empresa=7'],
    ['sin code', '#mercadopago=completar&empresa=7&state=abc'],
    ['sin state', '#mercadopago=completar&empresa=7&code=abc'],
    ['code vacío', '#mercadopago=completar&empresa=7&code=&state=abc'],
    ['sin empresa', '#mercadopago=completar&code=a&state=b'],
    ['empresa no numérica', '#mercadopago=completar&empresa=siete&code=a&state=b'],
    ['empresa cero', '#mercadopago=cancelado&empresa=0'],
    ['empresa negativa', '#mercadopago=cancelado&empresa=-3'],
    ['empresa decimal', '#mercadopago=cancelado&empresa=3.5'],
    ['empresa con ceros a la izquierda', '#mercadopago=cancelado&empresa=007'],
    ['empresa fuera de rango', '#mercadopago=cancelado&empresa=99999999999'],
  ])('ante la duda no devuelve nada: %s', (_caso, hash) => {
    expect(leerRetornoDeMercadoPago(hash)).toBeNull()
  })
})

describe('copiaDeFalloDeMercadoPago', () => {
  it.each([
    ['mercadopago_no_configurado', /no está configurado en el servidor/],
    ['mercadopago_ya_conectada', /ya tiene Mercado Pago conectado/],
    ['mercadopago_cuenta_en_uso', /ya está conectada a otra empresa/],
    ['mercadopago_intercambio_fallido', /no aceptó la conexión/],
    ['mercadopago_estado_ajeno', /otra sesión o otra empresa/],
    ['mercadopago_estado_invalido', /venció o no es válido/],
    ['cuenta_tesoreria_inactiva', /inactiva/],
    ['cuenta_tesoreria_de_otra_empresa', /otra empresa/],
    ['referencia_invalida', /ya no existe/],
  ])('elige la copia por el código %s y no por el mensaje', (codigo, esperada) => {
    const error = new ErrorApi(409, codigo, 'texto libre del servidor que no cuenta')

    expect(copiaDeFalloDeMercadoPago(error, 'completar')).toMatch(esperada)
    expect(copiaDeFalloDeMercadoPago(error, 'completar')).not.toContain('texto libre')
  })

  it('un 404 sin código conocido habla de la empresa o de la conexión', () => {
    expect(copiaDeFalloDeMercadoPago(new ErrorApi(404, 'no_encontrado', 'x'), 'desconectar')).toMatch(/ya no existe/)
  })

  it('un 403 sin código conocido habla del permiso', () => {
    expect(copiaDeFalloDeMercadoPago(new ErrorApi(403, 'prohibido', 'x'), 'iniciar')).toMatch(/permiso/)
  })

  it.each([
    ['leer', /leer el estado/],
    ['iniciar', /iniciar la conexión/],
    ['completar', /completar la conexión/],
    ['desconectar', /desconectar/],
  ] as const)('un error sin código conocido o que no es de la API cae en la copia genérica de %s', (accion, esperada) => {
    expect(copiaDeFalloDeMercadoPago(new ErrorApi(500, 'error_interno', 'x'), accion)).toMatch(esperada)
    expect(copiaDeFalloDeMercadoPago(new Error('red'), accion)).toMatch(esperada)
    expect(copiaDeFalloDeMercadoPago(null, accion)).toMatch(esperada)
  })
})

describe('esUrlDeAutorizacionNavegable', () => {
  it.each(['https://auth.mercadopago.com/authorization?client_id=1', 'http://localhost:5000/x'])('acepta %s', (url) => {
    expect(esUrlDeAutorizacionNavegable(url)).toBe(true)
  })

  it.each(['javascript:alert(1)', 'data:text/html,hola', '/relativa', 'no es una url', '', null, undefined, 42])(
    'rechaza %s',
    (url) => {
      expect(esUrlDeAutorizacionNavegable(url)).toBe(false)
    },
  )
})

describe('clienteDeMercadoPago', () => {
  beforeEach(() => {
    apiGetMock.mockReset()
    apiPostMock.mockReset()
    apiDeleteMock.mockReset()
  })

  it('apunta cada llamada a su ruta, con el cuerpo que espera el servidor', async () => {
    await clienteDeMercadoPago.obtener(7)
    await clienteDeMercadoPago.iniciar(7, 31)
    await clienteDeMercadoPago.completar(7, { code: 'c', state: 's' })
    await clienteDeMercadoPago.desconectar(7)

    expect(apiGetMock).toHaveBeenCalledWith('/empresas/7/mercadopago/conexion')
    expect(apiPostMock).toHaveBeenNthCalledWith(1, '/empresas/7/mercadopago/conexion/inicio', { idCuentaTesoreria: 31 })
    expect(apiPostMock).toHaveBeenNthCalledWith(2, '/empresas/7/mercadopago/conexion/completar', { code: 'c', state: 's' })
    expect(apiDeleteMock).toHaveBeenCalledWith('/empresas/7/mercadopago/conexion')
  })
})

describe('clientes de locales y cajas de Mercado Pago', () => {
  beforeEach(() => {
    apiGetMock.mockReset()
    apiPostMock.mockReset()
    apiDeleteMock.mockReset()
  })

  it('el local de una sucursal se lee y se registra en su ruta, sin cuerpo', async () => {
    await clienteDeLocalesMercadoPago.obtener(5)
    await clienteDeLocalesMercadoPago.registrar(5)

    expect(apiGetMock).toHaveBeenCalledWith('/sucursales/5/mercadopago')
    expect(apiPostMock).toHaveBeenCalledWith('/sucursales/5/mercadopago')
  })

  it('la caja de un punto de venta, sus terminales y su asignación van cada una a su ruta', async () => {
    await clienteDeCajasMercadoPago.obtener(9)
    await clienteDeCajasMercadoPago.registrar(9)
    await clienteDeCajasMercadoPago.listarTerminales(9)
    await clienteDeCajasMercadoPago.asignarTerminal(9, 'PAX_A910__8701')
    await clienteDeCajasMercadoPago.quitarTerminal(9)

    expect(apiGetMock).toHaveBeenNthCalledWith(1, '/puntos-venta/9/mercadopago')
    expect(apiGetMock).toHaveBeenNthCalledWith(2, '/puntos-venta/9/mercadopago/terminales')
    expect(apiPostMock).toHaveBeenNthCalledWith(1, '/puntos-venta/9/mercadopago')
    expect(apiPostMock).toHaveBeenNthCalledWith(2, '/puntos-venta/9/mercadopago/terminal', { idTerminal: 'PAX_A910__8701' })
    expect(apiDeleteMock).toHaveBeenCalledWith('/puntos-venta/9/mercadopago/terminal')
  })
})

describe('copiaDeFalloDeMercadoPago — locales, cajas y terminales', () => {
  it.each([
    ['direccion_incompleta_para_mercadopago', /faltan datos de la dirección/],
    ['nombre_invalido_para_mercadopago', /nombre del punto de venta/],
    ['mercadopago_no_conectada', /no tiene Mercado Pago conectado/],
    ['mercadopago_requiere_reconexion', /volver a conectar/],
    ['mercadopago_renovacion_rechazada', /rechazó la renovación/],
    ['mercadopago_credenciales_rechazadas', /credenciales de la plataforma/],
    ['mercadopago_local_no_registrado', /registrar la sucursal/],
    ['mercadopago_local_duplicado', /más de un local/],
    ['mercadopago_caja_duplicada', /más de una caja/],
    ['mercadopago_punto_venta_movido', /cambió de sucursal/],
    ['mercadopago_caja_no_registrada', /todavía no está registrado como caja/],
    ['mercadopago_terminal_no_vinculada', /no está vinculada a la caja/],
    ['mercadopago_terminal_modelo_no_admitido', /no admite el modo/],
    ['mercadopago_caja_con_terminal_en_pdv', /modo PDV/],
    ['terminal_point_invalida', /identificador de la terminal/],
    ['mercadopago_solicitud_rechazada', /rechazó la solicitud/],
    ['mercadopago_respuesta_invalida', /respondió algo inesperado/],
    ['mercadopago_no_disponible', /no está disponible/],
  ])('el código %s tiene su copia propia, sin usar el mensaje del servidor', (codigo, esperada) => {
    const error = new ErrorApi(409, codigo, 'texto libre del servidor')

    expect(copiaDeFalloDeMercadoPago(error, 'registrar_caja')).toMatch(esperada)
    expect(copiaDeFalloDeMercadoPago(error, 'registrar_caja')).not.toContain('texto libre')
  })

  it('un 404 nombra la sucursal para las acciones del local y el punto de venta para las de la caja', () => {
    const error = new ErrorApi(404, 'no_encontrado', 'x')

    expect(copiaDeFalloDeMercadoPago(error, 'leer_local')).toMatch(/La sucursal ya no existe/)
    expect(copiaDeFalloDeMercadoPago(error, 'registrar_local')).toMatch(/La sucursal ya no existe/)
    expect(copiaDeFalloDeMercadoPago(error, 'leer_terminales')).toMatch(/El punto de venta ya no existe/)
    expect(copiaDeFalloDeMercadoPago(error, 'asignar_terminal')).toMatch(/El punto de venta ya no existe/)
  })

  it.each([
    ['leer_local', /sucursal/],
    ['registrar_local', /registrar la sucursal/],
    ['leer_caja', /estado del punto de venta/],
    ['registrar_caja', /registrar el punto de venta/],
    ['leer_terminales', /terminales/],
    ['asignar_terminal', /asignar la terminal/],
    ['quitar_terminal', /quitar la terminal/],
  ] as const)('un error sin código conocido cae en la copia genérica de %s', (accion, esperada) => {
    expect(copiaDeFalloDeMercadoPago(new ErrorApi(500, 'error_interno', 'x'), accion)).toMatch(esperada)
    expect(copiaDeFalloDeMercadoPago(new Error('red'), accion)).toMatch(esperada)
  })
})
