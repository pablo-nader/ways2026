import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ModalAltaRapidaDeCliente } from './ModalAltaRapidaDeCliente'
import { ErrorApi } from '../api/cliente'
import type { ClienteListado } from '../api/tipos'

const crearRapidoMock = vi.fn()

vi.mock('../api/clientes', () => ({
  clienteDeClientes: { crearRapido: (...args: unknown[]) => crearRapidoMock(...args) },
}))

const cliente = { id: 50, numero: 12, nombre: 'Ana' } as ClienteListado

beforeEach(() => {
  crearRapidoMock.mockReset()
})

afterEach(() => {
  cleanup()
})

function escribir(etiqueta: RegExp, valor: string) {
  fireEvent.change(screen.getByLabelText(etiqueta), { target: { value: valor } })
}

function montar(props: Partial<React.ComponentProps<typeof ModalAltaRapidaDeCliente>> = {}) {
  const alCreado = vi.fn()
  const alCerrar = vi.fn()
  render(<ModalAltaRapidaDeCliente alCreado={alCreado} alCerrar={alCerrar} {...props} />)
  return { alCreado, alCerrar }
}

describe('ModalAltaRapidaDeCliente', () => {
  it('no ofrece campos de crédito y deja el botón inerte con el nombre vacío', () => {
    montar()
    expect(screen.queryByLabelText(/crédito|límite/i)).toBeNull()
    expect((screen.getByRole('button', { name: 'Crear cliente' }) as HTMLButtonElement).disabled).toBe(true)
    escribir(/^Nombre/, '   ')
    expect((screen.getByRole('button', { name: 'Crear cliente' }) as HTMLButtonElement).disabled).toBe(true)
    escribir(/^Nombre/, 'Ana')
    expect((screen.getByRole('button', { name: 'Crear cliente' }) as HTMLButtonElement).disabled).toBe(false)
  })

  it('envía los valores recortados con null en los opcionales en blanco y avisa el alta', async () => {
    crearRapidoMock.mockResolvedValue(cliente)
    const { alCreado } = montar()
    escribir(/^Nombre/, '  Ana ')
    escribir(/^Teléfono/, '  ')
    escribir(/^Domicilio/, ' Mitre 12 ')
    fireEvent.click(screen.getByRole('button', { name: 'Crear cliente' }))
    await waitFor(() => expect(alCreado).toHaveBeenCalledWith(cliente))
    expect(crearRapidoMock).toHaveBeenCalledTimes(1)
    expect(crearRapidoMock).toHaveBeenCalledWith({ nombre: 'Ana', telefono: null, domicilio: 'Mitre 12' })
  })

  it('dos clics sincrónicos envían una sola solicitud', async () => {
    let resolver: (c: ClienteListado) => void = () => {}
    crearRapidoMock.mockReturnValue(new Promise<ClienteListado>((r) => (resolver = r)))
    const { alCreado } = montar()
    escribir(/^Nombre/, 'Ana')
    const boton = screen.getByRole('button', { name: 'Crear cliente' })
    act(() => {
      boton.click()
      boton.click()
    })
    expect(crearRapidoMock).toHaveBeenCalledTimes(1)
    await act(async () => resolver(cliente))
    expect(alCreado).toHaveBeenCalledTimes(1)
  })

  it('muestra el mensaje del servidor y mantiene el modal abierto con los datos', async () => {
    crearRapidoMock.mockRejectedValue(new ErrorApi(400, 'cuerpo_invalido', 'El nombre es obligatorio.'))
    const { alCreado, alCerrar } = montar()
    escribir(/^Nombre/, 'Ana')
    fireEvent.click(screen.getByRole('button', { name: 'Crear cliente' }))
    expect(await screen.findByText('El nombre es obligatorio.')).toBeTruthy()
    expect(alCreado).not.toHaveBeenCalled()
    expect(alCerrar).not.toHaveBeenCalled()
    expect((screen.getByLabelText(/^Nombre/) as HTMLInputElement).value).toBe('Ana')
    expect((screen.getByRole('button', { name: 'Crear cliente' }) as HTMLButtonElement).disabled).toBe(false)
  })

  it('un fallo de red muestra un mensaje genérico', async () => {
    crearRapidoMock.mockRejectedValue(new TypeError('Failed to fetch'))
    montar()
    escribir(/^Nombre/, 'Ana')
    fireEvent.click(screen.getByRole('button', { name: 'Crear cliente' }))
    expect(await screen.findByText(/No se pudo crear el cliente/)).toBeTruthy()
  })

  it('Cancelar cierra sin crear', () => {
    const { alCerrar } = montar()
    fireEvent.click(screen.getByRole('button', { name: 'Cancelar' }))
    expect(alCerrar).toHaveBeenCalledTimes(1)
    expect(crearRapidoMock).not.toHaveBeenCalled()
  })
})
