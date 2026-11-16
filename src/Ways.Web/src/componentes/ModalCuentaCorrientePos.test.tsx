import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ESPERA_MAXIMA_DEL_RECIBO_MS, ModalCuentaCorrientePos } from './ModalCuentaCorrientePos'
import { Toaster } from '../toasts/Toaster'
import type { PagoACuentaRegistrado } from './ModalCuentaCorrientePos'
import type { ClienteListado, ComprobanteEmitido, EstadoDeCuenta, MedioPagoListado, MovimientoDeCuentaCorriente, PuntoVentaListado } from '../api/tipos'

const obtenerEstadoMock = vi.fn()
const registrarPagoMock = vi.fn()

vi.mock('../api/cuentaCorriente', async (importarOriginal) => {
  const real = await importarOriginal<typeof import('../api/cuentaCorriente')>()
  return {
    ...real,
    clienteDeCuentaCorriente: {
      ...real.clienteDeCuentaCorriente,
      obtenerEstado: (...args: unknown[]) => obtenerEstadoMock(...args),
      registrarPago: (...args: unknown[]) => registrarPagoMock(...args),
    },
  }
})

const clienteListarMock = vi.fn()
vi.mock('../api/clientes', () => ({
  clienteDeClientes: { listar: (...args: unknown[]) => clienteListarMock(...args) },
}))

const obtenerVentaMock = vi.fn()
vi.mock('../api/ventas', async (importarOriginal) => {
  const real = await importarOriginal<typeof import('../api/ventas')>()
  return { ...real, clienteDeVentas: { ...real.clienteDeVentas, obtener: (...args: unknown[]) => obtenerVentaMock(...args) } }
})

const listarCatalogoMock = vi.fn()
vi.mock('../api/catalogos', async (importarOriginal) => {
  const real = await importarOriginal<typeof import('../api/catalogos')>()
  return {
    ...real,
    clienteDeCatalogo: (...args: unknown[]) => ({
      ...(real.clienteDeCatalogo as (...a: unknown[]) => object)(...args),
      listar: (...a: unknown[]) => listarCatalogoMock(...a),
    }),
  }
})

const CLIENTE: ClienteListado = {
  id: 2,
  numero: 2,
  nombre: 'Juan',
  apellido: 'Pérez',
  razonSocial: null,
  tipoDocumento: null,
  numeroDocumento: null,
  idCondicionFiscal: 1,
  nacimiento: null,
  domicilio: null,
  telefono: null,
  celular: null,
  email: null,
  observaciones: null,
  idListaPrecio: 1,
  limiteCredito: 5000,
  creditoIlimitado: false,
  saldo: 500,
  activo: true,
  idEmpresa: null,
  esConsumidorFinal: false,
}

const CONSUMIDOR_FINAL: ClienteListado = { ...CLIENTE, id: 1, numero: 1, nombre: 'Consumidor Final', apellido: null, esConsumidorFinal: true }

const PUNTO_VENTA = { id: 7, nombre: 'Local Centro' } as PuntoVentaListado

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

const MEDIOS = [medio({}), medio({ id: 2, nombre: 'Tarjeta', comportamiento: 'Electronico', admiteVuelto: false })]

function movimiento(sobrescribir: Partial<MovimientoDeCuentaCorriente> = {}): MovimientoDeCuentaCorriente {
  return {
    id: 1,
    fecha: '2026-09-10T15:00:00Z',
    tipo: 'Consumo',
    importe: 500,
    saldoResultante: 500,
    detalle: null,
    idComprobanteVenta: 30,
    etiqueta: null,
    ...sobrescribir,
  }
}

function estado(saldo = 500, movimientos: MovimientoDeCuentaCorriente[] = [movimiento()]): EstadoDeCuenta {
  return {
    header: { saldo, limiteCredito: 5000, creditoIlimitado: false, disponibilidad: 5000 - saldo },
    movimientos,
    historico: false,
    desde: null,
    hasta: null,
  }
}

function comprobanteDePago(): ComprobanteEmitido {
  return {
    id: 900,
    numero: 900,
    numeroVisible: '0007-00000900',
    estado: 'Emitido',
    fecha: '2026-09-16T15:30:00Z',
    idPuntoVenta: 7,
    idCliente: 2,
    idComprobanteAsociado: null,
    subtotal: 0,
    descuentoTotal: 0,
    descuentoManualTotal: 0,
    recargoManualTotal: 0,
    total: 0,
    direccionEntrega: null,
    observaciones: null,
    items: [],
    pagos: [{ idMedioPago: 1, importe: 300, referencia: null, vuelto: 0 }],
    idPresupuestoOrigen: null,
  }
}

