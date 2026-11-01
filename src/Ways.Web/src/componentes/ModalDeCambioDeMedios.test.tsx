import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ModalDeCambioDeMedios } from './ModalDeCambioDeMedios'
import { MENSAJES_DE_CAMBIO_DE_MEDIOS } from '../api/cambioDeMedios'
import type { ComprobanteEmitido, MedioPagoListado, PagoEmitido } from '../api/tipos'

const apiGetMock = vi.fn()
const apiPutMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    put: (...args: unknown[]) => apiPutMock(...(args as [string, unknown])),
  },
  ErrorApi: class ErrorApiMock extends Error {
    estado: number
    codigo: string
    constructor(estado: number, codigo: string, mensaje: string) {
      super(mensaje)
      this.name = 'ErrorApi'
      this.estado = estado
      this.codigo = codigo
    }
  },
}))

// Se importa después del mock para tomar la misma clase que usa el código bajo prueba.
const { ErrorApi } = await import('../api/cliente')

function medio(id: number, sobrescribir: Partial<MedioPagoListado> = {}): MedioPagoListado {
  return {
    id,
    nombre: `Medio ${id}`,
    activo: true,
    idEmpresa: null,
    orden: id,
    comportamiento: 'Electronico',
    admiteVuelto: false,
    requiereReferencia: false,
    recargoPorcentaje: null,
    porcentajeFacturacion: 0,
    ...sobrescribir,
  }
}

const MEDIOS = [
  medio(1, { nombre: 'Efectivo', comportamiento: 'Efectivo', admiteVuelto: true }),
  medio(2, { nombre: 'Tarjeta', requiereReferencia: true }),
  medio(3, { nombre: 'Transferencia' }),
  medio(4, { nombre: 'Cuenta corriente', comportamiento: 'CuentaCorriente' }),
]

function comprobante(pagos: PagoEmitido[], sobrescribir: Partial<ComprobanteEmitido> = {}): ComprobanteEmitido {
  return {
    id: 9,
    numero: 9,
    numeroVisible: '0007-00000009',
    estado: 'Emitido',
    fecha: '2026-09-16T12:05:00Z',
    idPuntoVenta: 7,
    idCliente: 3,
    idComprobanteAsociado: null,
    subtotal: 1000,
    descuentoTotal: 0,
    descuentoManualTotal: 0,
    recargoManualTotal: 0,
    total: 1000,
    direccionEntrega: null,
    observaciones: null,
    items: [],
    pagos,
    idPresupuestoOrigen: null,
    ...sobrescribir,
  }
}

const EN_EFECTIVO = comprobante([{ idMedioPago: 1, importe: 1000, referencia: null, vuelto: 0 }])

const alGuardar = vi.fn()
const alCerrar = vi.fn()

function montar(opciones: { modo?: 'pos' | 'administracion'; venta?: { id: number; numeroVisible: string } } = {}) {
  return render(
    <ModalDeCambioDeMedios
      venta={opciones.venta ?? { id: 9, numeroVisible: '0007-00000009' }}
      medios={MEDIOS}
      modo={opciones.modo}
      onGuardado={alGuardar}
      onCerrar={alCerrar}
    />,
  )
}

async function esperarFormulario() {
  return screen.findByLabelText('Medio de la línea 1')
}

const guardar = () => screen.getByRole('button', { name: /Guardar cambio|Guardando/ })

beforeEach(() => {
  apiGetMock.mockReset()
  apiPutMock.mockReset()
  alGuardar.mockReset()
  alCerrar.mockReset()
  apiGetMock.mockResolvedValue(EN_EFECTIVO)
})

