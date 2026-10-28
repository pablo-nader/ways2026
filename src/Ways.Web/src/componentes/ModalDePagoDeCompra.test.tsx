import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ModalDePagoDeCompra } from './ModalDePagoDeCompra'
import { ErrorApi } from '../api/cliente'
import type { MedioPagoListado, ResultadoDePagoDeCompra } from '../api/tipos'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown?])),
    put: vi.fn(),
    delete: vi.fn(),
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

function medio(sobrescribir: Partial<MedioPagoListado>): MedioPagoListado {
  return {
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
    ...sobrescribir,
  }
}

const efectivo = medio({ id: 1, nombre: 'Efectivo' })
const transferencia = medio({ id: 3, nombre: 'Transferencia', comportamiento: 'Electronico', admiteVuelto: false })
const cuentaCorriente = medio({ id: 2, nombre: 'Cuenta corriente', comportamiento: 'CuentaCorriente', admiteVuelto: false })

function resultado(sobrescribir: Partial<ResultadoDePagoDeCompra> = {}): ResultadoDePagoDeCompra {
  return {
    pago: {
      id: 50,
      idTurnoCaja: null,
      idPuntoVenta: 2,
      fecha: '2026-10-03T03:00:00Z',
      categoria: 'Proveedor',
      idProveedor: 1,
      idArea: null,
      concepto: 'Pago Factura A 0001-00000009',
      detalle: null,
      numeroFactura: null,
      importe: 320,
      idEmpleado: 1,
      idComprobanteCompra: 9,
      origenFondos: 'Tesoreria',
      lineas: [{ idItem: 1, idMedioPago: 1, nombreMedioPago: 'Efectivo', importe: 320, referencia: null }],
    },
    pagado: 320,
    saldoPendiente: 0,
    ...sobrescribir,
  }
}

function mockearMedios(medios: MedioPagoListado[] = [efectivo, cuentaCorriente, transferencia]) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/catalogos/medios-pago') return Promise.resolve(medios)
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
}

function montar(props: Partial<Parameters<typeof ModalDePagoDeCompra>[0]> = {}) {
  const onCerrar = vi.fn()
  const onPagado = vi.fn()
  const vista = render(
    <ModalDePagoDeCompra
      idCompra={9}
      etiquetaDeLaCompra="0001-00000009"
      saldoPendiente={320}
      onCerrar={onCerrar}
      onPagado={onPagado}
      {...props}
    />,
  )
  return { ...vista, onCerrar, onPagado }
}

/** Espera al DATO (el medio de pago ya cargado), no al `<select>` que se renderiza antes. */
async function esperarMedios() {
  await screen.findByRole('option', { name: 'Efectivo' })
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  vi.useFakeTimers({ toFake: ['Date'] })
  vi.setSystemTime(new Date(2026, 9, 3, 12, 0, 0))
})

afterEach(() => {
  vi.useRealTimers()
})

