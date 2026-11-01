import { act, cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ModalDeMercadoPagoDeSucursal } from './ModalDeMercadoPagoDeSucursal'
import { ErrorApi } from '../../api/cliente'
import type { EstadoDeLocalMercadoPago } from '../../api/mercadoPago'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()

vi.mock('../../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...args),
    post: (...args: unknown[]) => apiPostMock(...args),
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

afterEach(() => cleanup())

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
})

function estado(sobrescribir: Partial<EstadoDeLocalMercadoPago> = {}): EstadoDeLocalMercadoPago {
  return {
    estado: 'no_registrada',
    idStoreMercadoPago: null,
    externalId: 'WAYSS500',
    fechaRegistro: null,
    fechaSincronizacion: null,
    camposFaltantes: [],
    ...sobrescribir,
  }
}

function diferida<T>() {
  let resolver!: (v: T) => void
  let rechazador!: (e: unknown) => void
  const promesa = new Promise<T>((resolve, reject) => {
    resolver = resolve
    rechazador = reject
  })

  return { promesa, resolver, rechazador }
}

const centro = { id: 500, nombre: 'Centro' }

function montar(sucursal = centro) {
  return render(<ModalDeMercadoPagoDeSucursal sucursal={sucursal} onCerrar={vi.fn()} />)
}

describe('ModalDeMercadoPagoDeSucursal — lectura', () => {
  it('pide el estado de la sucursal y mientras tanto dice que carga', async () => {
    const lectura = diferida<EstadoDeLocalMercadoPago>()
    apiGetMock.mockReturnValue(lectura.promesa)
    montar()

    expect(screen.getByText('Cargando…')).toBeInTheDocument()
    expect(apiGetMock).toHaveBeenCalledWith('/sucursales/500/mercadopago')

    await act(async () => lectura.resolver(estado()))

    expect(await screen.findByText('No registrada')).toBeInTheDocument()
    expect(screen.queryByText('Cargando…')).not.toBeInTheDocument()
  })

  it('sin conexión manda a conectar Mercado Pago y no ofrece registrar', async () => {
    apiGetMock.mockResolvedValue(estado({ estado: 'sin_conexion' }))
    montar()

    expect(await screen.findByText(/no tiene Mercado Pago conectado/)).toBeInTheDocument()
    expect(screen.getByText('Sin conexión')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /en Mercado Pago/ })).not.toBeInTheDocument()
  })

  it('registrada muestra el local y las fechas, y no ofrece registrar de nuevo', async () => {
    apiGetMock.mockResolvedValue(
      estado({ estado: 'registrada', idStoreMercadoPago: 777, fechaRegistro: '2026-11-01T15:00:00Z', fechaSincronizacion: '2026-11-01T15:00:00Z' }),
    )
    montar()

    expect(await screen.findByText('Registrada')).toBeInTheDocument()
    expect(screen.getByText('777')).toBeInTheDocument()
    expect(screen.getByText(/Registrada el /)).toBeInTheDocument()
    expect(screen.getByText(/Última sincronización/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /en Mercado Pago/ })).not.toBeInTheDocument()
  })

  it('desactualizada avisa y ofrece actualizar', async () => {
    apiGetMock.mockResolvedValue(estado({ estado: 'desactualizada', idStoreMercadoPago: 777 }))
    montar()

    expect(await screen.findByText('Desactualizada')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Actualizar en Mercado Pago' })).toBeEnabled()
  })

  it('un fallo de la lectura muestra su copia y Reintentar vuelve a pedir', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockRejectedValueOnce(new ErrorApi(503, 'mercadopago_no_disponible', 'x'))
    montar()

    expect(await screen.findByText(/no está disponible en este momento/)).toBeInTheDocument()

    apiGetMock.mockResolvedValueOnce(estado())
    await usuario.click(screen.getByRole('button', { name: 'Reintentar' }))

    expect(await screen.findByText('No registrada')).toBeInTheDocument()
    expect(apiGetMock).toHaveBeenCalledTimes(2)
  })
})

