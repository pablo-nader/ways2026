import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useNavigate } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { CajaZ } from './CajaZ'
import { RutaProtegida } from '../auth/RutaProtegida'
import { cierreDeTurno } from '../impresion/plantillas'
import { ROL } from '../api/tipos'
import type { ComprobanteEmitido, DetalleDeTurno, ResumenDeCierrePorRetiro, ResumenDeTurno, TurnoResumen, UsuarioAutenticado } from '../api/tipos'

const apiGetMock = vi.fn()
const apiDescargarMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: vi.fn(),
    put: vi.fn(),
    delete: vi.fn(),
    descargar: (...args: unknown[]) => apiDescargarMock(...(args as [string])),
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

function usuarioFixture(sobrescribir: Partial<UsuarioAutenticado> = {}): UsuarioAutenticado {
  return {
    id: 4,
    usuario: 'vendedor',
    mail: 'vendedor@ways.test',
    rolId: ROL.Vendedor,
    rol: 'Vendedor',
    ultimaConexion: null,
    idTenant: 1,
    ...sobrescribir,
  }
}

let usuarioActual: UsuarioAutenticado | null = usuarioFixture()

vi.mock('../auth/useAuth', () => ({
  useAuth: () => ({ usuario: usuarioActual, cargando: false, iniciarSesion: vi.fn(), cerrarSesion: vi.fn() }),
}))

// stage-desktop-pos: mismo mock que CierreDeCaja.test.tsx — controla `enEscritorio()` y observa
// los bytes que recibe `imprimir`.
const imprimirMock = vi.fn()
let escritorioMock = false

vi.mock('../impresion/impresora', () => ({
  enEscritorio: () => escritorioMock,
  imprimir: (...args: unknown[]) => imprimirMock(...args),
}))

function resumenFixture(sobrescribir: Partial<ResumenDeTurno> = {}): ResumenDeTurno {
  return {
    idTurnoCaja: 412,
    idMedioAncla: 1,
    medios: [
      { idMedioPago: 1, importeEsperado: 1000, nombre: 'Efectivo' },
      { idMedioPago: 2, importeEsperado: 500, nombre: 'Tarjeta' },
    ],
    cantidadTickets: 2,
    primerTicket: { numero: 1, fecha: '2026-08-05T08:30:00Z', codigo: 'TX' },
    ultimoTicket: { numero: 2, fecha: '2026-08-05T19:30:00Z', codigo: 'TX' },
    ingresosPorArea: [],
    egresos: { porCategoria: [], porArea: [], retiros: 100 },
    ...sobrescribir,
  }
}

function detalleFixture(sobrescribir: Partial<DetalleDeTurno> = {}): DetalleDeTurno {
  return {
    resumen: resumenFixture(),
    tickets: [
      { id: 1, numero: 1, numeroVisible: '0003-00000001', estado: 'Emitido', fecha: '2026-08-05T08:30:00Z', idCliente: 1, nombreCliente: 'Cliente 1', total: 750, descuentoManualTotal: 0, recargoManualTotal: 0, mediosDePago: [], estadoNotaDeCredito: null },
      { id: 2, numero: 2, numeroVisible: '0003-00000002', estado: 'Anulado', fecha: '2026-08-05T19:30:00Z', idCliente: 2, nombreCliente: 'Cliente 2', total: 250, descuentoManualTotal: 0, recargoManualTotal: 0, mediosDePago: [], estadoNotaDeCredito: null },
    ],
    gastos: [
      { id: 1, idPuntoVenta: 10, fecha: '2026-08-05T09:00:00Z', categoria: 'Sueldos', idMedioPago: 1, importe: 300, origenFondos: 'CajaTurno', idTurnoCaja: 55, turnoAbierto: true, idProveedor: null, idArea: null, concepto: 'Pago', detalle: null, numeroFactura: null, idComprobanteCompra: null, idMovimientoPersonal: null },
      { id: 2, idPuntoVenta: 10, fecha: '2026-08-05T10:00:00Z', categoria: 'Viaticos', idMedioPago: 2, importe: 120, origenFondos: 'Tesoreria', idTurnoCaja: 55, turnoAbierto: true, idProveedor: null, idArea: null, concepto: 'Pago', detalle: null, numeroFactura: null, idComprobanteCompra: null, idMovimientoPersonal: null },
    ],
    fechaRecalculo: null,
    idEmpleadoRecalculo: null,
    ...sobrescribir,
  }
}

function renderCajaZ(idTurno = 412) {
  return render(<CajaZ />, {
    wrapper: ({ children }) => (
      <MemoryRouter initialEntries={[`/caja/turnos/${idTurno}/z`]}>
        <Routes>
          <Route path="/caja/turnos/:id/z" element={children} />
        </Routes>
      </MemoryRouter>
    ),
  })
}

/** Monta detrás del mismo gate de rol que `App.tsx` usa para `/caja/turnos/:id/z`
 * (`Politicas.OperacionDePos`). */
