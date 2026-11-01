import { act, cleanup, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ModalDeMercadoPagoDePuntoVenta } from './ModalDeMercadoPagoDePuntoVenta'
import { ErrorApi } from '../../api/cliente'
import type { EstadoDeCajaMercadoPago, ListadoDeTerminales } from '../../api/mercadoPago'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()
const apiDeleteMock = vi.fn()

vi.mock('../../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...args),
    post: (...args: unknown[]) => apiPostMock(...args),
    delete: (...args: unknown[]) => apiDeleteMock(...args),
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
  apiDeleteMock.mockReset()
})

function caja(sobrescribir: Partial<EstadoDeCajaMercadoPago> = {}): EstadoDeCajaMercadoPago {
  return {
    estado: 'registrada',
    idPosMercadoPago: 4321,
    idStoreMercadoPago: 777,
    externalId: 'WAYSP100',
    urlQr: 'https://www.mercadopago.com.ar/qr/abc.png',
    fechaRegistro: '2026-11-01T15:00:00Z',
    fechaSincronizacion: '2026-11-01T15:00:00Z',
    idTerminalPoint: null,
    ...sobrescribir,
  }
}

function listado(sobrescribir: Partial<ListadoDeTerminales> = {}): ListadoDeTerminales {
  return {
    terminales: [
      { id: 'PAX_A910__8701', modoOperativo: 'STANDALONE', asignada: false },
      { id: 'PAX_A910__8702', modoOperativo: 'PDV', asignada: false },
    ],
    instrucciones: null,
    idTerminalAsignada: null,
    asignadaVigente: false,
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

const RUTA_CAJA = '/puntos-venta/100/mercadopago'
const RUTA_TERMINALES = '/puntos-venta/100/mercadopago/terminales'

/** Reparte las lecturas por ruta; cada una puede cambiar entre llamadas mutando lo que devuelve. */
function lecturas(estadoDeCaja: EstadoDeCajaMercadoPago | Promise<EstadoDeCajaMercadoPago>, terminales: () => Promise<ListadoDeTerminales> = () => Promise.resolve(listado())) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === RUTA_CAJA) return Promise.resolve(estadoDeCaja)
    if (ruta === RUTA_TERMINALES) return terminales()

    return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
  })
}

const pv = { id: 100, nombre: 'Caja 1' }

function montar(puntoVenta = pv) {
  return render(<ModalDeMercadoPagoDePuntoVenta puntoVenta={puntoVenta} onCerrar={vi.fn()} />)
}

describe('ModalDeMercadoPagoDePuntoVenta — estados de la caja', () => {
  it('mientras lee la caja dice que carga', async () => {
    const lectura = diferida<EstadoDeCajaMercadoPago>()
    lecturas(lectura.promesa)
    montar()

    expect(screen.getByText('Cargando…')).toBeInTheDocument()
    await act(async () => lectura.resolver(caja()))
    expect(await screen.findByText('Registrada')).toBeInTheDocument()
  })

  it.each([
    ['sin_conexion', 'Sin conexión', /no tiene Mercado Pago conectado/],
    ['local_no_registrado', 'Sucursal sin registrar', /Registrala primero desde la pantalla de Sucursales/],
  ] as const)('%s explica qué falta y no ofrece registrar ni terminales', async (estado, etiqueta, texto) => {
    lecturas(caja({ estado, idPosMercadoPago: null, urlQr: null }))
    montar()

    expect(await screen.findByText(etiqueta)).toBeInTheDocument()
    expect(screen.getByText(texto)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /en Mercado Pago/ })).not.toBeInTheDocument()
    expect(screen.queryByText('Terminal Point')).not.toBeInTheDocument()
    expect(apiGetMock).not.toHaveBeenCalledWith(RUTA_TERMINALES)
  })

  it('no registrada ofrece registrar y todavía no pide terminales', async () => {
    lecturas(caja({ estado: 'no_registrada', idPosMercadoPago: null, urlQr: null }))
    montar()

    expect(await screen.findByRole('button', { name: 'Registrar en Mercado Pago' })).toBeEnabled()
    expect(apiGetMock).not.toHaveBeenCalledWith(RUTA_TERMINALES)
  })

  it('desactualizada ofrece actualizar y sí muestra QR y terminales', async () => {
    lecturas(caja({ estado: 'desactualizada' }))
    montar()

    expect(await screen.findByRole('button', { name: 'Actualizar en Mercado Pago' })).toBeEnabled()
    expect(await screen.findByText('PAX_A910__8701')).toBeInTheDocument()
  })

  it('un fallo de la lectura muestra su copia y Reintentar vuelve a pedir', async () => {
    const usuario = userEvent.setup()
    apiGetMock.mockRejectedValueOnce(new ErrorApi(503, 'mercadopago_no_disponible', 'x'))
    montar()
    expect(await screen.findByText(/no está disponible en este momento/)).toBeInTheDocument()

    lecturas(caja({ estado: 'no_registrada', idPosMercadoPago: null }))
    await usuario.click(screen.getByRole('button', { name: 'Reintentar' }))

    expect(await screen.findByText('No registrada')).toBeInTheDocument()
  })
})