/** Una consulta que el test resuelve a mano, para ordenar las respuestas como quiera. */
function consultaControlable() {
  const pendientes: { resolver: (e: EstadoDeCuenta) => void; rechazar: (e: Error) => void }[] = []
  obtenerEstadoMock.mockImplementation(
    () =>
      new Promise<EstadoDeCuenta>((resolver, rechazar) => {
        pendientes.push({ resolver, rechazar })
      }),
  )
  return pendientes
}

function montar(props: Partial<Parameters<typeof ModalCuentaCorrientePos>[0]> = {}) {
  const onCerrar = vi.fn()
  const onPagoRegistrado = vi.fn()
  const onReciboListo = vi.fn<(pago: PagoACuentaRegistrado) => void>()
  render(
    <>
      <ModalCuentaCorrientePos
        clienteInicial={CLIENTE}
        puntoVenta={PUNTO_VENTA}
        medios={MEDIOS}
        onCerrar={onCerrar}
        onPagoRegistrado={onPagoRegistrado}
        onReciboListo={onReciboListo}
        {...props}
      />
      <Toaster />
    </>,
  )
  return { onCerrar, onPagoRegistrado, onReciboListo }
}

const dialogo = () => within(screen.getByRole('dialog', { name: 'Cuenta corriente' }))

async function escribirFecha(etiqueta: 'Desde' | 'Hasta', texto: string) {
  const campo = dialogo().getByLabelText(etiqueta)
  await userEvent.clear(campo)
  await userEvent.type(campo, texto)
}

/** Miércoles 16/09/2026: los últimos siete días son del 09/09 al 16/09. Solo se congela `Date`, el resto de los relojes corre normal. */

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['Date'] })
  vi.setSystemTime(new Date(2026, 8, 16, 12, 0, 0))
  obtenerEstadoMock.mockReset()
  registrarPagoMock.mockReset()
  clienteListarMock.mockReset()
  obtenerVentaMock.mockReset()
  listarCatalogoMock.mockReset()
  obtenerEstadoMock.mockResolvedValue(estado())
})

afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

describe('ModalCuentaCorrientePos — forma del diálogo', () => {
  it('ocupa como mucho el alto de la ventana: el cuerpo se desplaza por dentro', async () => {
    montar()
    await dialogo().findByText('Movimientos')
    expect(document.querySelector('.modal-dialog')).toHaveClass('modal-dialog-scrollable')
  })

  it('no tiene botón "Cerrar" ni pie: se cierra con la × o con Escape', async () => {
    const { onCerrar } = montar()
    await dialogo().findByText('Movimientos')

    expect(dialogo().getAllByRole('button', { name: 'Cerrar' })).toHaveLength(1)
    expect(document.querySelector('.modal-footer')).toBeNull()

    await userEvent.click(dialogo().getByRole('button', { name: 'Cerrar' }))
    expect(onCerrar).toHaveBeenCalledTimes(1)
    fireEvent.keyDown(document, { key: 'Escape' })
    expect(onCerrar).toHaveBeenCalledTimes(2)
  })

  it('"Registrar pago" queda arriba de los saldos y de los movimientos', async () => {
    montar()
    const etiquetaDeSaldo = await dialogo().findByText('Saldo', { selector: '.small' })
    const boton = dialogo().getByRole('button', { name: 'Registrar pago' })
    const siguiente = (nodo: HTMLElement) => boton.compareDocumentPosition(nodo) & Node.DOCUMENT_POSITION_FOLLOWING
    expect(siguiente(etiquetaDeSaldo)).toBeTruthy()
    expect(siguiente(dialogo().getByText('Movimientos'))).toBeTruthy()
  })

  it('"Registrar pago" se habilita recién con el estado de cuenta cargado', async () => {
    const pendientes = consultaControlable()
    montar()
    expect(dialogo().getByRole('button', { name: 'Registrar pago' })).toBeDisabled()

    pendientes[0].resolver(estado())
    await waitFor(() => expect(dialogo().getByRole('button', { name: 'Registrar pago' })).toBeEnabled())
  })

  it('con el Consumidor Final arranca en el buscador, con "Registrar pago" deshabilitado y sin consultar', async () => {
    montar({ clienteInicial: CONSUMIDOR_FINAL })
    expect(dialogo().getByLabelText('Buscar cliente de cuenta corriente')).toBeInTheDocument()
    expect(dialogo().getByRole('button', { name: 'Registrar pago' })).toBeDisabled()
    expect(obtenerEstadoMock).not.toHaveBeenCalled()
  })

  it('"Cambiar cliente" vuelve al buscador y elegir otro cliente consulta el suyo', async () => {
    clienteListarMock.mockResolvedValue({
      items: [CONSUMIDOR_FINAL, { ...CLIENTE, id: 5, numero: 5, nombre: 'Ana', apellido: 'Gómez' }],
      total: 2,
      pagina: 1,
      tamanio: 25,
    })
    montar()
    await dialogo().findByText('Movimientos')

    await userEvent.click(dialogo().getByRole('button', { name: 'Cambiar cliente' }))
    await userEvent.type(dialogo().getByLabelText('Buscar cliente de cuenta corriente'), 'ana{Enter}')
    expect(dialogo().queryByRole('option', { name: /Consumidor/ })).not.toBeInTheDocument()
    await userEvent.selectOptions(await dialogo().findByLabelText('Resultados de la búsqueda'), '5')

    await dialogo().findByText('#5 — Ana Gómez')
    await waitFor(() => expect(obtenerEstadoMock.mock.calls.at(-1)?.[0]).toBe(5))
  })
})

