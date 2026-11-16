import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ErrorApi } from '../api/cliente'
import type { LineaDevolvible, PrevisualizacionDeDevolucion, TicketDevolvible } from '../api/devoluciones'
import type { ComprobanteEmitido, VentaDeTurnoListado } from '../api/tipos'
import { formatearImporte } from '../formato/importes'
import { ModalDeDevolucion, type ImpresionDeDevolucion } from './ModalDeDevolucion'

const buscarPorNumeroMock = vi.fn()
const obtenerMock = vi.fn()
const previsualizarMock = vi.fn()
const listarPorTurnoMock = vi.fn()
const emitirMock = vi.fn()

vi.mock('../api/devoluciones', () => ({
  clienteDeDevoluciones: {
    buscarPorNumero: (...args: unknown[]) => buscarPorNumeroMock(...args),
    obtener: (...args: unknown[]) => obtenerMock(...args),
    previsualizar: (...args: unknown[]) => previsualizarMock(...args),
  },
}))

vi.mock('../api/ventas', () => ({
  clienteDeVentas: {
    listarPorTurno: (...args: unknown[]) => listarPorTurnoMock(...args),
    emitir: (...args: unknown[]) => emitirMock(...args),
  },
}))

const ID_EFECTIVO = 5

function linea(sobrescribir: Partial<LineaDevolvible> = {}): LineaDevolvible {
  return {
    idArticulo: 1,
    descripcion: 'Yerba 1 kg',
    codigoBarra: '7790001',
    importe: null,
    cantidadVendida: 5,
    cantidadDevuelta: 1,
    cantidadDevolvible: 4,
    importeUnitario: 100,
    importeDeLoRestante: 400,
    ...sobrescribir,
  }
}

function ticket(sobrescribir: Partial<TicketDevolvible> = {}): TicketDevolvible {
  return {
    id: 77,
    numero: 12,
    numeroVisible: '0003-00000012',
    fecha: '2026-11-15T10:00:00Z',
    idPuntoVenta: 3,
    idCliente: 9,
    nombreCliente: 'Ana',
    total: 500,
    devolvible: true,
    motivo: null,
    lineas: [linea(), linea({ idArticulo: 2, descripcion: 'Fideos', codigoBarra: '7790002', cantidadVendida: 2, cantidadDevuelta: 0, cantidadDevolvible: 2 })],
    ...sobrescribir,
  }
}

function venta(sobrescribir: Partial<VentaDeTurnoListado> = {}): VentaDeTurnoListado {
  return {
    id: 77,
    numero: 12,
    numeroVisible: '0003-00000012',
    estado: 'Emitido',
    fecha: '2026-11-15T10:00:00Z',
    idCliente: 9,
    nombreCliente: 'Ana',
    total: 500,
    descuentoManualTotal: 0,
    recargoManualTotal: 0,
    mediosDePago: [],
    ...sobrescribir,
  }
}

function previa(reintegro: number, creditos: number[]): PrevisualizacionDeDevolucion {
  return { reintegro, lineas: creditos.map((credito, i) => ({ idArticulo: i + 1, cantidad: 1, credito })) }
}

const comprobanteEmitido = { id: 900, numeroVisible: '0003-00000045', fecha: '2026-11-15T11:00:00Z', total: -200 } as unknown as ComprobanteEmitido

const dinero = (valor: number) => formatearImporte(valor, { simbolo: true })

function montar(props: Partial<React.ComponentProps<typeof ModalDeDevolucion>> = {}) {
  const alCerrar = vi.fn()
  const encolar = vi.fn()
  const impresion: ImpresionDeDevolucion = { contexto: { empresa: 'Demo', puntoVenta: 'Local', cajero: 'jperez' }, encolar }
  render(
    <ModalDeDevolucion
      idPuntoVenta={3}
      puntosVenta={[{ id: 3, nombre: 'Local' }]}
      idTurno={50}
      idMedioEfectivo={ID_EFECTIVO}
      impresion={impresion}
      alCerrar={alCerrar}
      {...props}
    />,
  )
  return { alCerrar, encolar }
}