describe('ModalDeMercadoPagoDePuntoVenta — registrar', () => {
  it('registra, muestra el estado nuevo, el QR y carga las terminales', async () => {
    const usuario = userEvent.setup()
    lecturas(caja({ estado: 'no_registrada', idPosMercadoPago: null, urlQr: null }))
    apiPostMock.mockResolvedValue(caja())
    montar()

    await usuario.click(await screen.findByRole('button', { name: 'Registrar en Mercado Pago' }))

    expect(await screen.findByText('El punto de venta quedó registrado en Mercado Pago.')).toBeInTheDocument()
    expect(apiPostMock).toHaveBeenCalledWith(RUTA_CAJA)
    expect(await screen.findByText('PAX_A910__8701')).toBeInTheDocument()
    expect(screen.getByRole('img', { name: 'QR de Caja 1' })).toBeInTheDocument()
  })

  it('un rechazo muestra el código en copia propia', async () => {
    const usuario = userEvent.setup()
    lecturas(caja({ estado: 'no_registrada', idPosMercadoPago: null, urlQr: null }))
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'mercadopago_local_no_registrado', 'x'))
    montar()

    await usuario.click(await screen.findByRole('button', { name: 'Registrar en Mercado Pago' }))

    expect(await screen.findByText(/registrar la sucursal de este punto de venta/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Registrar en Mercado Pago' })).toBeEnabled()
  })

  it('dos clics en el mismo tick emiten una sola solicitud', async () => {
    const escritura = diferida<EstadoDeCajaMercadoPago>()
    lecturas(caja({ estado: 'no_registrada', idPosMercadoPago: null, urlQr: null }))
    apiPostMock.mockReturnValue(escritura.promesa)
    montar()
    const boton = await screen.findByRole('button', { name: 'Registrar en Mercado Pago' })

    act(() => {
      boton.click()
      boton.click()
    })

    expect(apiPostMock).toHaveBeenCalledTimes(1)
    await act(async () => escritura.resolver(caja()))
  })
})

describe('ModalDeMercadoPagoDePuntoVenta — QR', () => {
  it('muestra la imagen y un enlace seguro para abrirla e imprimirla', async () => {
    lecturas(caja())
    montar()

    const imagen = await screen.findByRole('img', { name: 'QR de Caja 1' })
    expect(imagen).toHaveAttribute('src', 'https://www.mercadopago.com.ar/qr/abc.png')

    const enlace = screen.getByRole('link', { name: 'Abrir para imprimir' })
    expect(enlace).toHaveAttribute('href', 'https://www.mercadopago.com.ar/qr/abc.png')
    expect(enlace).toHaveAttribute('target', '_blank')
    expect(enlace).toHaveAttribute('rel', 'noopener noreferrer')
  })

  it('una URL que no es http(s) no se renderiza ni como imagen ni como enlace', async () => {
    lecturas(caja({ urlQr: 'javascript:alert(1)' }))
    montar()

    expect(await screen.findByText(/todavía no devolvió el QR/)).toBeInTheDocument()
    expect(screen.queryByRole('img')).not.toBeInTheDocument()
    expect(screen.queryByRole('link')).not.toBeInTheDocument()
  })

  it('sin QR lo dice', async () => {
    lecturas(caja({ urlQr: null }))
    montar()

    expect(await screen.findByText(/todavía no devolvió el QR/)).toBeInTheDocument()
  })
})