function renderCajaZProtegido(idTurno = 412) {
  return render(
    <MemoryRouter initialEntries={[`/caja/turnos/${idTurno}/z`]}>
      <Routes>
        <Route
          path="/caja/turnos/:id/z"
          element={
            <RutaProtegida rolesPermitidos={[ROL.Vendedor, ROL.Supervisor, ROL.Admin]}>
              <CajaZ />
            </RutaProtegida>
          }
        />
        <Route path="/" element={<div>Inicio (redirigido)</div>} />
      </Routes>
    </MemoryRouter>,
  )
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiDescargarMock.mockReset()
  apiDescargarMock.mockResolvedValue(undefined)
  usuarioActual = usuarioFixture()
  imprimirMock.mockReset()
  imprimirMock.mockResolvedValue({ ok: true })
  escritorioMock = false
})

describe('CajaZ — detalle del turno (stage-11-exportacion-reportes, Slice 6b)', () => {
  it('renderiza el resumen (tickets/primer/último/retiros) y cada fila de medios/tickets/pagos', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/412/detalle') return Promise.resolve(detalleFixture())
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    renderCajaZ()

    expect(await screen.findByText('Caja Z — turno #412')).toBeInTheDocument()
    expect(screen.getByText('2')).toBeInTheDocument() // cantidadTickets
    expect(screen.getByText('TX #1')).toBeInTheDocument() // primerTicket
    expect(screen.getByText('TX #2')).toBeInTheDocument() // ultimoTicket
    expect(screen.getByText('$ 100,00')).toBeInTheDocument() // retiros

    // mutation-proof-tests rule 6: dos filas con valores DISTINTOS por columna.
    const filaMedioUno = screen.getByRole('row', { name: /Efectivo/ })
    expect(within(filaMedioUno).getByText('$ 1.000,00')).toBeInTheDocument()
    const filaMedioDos = screen.getByRole('row', { name: /Tarjeta/ })
    expect(within(filaMedioDos).getByText('$ 500,00')).toBeInTheDocument()

    const filaTicketUno = screen.getByRole('row', { name: /0003-00000001/ })
    expect(within(filaTicketUno).getByText('Emitida')).toBeInTheDocument()
    expect(within(filaTicketUno).getByText('$ 750,00')).toBeInTheDocument()
    const filaTicketDos = screen.getByRole('row', { name: /0003-00000002/ })
    expect(within(filaTicketDos).getByText('Anulada')).toBeInTheDocument()
    expect(within(filaTicketDos).getByText('$ 250,00')).toBeInTheDocument()

    const filaPagoUno = screen.getByRole('row', { name: /Sueldos/ })
    expect(within(filaPagoUno).getByText('$ 300,00')).toBeInTheDocument()
    // origenFondos CajaTurno: sin badge "Caja general".
    expect(within(filaPagoUno).queryByText('Caja general')).not.toBeInTheDocument()

    const filaPagoDos = screen.getByRole('row', { name: /Viaticos/ })
    expect(within(filaPagoDos).getByText('$ 120,00')).toBeInTheDocument()
    // origenFondos Tesoreria: la fila lleva el badge "Caja general".
    expect(within(filaPagoDos).getByText('Caja general')).toBeInTheDocument()
  })

  it('muestra la marca "Recalculado" solo cuando el turno tiene un recálculo administrativo', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/412/detalle') {
        return Promise.resolve(detalleFixture({ fechaRecalculo: '2026-08-06T10:00:00Z', idEmpleadoRecalculo: 9 }))
      }
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    renderCajaZ()

    expect(await screen.findByText('Recalculado')).toBeInTheDocument()
  })

  it('sin recálculo no muestra la marca "Recalculado"', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/412/detalle') return Promise.resolve(detalleFixture())
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    renderCajaZ()

    // Se espera al dato (la fila de medios) antes de afirmar la ausencia.
    await screen.findByRole('row', { name: /Efectivo/ })
    expect(screen.queryByText('Recalculado')).not.toBeInTheDocument()
  })

  it('sin medios/tickets/pagos muestra los estados vacíos de cada sección', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/412/detalle') {
        return Promise.resolve(detalleFixture({ resumen: resumenFixture({ medios: [] }), tickets: [], gastos: [] }))
      }
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    renderCajaZ()

    expect(await screen.findByText('Este turno no tuvo actividad: no hay ningún medio arqueado.')).toBeInTheDocument()
    expect(screen.getByText('Sin tickets.')).toBeInTheDocument()
    expect(screen.getByText('Sin pagos.')).toBeInTheDocument()
  })

  it('el botón de descarga apunta a /caja/turnos/{id}/detalle/export y limpia el error al reintentar', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/412/detalle') return Promise.resolve(detalleFixture())
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    apiDescargarMock.mockRejectedValueOnce(new Error('no se pudo descargar'))
    const usuario = userEvent.setup()
    renderCajaZ()

    await screen.findByText('Caja Z — turno #412')
    await usuario.click(screen.getByRole('button', { name: 'Descargar' }))

    expect(await screen.findByText('No se pudo descargar el archivo.')).toBeInTheDocument()
    expect(apiDescargarMock).toHaveBeenCalledWith('/caja/turnos/412/detalle/export?formato=xlsx')

    apiDescargarMock.mockResolvedValueOnce(undefined)
    await usuario.click(screen.getByRole('button', { name: 'Descargar' }))
    await waitFor(() => expect(screen.queryByText('No se pudo descargar el archivo.')).not.toBeInTheDocument())
  })

  it('un id de turno inválido en la URL no dispara ningún fetch', () => {
    render(<CajaZ />, {
      wrapper: ({ children }) => (
        <MemoryRouter initialEntries={['/caja/turnos/no-numerico/z']}>
          <Routes>
            <Route path="/caja/turnos/:id/z" element={children} />
          </Routes>
        </MemoryRouter>
      ),
    })

    expect(screen.getByText('No se especificó el turno a mostrar.')).toBeInTheDocument()
    expect(apiGetMock).not.toHaveBeenCalled()
  })

  it('cambiar de turno (misma pantalla montada) descarta una respuesta desactualizada del turno anterior', async () => {
    let resolverPrimera: (valor: DetalleDeTurno) => void = () => {}
    const primera = new Promise<DetalleDeTurno>((resolve) => {
      resolverPrimera = resolve
    })

    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/412/detalle') return primera
      if (ruta === '/caja/turnos/413/detalle') {
        return Promise.resolve(detalleFixture({ resumen: resumenFixture({ idTurnoCaja: 413, cantidadTickets: 7 }) }))
      }
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })

    function ArnesDeNavegacion() {
      const navegar = useNavigate()
      return (
        <button type="button" onClick={() => navegar('/caja/turnos/413/z')}>
          ir al turno 413
        </button>
      )
    }

    render(
      <MemoryRouter initialEntries={['/caja/turnos/412/z']}>
        <Routes>
          <Route
            path="/caja/turnos/:id/z"
            element={
              <>
                <ArnesDeNavegacion />
                <CajaZ />
              </>
            }
          />
        </Routes>
      </MemoryRouter>,
    )

    const usuario = userEvent.setup()
    await screen.findByText('ir al turno 413')
    await usuario.click(screen.getByText('ir al turno 413'))
    expect(await screen.findByText('Caja Z — turno #413')).toBeInTheDocument()

    expect(await screen.findByText('7')).toBeInTheDocument() // cantidadTickets del turno 413

    // La respuesta stale del 412 trae cantidadTickets 99: si pisara el estado, el 7 desaparece.
    // El flush del microtask va DENTRO de act: waitFor solo pasaria en su primer tick,
    // antes de que el .then stale aterrice, y saldria verde sin probar nada.
    await act(async () => {
      resolverPrimera(detalleFixture({ resumen: resumenFixture({ idTurnoCaja: 412, cantidadTickets: 99 }) }))
      await primera
    })
    expect(screen.getByText('7')).toBeInTheDocument()
    expect(screen.queryByText('99')).not.toBeInTheDocument()
  })
})