function textoDeBytes(bytes: Uint8Array): string {
  return Array.from(bytes)
    .map((b) => (b >= 0x20 && b < 0x7f ? String.fromCharCode(b) : ''))
    .join('')
}

/** Busca el ticket por número y espera a que las líneas (el dato) estén en pantalla. */
async function abrirTicket(usuario: ReturnType<typeof userEvent.setup>, numero = '12') {
  await usuario.type(await screen.findByLabelText('Número de ticket'), numero)
  await usuario.click(screen.getByRole('button', { name: 'Buscar' }))
  await screen.findByLabelText('Cantidad a devolver de Yerba 1 kg')
}

beforeEach(() => {
  buscarPorNumeroMock.mockReset().mockResolvedValue(ticket())
  obtenerMock.mockReset().mockResolvedValue(ticket())
  previsualizarMock.mockReset().mockResolvedValue(previa(200, [200]))
  listarPorTurnoMock.mockReset().mockResolvedValue([venta()])
  emitirMock.mockReset().mockResolvedValue(comprobanteEmitido)
})

afterEach(() => {
  cleanup()
})

describe('ModalDeDevolucion — elegir el ticket', () => {
  it('busca por número en el punto de venta de la sesión', async () => {
    const usuario = userEvent.setup()
    montar()
    await abrirTicket(usuario, '0003-00000012')
    expect(buscarPorNumeroMock).toHaveBeenCalledWith(3, 12)
  })

  it('ofrece los últimos tickets emitidos del turno y elegir uno lo lee por id', async () => {
    listarPorTurnoMock.mockResolvedValue([
      venta({ id: 77 }),
      venta({ id: 78, numeroVisible: '0003-00000013', estado: 'Anulado' }),
      venta({ id: 79, numeroVisible: '0003-00000014', total: -100 }),
    ])
    const usuario = userEvent.setup()
    montar()
    const elegir = await screen.findByRole('button', { name: 'Devolver del ticket 0003-00000012' })
    expect(screen.queryByText('0003-00000013')).not.toBeInTheDocument()
    expect(screen.queryByText('0003-00000014')).not.toBeInTheDocument()
    await usuario.click(elegir)
    await screen.findByLabelText('Cantidad a devolver de Yerba 1 kg')
    expect(obtenerMock).toHaveBeenCalledWith(77)
  })

  it('sin turno abierto no pide la lista y solo busca por número', async () => {
    montar({ idTurno: null })
    await screen.findByLabelText('Número de ticket')
    expect(listarPorTurnoMock).not.toHaveBeenCalled()
    expect(screen.queryByText('Últimos tickets del turno')).not.toBeInTheDocument()
  })

  it('un 404 dice que no se encontró el ticket y deja buscar de nuevo', async () => {
    buscarPorNumeroMock.mockRejectedValue(new ErrorApi(404, 'no_encontrado', 'x'))
    const usuario = userEvent.setup()
    montar()
    await usuario.type(await screen.findByLabelText('Número de ticket'), '99')
    await usuario.click(screen.getByRole('button', { name: 'Buscar' }))
    expect(await screen.findByText('No se encontró ese ticket en este punto de venta.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Buscar' })).toBeEnabled()
  })

  it('un número tipeado inválido no pide nada y lo explica', async () => {
    const usuario = userEvent.setup()
    montar()
    await usuario.type(await screen.findByLabelText('Número de ticket'), 'abc')
    await usuario.click(screen.getByRole('button', { name: 'Buscar' }))
    expect(await screen.findByText(/Ingresá el número del ticket/)).toBeInTheDocument()
    expect(buscarPorNumeroMock).not.toHaveBeenCalled()
  })

  it('un ticket que no se puede devolver muestra su motivo en español y no ofrece confirmar', async () => {
    buscarPorNumeroMock.mockResolvedValue(ticket({ devolvible: false, motivo: 'con_factura_fiscal' }))
    const usuario = userEvent.setup()
    montar()
    await usuario.type(await screen.findByLabelText('Número de ticket'), '12')
    await usuario.click(screen.getByRole('button', { name: 'Buscar' }))
    expect(await screen.findByText('El ticket tiene factura fiscal: todavía no se puede devolver desde el POS.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Confirmar devolución' })).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Cantidad a devolver de Yerba 1 kg')).not.toBeInTheDocument()
  })
})