describe('ModalDeMercadoPagoDePuntoVenta — terminales', () => {
  it('lista cada terminal con su modo y ofrece asignar solo las no asignadas', async () => {
    lecturas(caja(), () => Promise.resolve(listado({ idTerminalAsignada: 'PAX_A910__8702', asignadaVigente: true, terminales: [
      { id: 'PAX_A910__8701', modoOperativo: 'STANDALONE', asignada: false },
      { id: 'PAX_A910__8702', modoOperativo: 'PDV', asignada: true },
    ] })))
    montar()

    const fila1 = (await screen.findByRole('cell', { name: /PAX_A910__8701/ })).closest('tr') as HTMLElement
    const fila2 = screen.getByRole('cell', { name: /PAX_A910__8702/ }).closest('tr') as HTMLElement

    expect(within(fila1).getByText('Independiente (STANDALONE)')).toBeInTheDocument()
    expect(within(fila1).getByRole('button', { name: 'Asignar' })).toBeEnabled()
    expect(within(fila2).getByText('Punto de venta (PDV)')).toBeInTheDocument()
    expect(within(fila2).getByText('Asignada')).toBeInTheDocument()
    expect(within(fila2).queryByRole('button', { name: 'Asignar' })).not.toBeInTheDocument()
    expect(screen.queryByText(/ya no está vinculada/)).not.toBeInTheDocument()
  })

  it('con la lista vacía muestra las instrucciones que manda el servidor', async () => {
    lecturas(caja(), () => Promise.resolve(listado({ terminales: [], instrucciones: 'Abrí la app de Mercado Pago y vinculá la terminal a la caja.' })))
    montar()

    expect(await screen.findByText('Abrí la app de Mercado Pago y vinculá la terminal a la caja.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Actualizar lista' })).toBeEnabled()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
  })

  it('asignar manda el id, avisa que hay que reiniciar si cambió el modo y relee la lista', async () => {
    const usuario = userEvent.setup()
    let asignada = false
    lecturas(caja(), () =>
      Promise.resolve(
        listado({
          idTerminalAsignada: asignada ? 'PAX_A910__8701' : null,
          asignadaVigente: asignada,
          terminales: [{ id: 'PAX_A910__8701', modoOperativo: asignada ? 'PDV' : 'STANDALONE', asignada }],
        }),
      ),
    )
    apiPostMock.mockImplementation(() => {
      asignada = true

      return Promise.resolve({ idTerminalPoint: 'PAX_A910__8701', fechaTerminal: '2026-11-01T16:00:00Z', modoCambiado: true })
    })
    montar()

    await usuario.click(await screen.findByRole('button', { name: 'Asignar' }))

    expect(await screen.findByText(/Se asignó la terminal PAX_A910__8701 y se pasó a modo PDV. Reiniciala/)).toBeInTheDocument()
    expect(apiPostMock).toHaveBeenCalledWith('/puntos-venta/100/mercadopago/terminal', { idTerminal: 'PAX_A910__8701' })
    expect(await screen.findByText('Asignada')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Quitar asignación' })).toBeEnabled()
  })

  it('un rechazo de la asignación muestra su copia y la terminal queda sin asignar', async () => {
    const usuario = userEvent.setup()
    lecturas(caja())
    apiPostMock.mockRejectedValue(new ErrorApi(409, 'mercadopago_terminal_modelo_no_admitido', 'x'))
    montar()

    await usuario.click((await screen.findAllByRole('button', { name: 'Asignar' }))[0])

    expect(await screen.findByText(/no admite el modo punto de venta/)).toBeInTheDocument()
    expect(screen.queryByText('Asignada')).not.toBeInTheDocument()
  })

  it('quitar llama al DELETE, avisa que Mercado Pago no se toca y relee la lista', async () => {
    const usuario = userEvent.setup()
    let asignada = true
    lecturas(caja(), () =>
      Promise.resolve(
        listado({
          idTerminalAsignada: asignada ? 'PAX_A910__8701' : null,
          asignadaVigente: asignada,
          terminales: [{ id: 'PAX_A910__8701', modoOperativo: 'PDV', asignada }],
        }),
      ),
    )
    apiDeleteMock.mockImplementation(() => {
      asignada = false

      return Promise.resolve({ idTerminalPoint: null, fechaTerminal: null, modoCambiado: false })
    })
    montar()

    await usuario.click(await screen.findByRole('button', { name: 'Quitar asignación' }))

    expect(await screen.findByText(/Se quitó la terminal de este punto de venta en Ways/)).toBeInTheDocument()
    expect(apiDeleteMock).toHaveBeenCalledWith('/puntos-venta/100/mercadopago/terminal')
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Quitar asignación' })).not.toBeInTheDocument())
  })

  it('si la relectura posterior falla, lo guardado se informa como guardado y no como error', async () => {
    const usuario = userEvent.setup()
    let relecturas = 0
    lecturas(caja(), () => {
      relecturas++

      return relecturas === 1 ? Promise.resolve(listado()) : Promise.reject(new ErrorApi(503, 'mercadopago_no_disponible', 'x'))
    })
    apiPostMock.mockResolvedValue({ idTerminalPoint: 'PAX_A910__8701', fechaTerminal: null, modoCambiado: false })
    montar()

    await usuario.click((await screen.findAllByRole('button', { name: 'Asignar' }))[0])

    expect(await screen.findByText(/Se asignó la terminal PAX_A910__8701\. No se pudo actualizar la lista de terminales\./)).toBeInTheDocument()
    expect(screen.queryByText('La terminal no se pudo asignar')).not.toBeInTheDocument()
  })

  it('un fallo al leer las terminales muestra su copia con Reintentar', async () => {
    const usuario = userEvent.setup()
    let intento = 0
    lecturas(caja(), () => (++intento === 1 ? Promise.reject(new ErrorApi(409, 'mercadopago_caja_no_registrada', 'x')) : Promise.resolve(listado())))
    montar()

    expect(await screen.findByText(/todavía no está registrado como caja/)).toBeInTheDocument()
    await usuario.click(screen.getByRole('button', { name: 'Reintentar' }))

    expect(await screen.findByText('PAX_A910__8701')).toBeInTheDocument()
  })

  it('dos clics en Asignar emiten una sola solicitud', async () => {
    const escritura = diferida<unknown>()
    lecturas(caja())
    apiPostMock.mockReturnValue(escritura.promesa)
    montar()
    const boton = (await screen.findAllByRole('button', { name: 'Asignar' }))[0]

    act(() => {
      boton.click()
      boton.click()
    })

    expect(apiPostMock).toHaveBeenCalledTimes(1)
    await act(async () => escritura.resolver({ idTerminalPoint: 'PAX_A910__8701', fechaTerminal: null, modoCambiado: false }))
  })

  it('durante la escritura quedan inertes las acciones de terminal y el cierre', async () => {
    const usuario = userEvent.setup()
    const escritura = diferida<unknown>()
    lecturas(caja(), () => Promise.resolve(listado({ idTerminalAsignada: 'PAX_A910__8702', asignadaVigente: true })))
    apiPostMock.mockReturnValue(escritura.promesa)
    montar()
    await usuario.click((await screen.findAllByRole('button', { name: 'Asignar' }))[0])

    for (const boton of screen.getAllByRole('button', { name: 'Asignar' })) expect(boton).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Quitar asignación' })).toBeDisabled()
    for (const cerrar of screen.getAllByRole('button', { name: 'Cerrar' })) expect(cerrar).toBeDisabled()

    await act(async () => escritura.resolver({ idTerminalPoint: 'PAX_A910__8701', fechaTerminal: null, modoCambiado: false }))
    await waitFor(() => expect(screen.getAllByRole('button', { name: 'Asignar' })[0]).toBeEnabled())
  })
})