describe('ModalCuentaCorrientePos — rango de fechas', () => {
  it('arranca con los últimos siete días y consulta ese rango', async () => {
    montar()

    await dialogo().findByText('Movimientos')
    expect(obtenerEstadoMock).toHaveBeenCalledTimes(1)
    expect(obtenerEstadoMock).toHaveBeenCalledWith(2, '2026-09-09', '2026-09-16', false)
    expect(dialogo().getByLabelText('Desde')).toHaveValue('09/09/2026')
    expect(dialogo().getByLabelText('Hasta')).toHaveValue('16/09/2026')
  })

  it('el título de la sección ya no dice "recientes"', async () => {
    montar()
    await dialogo().findByText('Movimientos')
    expect(dialogo().queryByText(/recientes/)).not.toBeInTheDocument()
  })

  it('cambiar una fecha vuelve a consultar con el rango nuevo', async () => {
    montar()
    await dialogo().findByText('Movimientos')
    await escribirFecha('Desde', '01/01/2020')

    await waitFor(() => expect(obtenerEstadoMock).toHaveBeenLastCalledWith(2, '2020-01-01', '2026-09-16', false))
  })

  it('una respuesta vieja que llega después de la del rango nuevo no la pisa', async () => {
    const pendientes = consultaControlable()
    montar()
    await escribirFecha('Desde', '01/01/2020')
    await waitFor(() => expect(pendientes).toHaveLength(2))

    pendientes[1].resolver(estado(500, [movimiento({ id: 11, importe: 111 })]))
    await dialogo().findByText('$ 111,00')
    pendientes[0].resolver(estado(500, [movimiento({ id: 12, importe: 222 })]))

    await waitFor(() => expect(obtenerEstadoMock).toHaveBeenCalledTimes(2))
    expect(dialogo().getByText('$ 111,00')).toBeInTheDocument()
    expect(dialogo().queryByText('$ 222,00')).not.toBeInTheDocument()
  })

  it('con Desde posterior a Hasta no consulta y dice por qué; al corregirlo consulta de nuevo', async () => {
    montar()
    await dialogo().findByText('Movimientos')
    expect(obtenerEstadoMock).toHaveBeenCalledTimes(1)

    await escribirFecha('Desde', '31/12/2099')

    expect(await dialogo().findByText('La fecha Desde no puede ser posterior a Hasta.')).toBeInTheDocument()
    expect(obtenerEstadoMock).toHaveBeenCalledTimes(1)
    expect(dialogo().queryByRole('table')).not.toBeInTheDocument()

    await escribirFecha('Desde', '01/01/2020')
    await waitFor(() => expect(obtenerEstadoMock).toHaveBeenCalledTimes(2))
    expect(dialogo().queryByText(/no puede ser posterior/)).not.toBeInTheDocument()
  })

  it('con una fecha vacía no consulta y pide completar las dos', async () => {
    montar()
    await dialogo().findByText('Movimientos')

    await userEvent.clear(dialogo().getByLabelText('Hasta'))

    expect(await dialogo().findByText('Completá las fechas Desde y Hasta para ver los movimientos.')).toBeInTheDocument()
    expect(obtenerEstadoMock).toHaveBeenCalledTimes(1)
  })

  it('con un rango inválido, cambiar de cliente no deja ver el saldo del cliente anterior', async () => {
    clienteListarMock.mockResolvedValue({ items: [{ ...CLIENTE, id: 5, numero: 5, nombre: 'Ana', apellido: 'Gómez' }], total: 1, pagina: 1, tamanio: 25 })
    montar()
    await dialogo().findByText('$ 500,00', { selector: '.fs-5' })
    await escribirFecha('Desde', '31/12/2099')
    await dialogo().findByText('La fecha Desde no puede ser posterior a Hasta.')

    await userEvent.click(dialogo().getByRole('button', { name: 'Cambiar cliente' }))
    await userEvent.type(dialogo().getByLabelText('Buscar cliente de cuenta corriente'), 'ana{Enter}')
    await userEvent.selectOptions(await dialogo().findByLabelText('Resultados de la búsqueda'), '5')

    await dialogo().findByText('#5 — Ana Gómez')
    expect(dialogo().queryByText('$ 500,00', { selector: '.fs-5' })).not.toBeInTheDocument()
    expect(dialogo().getByRole('button', { name: 'Registrar pago' })).toBeDisabled()
  })

  it('un rango inválido descarta la respuesta que seguía en vuelo', async () => {
    const pendientes = consultaControlable()
    montar()
    await escribirFecha('Desde', '31/12/2099')
    await dialogo().findByText('La fecha Desde no puede ser posterior a Hasta.')

    pendientes[0].resolver(estado(999, [movimiento({ importe: 333 })]))

    await waitFor(() => expect(dialogo().queryByText('Cargando estado de cuenta…')).not.toBeInTheDocument())
    expect(dialogo().queryByText('$ 999,00', { selector: '.fs-5' })).not.toBeInTheDocument()
  })

  it('sin movimientos, el texto nombra el rango consultado', async () => {
    obtenerEstadoMock.mockResolvedValue(estado(0, []))
    montar()

    expect(await dialogo().findByText('Sin movimientos entre el 09/09/2026 y el 16/09/2026.')).toBeInTheDocument()
  })

  it('el texto sin movimientos sigue el rango ya consultado, no el que se está editando', async () => {
    obtenerEstadoMock.mockResolvedValue(estado(0, []))
    montar()
    await dialogo().findByText(/Sin movimientos entre/)

    const pendientes = consultaControlable()
    await escribirFecha('Desde', '01/01/2020')
    await waitFor(() => expect(pendientes).toHaveLength(1))
    pendientes[0].resolver(estado(0, []))

    expect(await dialogo().findByText('Sin movimientos entre el 01/01/2020 y el 16/09/2026.')).toBeInTheDocument()
  })
})