describe('CajaZ — role gating (spec historico-de-cajas: A Vendedor Downloads Their Own Turno\'s Z-Report)', () => {
  // La API no tiene un límite cross-turno propio: OperacionDePos es un gate solo de rol, sin
  // claim de PV ni de turno (verificado en la Slice 5b) — así que a diferencia de la matriz de
  // 5b.8, esta pantalla solo prueba la mitad de 200 (un Vendedor llega a SU Z). No existe un
  // límite estructural que produzca un 403 cross-turno para probar del lado del cliente.
  it('un Vendedor llega a la Caja Z de su propio turno', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/412/detalle') return Promise.resolve(detalleFixture())
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    renderCajaZProtegido()

    expect(await screen.findByText('Caja Z — turno #412')).toBeInTheDocument()
    expect(screen.queryByText('Inicio (redirigido)')).not.toBeInTheDocument()
  })

  it('un Root nunca llega a /caja/turnos/:id/z: redirige a Inicio', async () => {
    usuarioActual = usuarioFixture({ id: 1, usuario: 'root', rolId: ROL.Root, rol: 'Root', idTenant: null })
    apiGetMock.mockImplementation(() => Promise.reject(new Error('no debería llamarse')))

    renderCajaZProtegido()

    expect(await screen.findByText('Inicio (redirigido)')).toBeInTheDocument()
  })
})

/**
 * Vista de impresión (stage-11-exportacion-reportes, Slice 8, design decisión 13: mismo
 * componente, `@media print`, sin ruta ni fetch dedicados — print rendering en sí es una
 * exención registrada, verificada a ojo). Cubre lo único automatizable: el bloque de impresión
 * muestra quién/cuándo lo generó con el usuario REAL de la sesión, y el chrome que no debe
 * imprimirse (Imprimir, Descargar) lleva `d-print-none`.
 */
