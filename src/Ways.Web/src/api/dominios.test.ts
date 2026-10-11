import { describe, expect, it } from 'vitest'
import { ErrorApi } from './cliente'
import { COPIA_RESULTADO_INCIERTO } from './bajas'
import {
  AYUDA_DE_VERIFICACION,
  copiaDeFalloDeDominio,
  TEXTO_SIN_VERIFICAR,
  textoDeVerificacion,
} from './dominios'

describe('textoDeVerificacion', () => {
  it('sin marca rinde "Sin verificar"', () => {
    expect(textoDeVerificacion(null)).toBe(TEXTO_SIN_VERIFICAR)
    expect(TEXTO_SIN_VERIFICAR).toBe('Sin verificar')
  })

  it('con marca rinde la fecha de la marca, no la de hoy ni "Sin verificar"', () => {
    // Mediodía UTC: la misma fecha local en cualquier huso entre -12 y +11.
    expect(textoDeVerificacion('2026-10-11T12:00:00Z')).toBe('Verificado el 11/10/2026')
  })
})

describe('copiaDeFalloDeDominio', () => {
  const encabezado = 'No se pudo agregar el dominio.'

  it('host_duplicado rinde su copia propia aunque el servidor mande otro texto', () => {
    const error = new ErrorApi(409, 'host_duplicado', 'texto crudo del servidor')

    expect(copiaDeFalloDeDominio(error, encabezado)).toBe('Ese host ya está cargado en otro dominio.')
  })

  it('host_invalido rinde su copia propia aunque el servidor mande otro texto', () => {
    const error = new ErrorApi(400, 'host_invalido', 'texto crudo del servidor')

    expect(copiaDeFalloDeDominio(error, encabezado)).toBe(
      'El host no es válido: indica solo el nombre de dominio, sin https:// ni rutas.',
    )
  })

  it('un 404 dice que el tenant o el dominio ya no existe, sin repetir el texto del servidor', () => {
    const error = new ErrorApi(404, 'no_encontrado', 'texto crudo del servidor')

    expect(copiaDeFalloDeDominio(error, encabezado)).toBe(
      'No se pudo agregar el dominio. El tenant o el dominio ya no existe. Actualiza la pantalla.',
    )
  })

  it('un 5xx sin código de resultado incierto manda a verificar el listado', () => {
    const error = new ErrorApi(500, 'error_interno', 'Ocurrió un error inesperado')

    expect(copiaDeFalloDeDominio(error, encabezado)).toBe(`${encabezado} ${COPIA_RESULTADO_INCIERTO}`)
  })

  it('un 5xx con resultado_incierto rinde el texto del servidor, que dice qué verificar', () => {
    const error = new ErrorApi(503, 'resultado_incierto', 'No se pudo confirmar. Revisa la lista de dominios.')

    expect(copiaDeFalloDeDominio(error, encabezado)).toBe(
      `${encabezado} No se pudo confirmar. Revisa la lista de dominios.`,
    )
  })

  it('un error que no viene de la API tampoco dice si se escribió', () => {
    expect(copiaDeFalloDeDominio(new TypeError('boom'), encabezado)).toBe(`${encabezado} ${COPIA_RESULTADO_INCIERTO}`)
  })

  it('cualquier otro rechazo rinde el mensaje de la API', () => {
    const error = new ErrorApi(400, 'solicitud_invalida', 'El cuerpo no es válido.')

    expect(copiaDeFalloDeDominio(error, encabezado)).toBe('El cuerpo no es válido.')
  })

  it('un rechazo sin mensaje rinde el encabezado: nunca queda un aviso vacío', () => {
    expect(copiaDeFalloDeDominio(new ErrorApi(400, 'solicitud_invalida', '  '), encabezado)).toBe(encabezado)
  })

  it('un código exótico del servidor no resuelve contra el prototipo del mapa', () => {
    const error = new ErrorApi(400, 'constructor', 'Mensaje del servidor.')

    expect(copiaDeFalloDeDominio(error, encabezado)).toBe('Mensaje del servidor.')
  })
})

describe('AYUDA_DE_VERIFICACION', () => {
  it('nombra el DNS y el panel del hosting, y aclara que marcar no comprueba nada', () => {
    expect(AYUDA_DE_VERIFICACION).toContain('DNS')
    expect(AYUDA_DE_VERIFICACION).toContain('panel del hosting')
    expect(AYUDA_DE_VERIFICACION).toContain('no comprueba el DNS')
  })
})