describe('ModalDeMercadoPagoDeSucursal — dirección incompleta', () => {
  it('nombra por rótulo los campos que faltan y no deja registrar', async () => {
    apiGetMock.mockResolvedValue(estado({ camposFaltantes: ['altura', 'latitud', 'longitud'] }))
    montar()

    expect(await screen.findByText('Altura (número), Latitud, Longitud')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Registrar en Mercado Pago' })).not.toBeInTheDocument()
  })

  it('si el servidor rechaza por dirección incompleta, muestra su copia y relee los campos faltantes', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValueOnce(estado()).mockResolvedValueOnce(estado({ camposFaltantes: ['calle'] }))
    apiPostMock.mockRejectedValue(new ErrorApi(400, 'direccion_incompleta_para_mercadopago', 'completá: calle'))
    montar()
    await usuario.click(await screen.findByRole('button', { name: 'Registrar en Mercado Pago' }))

    expect(await screen.findByText(/le faltan datos de la dirección/)).toBeInTheDocument()
    expect(await screen.findByText('Calle')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Registrar en Mercado Pago' })).not.toBeInTheDocument()
  })
})

describe('ModalDeMercadoPagoDeSucursal — registrar', () => {
  it('registra, muestra el estado que devuelve el servidor y avisa', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue(estado())
    apiPostMock.mockResolvedValue(estado({ estado: 'registrada', idStoreMercadoPago: 888 }))
    montar()

    await usuario.click(await screen.findByRole('button', { name: 'Registrar en Mercado Pago' }))

    expect(await screen.findByText('La sucursal quedó registrada en Mercado Pago.')).toBeInTheDocument()
    expect(screen.getByText('888')).toBeInTheDocument()
    expect(apiPostMock).toHaveBeenCalledWith('/sucursales/500/mercadopago')
    expect(screen.queryByRole('button', { name: /en Mercado Pago/ })).not.toBeInTheDocument()
  })

  it('actualizar una desactualizada lo dice en el aviso', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue(estado({ estado: 'desactualizada', idStoreMercadoPago: 777 }))
    apiPostMock.mockResolvedValue(estado({ estado: 'registrada', idStoreMercadoPago: 777 }))
    montar()

    await usuario.click(await screen.findByRole('button', { name: 'Actualizar en Mercado Pago' }))

    expect(await screen.findByText('Se actualizó la sucursal en Mercado Pago.')).toBeInTheDocument()
  })

  it.each([
    ['mercadopago_solicitud_rechazada', /rechazó la solicitud/],
    ['mercadopago_local_duplicado', /más de un local/],
    ['mercadopago_requiere_reconexion', /volver a conectar/],
  ])('un rechazo %s muestra su copia y deja volver a intentar', async (codigo, copia) => {
    const usuario = userEvent.setup()
    apiGetMock.mockResolvedValue(estado())
    apiPostMock.mockRejectedValue(new ErrorApi(502, codigo, 'x'))
    montar()

    await usuario.click(await screen.findByRole('button', { name: 'Registrar en Mercado Pago' }))

    expect(await screen.findByText(copia)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Registrar en Mercado Pago' })).toBeEnabled()
  })

  it('dos clics en el mismo tick emiten una sola solicitud', async () => {
    const escritura = diferida<EstadoDeLocalMercadoPago>()
    apiGetMock.mockResolvedValue(estado())
    apiPostMock.mockReturnValue(escritura.promesa)
    montar()
    const boton = await screen.findByRole('button', { name: 'Registrar en Mercado Pago' })

    act(() => {
      boton.click()
      boton.click()
    })

    expect(apiPostMock).toHaveBeenCalledTimes(1)
    await act(async () => escritura.resolver(estado({ estado: 'registrada', idStoreMercadoPago: 1 })))
  })

  it('durante la escritura todo el modal queda inerte y al terminar vuelve a estar libre', async () => {
    const escritura = diferida<EstadoDeLocalMercadoPago>()
    apiGetMock.mockResolvedValue(estado())
    apiPostMock.mockReturnValue(escritura.promesa)
    montar()
    const usuario = userEvent.setup()
    await usuario.click(await screen.findByRole('button', { name: 'Registrar en Mercado Pago' }))

    expect(screen.getByRole('button', { name: 'Enviando…' })).toBeDisabled()
    for (const cerrar of screen.getAllByRole('button', { name: 'Cerrar' })) expect(cerrar).toBeDisabled()

    await act(async () => escritura.rechazador(new ErrorApi(503, 'mercadopago_no_disponible', 'x')))

    await waitFor(() => expect(screen.getByRole('button', { name: 'Registrar en Mercado Pago' })).toBeEnabled())
    for (const cerrar of screen.getAllByRole('button', { name: 'Cerrar' })) expect(cerrar).toBeEnabled()
  })
})

