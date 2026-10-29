import { describe, expect, it } from 'vitest'
import { ErrorApi } from '../../api/cliente'
import { mensajeDeErrorDeEdicion, MENSAJE_DE_CAMBIO_CONCURRENTE } from './errorDeEdicionDeCompra'

describe('mensajeDeErrorDeEdicion', () => {
  it('el cambio concurrente usa la copia propia y no el texto del servidor', () => {
    const e = new ErrorApi(409, 'compra_cambio_concurrente', 'La compra cambió de estado mientras se editaba. Recargala y volvé a intentar.')

    expect(mensajeDeErrorDeEdicion(e)).toBe(MENSAJE_DE_CAMBIO_CONCURRENTE)
  })

  it.each(['compra_edicion_stock_negativo', 'compra_duplicada', 'lote_vencido_en_recepcion', 'compra_no_editable'])(
    'el rechazo %s se muestra con el mensaje del servidor',
    (codigo) => {
      expect(mensajeDeErrorDeEdicion(new ErrorApi(409, codigo, `motivo de ${codigo}`))).toBe(`motivo de ${codigo}`)
    },
  )

  it('un error de red o desconocido usa la copia genérica', () => {
    expect(mensajeDeErrorDeEdicion(new Error('boom'))).toBe('No se pudieron guardar los cambios de la compra.')
  })

  it('un rechazo sin mensaje usa la copia genérica', () => {
    expect(mensajeDeErrorDeEdicion(new ErrorApi(500, 'x', ''))).toBe('No se pudieron guardar los cambios de la compra.')
  })
})
