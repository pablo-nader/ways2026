import { cleanup, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ConexionMercadoPago } from './ConexionMercadoPago'
import type { EstadoDeConexionMercadoPago } from '../../api/mercadoPago'
import type { EmpresaListado } from '../../api/tipos'

const apiGetMock = vi.fn()
const apiDeleteMock = vi.fn()

vi.mock('../../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...args),
    post: vi.fn(),
    delete: (...args: unknown[]) => apiDeleteMock(...args),
  },
  ErrorApi: class ErrorApiMock extends Error {},
}))

const EMPRESA: EmpresaListado = {
  id: 10,
  idTenant: 2,
  razonSocial: 'Sur SRL',
  nombreFantasia: null,
  cuit: null,
  nombreTenant: 'Comercio Sur',
  alicuotaPercepcionIibb: null,
  alicuotaPercepcionIva: null,
  ingresosBrutos: null,
  inicioActividades: null,
}

const ACTIVA: EstadoDeConexionMercadoPago = {
  conectada: true,
  estado: 'activa',
  idUsuarioMercadoPago: 123456789,
  fechaConexion: '2026-10-31T15:00:00Z',
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiDeleteMock.mockReset()
  apiGetMock.mockResolvedValue(ACTIVA)
})

afterEach(cleanup)

function tarjeta(bloqueado: boolean) {
  return (
    <ConexionMercadoPago empresa={EMPRESA} cuentas={[]} errorDeCuentas="" version={0} bloqueado={bloqueado} onAviso={vi.fn()} />
  )
}

describe('ConexionMercadoPago — confirmación de desconexión abierta', () => {
  it('si empieza un canje con la confirmación abierta, su botón queda inerte y no desconecta', async () => {
    const usuario = userEvent.setup()
    const { rerender } = render(tarjeta(false))
    await usuario.click(await screen.findByRole('button', { name: 'Desconectar' }))
    const puerta = await screen.findByRole('alertdialog')
    expect(within(puerta).getByRole('button', { name: 'Desconectar' })).toBeEnabled()

    rerender(tarjeta(true))

    const confirmar = await within(screen.getByRole('alertdialog')).findByRole('button', { name: 'Completando la conexión…' })
    expect(confirmar).toBeDisabled()
    await usuario.click(confirmar)
    expect(apiDeleteMock).not.toHaveBeenCalled()

    rerender(tarjeta(false))
    await waitFor(() => expect(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Desconectar' })).toBeEnabled())
  })
})