describe('ModalCuentaCorrientePos — pago a cuenta y recibo', () => {
  async function registrarPagoDe300() {
    await dialogo().findByText('Movimientos')
    await userEvent.click(dialogo().getByRole('button', { name: 'Registrar pago' }))
    const pago = within(await screen.findByRole('dialog'))
    await userEvent.selectOptions(pago.getByLabelText('Medio de pago'), 'Efectivo')
    await userEvent.type(pago.getByLabelText('Importe'), '300')
    await waitFor(() => expect(pago.getByRole('button', { name: 'Registrar pago' })).toBeEnabled())
    await userEvent.click(pago.getByRole('button', { name: 'Registrar pago' }))
  }

  it('entrega el recibo con el saldo que informó el servidor tras el pago y el que había en pantalla', async () => {
    registrarPagoMock.mockResolvedValue(comprobanteDePago())
    obtenerEstadoMock.mockReset()
    obtenerEstadoMock.mockResolvedValueOnce(estado(500)).mockResolvedValueOnce(estado(150))
    const { onReciboListo, onPagoRegistrado } = montar()

    await registrarPagoDe300()

    await waitFor(() => expect(onReciboListo).toHaveBeenCalledTimes(1))
    expect(onReciboListo).toHaveBeenCalledWith({ comprobante: comprobanteDePago(), cliente: CLIENTE, saldoEnPantalla: 500, saldoPosterior: 150 })
    expect(onPagoRegistrado).toHaveBeenCalledWith(2)
    expect(await dialogo().findByText('$ 150,00', { selector: '.fs-5' })).toBeInTheDocument()
  })

  it('si la consulta posterior falla entrega el recibo sin saldo posterior, y el pago sigue informado como registrado', async () => {
    registrarPagoMock.mockResolvedValue(comprobanteDePago())
    obtenerEstadoMock.mockReset()
    obtenerEstadoMock.mockResolvedValueOnce(estado(500)).mockRejectedValueOnce(new Error('sin red'))
    const { onReciboListo } = montar()

    await registrarPagoDe300()

    await waitFor(() => expect(onReciboListo).toHaveBeenCalledTimes(1))
    expect(onReciboListo.mock.calls[0][0]).toMatchObject({ saldoEnPantalla: 500, saldoPosterior: null })
    const aviso = await screen.findByText('Pago registrado: comprobante 0007-00000900.')
    expect(aviso.closest('[role="status"]')).not.toBeNull()
    expect(document.body.querySelector('.alert-success')).toBeNull()
  })

  it('el recibo sale una sola vez aunque la consulta posterior quede superada por un cambio de rango', async () => {
    registrarPagoMock.mockResolvedValue(comprobanteDePago())
    const pendientes = consultaControlable()
    obtenerEstadoMock.mockResolvedValueOnce(estado(500))
    const { onReciboListo } = montar()
    await dialogo().findByText('Movimientos')

    await userEvent.click(dialogo().getByRole('button', { name: 'Registrar pago' }))
    const pago = within(await screen.findByRole('dialog'))
    await userEvent.selectOptions(pago.getByLabelText('Medio de pago'), 'Efectivo')
    await userEvent.type(pago.getByLabelText('Importe'), '300')
    await userEvent.click(pago.getByRole('button', { name: 'Registrar pago' }))
    await waitFor(() => expect(pendientes).toHaveLength(1))

    await escribirFecha('Desde', '01/01/2020')
    await waitFor(() => expect(pendientes).toHaveLength(2))
    pendientes[1].resolver(estado(200))
    await dialogo().findByText('$ 200,00', { selector: '.fs-5' })
    pendientes[0].resolver(estado(150))

    await waitFor(() => expect(onReciboListo).toHaveBeenCalledTimes(1))
    expect(onReciboListo.mock.calls[0][0].saldoPosterior).toBe(150)
    expect(dialogo().getByText('$ 200,00', { selector: '.fs-5' })).toBeInTheDocument()
  })

  it('tras el pago vuelve al rango por defecto para que el pago aparezca entre los movimientos', async () => {
    registrarPagoMock.mockResolvedValue(comprobanteDePago())
    montar()
    await dialogo().findByText('Movimientos')
    await escribirFecha('Desde', '01/01/2020')
    await waitFor(() => expect(obtenerEstadoMock).toHaveBeenCalledTimes(2))

    await registrarPagoDe300()

    await waitFor(() => expect(obtenerEstadoMock).toHaveBeenLastCalledWith(2, '2026-09-09', '2026-09-16', false))
    expect(obtenerEstadoMock).toHaveBeenCalledTimes(3)
    expect(dialogo().getByLabelText('Desde')).toHaveValue('09/09/2026')
  })
})