describe('ModalDeMercadoPagoDePuntoVenta — asignación vieja', () => {
  const vieja = () => listado({ terminales: [], instrucciones: 'Vinculá la terminal desde la app.', idTerminalAsignada: 'PAX_A910__9999', asignadaVigente: false })

  it('avisa que la terminal guardada ya no está vinculada y ofrece quitarla', async () => {
    lecturas(caja(), () => Promise.resolve(vieja()))
    montar()

    const alerta = await screen.findByText(/ya no está vinculada a esta caja en Mercado Pago/)
    expect(alerta.closest('[role="alert"]')).toHaveTextContent('PAX_A910__9999')
    expect(screen.getByRole('button', { name: 'Quitar asignación' })).toBeEnabled()
  })

  it('quitar una asignación vieja limpia el aviso', async () => {
    const usuario = userEvent.setup()
    let hay = true
    lecturas(caja(), () => Promise.resolve(hay ? vieja() : listado({ terminales: [], instrucciones: 'Vinculá la terminal desde la app.' })))
    apiDeleteMock.mockImplementation(() => {
      hay = false

      return Promise.resolve({ idTerminalPoint: null, fechaTerminal: null, modoCambiado: false })
    })
    montar()

    await usuario.click(await screen.findByRole('button', { name: 'Quitar asignación' }))

    await waitFor(() => expect(screen.queryByText(/ya no está vinculada/)).not.toBeInTheDocument())
  })
})