describe('ModalDeMercadoPagoDeSucursal — respuestas tardías', () => {
  it('la lectura de una sucursal anterior no pisa a la sucursal actual', async () => {
    const lecturaVieja = diferida<EstadoDeLocalMercadoPago>()
    apiGetMock.mockImplementation((ruta: string) =>
      ruta === '/sucursales/500/mercadopago' ? lecturaVieja.promesa : Promise.resolve(estado({ estado: 'registrada', idStoreMercadoPago: 999 })),
    )
    const { rerender } = montar()

    rerender(<ModalDeMercadoPagoDeSucursal sucursal={{ id: 501, nombre: 'Depósito' }} onCerrar={vi.fn()} />)
    expect(await screen.findByText('999')).toBeInTheDocument()

    await act(async () => lecturaVieja.resolver(estado({ estado: 'registrada', idStoreMercadoPago: 111 })))

    expect(screen.getByText('999')).toBeInTheDocument()
    expect(screen.queryByText('111')).not.toBeInTheDocument()
  })

  it('el error de una lectura anterior tampoco aparece', async () => {
    const lecturaVieja = diferida<EstadoDeLocalMercadoPago>()
    apiGetMock.mockImplementation((ruta: string) =>
      ruta === '/sucursales/500/mercadopago' ? lecturaVieja.promesa : Promise.resolve(estado({ estado: 'registrada', idStoreMercadoPago: 999 })),
    )
    const { rerender } = montar()

    rerender(<ModalDeMercadoPagoDeSucursal sucursal={{ id: 501, nombre: 'Depósito' }} onCerrar={vi.fn()} />)
    expect(await screen.findByText('999')).toBeInTheDocument()

    await act(async () => lecturaVieja.rechazador(new ErrorApi(500, 'error_interno', 'x')))

    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('la respuesta de un registro de la sucursal anterior no se aplica a la actual', async () => {
    const usuario = userEvent.setup()
    const escritura = diferida<EstadoDeLocalMercadoPago>()
    apiGetMock.mockImplementation((ruta: string) =>
      Promise.resolve(ruta === '/sucursales/500/mercadopago' ? estado() : estado({ estado: 'desactualizada', idStoreMercadoPago: 999 })),
    )
    apiPostMock.mockReturnValue(escritura.promesa)
    const { rerender } = montar()
    await usuario.click(await screen.findByRole('button', { name: 'Registrar en Mercado Pago' }))

    rerender(<ModalDeMercadoPagoDeSucursal sucursal={{ id: 501, nombre: 'Depósito' }} onCerrar={vi.fn()} />)
    expect(await screen.findByText('999')).toBeInTheDocument()
    await act(async () => escritura.resolver(estado({ estado: 'registrada', idStoreMercadoPago: 111 })))

    expect(screen.getByText('999')).toBeInTheDocument()
    expect(screen.queryByText('111')).not.toBeInTheDocument()
    expect(screen.queryByText('La sucursal quedó registrada en Mercado Pago.')).not.toBeInTheDocument()
  })
})