describe('ModalCuentaCorrientePos — el recibo no espera para siempre', () => {
  /** Además de `Date`, se congelan `setTimeout`/`clearTimeout` para mandar el reloj del recibo; el
   * tipeo no usa temporizadores (`delay: null`) y los `waitFor` siguen sondeando con `setInterval`. */
  async function pagarConRelojFalso() {
    vi.useFakeTimers({ toFake: ['Date', 'setTimeout', 'clearTimeout'] })
    vi.setSystemTime(new Date(2026, 8, 16, 12, 0, 0))
    // Testing Library detecta los relojes falsos por el global `jest`: sin él, esperar a un `findBy` cuelga.
    vi.stubGlobal('jest', { advanceTimersByTime: vi.advanceTimersByTime })
    const usuario = userEvent.setup({ delay: null })
    // Instantes del reloj falso: los `waitFor` también lo avanzan, así que se mide en vez de contar milisegundos.
    const instantes: { pago?: number; recibo?: number } = {}
    registrarPagoMock.mockImplementation(async () => {
      instantes.pago = Date.now()
      return comprobanteDePago()
    })
    const siguientes: { resolver: (e: EstadoDeCuenta) => void; rechazar: (e: Error) => void }[] = []
    obtenerEstadoMock.mockReset()
    obtenerEstadoMock.mockResolvedValueOnce(estado(500))
    obtenerEstadoMock.mockImplementation(
      () =>
        new Promise<EstadoDeCuenta>((resolver, rechazar) => {
          siguientes.push({ resolver, rechazar })
        }),
    )
    const modal = montar()
    modal.onReciboListo.mockImplementation(() => {
      instantes.recibo = Date.now()
    })
    await waitFor(() => expect(dialogo().getByRole('button', { name: 'Registrar pago' })).toBeEnabled())
    await usuario.click(dialogo().getByRole('button', { name: 'Registrar pago' }))
    await screen.findByRole('heading', { name: 'Ingresar pago a cuenta' })
    const pago = within(screen.getAllByRole('dialog').at(-1)!)
    await usuario.selectOptions(pago.getByLabelText('Medio de pago'), 'Efectivo')
    await usuario.type(pago.getByLabelText('Importe'), '300')
    await usuario.click(pago.getByRole('button', { name: 'Registrar pago' }))
    await waitFor(() => expect(siguientes).toHaveLength(1))
    return { ...modal, siguientes, instantes }
  }

  it('consulta rápida: un único recibo con el saldo posterior, y el vencimiento posterior no imprime otro', async () => {
    const { onReciboListo, siguientes } = await pagarConRelojFalso()

    await act(async () => siguientes[0].resolver(estado(150)))
    expect(onReciboListo).toHaveBeenCalledTimes(1)
    expect(onReciboListo.mock.calls[0][0].saldoPosterior).toBe(150)

    await act(() => vi.advanceTimersByTimeAsync(ESPERA_MAXIMA_DEL_RECIBO_MS * 2))
    expect(onReciboListo).toHaveBeenCalledTimes(1)
  })

  it('consulta lenta: al vencer la espera sale un único recibo sin saldo posterior; la respuesta tardía actualiza la pantalla pero no imprime otro', async () => {
    const { onReciboListo, siguientes, instantes } = await pagarConRelojFalso()

    await act(() => vi.advanceTimersByTimeAsync(ESPERA_MAXIMA_DEL_RECIBO_MS))
    expect(onReciboListo).toHaveBeenCalledTimes(1)
    expect(instantes.recibo! - instantes.pago!).toBeGreaterThanOrEqual(ESPERA_MAXIMA_DEL_RECIBO_MS)
    expect(onReciboListo.mock.calls[0][0]).toMatchObject({ saldoEnPantalla: 500, saldoPosterior: null })

    await act(async () => siguientes[0].resolver(estado(150)))
    expect(await dialogo().findByText('$ 150,00', { selector: '.fs-5' })).toBeInTheDocument()
    await act(() => vi.advanceTimersByTimeAsync(ESPERA_MAXIMA_DEL_RECIBO_MS * 2))
    expect(onReciboListo).toHaveBeenCalledTimes(1)
  })

  it('consulta fallida: un único recibo sin saldo posterior, y el vencimiento posterior no imprime otro', async () => {
    const { onReciboListo, siguientes } = await pagarConRelojFalso()

    await act(async () => siguientes[0].rechazar(new Error('sin red')))
    expect(onReciboListo).toHaveBeenCalledTimes(1)
    expect(onReciboListo.mock.calls[0][0].saldoPosterior).toBeNull()

    await act(() => vi.advanceTimersByTimeAsync(ESPERA_MAXIMA_DEL_RECIBO_MS * 2))
    expect(onReciboListo).toHaveBeenCalledTimes(1)
  })
})

