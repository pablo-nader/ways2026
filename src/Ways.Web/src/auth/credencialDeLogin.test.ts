import { describe, expect, it } from 'vitest'
import { ErrorApi } from '../api/cliente'
import { credencialDeLogin, MENSAJE_USUARIO_REQUIERE_DOMINIO, mensajeDeFalloDeLogin } from './credencialDeLogin'

describe('credencialDeLogin', () => {
  it('bajo la plataforma todo es un correo, aunque no tenga @', () => {
    expect(credencialDeLogin('jperez', false)).toEqual({ mail: 'jperez' })
    expect(credencialDeLogin('ana@ways.test', false)).toEqual({ mail: 'ana@ways.test' })
  })

  it('bajo un comercio, un valor sin @ es un usuario', () => {
    expect(credencialDeLogin('jperez', true)).toEqual({ usuario: 'jperez' })
  })

  it('bajo un comercio, un valor con @ es un correo', () => {
    expect(credencialDeLogin('ana@ways.test', true)).toEqual({ mail: 'ana@ways.test' })
  })

  it('devuelve exactamente una clave, nunca las dos', () => {
    expect(Object.keys(credencialDeLogin('jperez', true))).toEqual(['usuario'])
    expect(Object.keys(credencialDeLogin('ana@ways.test', true))).toEqual(['mail'])
  })

  it('descarta los espacios de los extremos antes de decidir', () => {
    expect(credencialDeLogin('  jperez  ', true)).toEqual({ usuario: 'jperez' })
    expect(credencialDeLogin(' ana@ways.test ', true)).toEqual({ mail: 'ana@ways.test' })
  })
})

describe('mensajeDeFalloDeLogin', () => {
  it('usuario_requiere_dominio rinde la copia propia aunque el servidor mande otro texto', () => {
    const error = new ErrorApi(400, 'usuario_requiere_dominio', 'otro texto')

    expect(mensajeDeFalloDeLogin(error)).toBe(MENSAJE_USUARIO_REQUIERE_DOMINIO)
  })

  it('cualquier otro rechazo rinde el mensaje de la API', () => {
    const error = new ErrorApi(401, 'credenciales_invalidas', 'Usuario o contraseña incorrectos.')

    expect(mensajeDeFalloDeLogin(error)).toBe('Usuario o contraseña incorrectos.')
  })

  it('un error que no viene de la API rinde la copia genérica', () => {
    expect(mensajeDeFalloDeLogin(new TypeError('boom'))).toBe('No se pudo iniciar sesión.')
  })
})