describe('ModalDeDevolucion — cantidades y vista previa', () => {
  it('pide la vista previa de la selección, muestra el crédito por línea y el efectivo a reintegrar', async () => {
    const usuario = userEvent.setup()
    montar()
    await abrirTicket(usuario)
    await usuario.type(screen.getByLabelText('Cantidad a devolver de Yerba 1 kg'), '2')
    await waitFor(() => expect(previsualizarMock).toHaveBeenCalledTimes(1))
    expect(previsualizarMock.mock.calls[0][0]).toEqual({
      idPuntoVenta: 3,
      idComprobanteAsociado: 77,
      lineas: [{ idArticulo: 1, cantidad: 2, codigoBarra: '7790001', idLote: null }],
    })
    const fila = screen.getByText('Yerba 1 kg').closest('tr') as HTMLElement
    await waitFor(() => expect(within(fila).getByText(dinero(200))).toBeInTheDocument())
    expect(screen.getByText('A reintegrar en efectivo').parentElement).toHaveTextContent(dinero(200))
  })

  it('una cantidad por encima de lo que se puede devolver marca la línea, no pide vista previa y bloquea confirmar', async () => {
    const usuario = userEvent.setup()
    montar()
    await abrirTicket(usuario)
    await usuario.type(screen.getByLabelText('Cantidad a devolver de Yerba 1 kg'), '5')
    expect(await screen.findByText('Se puede devolver hasta 4.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Confirmar devolución' })).toBeDisabled()
    await new Promise((resolver) => setTimeout(resolver, 400))
    expect(previsualizarMock).not.toHaveBeenCalled()
  })

  it('confirmar está bloqueado mientras la vista previa de la selección vigente no llegó', async () => {
    previsualizarMock.mockReturnValue(new Promise(() => {}))
    const usuario = userEvent.setup()
    montar()
    await abrirTicket(usuario)
    await usuario.type(screen.getByLabelText('Cantidad a devolver de Yerba 1 kg'), '2')
    await waitFor(() => expect(previsualizarMock).toHaveBeenCalled())
    expect(screen.getByRole('button', { name: 'Confirmar devolución' })).toBeDisabled()
  })

  it('una respuesta vieja de la vista previa no pisa a la de la selección actual', async () => {
    const pendientes: Array<(valor: PrevisualizacionDeDevolucion) => void> = []
    previsualizarMock.mockImplementation(() => new Promise((resolver) => pendientes.push(resolver)))
    const usuario = userEvent.setup()
    montar()
    await abrirTicket(usuario)
    const campo = screen.getByLabelText('Cantidad a devolver de Yerba 1 kg')
    await usuario.type(campo, '1')
    await waitFor(() => expect(pendientes).toHaveLength(1))
    await usuario.type(campo, '{Backspace}2')
    await waitFor(() => expect(pendientes).toHaveLength(2))

    await act(async () => pendientes[1](previa(200, [200])))
    await act(async () => pendientes[0](previa(100, [100])))

    expect(screen.getByText('A reintegrar en efectivo').parentElement).toHaveTextContent(dinero(200))
    expect(screen.getByRole('button', { name: 'Confirmar devolución' })).toBeEnabled()
  })

  it('escanear el código de una línea le suma uno y en el tope avisa', async () => {
    const usuario = userEvent.setup()
    montar()
    await abrirTicket(usuario)
    const escaneo = screen.getByLabelText('Escanear código de barras')
    await usuario.type(escaneo, '7790002{Enter}')
    expect(screen.getByLabelText('Cantidad a devolver de Fideos')).toHaveValue('1')
    await usuario.type(escaneo, '7790002{Enter}')
    expect(screen.getByLabelText('Cantidad a devolver de Fideos')).toHaveValue('2')
    await usuario.type(escaneo, '7790002{Enter}')
    expect(screen.getByLabelText('Cantidad a devolver de Fideos')).toHaveValue('2')
    expect(screen.getByText('Ya se agregó todo lo que se puede devolver de Fideos.')).toBeInTheDocument()
    await usuario.type(escaneo, '0000{Enter}')
    expect(screen.getByText('Ese código no está en el ticket.')).toBeInTheDocument()
  })
})