describe('ModalCuentaCorrientePos — reintentar tras un error de carga', () => {
  it('con la carga inicial fallida muestra "Reintentar", que recarga el mismo cliente y rango y habilita "Registrar pago"', async () => {
    obtenerEstadoMock.mockReset()
    obtenerEstadoMock.mockRejectedValueOnce(new Error('sin red')).mockResolvedValueOnce(estado(500))
    montar()

    expect(await dialogo().findByText('No se pudo cargar el estado de cuenta.')).toBeInTheDocument()
    expect(dialogo().getByRole('button', { name: 'Registrar pago' })).toBeDisabled()

    await userEvent.click(dialogo().getByRole('button', { name: 'Reintentar' }))

    expect(await dialogo().findByText('$ 500,00', { selector: '.fs-5' })).toBeInTheDocument()
    expect(obtenerEstadoMock).toHaveBeenLastCalledWith(2, '2026-09-09', '2026-09-16', false)
    expect(obtenerEstadoMock).toHaveBeenCalledTimes(2)
    expect(dialogo().getByRole('button', { name: 'Registrar pago' })).toBeEnabled()
    expect(dialogo().queryByRole('button', { name: 'Reintentar' })).not.toBeInTheDocument()
  })

  it('tras un pago cuya consulta posterior falla, "Reintentar" recupera el saldo nuevo', async () => {
    registrarPagoMock.mockResolvedValue(comprobanteDePago())
    obtenerEstadoMock.mockReset()
    obtenerEstadoMock.mockResolvedValueOnce(estado(500)).mockRejectedValueOnce(new Error('sin red')).mockResolvedValueOnce(estado(200))
    montar()
    await dialogo().findByText('Movimientos')
    await userEvent.click(dialogo().getByRole('button', { name: 'Registrar pago' }))
    const pago = within(await screen.findByRole('dialog'))
    await userEvent.selectOptions(pago.getByLabelText('Medio de pago'), 'Efectivo')
    await userEvent.type(pago.getByLabelText('Importe'), '300')
    await userEvent.click(pago.getByRole('button', { name: 'Registrar pago' }))

    await dialogo().findByRole('button', { name: 'Reintentar' })
    expect(dialogo().getByRole('button', { name: 'Registrar pago' })).toBeDisabled()
    await userEvent.click(dialogo().getByRole('button', { name: 'Reintentar' }))

    expect(await dialogo().findByText('$ 200,00', { selector: '.fs-5' })).toBeInTheDocument()
    expect(dialogo().getByRole('button', { name: 'Registrar pago' })).toBeEnabled()
  })
})