describe('ModalDeCambioDeMedios — precarga', () => {
  it('lee la venta y precarga medio, importe y referencia de sus cobros', async () => {
    apiGetMock.mockResolvedValue(comprobante([{ idMedioPago: 2, importe: 1000, referencia: 'cupón 41', vuelto: 0 }]))
    montar()

    const selector = await esperarFormulario()
    expect(apiGetMock).toHaveBeenCalledWith('/ventas/9')
    expect(selector).toHaveValue('2')
    expect(screen.getByLabelText('Importe de la línea 1')).toHaveValue('1.000,00')
    expect(screen.getByLabelText('Referencia de la línea 1')).toHaveValue('cupón 41')
    expect(screen.getByRole('dialog', { name: 'Cambiar medio de pago — 0007-00000009' })).toBeInTheDocument()
  })

  it('precarga varias líneas con su vuelto', async () => {
    apiGetMock.mockResolvedValue(
      comprobante([
        { idMedioPago: 1, importe: 700, referencia: null, vuelto: 100 },
        { idMedioPago: 3, importe: 400, referencia: null, vuelto: 0 },
      ]),
    )
    montar()

    await esperarFormulario()
    expect(screen.getByLabelText('Medio de la línea 2')).toHaveValue('3')
    expect(screen.getByLabelText('Vuelto de la línea 1')).toHaveValue('100,00')
    expect(screen.getByText('Sin diferencia')).toBeInTheDocument()
  })

  it('el selector no ofrece la cuenta corriente', async () => {
    montar()

    const selector = await esperarFormulario()
    const opciones = within(selector).getAllByRole('option').map((o) => o.textContent)
    expect(opciones).toEqual(['Elegir…', 'Efectivo', 'Tarjeta', 'Transferencia'])
  })

  it('una venta cobrada con cuenta corriente no abre el formulario y explica por qué', async () => {
    apiGetMock.mockResolvedValue(comprobante([{ idMedioPago: 4, importe: 1000, referencia: null, vuelto: 0 }]))
    montar()

    expect(await screen.findByRole('alert')).toHaveTextContent(MENSAJES_DE_CAMBIO_DE_MEDIOS.cambio_de_medios_con_cuenta_corriente)
    expect(screen.queryByLabelText('Medio de la línea 1')).not.toBeInTheDocument()
    expect(guardar()).toBeDisabled()
  })

  it('una venta anulada no abre el formulario', async () => {
    apiGetMock.mockResolvedValue(comprobante([{ idMedioPago: 1, importe: 1000, referencia: null, vuelto: 0 }], { estado: 'Anulado' }))
    montar()

    expect(await screen.findByRole('alert')).toHaveTextContent(MENSAJES_DE_CAMBIO_DE_MEDIOS.venta_anulada)
    expect(screen.queryByLabelText('Medio de la línea 1')).not.toBeInTheDocument()
  })

  it('si la lectura falla muestra el mensaje y no deja guardar', async () => {
    apiGetMock.mockRejectedValue(new ErrorApi(500, 'error', 'No se pudo leer la venta.'))
    montar()

    expect(await screen.findByRole('alert')).toHaveTextContent('No se pudo leer la venta.')
    expect(guardar()).toBeDisabled()
  })

  it('la respuesta de una lectura vieja no pisa la de la venta que la reemplazó', async () => {
    let resolverVieja: (c: ComprobanteEmitido) => void = () => undefined
    apiGetMock.mockImplementation((ruta: string) =>
      ruta === '/ventas/9'
        ? new Promise<ComprobanteEmitido>((resolver) => {
            resolverVieja = resolver
          })
        : Promise.resolve(comprobante([{ idMedioPago: 3, importe: 1000, referencia: null, vuelto: 0 }], { id: 10 })),
    )
    const { rerender } = montar()
    rerender(
      <ModalDeCambioDeMedios
        venta={{ id: 10, numeroVisible: '0007-00000010' }}
        medios={MEDIOS}
        onGuardado={alGuardar}
        onCerrar={alCerrar}
      />,
    )

    const selector = await esperarFormulario()
    expect(selector).toHaveValue('3')

    await act(async () => {
      resolverVieja(EN_EFECTIVO)
    })
    expect(screen.getByLabelText('Medio de la línea 1')).toHaveValue('3')
  })
})

