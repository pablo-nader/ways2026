import { act, cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ModalDeConciliacion } from './ModalDeConciliacion'
import { ErrorApi } from '../api/cliente'
import { valorLocalDeAhora } from '../api/tesoreria'

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

const resultado = {
  idConciliacion: 1,
  idEmpresa: 3,
  hasta: '2026-11-01T13:00:00Z',
  nota: null,
  creadoEl: '2026-11-01T13:01:00Z',
  idUsuario: 9,
}

function diferido<T>() {
  let resolver!: (valor: T) => void
  const promesa = new Promise<T>((res) => {
    resolver = res
  })
  return { promesa, resolver }
}

function montar(conciliadoHasta: string | null = null) {
  const onCerrar = vi.fn()
  const onConciliado = vi.fn()
  const onIncierto = vi.fn()
  const vista = render(
    <ModalDeConciliacion
      idEmpresa={3}
      conciliadoHasta={conciliadoHasta}
      onCerrar={onCerrar}
      onConciliado={onConciliado}
      onIncierto={onIncierto}
    />,
  )
  return { onCerrar, onConciliado, onIncierto, ...vista }
}

const botonConciliar = () => screen.getByRole('button', { name: /^Conciliar$|^Conciliando/ })

async function confirmar(usuario: ReturnType<typeof userEvent.setup>) {
  await usuario.click(screen.getByLabelText('Entiendo que no se puede deshacer'))
}

/** `datetime-local` y los textos largos no se tipean bien con user-event: se fija el valor con el evento nativo. */
function fijarValor(elemento: HTMLElement, valor: string) {
  const prototipo = elemento instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype
  const asignar = Object.getOwnPropertyDescriptor(prototipo, 'value')?.set
  act(() => {
    asignar?.call(elemento, valor)
    elemento.dispatchEvent(new Event('input', { bubbles: true }))
  })
}

beforeEach(() => {
  apiPostMock.mockReset()
})

