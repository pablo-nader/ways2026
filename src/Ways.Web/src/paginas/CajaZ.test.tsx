import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useNavigate } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { CajaZ } from './CajaZ'
import { RutaProtegida } from '../auth/RutaProtegida'
import { ROL } from '../api/tipos'
import type { DetalleDeTurno, ResumenDeTurno, UsuarioAutenticado } from '../api/tipos'

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

function resumenFixture(sobrescribir: Partial<ResumenDeTurno> = {}): ResumenDeTurno {
  return {
    idTurnoCaja: 412,
    idMedioAncla: 1,
    medios: [
      { idMedioPago: 1, importeEsperado: 1000 },
      { idMedioPago: 2, importeEsperado: 500 },
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
      { id: 1, numero: 1, numeroVisible: '0003-00000001', estado: 'Emitido', fecha: '2026-08-05T08:30:00Z', idPuntoVenta: 10, idCliente: 1, total: 750 },
      { id: 2, numero: 2, numeroVisible: '0003-00000002', estado: 'Anulado', fecha: '2026-08-05T19:30:00Z', idPuntoVenta: 10, idCliente: 2, total: 250 },
    ],
    gastos: [
      { id: 1, idPuntoVenta: 10, fecha: '2026-08-05T09:00:00Z', categoria: 'Sueldos', idMedioPago: 1, importe: 300 },
      { id: 2, idPuntoVenta: 10, fecha: '2026-08-05T10:00:00Z', categoria: 'Viaticos', idMedioPago: 2, importe: 120 },
    ],
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
})

describe('CajaZ — detalle del turno (stage-11-exportacion-reportes, Slice 6b)', () => {
  it('renderiza el resumen (tickets/primer/último/retiros) y cada fila de medios/tickets/gastos', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/412/detalle') return Promise.resolve(detalleFixture())
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    renderCajaZ()

    expect(await screen.findByText('Caja Z — turno #412')).toBeInTheDocument()
    expect(screen.getByText('2')).toBeInTheDocument() // cantidadTickets
    expect(screen.getByText('TX #1')).toBeInTheDocument() // primerTicket
    expect(screen.getByText('TX #2')).toBeInTheDocument() // ultimoTicket
    expect(screen.getByText('$100,00')).toBeInTheDocument() // retiros

    // mutation-proof-tests rule 6: dos filas con valores DISTINTOS por columna.
    const filaMedioUno = screen.getByRole('row', { name: /Medio #1/ })
    expect(within(filaMedioUno).getByText('$1.000,00')).toBeInTheDocument()
    const filaMedioDos = screen.getByRole('row', { name: /Medio #2/ })
    expect(within(filaMedioDos).getByText('$500,00')).toBeInTheDocument()

    const filaTicketUno = screen.getByRole('row', { name: /0003-00000001/ })
    expect(within(filaTicketUno).getByText('Emitido')).toBeInTheDocument()
    expect(within(filaTicketUno).getByText('$750,00')).toBeInTheDocument()
    const filaTicketDos = screen.getByRole('row', { name: /0003-00000002/ })
    expect(within(filaTicketDos).getByText('Anulado')).toBeInTheDocument()
    expect(within(filaTicketDos).getByText('$250,00')).toBeInTheDocument()

    const filaGastoUno = screen.getByRole('row', { name: /Sueldos/ })
    expect(within(filaGastoUno).getByText('$300,00')).toBeInTheDocument()
    const filaGastoDos = screen.getByRole('row', { name: /Viaticos/ })
    expect(within(filaGastoDos).getByText('$120,00')).toBeInTheDocument()
  })

  it('sin medios/tickets/gastos muestra los estados vacíos de cada sección', async () => {
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/caja/turnos/412/detalle') {
        return Promise.resolve(detalleFixture({ resumen: resumenFixture({ medios: [] }), tickets: [], gastos: [] }))
      }
      return Promise.reject(new Error(`ruta no mockeada: ${ruta}`))
    })
    renderCajaZ()

    expect(await screen.findByText('Este turno no tuvo actividad: no hay ningún medio arqueado.')).toBeInTheDocument()
    expect(screen.getByText('Sin tickets.')).toBeInTheDocument()
    expect(screen.getByText('Sin gastos.')).toBeInTheDocument()
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
