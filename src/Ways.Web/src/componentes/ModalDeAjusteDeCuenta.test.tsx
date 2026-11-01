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
    <ModalDeAjusteDeCuenta cuenta={cuenta} onCerrar={onCerrar} onAjustado={onAjustado} onIncierto={onIncierto} />,
  )

  return { onCerrar, onAjustado, onIncierto, ...vista }
}

beforeEach(() => {
  apiPostMock.mockReset()
  apiGetMock.mockReset()
})

describe('ModalDeAjusteDeCuenta', () => {
  it('ofrece solo el importe con signo, muestra el saldo recibido y no lee nada del servidor', () => {
    montar()

    expect(screen.queryByRole('radio')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Saldo real de la plataforma')).not.toBeInTheDocument()
    expect(screen.getByText(/Saldo actual de la cuenta/)).toHaveTextContent('$ 1.000,00')
    expect(screen.queryByText(/Saldo resultante/)).not.toBeInTheDocument()
    expect(apiGetMock).not.toHaveBeenCalled()
  })

  it('muestra el saldo resultante como vista previa y acepta un importe negativo', async () => {
    const usuario = userEvent.setup()
    montar()

    await usuario.type(screen.getByLabelText(/Importe/), '-300,5')

    expect(screen.getByText(/Saldo resultante \(estimación\)/)).toHaveTextContent('$ 699,50')
  })

  it('sin concepto no llama al servidor y marca el campo', async () => {
    const usuario = userEvent.setup()
    montar()

    await usuario.type(screen.getByLabelText(/Importe/), '250')
    await usuario.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    expect(screen.getByText('Ingresá el concepto del ajuste.')).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('un importe que el saldo recibido dejaría fuera de rango tampoco se rechaza localmente', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(400, 'saldo_resultante_fuera_de_rango', 'Fuera de rango'))
    const usuario = userEvent.setup()
    montar()

    await usuario.type(screen.getByLabelText(/Importe/), '999999999999')
    await usuario.type(screen.getByLabelText('Concepto'), 'Desborde')
    await usuario.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    expect(await screen.findByText('El saldo resultante quedaría fuera del rango permitido.')).toBeInTheDocument()
    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })

  it('manda SOLO importe con su signo y el concepto recortado, y avisa una vez al llamador', async () => {
    apiPostMock.mockResolvedValue(resultado(940))
    const usuario = userEvent.setup()
    const { onAjustado } = montar()

    await usuario.type(screen.getByLabelText(/Importe/), '-60')
    await usuario.type(screen.getByLabelText('Concepto'), '  Retiro no registrado  ')
    await usuario.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    await waitFor(() => expect(onAjustado).toHaveBeenCalledTimes(1))
    expect(apiPostMock).toHaveBeenCalledTimes(1)
    expect(apiPostMock).toHaveBeenCalledWith('/cuentas-tesoreria/7/ajustes', { concepto: 'Retiro no registrado', importe: -60 })
    expect(onAjustado).toHaveBeenCalledWith(resultado(940))
  })

  it('un doble clic en el mismo tick emite una sola escritura', async () => {
    const pendiente = diferido<ResultadoDeAjusteDeCuenta>()
    apiPostMock.mockReturnValue(pendiente.promesa)
    const usuario = userEvent.setup()
    const { onAjustado } = montar()

    await usuario.type(screen.getByLabelText(/Importe/), '250')
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

  it('mientras escribe toda la ventana queda deshabilitada: campos, cancelar y registrar', async () => {
    const pendiente = diferido<ResultadoDeAjusteDeCuenta>()
    apiPostMock.mockReturnValue(pendiente.promesa)
    const usuario = userEvent.setup()
    montar()

    await usuario.type(screen.getByLabelText(/Importe/), '250')
    await usuario.type(screen.getByLabelText('Concepto'), 'Conciliación')
    await usuario.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    expect(screen.getByRole('button', { name: 'Registrando…' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(screen.getByLabelText('Concepto')).toBeDisabled()
    expect(screen.getByLabelText(/Importe/)).toBeDisabled()

    await act(async () => {
      pendiente.resolver(resultado())
    })
  })

  it('un rechazo cierto deja el modal abierto con el error en su campo y vuelve a habilitar', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(404, 'cuenta_no_encontrada', 'No existe.'))
    const usuario = userEvent.setup()
    const { onAjustado, onIncierto } = montar()

    await usuario.type(screen.getByLabelText(/Importe/), '250')
    await usuario.type(screen.getByLabelText('Concepto'), 'Conciliación')
    await usuario.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    expect(await screen.findByText('La cuenta ya no existe. Actualizá la pantalla.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Registrar ajuste' })).toBeEnabled()
    expect(screen.getByLabelText('Concepto')).toBeEnabled()
    expect(onAjustado).not.toHaveBeenCalled()
    expect(onIncierto).not.toHaveBeenCalled()
  })

  it('el código del servidor elige el campo: concepto_requerido va al concepto', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(400, 'concepto_requerido', 'El campo concepto es obligatorio.'))
    const usuario = userEvent.setup()
    montar()

    await usuario.type(screen.getByLabelText(/Importe/), '250')
    await usuario.type(screen.getByLabelText('Concepto'), 'x')
    await usuario.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    expect(await screen.findByText('Ingresá el concepto del ajuste.')).toBeInTheDocument()
  })

  it('un resultado incierto avisa al llamador una sola vez y nunca reintenta solo', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(503, 'resultado_incierto', 'No se pudo confirmar.'))
    const usuario = userEvent.setup()
    const { onAjustado, onIncierto } = montar()

    await usuario.type(screen.getByLabelText(/Importe/), '250')
    await usuario.type(screen.getByLabelText('Concepto'), 'Conciliación')
    await usuario.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    await waitFor(() => expect(onIncierto).toHaveBeenCalledTimes(1))
    expect(apiPostMock).toHaveBeenCalledTimes(1)
    expect(onAjustado).not.toHaveBeenCalled()
  })

  it('una respuesta que llega con el modal ya desmontado no avisa a nadie', async () => {
    const pendiente = diferido<ResultadoDeAjusteDeCuenta>()
    apiPostMock.mockReturnValue(pendiente.promesa)
    const usuario = userEvent.setup()
    const { onAjustado, onIncierto, unmount } = montar()

    await usuario.type(screen.getByLabelText(/Importe/), '250')
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