describe('ModalCuentaCorrientePos — orden de los movimientos', () => {
  const MOVIMIENTOS = [
    movimiento({ id: 1, fecha: '2026-09-10T15:00:00Z', importe: 5000, saldoResultante: 5000 }),
    movimiento({ id: 2, fecha: '2026-09-12T15:00:00Z', tipo: 'Pago', importe: -20, saldoResultante: 4980 }),
    movimiento({ id: 3, fecha: '2026-09-11T15:00:00Z', importe: 100, saldoResultante: 5080 }),
  ]

  const filas = () =>
    Array.from(dialogo().getByRole('table').querySelectorAll('tbody tr')).map((fila) =>
      Array.from(fila.querySelectorAll('td')).map((c) => c.textContent),
    )

  it('conserva el orden del servidor hasta elegir una columna; el importe ordena por valor y cada fila conserva su saldo', async () => {
    obtenerEstadoMock.mockResolvedValue(estado(5080, MOVIMIENTOS))
    montar()
    await dialogo().findByRole('table')

    expect(filas().map((f) => f[3])).toEqual(['$ 5.000,00', '$ 4.980,00', '$ 5.080,00'])

    const importe = dialogo().getByRole('columnheader', { name: /Importe/ })
    fireEvent.click(within(importe).getByRole('button'))

    expect(importe).toHaveAttribute('aria-sort', 'ascending')
    expect(filas().map((f) => [f[2], f[3]])).toEqual([
      ['-$ 20,00', '$ 4.980,00'],
      ['$ 100,00', '$ 5.080,00'],
      ['$ 5.000,00', '$ 5.000,00'],
    ])
  })

  it('la fecha ordena cronológicamente y el tercer clic vuelve al orden del servidor', async () => {
    obtenerEstadoMock.mockResolvedValue(estado(5080, MOVIMIENTOS))
    montar()
    await dialogo().findByRole('table')

    const fecha = dialogo().getByRole('columnheader', { name: /Fecha/ })
    fireEvent.click(within(fecha).getByRole('button'))
    fireEvent.click(within(fecha).getByRole('button'))
    expect(fecha).toHaveAttribute('aria-sort', 'descending')
    expect(filas().map((f) => f[3])).toEqual(['$ 4.980,00', '$ 5.080,00', '$ 5.000,00'])

    fireEvent.click(within(fecha).getByRole('button'))
    expect(fecha).toHaveAttribute('aria-sort', 'none')
    expect(filas().map((f) => f[3])).toEqual(['$ 5.000,00', '$ 4.980,00', '$ 5.080,00'])
  })
})