describe('CajaZ — vista de impresión (Slice 8)', () => {
  it('el bloque de impresión muestra quién generó la vista con el usuario real de la sesión', async () => {
    usuarioActual = usuarioFixture({ usuario: 'cajero_luis' })
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/412/detalle') return Promise.resolve(detalleFixture())
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    renderCajaZ()

    expect(await screen.findByText(/Generado:.*— cajero_luis/)).toBeInTheDocument()
  })

  it('el botón Imprimir dispara window.print() y tanto Imprimir como Descargar quedan marcados d-print-none', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/412/detalle') return Promise.resolve(detalleFixture())
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    const imprimirSpy = vi.fn()
    window.print = imprimirSpy
    renderCajaZ()

    await screen.findByText('Caja Z — turno #412')

    const botonImprimir = screen.getByRole('button', { name: 'Imprimir' })
    expect(botonImprimir).toHaveClass('d-print-none')
    await userEvent.click(botonImprimir)
    expect(imprimirSpy).toHaveBeenCalledTimes(1)

    expect(screen.getByRole('button', { name: 'Descargar' })).toHaveClass('d-print-none')
  })
})

function turnoFixture(sobrescribir: Partial<TurnoResumen> = {}): TurnoResumen {
  return {
    id: 412,
    idPuntoVenta: 10,
    idEmpleadoApertura: 4,
    idEmpleadoCierre: 4,
    fechaApertura: '2026-08-05T08:00:00Z',
    fechaCierre: '2026-08-05T20:00:00Z',
    fondoInicial: 500,
    estado: 'Cerrado',
    observaciones: null,
    ...sobrescribir,
  }
}

function resumenDeCierreFixture(sobrescribir: Partial<ResumenDeCierrePorRetiro> = {}): ResumenDeCierrePorRetiro {
  return {
    idTurnoCaja: 412,
    puntoVenta: { id: 10, numero: 10, nombre: 'Local Centro' },
    fechaApertura: '2026-08-05T08:00:00Z',
    fechaCierre: '2026-08-05T20:00:00Z',
    vendedor: 'jperez',
    empleadoCierre: 'jperez',
    fondoInicial: 500,
    ventasPorMedio: [{ idMedioPago: 1, nombre: 'Efectivo', comportamiento: 'Efectivo', importe: 8000 }],
    totalVentas: 8000,
    totalDigitales: 0,
    totalEfectivo: 8000,
    ventasACuentaCorriente: 0,
    retiros: [{ fecha: '2026-08-05T15:00:00Z', importe: 3000, motivo: 'Retiro de efectivo', empleado: 'jperez' }],
    totalRetiros: 3000,
    ventasEnEfectivoNetas: 8000,
    gastosEnEfectivo: 0,
    refuerzos: 0,
    diferencia: 0,
    ...sobrescribir,
  }
}

type RutasDeCierre = {
  turno?: TurnoResumen | Error
  resumenDeCierre?: ResumenDeCierrePorRetiro | Promise<ResumenDeCierrePorRetiro> | Error
  puntosVenta?: Error
}

function mockearRutasDeCierre({ turno = turnoFixture(), resumenDeCierre = resumenDeCierreFixture(), puntosVenta }: RutasDeCierre = {}) {
  const respuesta = <T,>(valor: T | Error | Promise<T>) => (valor instanceof Error ? Promise.reject(valor) : Promise.resolve(valor))
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/caja/turnos/412/detalle') return Promise.resolve(detalleFixture())
    if (ruta === '/caja/turnos/412') return respuesta(turno)
    if (ruta === '/caja/turnos/412/resumen-de-cierre') return respuesta(resumenDeCierre)
    if (ruta === '/caja/turnos/413') return Promise.resolve(turnoFixture({ id: 413 }))
    if (ruta === '/caja/turnos/413/detalle') return Promise.resolve(detalleFixture({ resumen: resumenFixture({ idTurnoCaja: 413 }) }))
    if (ruta === '/puntos-venta') {
      return puntosVenta
        ? Promise.reject(puntosVenta)
        : Promise.resolve([{ id: 10, razonSocialEmpresa: 'Empresa Demo', nombreTenant: 'Tenant Demo' }])
    }
    return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
  })
}

const CONTEXTO_DE_IMPRESION = { empresa: 'Almacén Demo', puntoVenta: 'Local Centro', cajero: 'jperez' }

// Sin default: un parámetro default en JS se aplica también cuando se pasa `undefined` explícito,
// lo que taparía justo el caso "sin contextoDeImpresion" que estos tests necesitan ejercitar.
function renderCajaZConContexto(contextoDeImpresion: typeof CONTEXTO_DE_IMPRESION | undefined) {
  return render(<CajaZ contextoDeImpresion={contextoDeImpresion} />, {
    wrapper: ({ children }) => (
      <MemoryRouter initialEntries={['/caja/turnos/412/z']}>
        <Routes>
          <Route path="/caja/turnos/:id/z" element={children} />
        </Routes>
      </MemoryRouter>
    ),
  })
}

const BOTON_CIERRE = 'Reimprimir ticket de cierre'

/** Reimpresión del ticket de cierre: el MISMO `cierreDeTurno` que imprime el POS al cerrar, sobre
 * `GET .../resumen-de-cierre`. En la web sale por el diálogo del navegador (solo el ticket en el
 * papel); en Tauri, por la térmica. */
