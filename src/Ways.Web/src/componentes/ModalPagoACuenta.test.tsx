import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ErrorApi } from '../api/cliente'
import type { ComprobanteEmitido, EstadoDeCuentaHeader, MedioPagoListado, PuntoVentaListado, VentaPendienteDeCobro } from '../api/tipos'
import { ModalPagoACuenta } from './ModalPagoACuenta'

const registrarPagoMock = vi.fn()
const listarVentasPendientesMock = vi.fn()

vi.mock('../api/cuentaCorriente', async (importarOriginal) => {
  const real = await importarOriginal<typeof import('../api/cuentaCorriente')>()
  return {
    ...real,
    clienteDeCuentaCorriente: {
      ...real.clienteDeCuentaCorriente,
      registrarPago: (...args: unknown[]) => registrarPagoMock(...args),
      listarVentasPendientes: (...args: unknown[]) => listarVentasPendientesMock(...args),
    },
  }
})

const PUNTO_VENTA = { id: 7, nombre: 'Local Centro' } as PuntoVentaListado
const HEADER: EstadoDeCuentaHeader = { saldo: 500, limiteCredito: 5000, creditoIlimitado: false, disponibilidad: 4500 }
const EFECTIVO: MedioPagoListado = {
  id: 1,
  nombre: 'Efectivo',
  activo: true,
  idEmpresa: null,
  orden: 1,
  comportamiento: 'Efectivo',
  admiteVuelto: true,
  requiereReferencia: false,
  recargoPorcentaje: null,
  porcentajeFacturacion: 0,
}

function venta(id: number, saldoPendiente: number, fecha: string): VentaPendienteDeCobro {
  return {
    id,
    codigoTipo: 'FA',
    letra: 'B',
    numero: id,
    numeroVisible: `0001-0000000${id}`,
    fecha,
    parteEnCuentaCorriente: saldoPendiente,
    cobrado: 0,
    saldoPendiente,
  }
}

// La más nueva primero, como las manda el servidor.
const VENTAS = [venta(2, 200, '2026-10-02T12:00:00Z'), venta(1, 100, '2026-10-01T12:00:00Z')]

const COMPROBANTE = { id: 99, numeroVisible: '0007-00000003' } as ComprobanteEmitido

type Props = { onRegistrado?: (c: ComprobanteEmitido) => void }

function renderizar({ onRegistrado = vi.fn() }: Props = {}) {
  return render(
    <ModalPagoACuenta
      idCliente={5}
      puntosVenta={[PUNTO_VENTA]}
      medios={[EFECTIVO]}
      header={HEADER}
      onCerrar={vi.fn()}
      onAntesDeEscribir={vi.fn()}
      onRegistrado={onRegistrado}
    />,
  )
}

async function cargarPago(importe: string) {
  await userEvent.selectOptions(screen.getByLabelText('Medio de pago'), 'Efectivo')
  await userEvent.type(screen.getByLabelText('Importe'), importe)
  await waitFor(() => expect(screen.getByRole('button', { name: 'Registrar pago' })).toBeEnabled())
}

/** Espera a que la lista de ventas pendientes haya llegado: el editor es lo único que prueba que el dato aterrizó. */
async function esperarEditor() {
  return screen.findByTestId('cc-pago-aplicar-editor')
}

beforeEach(() => {
  registrarPagoMock.mockReset()
  registrarPagoMock.mockResolvedValue(COMPROBANTE)
  listarVentasPendientesMock.mockReset()
  listarVentasPendientesMock.mockResolvedValue(VENTAS)
})

afterEach(() => {
  cleanup()
})