describe('ModalDeMercadoPagoDePuntoVenta — respuestas tardías', () => {
  it('la lectura de un punto de venta anterior no pisa al actual', async () => {
    const vieja = diferida<EstadoDeCajaMercadoPago>()
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === RUTA_CAJA) return vieja.promesa
      if (ruta === '/puntos-venta/101/mercadopago') return Promise.resolve(caja({ idPosMercadoPago: 999 }))
      if (ruta === '/puntos-venta/101/mercadopago/terminales') return Promise.resolve(listado())

      return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
    })
    const { rerender } = montar()

    rerender(<ModalDeMercadoPagoDePuntoVenta puntoVenta={{ id: 101, nombre: 'Caja 2' }} onCerrar={vi.fn()} />)
    expect(await screen.findByText('999')).toBeInTheDocument()
    await act(async () => vieja.resolver(caja({ idPosMercadoPago: 111 })))

    expect(screen.getByText('999')).toBeInTheDocument()
    expect(screen.queryByText('111')).not.toBeInTheDocument()
  })

  it('las terminales de un punto de venta anterior no pisan a las del actual', async () => {
    const vieja = diferida<ListadoDeTerminales>()
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === RUTA_CAJA || ruta === '/puntos-venta/101/mercadopago') return Promise.resolve(caja())
      if (ruta === RUTA_TERMINALES) return vieja.promesa
      if (ruta === '/puntos-venta/101/mercadopago/terminales') return Promise.resolve(listado({ terminales: [{ id: 'PAX_A910__0101', modoOperativo: 'PDV', asignada: false }] }))

      return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
    })
    const { rerender } = montar()
    await screen.findByText('Registrada')

    rerender(<ModalDeMercadoPagoDePuntoVenta puntoVenta={{ id: 101, nombre: 'Caja 2' }} onCerrar={vi.fn()} />)
    expect(await screen.findByText('PAX_A910__0101')).toBeInTheDocument()
    await act(async () => vieja.resolver(listado({ terminales: [{ id: 'PAX_A910__0100', modoOperativo: 'PDV', asignada: false }] })))

    expect(screen.getByText('PAX_A910__0101')).toBeInTheDocument()
    expect(screen.queryByText('PAX_A910__0100')).not.toBeInTheDocument()
  })

  it('la respuesta de un registro del punto de venta anterior no se aplica al actual', async () => {
    const usuario = userEvent.setup()
    const escritura = diferida<EstadoDeCajaMercadoPago>()
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === RUTA_CAJA) return Promise.resolve(caja({ estado: 'no_registrada', idPosMercadoPago: null, urlQr: null }))
      if (ruta === '/puntos-venta/101/mercadopago') return Promise.resolve(caja({ estado: 'desactualizada', idPosMercadoPago: 999 }))
      if (ruta === '/puntos-venta/101/mercadopago/terminales') return Promise.resolve(listado())

      return Promise.reject(new Error(`ruta inesperada: ${ruta}`))
    })
    apiPostMock.mockReturnValue(escritura.promesa)
    const { rerender } = montar()
    await usuario.click(await screen.findByRole('button', { name: 'Registrar en Mercado Pago' }))

    rerender(<ModalDeMercadoPagoDePuntoVenta puntoVenta={{ id: 101, nombre: 'Caja 2' }} onCerrar={vi.fn()} />)
    expect(await screen.findByText('999')).toBeInTheDocument()
    await act(async () => escritura.resolver(caja({ idPosMercadoPago: 111 })))

    expect(screen.getByText('999')).toBeInTheDocument()
    expect(screen.queryByText('111')).not.toBeInTheDocument()
    expect(screen.queryByText('El punto de venta quedó registrado en Mercado Pago.')).not.toBeInTheDocument()
  })
})