describe('CajaZ — reimpresión del ticket de cierre', () => {
  it('turno abierto: no ofrece el botón y nunca pide el resumen de cierre', async () => {
    mockearRutasDeCierre({ turno: turnoFixture({ estado: 'Abierto', fechaCierre: null }) })
    renderCajaZ()

    await screen.findByRole('row', { name: /Efectivo/ })
    await waitFor(() => expect(apiGetMock).toHaveBeenCalledWith('/caja/turnos/412'))
    expect(screen.queryByRole('button', { name: BOTON_CIERRE })).not.toBeInTheDocument()
    expect(apiGetMock).not.toHaveBeenCalledWith('/caja/turnos/412/resumen-de-cierre')
  })

  it('si no se puede leer el estado del turno no ofrece el botón y avisa por qué', async () => {
    mockearRutasDeCierre({ turno: new Error('boom') })
    renderCajaZ()

    expect(await screen.findByText(/No se pudo determinar el estado del turno\. No se ofrece la reimpresión/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: BOTON_CIERRE })).not.toBeInTheDocument()
  })

  it('web: pide el resumen de cierre, arma el ticket de cierre y abre el diálogo de impresión solo con él', async () => {
    mockearRutasDeCierre()
    const imprimirSpy = vi.fn()
    window.print = imprimirSpy
    renderCajaZ()

    await userEvent.click(await screen.findByRole('button', { name: BOTON_CIERRE }))

    const vista = await screen.findByTestId('vista-de-ticket')
    expect(apiGetMock).toHaveBeenCalledWith('/caja/turnos/412/resumen-de-cierre')
    expect(within(vista).getByText('CIERRE DE TURNO')).toBeInTheDocument()
    expect(within(vista).getByText('Turno #412')).toBeInTheDocument()
    expect(within(vista).getByText('Empresa Demo')).toBeInTheDocument()
    expect(within(vista).getByText('PV 10 — Local Centro')).toBeInTheDocument()
    expect(within(vista).getByText(/^Total general\s+\$ 8\.000,00$/)).toBeInTheDocument()
    expect(imprimirMock).not.toHaveBeenCalled()
    expect(imprimirSpy).toHaveBeenCalledTimes(1)
    expect(document.body).toHaveClass('imprimiendo-ticket')
  })

  it('web: al terminar la impresión (afterprint) el ticket y la marca de impresión desaparecen', async () => {
    mockearRutasDeCierre()
    window.print = vi.fn()
    renderCajaZ()

    await userEvent.click(await screen.findByRole('button', { name: BOTON_CIERRE }))
    await screen.findByTestId('vista-de-ticket')

    act(() => {
      window.dispatchEvent(new Event('afterprint'))
    })

    expect(screen.queryByTestId('vista-de-ticket')).not.toBeInTheDocument()
    expect(document.body).not.toHaveClass('imprimiendo-ticket')
  })

  it('web: sin poder leer los puntos de venta imprime igual, con la cabecera de empresa vacía', async () => {
    mockearRutasDeCierre({ puntosVenta: new Error('403') })
    window.print = vi.fn()
    renderCajaZ()

    await userEvent.click(await screen.findByRole('button', { name: BOTON_CIERRE }))

    const vista = await screen.findByTestId('vista-de-ticket')
    expect(within(vista).getByText('CIERRE DE TURNO')).toBeInTheDocument()
    expect(within(vista).queryByText('Empresa Demo')).not.toBeInTheDocument()
  })

  it('si falla el resumen de cierre muestra el error y no abre el diálogo de impresión', async () => {
    mockearRutasDeCierre({ resumenDeCierre: new Error('boom') })
    const imprimirSpy = vi.fn()
    window.print = imprimirSpy
    renderCajaZ()

    await userEvent.click(await screen.findByRole('button', { name: BOTON_CIERRE }))

    expect(await screen.findByText('No se pudo obtener el ticket de cierre.')).toBeInTheDocument()
    expect(screen.queryByTestId('vista-de-ticket')).not.toBeInTheDocument()
    expect(imprimirSpy).not.toHaveBeenCalled()
    expect(screen.getByRole('button', { name: BOTON_CIERRE })).toBeEnabled()
  })

  it('el error de un intento anterior se borra apenas empieza el siguiente', async () => {
    mockearRutasDeCierre({ resumenDeCierre: new Error('boom') })
    window.print = vi.fn()
    renderCajaZ()

    await userEvent.click(await screen.findByRole('button', { name: BOTON_CIERRE }))
    await screen.findByText('No se pudo obtener el ticket de cierre.')

    mockearRutasDeCierre({ resumenDeCierre: new Promise<ResumenDeCierrePorRetiro>(() => {}) })
    await userEvent.click(screen.getByRole('button', { name: BOTON_CIERRE }))

    expect(screen.queryByText('No se pudo obtener el ticket de cierre.')).not.toBeInTheDocument()
  })

  it('doble clic sincrónico: una sola request de resumen de cierre y el botón queda deshabilitado mientras tanto', async () => {
    let resolver: (r: ResumenDeCierrePorRetiro) => void = () => {}
    mockearRutasDeCierre({ resumenDeCierre: new Promise<ResumenDeCierrePorRetiro>((resolve) => (resolver = resolve)) })
    window.print = vi.fn()
    renderCajaZ()

    const boton = await screen.findByRole('button', { name: BOTON_CIERRE })
    act(() => {
      boton.click()
      boton.click()
    })

    expect(apiGetMock.mock.calls.filter(([ruta]) => ruta === '/caja/turnos/412/resumen-de-cierre')).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'Imprimiendo…' })).toBeDisabled()

    await act(async () => {
      resolver(resumenDeCierreFixture())
    })
    expect(await screen.findByTestId('vista-de-ticket')).toBeInTheDocument()
  })

  it('una respuesta que llega después de cambiar de turno se descarta: no imprime el ticket del turno anterior', async () => {
    let resolver: (r: ResumenDeCierrePorRetiro) => void = () => {}
    mockearRutasDeCierre({ resumenDeCierre: new Promise<ResumenDeCierrePorRetiro>((resolve) => (resolver = resolve)) })
    const imprimirSpy = vi.fn()
    window.print = imprimirSpy

    function Navegar() {
      const navegar = useNavigate()
      return <button onClick={() => navegar('/caja/turnos/413/z')}>ir al 413</button>
    }
    render(
      <MemoryRouter initialEntries={['/caja/turnos/412/z']}>
        <Routes>
          <Route
            path="/caja/turnos/:id/z"
            element={
              <>
                <Navegar />
                <CajaZ />
              </>
            }
          />
        </Routes>
      </MemoryRouter>,
    )

    await userEvent.click(await screen.findByRole('button', { name: BOTON_CIERRE }))
    await userEvent.click(screen.getByText('ir al 413'))
    await screen.findByText('Caja Z — turno #413')

    await act(async () => {
      resolver(resumenDeCierreFixture())
    })

    expect(screen.queryByTestId('vista-de-ticket')).not.toBeInTheDocument()
    expect(imprimirSpy).not.toHaveBeenCalled()
  })

  it('Tauri: manda a la térmica los bytes de cierreDeTurno(resumen, contexto) y no abre el diálogo del navegador', async () => {
    escritorioMock = true
    const resumen = resumenDeCierreFixture()
    mockearRutasDeCierre({ resumenDeCierre: resumen })
    const imprimirSpy = vi.fn()
    window.print = imprimirSpy
    renderCajaZConContexto(CONTEXTO_DE_IMPRESION)

    await userEvent.click(await screen.findByRole('button', { name: BOTON_CIERRE }))

    await waitFor(() => expect(imprimirMock).toHaveBeenCalledTimes(1))
    expect(imprimirMock.mock.calls[0][0]).toEqual(cierreDeTurno(resumen, CONTEXTO_DE_IMPRESION))
    expect(imprimirSpy).not.toHaveBeenCalled()
    expect(screen.queryByTestId('vista-de-ticket')).not.toBeInTheDocument()
  })

  it('Tauri: si la impresora falla muestra el aviso "No se pudo imprimir" no bloqueante', async () => {
    escritorioMock = true
    imprimirMock.mockResolvedValue({ ok: false, motivo: 'error', mensaje: 'sin papel' })
    mockearRutasDeCierre()
    renderCajaZConContexto(CONTEXTO_DE_IMPRESION)

    await userEvent.click(await screen.findByRole('button', { name: BOTON_CIERRE }))

    expect(await screen.findByText('No se pudo imprimir: sin papel')).toBeInTheDocument()
  })

  it('con contextoDeImpresion pero fuera de Tauri imprime por el navegador, no por la térmica', async () => {
    escritorioMock = false
    mockearRutasDeCierre()
    window.print = vi.fn()
    renderCajaZConContexto(CONTEXTO_DE_IMPRESION)

    await userEvent.click(await screen.findByRole('button', { name: BOTON_CIERRE }))

    expect(await screen.findByTestId('vista-de-ticket')).toBeInTheDocument()
    expect(imprimirMock).not.toHaveBeenCalled()
  })
})