describe('ModalDeDevolucion — confirmar', () => {
  async function dejarLista(usuario: ReturnType<typeof userEvent.setup>) {
    await abrirTicket(usuario)
    await usuario.type(screen.getByLabelText('Cantidad a devolver de Yerba 1 kg'), '2')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Confirmar devolución' })).toBeEnabled())
  }

  it('emite la nota de crédito asociada con el efectivo igual al reintegro, imprime el ticket y muestra el resultado', async () => {
    const usuario = userEvent.setup()
    const { encolar } = montar()
    await dejarLista(usuario)
    await usuario.click(screen.getByRole('button', { name: 'Confirmar devolución' }))

    expect(await screen.findByText(/Devolución 0003-00000045 registrada/)).toBeInTheDocument()
    expect(emitirMock).toHaveBeenCalledTimes(1)
    expect(emitirMock.mock.calls[0][0]).toEqual({
      idPuntoVenta: 3,
      idCliente: 9,
      codigoTipoComprobante: 'NCX',
      idComprobanteAsociado: 77,
      lineas: [{ idArticulo: 1, cantidad: 2, codigoBarra: '7790001', idLote: null }],
      pagos: [{ idMedioPago: ID_EFECTIVO, importe: 200, referencia: null, vuelto: 0 }],
      direccionEntrega: null,
      observaciones: null,
    })
    expect(encolar).toHaveBeenCalledTimes(1)
    expect(encolar.mock.calls[0][0]).toBe('el ticket de devolución')
    const impreso = textoDeBytes(encolar.mock.calls[0][1] as Uint8Array)
    expect(impreso).toContain('DEVOLUCION')
    expect(impreso).toContain('Ticket asociado: 0003-00000012')
    expect(impreso).toContain('2 x Yerba 1 kg')
  })

  it('en la web (sin impresora) emite igual y lo dice sin imprimir', async () => {
    const usuario = userEvent.setup()
    montar({ impresion: undefined })
    await dejarLista(usuario)
    await usuario.click(screen.getByRole('button', { name: 'Confirmar devolución' }))
    expect(await screen.findByText(/Devolución 0003-00000045 registrada/)).toBeInTheDocument()
    expect(screen.getByText('Esta caja no imprime tickets.')).toBeInTheDocument()
  })

  it('dos clics sincrónicos en confirmar emiten una sola devolución', async () => {
    let resolver: (c: ComprobanteEmitido) => void = () => {}
    emitirMock.mockReturnValue(new Promise<ComprobanteEmitido>((r) => (resolver = r)))
    const usuario = userEvent.setup()
    montar()
    await dejarLista(usuario)
    const boton = screen.getByRole('button', { name: 'Confirmar devolución' })
    act(() => {
      boton.click()
      boton.click()
    })
    expect(emitirMock).toHaveBeenCalledTimes(1)
    await act(async () => resolver(comprobanteEmitido))
  })

  it('mientras emite deja inerte todo el modal', async () => {
    emitirMock.mockReturnValue(new Promise(() => {}))
    const usuario = userEvent.setup()
    montar()
    await dejarLista(usuario)
    await usuario.click(screen.getByRole('button', { name: 'Confirmar devolución' }))
    expect(await screen.findByRole('button', { name: 'Registrando…' })).toBeDisabled()
    expect(screen.getByLabelText('Cantidad a devolver de Yerba 1 kg')).toBeDisabled()
    expect(screen.getByLabelText('Escanear código de barras')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Elegir otro ticket' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancelar' })).toBeDisabled()
  })

  it('con devolucion_desactualizada vuelve a leer el ticket y deja revisar las cantidades', async () => {
    emitirMock.mockRejectedValue(new ErrorApi(409, 'devolucion_desactualizada', 'x'))
    const usuario = userEvent.setup()
    const { encolar } = montar()
    await dejarLista(usuario)
    obtenerMock.mockResolvedValue(
      ticket({ lineas: [linea({ cantidadDevuelta: 4, cantidadDevolvible: 1 }), ticket().lineas[1]] }),
    )
    await usuario.click(screen.getByRole('button', { name: 'Confirmar devolución' }))

    await waitFor(() => expect(obtenerMock).toHaveBeenCalledWith(77))
    expect(await screen.findAllByText('Cambió lo que se puede devolver de este ticket. Revisá las cantidades y confirmá de nuevo.')).not.toHaveLength(0)
    expect(await screen.findByText('Se puede devolver hasta 1.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Confirmar devolución' })).toBeDisabled()
    expect(encolar).not.toHaveBeenCalled()
  })

  it('otro rechazo muestra su copia en español y no imprime', async () => {
    emitirMock.mockRejectedValue(new ErrorApi(409, 'devolucion_requiere_nota_de_credito_fiscal', 'x'))
    const usuario = userEvent.setup()
    const { encolar } = montar()
    await dejarLista(usuario)
    await usuario.click(screen.getByRole('button', { name: 'Confirmar devolución' }))
    expect(await screen.findByText(/necesita una nota de crédito fiscal/)).toBeInTheDocument()
    expect(encolar).not.toHaveBeenCalled()
    expect(obtenerMock).not.toHaveBeenCalled()
  })

  it('un resultado incierto pide verificar Ventas del turno y vuelve a leer el ticket', async () => {
    emitirMock.mockRejectedValue(new ErrorApi(503, 'resultado_incierto', 'x'))
    const usuario = userEvent.setup()
    montar()
    await dejarLista(usuario)
    await usuario.click(screen.getByRole('button', { name: 'Confirmar devolución' }))
    expect(await screen.findByText(/revisá Ventas del turno/)).toBeInTheDocument()
    await waitFor(() => expect(obtenerMock).toHaveBeenCalledWith(77))
  })

  it('el rechazo anterior desaparece al cambiar la selección', async () => {
    emitirMock.mockRejectedValue(new ErrorApi(409, 'devolucion_requiere_nota_de_credito_fiscal', 'x'))
    const usuario = userEvent.setup()
    montar()
    await dejarLista(usuario)
    await usuario.click(screen.getByRole('button', { name: 'Confirmar devolución' }))
    await screen.findByText(/necesita una nota de crédito fiscal/)
    previsualizarMock.mockReturnValue(new Promise(() => {}))
    fireEvent.change(screen.getByLabelText('Cantidad a devolver de Yerba 1 kg'), { target: { value: '3' } })
    expect(screen.queryByText(/necesita una nota de crédito fiscal/)).not.toBeInTheDocument()
  })

  it('sin medio en efectivo configurado no deja confirmar y lo dice', async () => {
    const usuario = userEvent.setup()
    montar({ idMedioEfectivo: '' })
    await abrirTicket(usuario)
    await usuario.type(screen.getByLabelText('Cantidad a devolver de Yerba 1 kg'), '2')
    expect(await screen.findByText(/No hay un medio de pago en efectivo configurado/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Confirmar devolución' })).toBeDisabled()
  })

  it('un error de la vista previa se muestra y bloquea confirmar', async () => {
    previsualizarMock.mockRejectedValue(new ErrorApi(409, 'devolucion_requiere_nota_de_credito_fiscal', 'x'))
    const usuario = userEvent.setup()
    montar()
    await abrirTicket(usuario)
    await usuario.type(screen.getByLabelText('Cantidad a devolver de Yerba 1 kg'), '2')
    expect(await screen.findByText(/necesita una nota de crédito fiscal/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Confirmar devolución' })).toBeDisabled()
  })
})
