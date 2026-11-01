import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ModalDeAjusteDeCuenta } from './ModalDeAjusteDeCuenta'
import { ErrorApi } from '../api/cliente'
import type { ResultadoDeAjusteDeCuenta } from '../api/cuentasTesoreria'

const apiPostMock = vi.fn()
const apiGetMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown])),
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

afterEach(cleanup)

const cuenta = { id: 7, nombre: 'Mercado Pago', saldoActual: 1000 }

function resultado(saldoActual = 1250): ResultadoDeAjusteDeCuenta {
  return {
    saldoActual,
    movimiento: {
      id: 99,
      fecha: '2026-10-12T10:00:00Z',
      tipo: 'Ajuste',
      concepto: 'Conciliación',
      ingreso: 250,
      egreso: 0,
      final: saldoActual,
      idTurnoCaja: null,
      idMedioPago: null,
      nombreMedioPago: null,
      idEmpleado: 1,
      nombreEmpleado: 'admin',
      baseBruta: null,
      porcentajeAplicado: null,
    },
  }
}

function cuentasConSaldo(saldoActual: number) {
  return {
    idEmpresa: 1,
    mediosSinCuenta: [],
    cuentas: [{ id: 7, nombre: 'Mercado Pago', activo: true, saldoInicial: 0, saldoActual, fechaUltimoMovimiento: null }],
  }
}

function diferido<T>() {
  let resolver!: (valor: T) => void
  let rechazar!: (motivo: unknown) => void
  const promesa = new Promise<T>((res, rej) => {
    resolver = res
    rechazar = rej
  })

  return { promesa, resolver, rechazar }
}

function montar() {
  const onCerrar = vi.fn()
  const onAjustado = vi.fn()
  const onIncierto = vi.fn()
  const vista = render(
    <ModalDeAjusteDeCuenta idEmpresa={1} cuenta={cuenta} onCerrar={onCerrar} onAjustado={onAjustado} onIncierto={onIncierto} />,
  )

  return { onCerrar, onAjustado, onIncierto, ...vista }
}

beforeEach(() => {
  apiPostMock.mockReset()
  apiGetMock.mockReset()
  apiGetMock.mockResolvedValue(cuentasConSaldo(cuenta.saldoActual))
})