describe('CajaZ — orden de las tablas', () => {
  const primerasCeldas = (tabla: HTMLElement) =>
    Array.from(tabla.querySelectorAll('tbody tr')).map((fila) => fila.querySelector('td')?.textContent ?? '')

  it('los medios ordenan por el esperado numérico y los tickets y pagos llevan cada uno su propio orden', async () => {
    apiGetMock.mockImplementation((ruta: string) =>
      ruta === '/caja/turnos/412/detalle' ? Promise.resolve(detalleFixture()) : Promise.reject(new Error(`ruta no mockeada: ${ruta}`)),
    )
    renderCajaZ()
    await screen.findByText('Caja Z — turno #412')
    const [medios, tickets, pagos] = screen.getAllByRole('table')

    const esperado = within(medios).getByRole('columnheader', { name: /Esperado/ })
    await userEvent.click(within(esperado).getByRole('button'))
    expect(esperado).toHaveAttribute('aria-sort', 'ascending')
    expect(primerasCeldas(medios)).toEqual(['Tarjeta', 'Efectivo'])

    const total = within(tickets).getByRole('columnheader', { name: /Total/ })
    await userEvent.click(within(total).getByRole('button'))
    await userEvent.click(within(total).getByRole('button'))
    expect(total).toHaveAttribute('aria-sort', 'descending')
    expect(primerasCeldas(tickets)).toEqual(['0003-00000001', '0003-00000002'])

    const importe = within(pagos).getByRole('columnheader', { name: /Importe/ })
    await userEvent.click(within(importe).getByRole('button'))
    expect(importe).toHaveAttribute('aria-sort', 'ascending')
    expect(within(pagos).getAllByRole('row')[1]).toHaveTextContent('Viaticos')
    expect(within(pagos).getAllByRole('row')[2]).toHaveTextContent('Sueldos')
  })
})