describe('ModalPagoACuenta — aplicar a ventas', () => {
  it('sin importes cargados no envía `aplicaciones`', async () => {
    renderizar()
    await esperarEditor()
    await cargarPago('300')

    await userEvent.click(screen.getByRole('button', { name: 'Registrar pago' }))

    await waitFor(() => expect(registrarPagoMock).toHaveBeenCalledTimes(1))
    const [idCliente, cuerpo] = registrarPagoMock.mock.calls[0] as [number, Record<string, unknown>]
    expect(idCliente).toBe(5)
    expect(cuerpo).not.toHaveProperty('aplicaciones')
  })

  it('con importes cargados envía las aplicaciones de las ventas elegidas', async () => {
    renderizar()
    await esperarEditor()
    await cargarPago('300')

    await userEvent.type(screen.getByLabelText('Importe a aplicar a la venta 0001-00000001'), '100')
    await userEvent.type(screen.getByLabelText('Importe a aplicar a la venta 0001-00000002'), '50')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar pago' }))

    await waitFor(() => expect(registrarPagoMock).toHaveBeenCalledTimes(1))
    const cuerpo = registrarPagoMock.mock.calls[0][1] as { aplicaciones: unknown[] }
    expect(cuerpo.aplicaciones).toEqual([
      { idComprobanteVenta: 2, importe: 50 },
      { idComprobanteVenta: 1, importe: 100 },
    ])
  })

  it('«Aplicar a las más viejas» reparte el importe del pago y ese reparto es lo que se envía', async () => {
    renderizar()
    await esperarEditor()
    await cargarPago('250')

    await userEvent.click(screen.getByRole('button', { name: 'Aplicar a las más viejas' }))
    expect(screen.getByTestId('cc-pago-aplicar-aplicado')).toHaveTextContent('$ 250,00')
    expect(screen.getByTestId('cc-pago-aplicar-restante')).toHaveTextContent('$ 0,00')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar pago' }))

    await waitFor(() => expect(registrarPagoMock).toHaveBeenCalledTimes(1))
    const cuerpo = registrarPagoMock.mock.calls[0][1] as { aplicaciones: unknown[] }
    expect(cuerpo.aplicaciones).toEqual([
      { idComprobanteVenta: 2, importe: 150 },
      { idComprobanteVenta: 1, importe: 100 },
    ])
  })

  it('no envía nada si lo aplicado supera el importe del pago', async () => {
    renderizar()
    await esperarEditor()
    await cargarPago('100')

    await userEvent.type(screen.getByLabelText('Importe a aplicar a la venta 0001-00000002'), '150')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar pago' }))

    expect(registrarPagoMock).not.toHaveBeenCalled()
    expect((await screen.findAllByText(/supera el importe disponible/)).length).toBeGreaterThan(0)
  })

  it('si no se pudo cargar la lista de ventas, avisa y el pago se registra igual, sin aplicaciones', async () => {
    listarVentasPendientesMock.mockRejectedValue(new ErrorApi(500, 'error', 'falló'))
    renderizar()

    expect(await screen.findByTestId('cc-pago-pendientes-error')).toHaveTextContent('No se pudieron cargar las ventas pendientes')
    expect(screen.queryByTestId('cc-pago-aplicar-editor')).not.toBeInTheDocument()
    await cargarPago('300')
    await userEvent.click(screen.getByRole('button', { name: 'Registrar pago' }))

    await waitFor(() => expect(registrarPagoMock).toHaveBeenCalledTimes(1))
    expect(registrarPagoMock.mock.calls[0][1]).not.toHaveProperty('aplicaciones')
  })

  it('un cliente sin ventas pendientes no ve la sección de aplicar', async () => {
    listarVentasPendientesMock.mockResolvedValue([])
    renderizar()

    await waitFor(() => expect(listarVentasPendientesMock).toHaveBeenCalledWith(5))
    await cargarPago('300')
    expect(screen.queryByTestId('cc-pago-aplicar-editor')).not.toBeInTheDocument()
    expect(screen.queryByTestId('cc-pago-pendientes-error')).not.toBeInTheDocument()
  })

  it('un rechazo de la aplicación muestra el mensaje del servidor y vuelve a pedir las ventas', async () => {
    registrarPagoMock.mockRejectedValue(
      new ErrorApi(409, 'aplicacion_excede_saldo_de_venta', 'La venta ya no tiene ese saldo pendiente.'),
    )
    renderizar()
    await esperarEditor()
    await cargarPago('300')
    await userEvent.type(screen.getByLabelText('Importe a aplicar a la venta 0001-00000001'), '100')

    await userEvent.click(screen.getByRole('button', { name: 'Registrar pago' }))

    expect(await screen.findByText('La venta ya no tiene ese saldo pendiente.')).toBeInTheDocument()
    await waitFor(() => expect(listarVentasPendientesMock).toHaveBeenCalledTimes(2))
  })

  it('durante la escritura la ventana queda inerte y un doble clic emite una sola solicitud', async () => {
    let resolver: (c: ComprobanteEmitido) => void = () => {}
    registrarPagoMock.mockReturnValue(new Promise<ComprobanteEmitido>((r) => (resolver = r)))
    const onRegistrado = vi.fn()
    renderizar({ onRegistrado })
    await esperarEditor()
    await cargarPago('300')

    const boton = screen.getByRole('button', { name: 'Registrar pago' })
    fireEvent.click(boton)
    fireEvent.click(boton)

    expect(registrarPagoMock).toHaveBeenCalledTimes(1)
    await waitFor(() => expect(screen.getByLabelText('Importe a aplicar a la venta 0001-00000001')).toBeDisabled())
    expect(screen.getByRole('button', { name: 'Aplicar a las más viejas' })).toBeDisabled()

    resolver(COMPROBANTE)
    await waitFor(() => expect(onRegistrado).toHaveBeenCalledWith(COMPROBANTE))
  })
})
