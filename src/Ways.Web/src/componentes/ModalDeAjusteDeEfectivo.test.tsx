import { act, cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ModalDeAjusteDeEfectivo } from './ModalDeAjusteDeEfectivo'
import { ErrorApi } from '../api/cliente'

const apiPostMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: vi.fn(),
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

const resultado = { idMovimiento: 5, fecha: '2026-10-31T10:00:00Z', concepto: 'Conteo', importe: -500 }

function diferido<T>() {
  let resolver!: (valor: T) => void
  const promesa = new Promise<T>((res) => {
    resolver = res
  })
  return { promesa, resolver }
}

function montar() {
  const onCerrar = vi.fn()
  const onAjustado = vi.fn()
  const onIncierto = vi.fn()
  const vista = render(<ModalDeAjusteDeEfectivo idEmpresa={3} onCerrar={onCerrar} onAjustado={onAjustado} onIncierto={onIncierto} />)
  return { onCerrar, onAjustado, onIncierto, ...vista }
}

async function completar(usuario: ReturnType<typeof userEvent.setup>, importe: string, concepto: string) {
  if (importe) await usuario.type(screen.getByLabelText(/Importe/), importe)
  if (concepto) await usuario.type(screen.getByLabelText('Concepto'), concepto)
}

beforeEach(() => {
  apiPostMock.mockReset()
})

describe('ModalDeAjusteDeEfectivo', () => {
  it('envía importe con signo y concepto recortado a /tesoreria/efectivo/ajustes y avisa una sola vez', async () => {
    apiPostMock.mockResolvedValue(resultado)
    const usuario = userEvent.setup()
    const { onAjustado } = montar()

    await completar(usuario, '-500', '  Conteo  ')
    await usuario.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    await waitFor(() => expect(onAjustado).toHaveBeenCalledTimes(1))
    expect(onAjustado).toHaveBeenCalledWith(resultado)
    expect(apiPostMock).toHaveBeenCalledWith('/tesoreria/efectivo/ajustes', { idEmpresa: 3, concepto: 'Conteo', importe: -500 })
  })

  it('sin concepto o con importe cero no llama al servidor y marca el campo', async () => {
    const usuario = userEvent.setup()
    montar()

    await completar(usuario, '100', '')
    await usuario.click(screen.getByRole('button', { name: 'Registrar ajuste' }))
    expect(screen.getByText('Ingresá el concepto del ajuste.')).toBeInTheDocument()

    await usuario.type(screen.getByLabelText('Concepto'), 'x')
    await usuario.clear(screen.getByLabelText(/Importe/))
    await usuario.click(screen.getByRole('button', { name: 'Registrar ajuste' }))
    expect(screen.getByText(/Ingresá un importe distinto de cero/)).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('mientras escribe deja la ventana inerte y un segundo clic no manda otro POST', async () => {
    const pendiente = diferido<typeof resultado>()
    apiPostMock.mockReturnValue(pendiente.promesa)
    const usuario = userEvent.setup()
    const { onAjustado } = montar()

    await completar(usuario, '100', 'Conteo')
    await usuario.click(screen.getByRole('button', { name: 'Registrar ajuste' }))
    const enCurso = screen.getByRole('button', { name: 'Registrando…' })
    expect(enCurso).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(screen.getByLabelText('Concepto')).toBeDisabled()
    await usuario.click(enCurso)
    expect(apiPostMock).toHaveBeenCalledTimes(1)

    await act(async () => {
      pendiente.resolver(resultado)
    })
    expect(onAjustado).toHaveBeenCalledTimes(1)
  })

  it.each([
    ['concepto_requerido', 'Ingresá el concepto del ajuste.'],
    ['concepto_muy_largo', 'El concepto no puede superar los 200 caracteres.'],
    ['importe_invalido', 'Ingresá un importe distinto de cero, con hasta dos decimales.'],
  ])('un rechazo %s muestra su mensaje y deja reintentar', async (codigo, mensaje) => {
    apiPostMock.mockRejectedValueOnce(new ErrorApi(400, codigo, 'del servidor'))
    const usuario = userEvent.setup()
    const { onAjustado, onIncierto } = montar()

    await completar(usuario, '100', 'Conteo')
    await usuario.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    expect(await screen.findByText(mensaje)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Registrar ajuste' })).toBeEnabled()
    expect(onAjustado).not.toHaveBeenCalled()
    expect(onIncierto).not.toHaveBeenCalled()
  })

  it('un 5xx es un resultado incierto: avisa al llamador y no deja reenviar', async () => {
    apiPostMock.mockRejectedValueOnce(new ErrorApi(503, 'resultado_incierto', 'no se sabe'))
    const usuario = userEvent.setup()
    const { onIncierto, onAjustado } = montar()

    await completar(usuario, '100', 'Conteo')
    await usuario.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    await waitFor(() => expect(onIncierto).toHaveBeenCalledTimes(1))
    expect(onAjustado).not.toHaveBeenCalled()
    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })

  it('un 403 o un 404 se explican en el formulario', async () => {
    apiPostMock.mockRejectedValueOnce(new ErrorApi(403, 'prohibido', 'x'))
    const usuario = userEvent.setup()
    montar()

    await completar(usuario, '100', 'Conteo')
    await usuario.click(screen.getByRole('button', { name: 'Registrar ajuste' }))
    expect(await screen.findByText('Solo un administrador puede ajustar el efectivo.')).toBeInTheDocument()

    apiPostMock.mockRejectedValueOnce(new ErrorApi(404, 'no_encontrado', 'x'))
    await usuario.click(screen.getByRole('button', { name: 'Registrar ajuste' }))
    expect(await screen.findByText('La empresa ya no existe. Actualizá la pantalla.')).toBeInTheDocument()
  })
})
