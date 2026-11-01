import { describe, expect, it } from 'vitest'
import {
  booleanoDeSelect,
  etiquetaDeEstadoDeTurno,
  motivoLegibleDeMedioNoEditable,
  tituloDeCambiarMedio,
  valorDeSelectBooleano,
} from './utilidadesVentasDeAdministracion'

describe('motivoLegibleDeMedioNoEditable', () => {
  it('traduce un código conocido', () => {
    expect(motivoLegibleDeMedioNoEditable('venta_con_cae')).toContain('CAE')
  })

  it('deja un texto desconocido tal cual', () => {
    expect(motivoLegibleDeMedioNoEditable('otro_motivo')).toBe('otro_motivo')
  })

  it.each([null, '', '   '])('sin motivo (%j) usa uno genérico', (motivo) => {
    expect(motivoLegibleDeMedioNoEditable(motivo)).toBe('No se puede cambiar el medio de pago de esta venta.')
  })
})

describe('tituloDeCambiarMedio', () => {
  it('una venta editable lleva el nombre de la acción', () => {
    expect(tituloDeCambiarMedio({ medioEditable: true, motivoMedioNoEditable: null })).toBe('Cambiar medio de pago')
  })

  it('una no editable lleva el motivo', () => {
    expect(tituloDeCambiarMedio({ medioEditable: false, motivoMedioNoEditable: 'periodo_conciliado' })).toContain('conciliada')
  })

  it('un motivo presente en una venta editable se ignora', () => {
    expect(tituloDeCambiarMedio({ medioEditable: true, motivoMedioNoEditable: 'venta_con_cae' })).toBe('Cambiar medio de pago')
  })
})

describe('etiquetaDeEstadoDeTurno', () => {
  it.each<[boolean | null, string]>([
    [null, '—'],
    [false, 'Abierto'],
    [true, 'Cerrado'],
  ])('%j se muestra como %s', (valor, esperado) => {
    expect(etiquetaDeEstadoDeTurno(valor)).toBe(esperado)
  })
})

describe('select booleano', () => {
  it.each<[boolean | null, string]>([
    [null, ''],
    [true, 'true'],
    [false, 'false'],
  ])('%j ida y vuelta con %j', (valor, texto) => {
    expect(valorDeSelectBooleano(valor)).toBe(texto)
    expect(booleanoDeSelect(texto)).toBe(valor)
  })
})