describe('ModalDeCambioDeMedios — validación', () => {
  it('sin cambios no deja guardar y lo dice', async () => {
    montar()
    await esperarFormulario()

    expect(guardar()).toBeDisabled()
    expect(screen.getByText('Todavía no cambiaste nada.')).toBeInTheDocument()
  })

  it('cambiar el medio con el mismo total habilita guardar', async () => {
    montar()
    const selector = await esperarFormulario()

    await userEvent.selectOptions(selector, '3')

    expect(guardar()).toBeEnabled()
    expect(screen.queryByText('Todavía no cambiaste nada.')).not.toBeInTheDocument()
  })

  it('un total distinto muestra cuánto falta y deshabilita guardar hasta que coincide', async () => {
    montar()
    const selector = await esperarFormulario()
    await userEvent.selectOptions(selector, '3')

    const importe = screen.getByLabelText('Importe de la línea 1')
    await userEvent.clear(importe)
    await userEvent.type(importe, '900')
    expect(screen.getByText('Falta $ 100,00')).toBeInTheDocument()
    expect(guardar()).toBeDisabled()

    await userEvent.clear(importe)
    await userEvent.type(importe, '1100')
    expect(screen.getByText('Sobra $ 100,00')).toBeInTheDocument()
    expect(guardar()).toBeDisabled()

    await userEvent.clear(importe)
    await userEvent.type(importe, '1000')
    expect(screen.getByText('Sin diferencia')).toBeInTheDocument()
    expect(guardar()).toBeEnabled()
  })

  it('repartir entre dos medios: la diferencia sigue la suma de las líneas', async () => {
    montar()
    const selector = await esperarFormulario()
    await userEvent.selectOptions(selector, '3')
    const importe = screen.getByLabelText('Importe de la línea 1')
    await userEvent.clear(importe)
    await userEvent.type(importe, '600')
    expect(screen.getByText('Falta $ 400,00')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Agregar medio' }))
    await userEvent.selectOptions(screen.getByLabelText('Medio de la línea 2'), '1')
    await userEvent.type(screen.getByLabelText('Importe de la línea 2'), '400')

    expect(screen.getByText('Sin diferencia')).toBeInTheDocument()
    expect(guardar()).toBeEnabled()

    await userEvent.click(screen.getByRole('button', { name: 'Quitar línea 2' }))
    expect(screen.queryByLabelText('Medio de la línea 2')).not.toBeInTheDocument()
    expect(screen.getByText('Falta $ 400,00')).toBeInTheDocument()
  })

  it('un medio que exige referencia no deja guardar sin ella', async () => {
    montar()
    const selector = await esperarFormulario()
    await userEvent.selectOptions(selector, '2')

    expect(guardar()).toBeDisabled()
    expect(screen.getByText('Este medio de pago requiere una referencia.')).toBeInTheDocument()

    await userEvent.type(screen.getByLabelText('Referencia de la línea 1'), 'cupón 9')
    expect(guardar()).toBeEnabled()
  })

  it('el vuelto solo se edita en un medio que lo admite y el exceso en efectivo lo sugiere', async () => {
    montar()
    const selector = await esperarFormulario()
    expect(screen.getByLabelText('Vuelto de la línea 1')).toBeEnabled()

    await userEvent.selectOptions(selector, '3')
    expect(screen.getByLabelText('Vuelto de la línea 1')).toBeDisabled()

    await userEvent.selectOptions(selector, '1')
    const importe = screen.getByLabelText('Importe de la línea 1')
    await userEvent.clear(importe)
    await userEvent.type(importe, '1100')
    expect(screen.getByLabelText('Vuelto de la línea 1')).toHaveValue('100,00')
    expect(screen.getByText('Sin diferencia')).toBeInTheDocument()
  })
})

describe('ModalDeCambioDeMedios — guardado', () => {
  async function cambiarATransferencia() {
    const selector = await esperarFormulario()
    await userEvent.selectOptions(selector, '3')
  }

  it('envía los pagos nuevos a PUT /ventas/{id}/medios y avisa con la venta corregida', async () => {
    const corregida = comprobante([{ idMedioPago: 3, importe: 1000, referencia: null, vuelto: 0 }])
    apiPutMock.mockResolvedValue(corregida)
    montar()
    await cambiarATransferencia()

    await userEvent.click(guardar())

    await waitFor(() => expect(alGuardar).toHaveBeenCalledWith(corregida))
    expect(apiPutMock).toHaveBeenCalledTimes(1)
    expect(apiPutMock).toHaveBeenCalledWith('/ventas/9/medios', { pagos: [{ idMedioPago: 3, importe: 1000, referencia: null, vuelto: 0 }] })
  })

  it('en modo administración usa el endpoint de administración', async () => {
    apiPutMock.mockResolvedValue(EN_EFECTIVO)
    montar({ modo: 'administracion' })
    await cambiarATransferencia()

    await userEvent.click(guardar())

    await waitFor(() => expect(alGuardar).toHaveBeenCalledTimes(1))
    expect(apiPutMock.mock.calls[0][0]).toBe('/ventas/administracion/9/medios')
  })

  it('envía el vuelto y la referencia que tipeó el cajero', async () => {
    apiPutMock.mockResolvedValue(EN_EFECTIVO)
    montar()
    const selector = await esperarFormulario()
    await userEvent.selectOptions(selector, '2')
    await userEvent.type(screen.getByLabelText('Referencia de la línea 1'), '  cupón 5 ')
    await userEvent.click(screen.getByRole('button', { name: 'Agregar medio' }))
    await userEvent.selectOptions(screen.getByLabelText('Medio de la línea 2'), '1')

    const importe1 = screen.getByLabelText('Importe de la línea 1')
    await userEvent.clear(importe1)
    await userEvent.type(importe1, '500')
    await userEvent.type(screen.getByLabelText('Importe de la línea 2'), '600')
    await userEvent.click(guardar())

    await waitFor(() => expect(alGuardar).toHaveBeenCalledTimes(1))
    expect(apiPutMock.mock.calls[0][1]).toEqual({
      pagos: [
        { idMedioPago: 2, importe: 500, referencia: 'cupón 5', vuelto: 0 },
        { idMedioPago: 1, importe: 600, referencia: null, vuelto: 100 },
      ],
    })
  })

  it.each(Object.keys(MENSAJES_DE_CAMBIO_DE_MEDIOS))('el rechazo %s del servidor se muestra con su mensaje y el diálogo queda abierto', async (codigo) => {
    apiPutMock.mockRejectedValue(new ErrorApi(409, codigo, 'mensaje crudo del servidor'))
    montar()
    await cambiarATransferencia()

    await userEvent.click(guardar())

    const alerta = await screen.findByText(MENSAJES_DE_CAMBIO_DE_MEDIOS[codigo])
    expect(alerta).toHaveAttribute('role', 'alert')
    expect(alGuardar).not.toHaveBeenCalled()
    await waitFor(() => expect(guardar()).toBeEnabled())
    expect(screen.getByRole('button', { name: 'Cancelar' })).toHaveFocus()
  })

  it('el rechazo de una regla de pago conserva el texto del servidor', async () => {
    apiPutMock.mockRejectedValue(new ErrorApi(400, 'vuelto_no_justificado', 'El vuelto de $100 no se justifica con los billetes entregados.'))
    montar()
    await cambiarATransferencia()

    await userEvent.click(guardar())

    expect(await screen.findByText('El vuelto de $100 no se justifica con los billetes entregados.')).toBeInTheDocument()
  })

  it('editar después de un rechazo borra el mensaje anterior', async () => {
    apiPutMock.mockRejectedValue(new ErrorApi(409, 'periodo_conciliado', 'x'))
    montar()
    await cambiarATransferencia()
    await userEvent.click(guardar())
    await screen.findByText(MENSAJES_DE_CAMBIO_DE_MEDIOS.periodo_conciliado)

    await userEvent.selectOptions(screen.getByLabelText('Medio de la línea 1'), '1')

    expect(screen.queryByText(MENSAJES_DE_CAMBIO_DE_MEDIOS.periodo_conciliado)).not.toBeInTheDocument()
  })

  it('reintentar tras un rechazo vuelve a borrar el mensaje mientras la escritura está en vuelo', async () => {
    apiPutMock.mockRejectedValueOnce(new ErrorApi(409, 'periodo_conciliado', 'x'))
    montar()
    await cambiarATransferencia()
    await userEvent.click(guardar())
    await screen.findByText(MENSAJES_DE_CAMBIO_DE_MEDIOS.periodo_conciliado)

    apiPutMock.mockReturnValue(new Promise(() => undefined))
    await userEvent.click(guardar())

    expect(screen.queryByText(MENSAJES_DE_CAMBIO_DE_MEDIOS.periodo_conciliado)).not.toBeInTheDocument()
    expect(guardar()).toBeDisabled()
  })

  it('mientras guarda todo el diálogo queda inerte', async () => {
    apiPutMock.mockReturnValue(new Promise(() => undefined))
    montar()
    await cambiarATransferencia()

    await userEvent.click(guardar())

    expect(screen.getByRole('button', { name: 'Guardando…' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cerrar cambio de medio de pago' })).toBeDisabled()
    expect(screen.getByLabelText('Medio de la línea 1')).toBeDisabled()
    expect(screen.getByLabelText('Importe de la línea 1')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Agregar medio' })).toBeDisabled()
  })

  it('dos clics sincrónicos sobre Guardar emiten una sola escritura', async () => {
    apiPutMock.mockReturnValue(new Promise(() => undefined))
    montar()
    await cambiarATransferencia()

    const boton = guardar()
    act(() => {
      boton.click()
      boton.click()
    })

    expect(apiPutMock).toHaveBeenCalledTimes(1)
  })

  it('Cancelar cierra sin escribir', async () => {
    montar()
    await esperarFormulario()

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(alCerrar).toHaveBeenCalledTimes(1)
    expect(apiPutMock).not.toHaveBeenCalled()
  })
})