describe('ModalDeAjusteDeCuenta', () => {
  it('arranca en "Tengo el saldo real", con el saldo actual a la vista y sin vista previa', () => {
    montar()

    expect(screen.getByRole('radio', { name: 'Tengo el saldo real' })).toBeChecked()
    expect(screen.getByRole('radio', { name: 'Sumar o restar un importe' })).not.toBeChecked()
    expect(screen.getByText(/Saldo actual de la cuenta/)).toHaveTextContent('$ 1.000,00')
    expect(screen.queryByText(/Diferencia \(estimación\)/)).not.toBeInTheDocument()
  })

  it('el saldo real muestra la diferencia como vista previa que el servidor recalcula', async () => {
    const usuario = userEvent.setup()
    montar()

    await usuario.type(screen.getByLabelText('Saldo real de la plataforma'), '1250')

    expect(screen.getByText(/Diferencia \(estimación\)/)).toHaveTextContent('+$ 250,00')
    expect(screen.getByText(/el sistema recalcula la diferencia al guardar/)).toBeInTheDocument()
  })

  it('el modo importe muestra el saldo resultante como vista previa y acepta un importe negativo', async () => {
    const usuario = userEvent.setup()
    montar()

    await usuario.click(screen.getByRole('radio', { name: 'Sumar o restar un importe' }))
    await usuario.type(screen.getByLabelText(/Importe/), '-300,5')

    expect(screen.getByText(/Saldo resultante \(estimación\)/)).toHaveTextContent('$ 699,50')
    expect(screen.queryByLabelText('Saldo real de la plataforma')).not.toBeInTheDocument()
  })

  it('sin concepto no llama al servidor y marca el campo', async () => {
    const usuario = userEvent.setup()
    montar()

    await usuario.type(screen.getByLabelText('Saldo real de la plataforma'), '1250')
    await usuario.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    expect(screen.getByText('Ingresá el concepto del ajuste.')).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('un saldo real igual al que mostraba la lista SÍ se envía: el servidor decide con el saldo vigente', async () => {
    apiGetMock.mockResolvedValue(cuentasConSaldo(1200))
    apiPostMock.mockResolvedValue(resultado(1000))
    const usuario = userEvent.setup()
    const { onAjustado } = montar()
    await waitFor(() => expect(screen.getByText(/Saldo actual de la cuenta/)).toHaveTextContent('$ 1.200,00'))

    await usuario.type(screen.getByLabelText('Saldo real de la plataforma'), '1000')
    await usuario.type(screen.getByLabelText('Concepto'), 'Conciliación')
    await usuario.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    await waitFor(() => expect(onAjustado).toHaveBeenCalledTimes(1))
    expect(apiPostMock).toHaveBeenCalledWith('/cuentas-tesoreria/7/ajustes', { concepto: 'Conciliación', saldoObjetivo: 1000 })
  })

  it('un importe que el saldo cacheado dejaría fuera de rango tampoco se rechaza localmente', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(400, 'saldo_resultante_fuera_de_rango', 'Fuera de rango'))
    const usuario = userEvent.setup()
    montar()

    await usuario.click(screen.getByRole('radio', { name: 'Sumar o restar un importe' }))
    await usuario.type(screen.getByLabelText(/Importe/), '999999999999')
    await usuario.type(screen.getByLabelText('Concepto'), 'Desborde')
    await usuario.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    expect(await screen.findByText('El saldo resultante quedaría fuera del rango permitido.')).toBeInTheDocument()
    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })

  it('al abrir relee el saldo de la cuenta y la vista previa se actualiza cuando llega', async () => {
    const fresco = diferido<ReturnType<typeof cuentasConSaldo>>()
    apiGetMock.mockReturnValue(fresco.promesa)
    const usuario = userEvent.setup()
    montar()

    expect(apiGetMock).toHaveBeenCalledWith('/reportes/tesoreria/cuentas?idEmpresa=1')
    await usuario.type(screen.getByLabelText('Saldo real de la plataforma'), '1250')
    expect(screen.getByText(/Diferencia \(estimación\)/)).toHaveTextContent('+$ 250,00')

    await act(async () => {
      fresco.resolver(cuentasConSaldo(1100))
    })

    expect(screen.getByText(/Diferencia \(estimación\)/)).toHaveTextContent('+$ 150,00')
    expect(screen.getByText(/Saldo actual de la cuenta/)).toHaveTextContent('$ 1.100,00')
  })

  it('si la relectura del saldo falla la vista previa sigue con el saldo conocido', async () => {
    apiGetMock.mockRejectedValue(new ErrorApi(500, 'x', 'falla'))
    const usuario = userEvent.setup()
    montar()

    await usuario.type(screen.getByLabelText('Saldo real de la plataforma'), '1250')

    expect(screen.getByText(/Diferencia \(estimación\)/)).toHaveTextContent('+$ 250,00')
    expect(screen.getByText(/posiblemente desactualizado/)).toBeInTheDocument()
    expect(screen.getByText(/el sistema recalcula la diferencia al guardar/)).toBeInTheDocument()
  })

  it('con la relectura exitosa el saldo no se marca como desactualizado', async () => {
    montar()

    await waitFor(() => expect(apiGetMock).toHaveBeenCalledTimes(1))
    expect(screen.queryByText(/posiblemente desactualizado/)).not.toBeInTheDocument()
  })

  it('por saldo real manda SOLO saldoObjetivo y el concepto recortado, y avisa una vez al llamador', async () => {
    apiPostMock.mockResolvedValue(resultado())
    const usuario = userEvent.setup()
    const { onAjustado } = montar()

    await usuario.type(screen.getByLabelText('Saldo real de la plataforma'), '1250')
    await usuario.type(screen.getByLabelText('Concepto'), '  Conciliación  ')
    await usuario.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    await waitFor(() => expect(onAjustado).toHaveBeenCalledTimes(1))
    expect(apiPostMock).toHaveBeenCalledTimes(1)
    expect(apiPostMock).toHaveBeenCalledWith('/cuentas-tesoreria/7/ajustes', { concepto: 'Conciliación', saldoObjetivo: 1250 })
    expect(onAjustado).toHaveBeenCalledWith(resultado())
  })

  it('por importe manda SOLO importe, con su signo', async () => {
    apiPostMock.mockResolvedValue(resultado(940))
    const usuario = userEvent.setup()
    const { onAjustado } = montar()

    await usuario.click(screen.getByRole('radio', { name: 'Sumar o restar un importe' }))
    await usuario.type(screen.getByLabelText(/Importe/), '-60')
    await usuario.type(screen.getByLabelText('Concepto'), 'Retiro no registrado')
    await usuario.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    await waitFor(() => expect(onAjustado).toHaveBeenCalledTimes(1))
    expect(apiPostMock).toHaveBeenCalledWith('/cuentas-tesoreria/7/ajustes', { concepto: 'Retiro no registrado', importe: -60 })
  })

  it('un doble clic en el mismo tick emite una sola escritura', async () => {
    const pendiente = diferido<ResultadoDeAjusteDeCuenta>()
    apiPostMock.mockReturnValue(pendiente.promesa)
    const usuario = userEvent.setup()
    const { onAjustado } = montar()

    await usuario.type(screen.getByLabelText('Saldo real de la plataforma'), '1250')
    await usuario.type(screen.getByLabelText('Concepto'), 'Conciliación')
    const boton = screen.getByRole('button', { name: 'Registrar ajuste' })

    act(() => {
      fireEvent.click(boton)
      fireEvent.click(boton)
    })

    expect(apiPostMock).toHaveBeenCalledTimes(1)

    await act(async () => {
      pendiente.resolver(resultado())
    })
    await waitFor(() => expect(onAjustado).toHaveBeenCalledTimes(1))
  })

  it('mientras escribe toda la ventana queda deshabilitada: campos, modos, cancelar y registrar', async () => {
    const pendiente = diferido<ResultadoDeAjusteDeCuenta>()
    apiPostMock.mockReturnValue(pendiente.promesa)
    const usuario = userEvent.setup()
    montar()

    await usuario.type(screen.getByLabelText('Saldo real de la plataforma'), '1250')
    await usuario.type(screen.getByLabelText('Concepto'), 'Conciliación')
    await usuario.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    expect(screen.getByRole('button', { name: 'Registrando…' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(screen.getByLabelText('Concepto')).toBeDisabled()
    expect(screen.getByLabelText('Saldo real de la plataforma')).toBeDisabled()
    expect(screen.getByRole('radio', { name: 'Sumar o restar un importe' })).toBeDisabled()

    await act(async () => {
      pendiente.resolver(resultado())
    })
  })

  it('un rechazo cierto deja el modal abierto con el error en su campo y vuelve a habilitar', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'ajuste_sin_diferencia', 'El saldo ya coincide.'))
    const usuario = userEvent.setup()
    const { onAjustado, onIncierto } = montar()

    await usuario.type(screen.getByLabelText('Saldo real de la plataforma'), '1250')
    await usuario.type(screen.getByLabelText('Concepto'), 'Conciliación')
    await usuario.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    expect(await screen.findByText(/ya coincide con el saldo informado/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Registrar ajuste' })).toBeEnabled()
    expect(screen.getByLabelText('Concepto')).toBeEnabled()
    expect(onAjustado).not.toHaveBeenCalled()
    expect(onIncierto).not.toHaveBeenCalled()
  })

  it('el código del servidor elige el campo: concepto_requerido va al concepto', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(400, 'concepto_requerido', 'El campo concepto es obligatorio.'))
    const usuario = userEvent.setup()
    montar()

    await usuario.type(screen.getByLabelText('Saldo real de la plataforma'), '1250')
    await usuario.type(screen.getByLabelText('Concepto'), 'x')
    await usuario.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    expect(await screen.findByText('Ingresá el concepto del ajuste.')).toBeInTheDocument()
  })

  it('un resultado incierto avisa al llamador una sola vez y nunca reintenta solo', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(503, 'resultado_incierto', 'No se pudo confirmar.'))
    const usuario = userEvent.setup()
    const { onAjustado, onIncierto } = montar()

    await usuario.type(screen.getByLabelText('Saldo real de la plataforma'), '1250')
    await usuario.type(screen.getByLabelText('Concepto'), 'Conciliación')
    await usuario.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    await waitFor(() => expect(onIncierto).toHaveBeenCalledTimes(1))
    expect(apiPostMock).toHaveBeenCalledTimes(1)
    expect(onAjustado).not.toHaveBeenCalled()
  })

  it('el modo de la pantalla de cuentas ofrece los dos modos y relee el saldo de la cadena', async () => {
    montar()

    expect(screen.getByRole('radio', { name: 'Tengo el saldo real' })).toBeInTheDocument()
    expect(screen.getByRole('radio', { name: 'Sumar o restar un importe' })).toBeInTheDocument()
    await waitFor(() => expect(apiGetMock).toHaveBeenCalledWith('/reportes/tesoreria/cuentas?idEmpresa=1'))
  })

  it('con soloImporte ofrece solo el importe con signo, muestra el saldo recibido y no relee la cadena', async () => {
    apiGetMock.mockResolvedValue(cuentasConSaldo(5))
    apiPostMock.mockResolvedValue(resultado(940))
    const usuario = userEvent.setup()
    const onAjustado = vi.fn()
    render(
      <ModalDeAjusteDeCuenta idEmpresa={1} cuenta={cuenta} soloImporte onCerrar={vi.fn()} onAjustado={onAjustado} onIncierto={vi.fn()} />,
    )

    expect(screen.queryByRole('radio')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Saldo real de la plataforma')).not.toBeInTheDocument()
    expect(screen.getByText(/Saldo actual de la cuenta/)).toHaveTextContent('$ 1.000,00')

    await usuario.type(screen.getByLabelText(/Importe/), '-60')
    await usuario.type(screen.getByLabelText('Concepto'), 'Retiro no registrado')
    await usuario.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    await waitFor(() => expect(onAjustado).toHaveBeenCalledTimes(1))
    expect(apiPostMock).toHaveBeenCalledWith('/cuentas-tesoreria/7/ajustes', { concepto: 'Retiro no registrado', importe: -60 })
    expect(apiGetMock).not.toHaveBeenCalled()
    expect(screen.getByText(/Saldo actual de la cuenta/)).toHaveTextContent('$ 1.000,00')
  })

  it('una respuesta que llega con el modal ya desmontado no avisa a nadie', async () => {
    const pendiente = diferido<ResultadoDeAjusteDeCuenta>()
    apiPostMock.mockReturnValue(pendiente.promesa)
    const usuario = userEvent.setup()
    const { onAjustado, onIncierto, unmount } = montar()

    await usuario.type(screen.getByLabelText('Saldo real de la plataforma'), '1250')
    await usuario.type(screen.getByLabelText('Concepto'), 'Conciliación')
    await usuario.click(screen.getByRole('button', { name: 'Registrar ajuste' }))
    unmount()

    await act(async () => {
      pendiente.resolver(resultado())
    })

    expect(onAjustado).not.toHaveBeenCalled()
    expect(onIncierto).not.toHaveBeenCalled()
  })
})