function ticketDeConsumo(): ComprobanteEmitido {
  return {
    ...comprobanteDePago(),
    id: 30,
    numero: 30,
    numeroVisible: '0007-00000030',
    fecha: '2026-09-10T15:00:00Z',
    subtotal: 500,
    total: 500,
    items: [
      {
        orden: 1,
        idArticulo: 12,
        descripcion: 'Yerba 1 kg',
        codigoBarra: null,
        idArea: 1,
        idListaPrecio: 1,
        idOferta: null,
        idAlicuotaIva: 1,
        porcentajeIva: 21,
        cantidad: 2,
        precioUnitario: 250,
        descuento: 0,
        total: 500,
        idLote: null,
        codigoLote: null,
        loteVencido: false,
        ajusteManualPorcentaje: null,
        ajusteManual: 0,
      },
    ],
    pagos: [{ idMedioPago: 2, importe: 500, referencia: null, vuelto: 0 }],
  }
}

describe('ModalCuentaCorrientePos — detalle del comprobante de un movimiento', () => {
  const BOTON_DEL_CONSUMO = /Ver el comprobante de Consumo del/

  it('abre el ticket del movimiento con sus líneas, cantidades, importes y medios, sin reimprimir', async () => {
    obtenerVentaMock.mockResolvedValue(ticketDeConsumo())
    const usuario = userEvent.setup()
    montar()

    await usuario.click(await dialogo().findByRole('button', { name: BOTON_DEL_CONSUMO }))

    const detalle = within(await screen.findByRole('dialog', { name: 'Detalle de la venta 0007-00000030' }))
    expect(obtenerVentaMock).toHaveBeenCalledWith(30)
    const linea = (await detalle.findByText('Yerba 1 kg')).closest('tr') as HTMLElement
    expect(Array.from(linea.querySelectorAll('td')).map((c) => c.textContent)).toEqual(['Yerba 1 kg', '2', '$ 250,00', '$ 0,00', '—', '$ 500,00'])
    expect(detalle.getByText('Tarjeta')).toBeInTheDocument()
    expect(detalle.getByText('Juan Pérez')).toBeInTheDocument()
    expect(detalle.queryByRole('button', { name: /Imprimir/ })).not.toBeInTheDocument()
  })

  it('nombra los medios con los del POS, sin pedir el catálogo', async () => {
    obtenerVentaMock.mockResolvedValue(ticketDeConsumo())
    const usuario = userEvent.setup()
    montar()

    await usuario.click(await dialogo().findByRole('button', { name: BOTON_DEL_CONSUMO }))
    await screen.findByRole('dialog', { name: 'Detalle de la venta 0007-00000030' })

    expect(listarCatalogoMock).not.toHaveBeenCalled()
  })

  it('cerrar el detalle vuelve a la cuenta corriente con el foco en el botón que lo abrió', async () => {
    obtenerVentaMock.mockResolvedValue(ticketDeConsumo())
    const usuario = userEvent.setup()
    montar()

    const boton = await dialogo().findByRole('button', { name: BOTON_DEL_CONSUMO })
    await usuario.click(boton)
    const detalle = within(await screen.findByRole('dialog', { name: 'Detalle de la venta 0007-00000030' }))
    await detalle.findByText('Yerba 1 kg')
    await usuario.click(detalle.getByRole('button', { name: 'Cerrar' }))

    await waitFor(() => expect(screen.queryByRole('dialog', { name: /Detalle de la venta/ })).not.toBeInTheDocument())
    expect(screen.getByRole('dialog', { name: 'Cuenta corriente' })).toBeInTheDocument()
    await waitFor(() => expect(boton).toHaveFocus())
  })

  it('un movimiento sin comprobante no ofrece detalle', async () => {
    obtenerEstadoMock.mockResolvedValue(
      estado(500, [movimiento(), movimiento({ id: 2, tipo: 'Ajuste', idComprobanteVenta: null, etiqueta: 'Manual' })]),
    )
    montar()
    await dialogo().findByRole('button', { name: BOTON_DEL_CONSUMO })

    expect(dialogo().getAllByRole('button', { name: /Ver el comprobante/ })).toHaveLength(1)
    expect(dialogo().queryByRole('button', { name: /Ver el comprobante de Ajuste manual/ })).not.toBeInTheDocument()
  })

  it('si el comprobante no se puede leer, el detalle lo dice', async () => {
    obtenerVentaMock.mockRejectedValue(new Error('sin red'))
    const usuario = userEvent.setup()
    montar()

    await usuario.click(await dialogo().findByRole('button', { name: BOTON_DEL_CONSUMO }))

    const detalle = within(await screen.findByRole('dialog', { name: 'Detalle de la venta' }))
    expect(await detalle.findByText('No se pudo cargar el detalle de la venta.')).toBeInTheDocument()
  })
})
