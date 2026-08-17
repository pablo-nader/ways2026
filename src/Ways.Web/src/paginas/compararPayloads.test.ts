import { describe, expect, it } from 'vitest'
import { compararPayloads } from './compararPayloads'

describe('compararPayloads', () => {
  // mutation target (tasks.md slice 7, fila única): tratar esta rama como 'sin_cambio' debe
  // hacer fallar este test.
  it('una clave presente solo en nuevo se marca agregada, nunca sin_cambio', () => {
    const resultado = compararPayloads({ id_lista_precio: 1 }, { id_lista_precio: 1, vigente_desde: '2026-08-14' })

    const agregada = resultado.find((c) => c.clave === 'vigente_desde')
    expect(agregada?.estado).toBe('agregada')
    expect(agregada?.valorAnterior).toBeUndefined()
    expect(agregada?.valorNuevo).toBe('2026-08-14')
  })

  it('anterior null (accion hecho puro) marca TODAS las claves de nuevo como agregadas', () => {
    const resultado = compararPayloads(null, { por_el_propio_usuario: true })

    expect(resultado).toEqual([{ clave: 'por_el_propio_usuario', valorAnterior: undefined, valorNuevo: true, estado: 'agregada' }])
  })

  it('una clave con valor distinto en anterior y nuevo se marca cambiada', () => {
    const resultado = compararPayloads({ monto: 100 }, { monto: 150 })

    expect(resultado).toEqual([{ clave: 'monto', valorAnterior: 100, valorNuevo: 150, estado: 'cambiada' }])
  })

  it('una clave con el mismo valor primitivo en anterior y nuevo se marca sin_cambio', () => {
    const resultado = compararPayloads({ estado: 'activo' }, { estado: 'activo' })

    expect(resultado).toEqual([{ clave: 'estado', valorAnterior: 'activo', valorNuevo: 'activo', estado: 'sin_cambio' }])
  })

  it('una clave null en ambos lados se marca sin_cambio, no cambiada', () => {
    const resultado = compararPayloads({ deleted_at: null }, { deleted_at: null })

    expect(resultado).toEqual([{ clave: 'deleted_at', valorAnterior: null, valorNuevo: null, estado: 'sin_cambio' }])
  })

  it('un valor objeto anidado igual estructuralmente se marca sin_cambio (comparacion profunda, no por referencia)', () => {
    const resultado = compararPayloads(
      { movimientos_generados: [1, 2, 3] },
      { movimientos_generados: [1, 2, 3] },
    )

    expect(resultado).toEqual([{ clave: 'movimientos_generados', valorAnterior: [1, 2, 3], valorNuevo: [1, 2, 3], estado: 'sin_cambio' }])
  })

  // judgment-day ronda 2, juez A, sugerencia: `sonIguales` compara con `JSON.stringify`, sensible
  // al orden de claves — fijado acá como comportamiento CONOCIDO, no como bug: un objeto anidado
  // semánticamente igual pero con distinto orden de claves se reporta 'cambiada'. El riesgo real
  // es bajo porque ambos payloads salen del mismo serializador (mismo orden para el mismo shape).
  it('(limitación conocida) un objeto anidado semánticamente igual pero con distinto orden de claves se marca cambiada', () => {
    const resultado = compararPayloads({ datos: { a: 1, b: 2 } }, { datos: { b: 2, a: 1 } })

    expect(resultado).toEqual([
      { clave: 'datos', valorAnterior: { a: 1, b: 2 }, valorNuevo: { b: 2, a: 1 }, estado: 'cambiada' },
    ])
  })
})