function comprobanteFixture(sobrescribir: Partial<ComprobanteEmitido> = {}): ComprobanteEmitido {
  return {
    id: 1,
    numero: 1,
    numeroVisible: '0003-00000001',
    estado: 'Emitido',
    fecha: '2026-08-05T08:30:00Z',
    idPuntoVenta: 10,
    idCliente: 1,
    idComprobanteAsociado: null,
    subtotal: 750,
    descuentoTotal: 0,
    descuentoManualTotal: 0,
    recargoManualTotal: 0,
    total: 750,
    direccionEntrega: null,
    observaciones: null,
    items: [
      {
        orden: 1,
        idArticulo: 1,
        descripcion: 'Coca Cola 1L',
        codigoBarra: null,
        idArea: 1,
        idListaPrecio: 1,
        idOferta: null,
        idAlicuotaIva: 1,
        porcentajeIva: 21,
        cantidad: 1,
        precioUnitario: 750,
        descuento: 0,
        total: 750,
        idLote: null,
        codigoLote: null,
        loteVencido: false,
        ajusteManualPorcentaje: null,
        ajusteManual: 0,
      },
    ],
    pagos: [{ idMedioPago: 1, importe: 750, referencia: null, vuelto: 0 }],
    idPresupuestoOrigen: null,
    ...sobrescribir,
  }
}

function mockearRutasDeTickets(ventas: Record<number, ComprobanteEmitido> = { 1: comprobanteFixture(), 2: comprobanteFixture({ id: 2, estado: 'Anulado' }) }) {
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/caja/turnos/412/detalle') return Promise.resolve(detalleFixture())
    if (ruta === '/caja/turnos/412') return Promise.resolve(turnoFixture())
    if (ruta === '/caja/turnos/412/resumen-de-cierre') return Promise.resolve(resumenDeCierreFixture())
    if (ruta === '/puntos-venta') return Promise.resolve([{ id: 10, razonSocialEmpresa: 'Empresa Demo', nombreTenant: 'Tenant Demo' }])
    if (ruta === '/medios-pago') return Promise.resolve([])
    const venta = /^\/ventas\/(\d+)$/.exec(ruta)
    if (venta && ventas[Number(venta[1])]) return Promise.resolve(ventas[Number(venta[1])])
    return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
  })
}

function renderCajaZConReimpresion(props: React.ComponentProps<typeof CajaZ> = {}) {
  return render(<CajaZ {...props} />, {
    wrapper: ({ children }) => (
      <MemoryRouter initialEntries={['/caja/turnos/412/z']}>
        <Routes>
          <Route path="/caja/turnos/:id/z" element={children} />
        </Routes>
      </MemoryRouter>
    ),
  })
}

/** Tickets del turno cerrado: las mismas columnas que "Ventas del turno" del POS y dos acciones
 * por fila, ver detalle y reimprimir; anular no existe en esta pantalla. */