describe('ModalDePagoDeCompra', () => {
  it('propone el saldo pendiente, la fecha de hoy y no ofrece la cuenta corriente como medio de pago', async () => {
    mockearMedios()
    montar()
    await esperarMedios()

    expect(screen.getByLabelText('Importe de la línea 1')).toHaveValue('320,00')
    expect(screen.getByLabelText('Fecha')).toHaveValue('03/10/2026')
    expect(screen.getByLabelText('Fecha').closest('.position-relative')!.querySelector('input[type="date"]')).toHaveAttribute('max', '2026-10-03')
    expect(screen.getByRole('option', { name: 'Transferencia' })).toBeInTheDocument()
    expect(screen.queryByRole('option', { name: 'Cuenta corriente' })).not.toBeInTheDocument()
    expect(screen.getByText(/Saldo pendiente de la compra: \$\s320,00/)).toBeInTheDocument()
  })

  it('registra el pago con los datos del formulario y avisa una sola vez', async () => {
    mockearMedios()
    apiPostMock.mockResolvedValue(resultado())
    const usuario = userEvent.setup({ advanceTimers: vi.advanceTimersByTime })
    const { onPagado } = montar()
    await esperarMedios()

    await usuario.selectOptions(screen.getByLabelText('Medio de la línea 1'), 'Transferencia')
    await usuario.type(screen.getByLabelText('Concepto (opcional)'), '  Pago parcial ')
    await usuario.click(screen.getByRole('button', { name: 'Registrar pago' }))

    await waitFor(() => expect(onPagado).toHaveBeenCalledTimes(1))
    expect(apiPostMock).toHaveBeenCalledTimes(1)
    expect(apiPostMock).toHaveBeenCalledWith('/pagos/de-compra/9', {
      fecha: '2026-10-03',
      lineas: [{ idMedioPago: 3, importe: 320, referencia: null }],
      concepto: 'Pago parcial',
    })
    expect(onPagado).toHaveBeenCalledWith(resultado())
  })

  it('paga con varios medios: prellena una línea con el saldo, informa lo que falta y manda una línea por medio', async () => {
    mockearMedios()
    apiPostMock.mockResolvedValue(resultado())
    const usuario = userEvent.setup({ advanceTimers: vi.advanceTimersByTime })
    const { onPagado } = montar()
    await esperarMedios()

    await usuario.selectOptions(screen.getByLabelText('Medio de la línea 1'), 'Efectivo')
    const importeUno = screen.getByLabelText('Importe de la línea 1')
    await usuario.clear(importeUno)
    await usuario.type(importeUno, '200')
    expect(screen.getByText(/Falta \$\s120,00/)).toBeInTheDocument()

    await usuario.click(screen.getByRole('button', { name: 'Agregar medio' }))
    expect(screen.getByLabelText('Importe de la línea 2')).toHaveValue('120,00')
    expect(screen.getByText('Sin diferencia')).toBeInTheDocument()
    await usuario.selectOptions(screen.getByLabelText('Medio de la línea 2'), 'Transferencia')
    await usuario.type(screen.getByLabelText('Referencia de la línea 2'), 'Op 77')
    await usuario.click(screen.getByRole('button', { name: 'Registrar pago' }))

    await waitFor(() => expect(onPagado).toHaveBeenCalledTimes(1))
    expect(apiPostMock).toHaveBeenCalledWith('/pagos/de-compra/9', {
      fecha: '2026-10-03',
      lineas: [
        { idMedioPago: 1, importe: 200, referencia: null },
        { idMedioPago: 3, importe: 120, referencia: 'Op 77' },
      ],
      concepto: null,
    })
  })

  it('un total mayor al saldo, sumando varias líneas, se rechaza sin llamar al servidor', async () => {
    mockearMedios()
    const usuario = userEvent.setup({ advanceTimers: vi.advanceTimersByTime })
    montar()
    await esperarMedios()

    await usuario.selectOptions(screen.getByLabelText('Medio de la línea 1'), 'Efectivo')
    await usuario.click(screen.getByRole('button', { name: 'Agregar medio' }))
    await usuario.selectOptions(screen.getByLabelText('Medio de la línea 2'), 'Transferencia')
    await usuario.type(screen.getByLabelText('Importe de la línea 2'), '10')
    expect(screen.getByText(/Sobra \$\s10,00/)).toBeInTheDocument()
    await usuario.click(screen.getByRole('button', { name: 'Registrar pago' }))

    expect(screen.getByText('El total no puede superar el saldo pendiente de la compra.')).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('un importe mayor al saldo o sin medio de pago se rechaza sin llamar al servidor', async () => {
    mockearMedios()
    const usuario = userEvent.setup({ advanceTimers: vi.advanceTimersByTime })
    montar()
    await esperarMedios()

    await usuario.click(screen.getByRole('button', { name: 'Registrar pago' }))
    expect(screen.getByText('Línea 1: elegí el medio de pago.')).toBeInTheDocument()

    await usuario.selectOptions(screen.getByLabelText('Medio de la línea 1'), 'Efectivo')
    const importe = screen.getByLabelText('Importe de la línea 1')
    await usuario.clear(importe)
    await usuario.type(importe, '320,01')
    await usuario.click(screen.getByRole('button', { name: 'Registrar pago' }))

    expect(screen.getByText('El total no puede superar el saldo pendiente de la compra.')).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('un doble click manda un solo POST y deja toda la ventana inerte mientras el pago está en vuelo', async () => {
    mockearMedios()
    let resolverPago: (valor: ResultadoDePagoDeCompra) => void = () => {}
    apiPostMock.mockImplementation(() => new Promise((resolve) => (resolverPago = resolve)))
    const usuario = userEvent.setup({ advanceTimers: vi.advanceTimersByTime })
    const { onPagado } = montar()
    await esperarMedios()
    await usuario.selectOptions(screen.getByLabelText('Medio de la línea 1'), 'Efectivo')

    const registrar = screen.getByRole('button', { name: 'Registrar pago' })
    await usuario.dblClick(registrar)

    expect(apiPostMock).toHaveBeenCalledTimes(1)
    expect(screen.getByRole('button', { name: 'Registrando…' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(screen.getByLabelText('Importe de la línea 1')).toBeDisabled()
    expect(screen.getByLabelText('Fecha')).toBeDisabled()
    expect(screen.getByLabelText('Medio de la línea 1')).toBeDisabled()
    expect(screen.getByLabelText('Concepto (opcional)')).toBeDisabled()

    await act(async () => resolverPago(resultado()))
    expect(onPagado).toHaveBeenCalledTimes(1)
  })

  it('dos clicks en el mismo tick, antes de que React deshabilite el botón, mandan un solo POST', async () => {
    mockearMedios()
    apiPostMock.mockImplementation(() => new Promise(() => {}))
    const usuario = userEvent.setup({ advanceTimers: vi.advanceTimersByTime })
    montar()
    await esperarMedios()
    await usuario.selectOptions(screen.getByLabelText('Medio de la línea 1'), 'Efectivo')

    const registrar = screen.getByRole('button', { name: 'Registrar pago' })
    act(() => {
      fireEvent.click(registrar)
      fireEvent.click(registrar)
    })

    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })

  it('un error del servidor se muestra tal cual, deja reintentar y no avisa un pago', async () => {
    mockearMedios()
    apiPostMock.mockRejectedValueOnce(
      new ErrorApi(409, 'pago_excede_saldo_pendiente', 'El importe del pago supera el saldo pendiente de la compra.'),
    )
    const usuario = userEvent.setup({ advanceTimers: vi.advanceTimersByTime })
    const { onPagado } = montar()
    await esperarMedios()
    await usuario.selectOptions(screen.getByLabelText('Medio de la línea 1'), 'Efectivo')

    await usuario.click(screen.getByRole('button', { name: 'Registrar pago' }))

    expect(await screen.findByText('El importe del pago supera el saldo pendiente de la compra.')).toBeInTheDocument()
    expect(onPagado).not.toHaveBeenCalled()
    expect(screen.getByRole('button', { name: 'Registrar pago' })).toBeEnabled()
    expect(screen.getByLabelText('Importe de la línea 1')).toBeEnabled()
  })

  it('no permite registrar hasta que llegan los medios de pago', async () => {
    let resolverMedios: (valor: MedioPagoListado[]) => void = () => {}
    apiGetMock.mockImplementation(() => new Promise((resolve) => (resolverMedios = resolve)))
    montar()

    expect(screen.getByRole('button', { name: 'Registrar pago' })).toBeDisabled()

    await act(async () => resolverMedios([efectivo]))
    await esperarMedios()
    expect(screen.getByRole('button', { name: 'Registrar pago' })).toBeEnabled()
  })

  it('si los medios no cargan lo dice y no ofrece ninguno', async () => {
    apiGetMock.mockRejectedValue(new ErrorApi(500, 'error_interno', 'Falló la carga de medios.'))
    montar()

    expect(await screen.findByText('Falló la carga de medios.')).toBeInTheDocument()
    expect(screen.getAllByRole('option')).toHaveLength(1)
  })

  it('un pago que resuelve con el modal ya desmontado no avisa', async () => {
    mockearMedios()
    let resolverPago: (valor: ResultadoDePagoDeCompra) => void = () => {}
    apiPostMock.mockImplementation(() => new Promise((resolve) => (resolverPago = resolve)))
    const usuario = userEvent.setup({ advanceTimers: vi.advanceTimersByTime })
    const { onPagado, unmount } = montar()
    await esperarMedios()
    await usuario.selectOptions(screen.getByLabelText('Medio de la línea 1'), 'Efectivo')
    await usuario.click(screen.getByRole('button', { name: 'Registrar pago' }))

    unmount()
    await act(async () => resolverPago(resultado()))

    expect(onPagado).not.toHaveBeenCalled()
  })
})