describe('ModalDeConciliacion', () => {
  it('abre con la fecha y hora de ahora redondeada al minuto y la advertencia de que no se deshace', () => {
    montar()

    expect(screen.getByLabelText('Conciliar hasta')).toHaveValue(valorLocalDeAhora(new Date()))
    expect(screen.getByText(/queda congelado y no se puede modificar/)).toBeInTheDocument()
    expect(screen.getByText(/Las correcciones solo se hacen con ajustes/)).toBeInTheDocument()
    expect(screen.getByText(/La conciliación no se puede deshacer\./)).toBeInTheDocument()
  })

  it('el botón queda deshabilitado hasta marcar la confirmación', async () => {
    const usuario = userEvent.setup()
    montar()

    expect(botonConciliar()).toBeDisabled()
    await confirmar(usuario)
    expect(botonConciliar()).toBeEnabled()
    await confirmar(usuario)
    expect(botonConciliar()).toBeDisabled()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('cuenta los caracteres de la nota', async () => {
    const usuario = userEvent.setup()
    montar()

    expect(screen.getByText('0/500')).toBeInTheDocument()
    await usuario.type(screen.getByLabelText(/Nota/), 'Revisado')
    expect(screen.getByText('8/500')).toBeInTheDocument()
  })

  it('manda empresa, hasta con offset local y la nota recortada, y avisa una sola vez', async () => {
    apiPostMock.mockResolvedValue(resultado)
    const usuario = userEvent.setup()
    const { onConciliado } = montar()

    fijarValor(screen.getByLabelText('Conciliar hasta'), '2026-10-31T18:30')
    await usuario.type(screen.getByLabelText(/Nota/), '  Revisado  ')
    await confirmar(usuario)
    await usuario.click(botonConciliar())

    await waitFor(() => expect(onConciliado).toHaveBeenCalledTimes(1))
    expect(onConciliado).toHaveBeenCalledWith(resultado)
    expect(apiPostMock).toHaveBeenCalledTimes(1)
    const [ruta, cuerpo] = apiPostMock.mock.calls[0] as [string, { idEmpresa: number; hasta: string; nota?: string }]
    expect(ruta).toBe('/tesoreria/conciliaciones')
    expect(cuerpo.idEmpresa).toBe(3)
    expect(cuerpo.nota).toBe('Revisado')
    expect(cuerpo.hasta).toMatch(/^2026-10-31T18:30:00[+-]\d{2}:\d{2}$/)
    expect(Date.parse(cuerpo.hasta)).toBe(new Date(2026, 9, 31, 18, 30).getTime())
  })

  it('sin nota no manda el campo', async () => {
    apiPostMock.mockResolvedValue(resultado)
    const usuario = userEvent.setup()
    montar()

    await confirmar(usuario)
    await usuario.click(botonConciliar())

    await waitFor(() => expect(apiPostMock).toHaveBeenCalledTimes(1))
    expect(apiPostMock.mock.calls[0][1]).not.toHaveProperty('nota')
  })

  it('con la fecha vacía no llama al servidor y marca el campo', async () => {
    const usuario = userEvent.setup()
    montar()

    fijarValor(screen.getByLabelText('Conciliar hasta'), '')
    await confirmar(usuario)
    await usuario.click(botonConciliar())

    expect(screen.getByText(/Ingresá la fecha y la hora/)).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('con una nota de más de 500 caracteres no llama al servidor', async () => {
    const usuario = userEvent.setup()
    montar()

    fijarValor(screen.getByLabelText(/Nota/), 'x'.repeat(501))
    await confirmar(usuario)
    await usuario.click(botonConciliar())

    expect(screen.getByText('La nota no puede superar los 500 caracteres.')).toBeInTheDocument()
    expect(apiPostMock).not.toHaveBeenCalled()
  })

  it('mientras escribe deja la ventana inerte y un segundo clic no manda otro POST', async () => {
    const pendiente = diferido<typeof resultado>()
    apiPostMock.mockReturnValue(pendiente.promesa)
    const usuario = userEvent.setup()
    const { onConciliado, onCerrar } = montar()

    await confirmar(usuario)
    await usuario.dblClick(botonConciliar())

    expect(apiPostMock).toHaveBeenCalledTimes(1)
    expect(botonConciliar()).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancelar' })).toBeDisabled()
    expect(screen.getByLabelText('Conciliar hasta')).toBeDisabled()
    expect(screen.getByLabelText(/Nota/)).toBeDisabled()
    expect(screen.getByLabelText('Entiendo que no se puede deshacer')).toBeDisabled()
    await usuario.keyboard('{Escape}')
    expect(onCerrar).not.toHaveBeenCalled()

    await act(async () => {
      pendiente.resolver(resultado)
    })
    expect(onConciliado).toHaveBeenCalledTimes(1)
  })

  it.each([
    ['conciliacion_futura', 409, 'La fecha no puede ser futura.'],
    ['conciliacion_con_turno_abierto', 409, 'Hay una caja abierta desde antes de esa fecha; cerrala primero.'],
    ['hasta_requerido', 400, 'Ingresá la fecha y la hora hasta las que se concilia.'],
    ['nota_muy_larga', 400, 'La nota no puede superar los 500 caracteres.'],
    ['no_encontrado', 404, 'La empresa ya no existe. Actualizá la pantalla.'],
  ])('%s se muestra en español y deja el formulario abierto para corregir', async (codigo, estado, mensaje) => {
    apiPostMock.mockRejectedValue(new ErrorApi(estado, codigo, 'texto del servidor'))
    const usuario = userEvent.setup()
    const { onIncierto, onConciliado } = montar()

    await confirmar(usuario)
    await usuario.click(botonConciliar())

    expect(await screen.findByText(mensaje)).toBeInTheDocument()
    expect(onIncierto).not.toHaveBeenCalled()
    expect(onConciliado).not.toHaveBeenCalled()
    expect(botonConciliar()).toBeEnabled()
  })

  it('conciliacion_anterior nombra la marca vigente', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'conciliacion_anterior', 'x'))
    const usuario = userEvent.setup()
    montar('2026-10-30T12:00:00Z')

    await confirmar(usuario)
    await usuario.click(botonConciliar())

    const esperada = new Date('2026-10-30T12:00:00Z').toLocaleString('es-AR', { hourCycle: 'h23' })
    expect(await screen.findByText(`Ya está conciliado hasta ${esperada}; elegí un instante posterior.`)).toBeInTheDocument()
  })

  it('un 5xx avisa al llamador una sola vez y no deja reintentar a ciegas', async () => {
    apiPostMock.mockRejectedValue(new ErrorApi(503, 'resultado_incierto', 'x'))
    const usuario = userEvent.setup()
    const { onIncierto, onConciliado } = montar()

    await confirmar(usuario)
    await usuario.click(botonConciliar())

    await waitFor(() => expect(onIncierto).toHaveBeenCalledTimes(1))
    expect(onConciliado).not.toHaveBeenCalled()
    expect(apiPostMock).toHaveBeenCalledTimes(1)
  })

  it('una red cortada también es resultado incierto', async () => {
    apiPostMock.mockRejectedValue(new TypeError('Failed to fetch'))
    const usuario = userEvent.setup()
    const { onIncierto } = montar()

    await confirmar(usuario)
    await usuario.click(botonConciliar())

    await waitFor(() => expect(onIncierto).toHaveBeenCalledTimes(1))
  })
})