describe('CajaZ — tickets del turno', () => {
  it('lista las columnas de Ventas del turno y los medios del detalle por nombre', async () => {
    mockearRutasDeTickets()
    renderCajaZConReimpresion()

    const fila = await screen.findByRole('row', { name: /0003-00000001/ })
    const tabla = within(fila.closest('table') as HTMLElement)
    expect(tabla.getAllByRole('columnheader').map((h) => h.textContent?.replace(/[↕↑↓]/g, ''))).toEqual([
      'Número', 'Fecha', 'Cliente', 'Total', 'Medios de pago', 'Estado', 'Acciones',
    ])
    expect(within(fila).getByText('Cliente 1')).toBeInTheDocument()
    expect(within(fila).getByText('Emitida')).toBeInTheDocument()
    expect(screen.getByRole('row', { name: /Efectivo/ })).toBeInTheDocument()
    expect(screen.queryByText(/Medio #/)).not.toBeInTheDocument()
  })

  it('nunca ofrece anular, ni en la fila ni en el detalle', async () => {
    mockearRutasDeTickets()
    const usuario = userEvent.setup()
    renderCajaZConReimpresion({ alReimprimirVenta: vi.fn() })

    const fila = await screen.findByRole('row', { name: /0003-00000001/ })
    expect(screen.queryByRole('button', { name: /anular/i })).not.toBeInTheDocument()
    await usuario.click(within(fila).getByRole('button', { name: 'Detalle' }))
    await screen.findByText('Coca Cola 1L')
    expect(screen.queryByRole('button', { name: /anular/i })).not.toBeInTheDocument()
  })

  it('Ver detalle pide la venta y abre el mismo modal de detalle', async () => {
    mockearRutasDeTickets()
    const usuario = userEvent.setup()
    renderCajaZConReimpresion()

    const fila = await screen.findByRole('row', { name: /0003-00000001/ })
    await usuario.click(within(fila).getByRole('button', { name: 'Detalle' }))

    const modal = await screen.findByRole('dialog')
    expect(await within(modal).findByText('Coca Cola 1L')).toBeInTheDocument()
    expect(within(modal).getByText('Detalle de la venta 0003-00000001')).toBeInTheDocument()
    expect(apiGetMock).toHaveBeenCalledWith('/ventas/1')
  })

  it('una venta anulada no ofrece Reimprimir y una emitida sí', async () => {
    mockearRutasDeTickets()
    renderCajaZConReimpresion()

    const emitida = await screen.findByRole('row', { name: /0003-00000001/ })
    const anulada = screen.getByRole('row', { name: /0003-00000002/ })
    expect(within(emitida).getByRole('button', { name: 'Reimprimir' })).toBeInTheDocument()
    expect(within(anulada).queryByRole('button', { name: 'Reimprimir' })).not.toBeInTheDocument()
  })

  it('web: reimprimir arma el ticket de la venta con la marca de reimpresión y abre el diálogo del navegador', async () => {
    mockearRutasDeTickets()
    const imprimirSpy = vi.fn()
    window.print = imprimirSpy
    const usuario = userEvent.setup()
    renderCajaZConReimpresion()

    const fila = await screen.findByRole('row', { name: /0003-00000001/ })
    await usuario.click(within(fila).getByRole('button', { name: 'Reimprimir' }))

    const vista = await screen.findByTestId('vista-de-ticket')
    expect(within(vista).getByText('*** REIMPRESION ***')).toBeInTheDocument()
    expect(within(vista).getByText('PV 10 — Local Centro')).toBeInTheDocument()
    expect(within(vista).getByText(/Coca Cola 1L/)).toBeInTheDocument()
    expect(imprimirSpy).toHaveBeenCalledTimes(1)
    expect(imprimirMock).not.toHaveBeenCalled()
    expect(document.body).toHaveClass('imprimiendo-ticket')
  })

  it('escritorio: reimprimir encola el ticket por la térmica y no abre el diálogo del navegador', async () => {
    mockearRutasDeTickets()
    escritorioMock = true
    const imprimirSpy = vi.fn()
    window.print = imprimirSpy
    const alReimprimirVenta = vi.fn()
    const usuario = userEvent.setup()
    renderCajaZConReimpresion({ contextoDeImpresion: CONTEXTO_DE_IMPRESION, alReimprimirVenta })

    const fila = await screen.findByRole('row', { name: /0003-00000001/ })
    await usuario.click(within(fila).getByRole('button', { name: 'Reimprimir' }))

    await waitFor(() => expect(alReimprimirVenta).toHaveBeenCalledTimes(1))
    expect(alReimprimirVenta.mock.calls[0][0]).toMatchObject({ id: 1, numeroVisible: '0003-00000001' })
    expect(imprimirSpy).not.toHaveBeenCalled()
    expect(screen.queryByTestId('vista-de-ticket')).not.toBeInTheDocument()
  })

  it('si al reimprimir la venta ya figura anulada no imprime nada y lo avisa en la fila', async () => {
    mockearRutasDeTickets({ 1: comprobanteFixture({ estado: 'Anulado' }) })
    const imprimirSpy = vi.fn()
    window.print = imprimirSpy
    const alReimprimirVenta = vi.fn()
    escritorioMock = true
    const usuario = userEvent.setup()
    renderCajaZConReimpresion({ contextoDeImpresion: CONTEXTO_DE_IMPRESION, alReimprimirVenta })

    const fila = await screen.findByRole('row', { name: /0003-00000001/ })
    await usuario.click(within(fila).getByRole('button', { name: 'Reimprimir' }))

    expect(await screen.findByText('La venta fue anulada, no se puede reimprimir.')).toBeInTheDocument()
    expect(alReimprimirVenta).not.toHaveBeenCalled()
    expect(imprimirSpy).not.toHaveBeenCalled()
    await waitFor(() =>
      expect(within(screen.getByRole('row', { name: /0003-00000001/ })).queryByRole('button', { name: 'Reimprimir' })).not.toBeInTheDocument(),
    )
  })

  it('dos clics sincrónicos en Reimprimir piden la venta una sola vez', async () => {
    mockearRutasDeTickets()
    escritorioMock = true
    const alReimprimirVenta = vi.fn()
    renderCajaZConReimpresion({ contextoDeImpresion: CONTEXTO_DE_IMPRESION, alReimprimirVenta })

    const fila = await screen.findByRole('row', { name: /0003-00000001/ })
    const boton = within(fila).getByRole('button', { name: 'Reimprimir' })
    await act(async () => {
      boton.click()
      boton.click()
    })

    await waitFor(() => expect(alReimprimirVenta).toHaveBeenCalledTimes(1))
    expect(apiGetMock.mock.calls.filter((c) => c[0] === '/ventas/1')).toHaveLength(1)
  })
})
