import { act, fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { MemoryRouter, Route, Routes, useLocation, useNavigate } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { PLAZO_DEL_PEDIDO_PENDIENTE_MS, ProveedorDeAccionesDePos, useAccionesDePos, useRegistroDeAccionesDePos } from './AccionesDePosContext'
import type { EjecutoresDePos, EstadoDeAccionesDePos } from './AccionesDePosContext'

function estadoFixture(sobrescribir: Partial<EstadoDeAccionesDePos> = {}): EstadoDeAccionesDePos {
  return {
    listo: true,
    turnoAbierto: true,
    imposible: false,
    verificandoCierre: false,
    retiroOfrecido: true,
    puedeSincronizar: true,
    sincronizando: false,
    habilitadas: { abrirTurno: false, cerrarTurno: true, retirar: true, sincronizar: true },
    ...sobrescribir,
  }
}

const ESTADO_SIN_CARGAR = { listo: false, turnoAbierto: null } as const
const ESTADO_CON_ERROR = { listo: false, turnoAbierto: null, imposible: true } as const

/** Pantalla de venta que atraviesa los estados dados, de a uno por cada `avanzar()`. */
function pantallaPorFases(fases: EstadoDeAccionesDePos[], ejecutores: EjecutoresDePos) {
  const control = { avanzar: () => {} }
  function Pantalla() {
    const [fase, setFase] = useState(0)
    control.avanzar = () => setFase((f) => Math.min(f + 1, fases.length - 1))
    return <PantallaDeVenta estado={fases[fase]} ejecutores={ejecutores} />
  }
  return { Pantalla, control }
}

afterEach(() => {
  vi.useRealTimers()
})

function ejecutoresFixture() {
  return { abrirTurno: vi.fn(), cerrarTurno: vi.fn(), retirar: vi.fn(), sincronizar: vi.fn(), enfocarEscaneo: vi.fn() } satisfies EjecutoresDePos
}

/** Hace de pantalla de venta: se registra con el estado y los ejecutores que le pasa el test. */
function PantallaDeVenta({ estado, ejecutores }: { estado: EstadoDeAccionesDePos; ejecutores: EjecutoresDePos }) {
  useRegistroDeAccionesDePos(estado, ejecutores)
  return <p>pantalla de venta</p>
}

/** Hace de header: pide acciones y expone lo que el proveedor sabe. */
function Pedidor() {
  const { estado, aviso, solicitar } = useAccionesDePos()
  const navegar = useNavigate()
  const { pathname } = useLocation()
  return (
    <div>
      <p data-testid="ruta">{pathname}</p>
      <p data-testid="aviso">{aviso}</p>
      <p data-testid="listo">{String(estado?.listo)}</p>
      <p data-testid="cerrar-habilitada">{String(estado?.habilitadas.cerrarTurno)}</p>
      <button onClick={() => solicitar('cerrarTurno')}>pedir cierre</button>
      <button onClick={() => solicitar('abrirTurno')}>pedir apertura</button>
      <button onClick={() => solicitar('sincronizar')}>pedir sincronizacion</button>
      <button onClick={() => solicitar('enfocarEscaneo')}>pedir foco</button>
      <button onClick={() => void navegar('/ventas-del-turno')}>ir a ventas</button>
      <button onClick={() => void navegar('/vender')}>volver a vender</button>
    </div>
  )
}

function montar(pantalla: React.ReactNode) {
  return render(
    <MemoryRouter initialEntries={['/vender']}>
      <ProveedorDeAccionesDePos>
        <Pedidor />
        <Routes>
          <Route path="/vender" element={pantalla} />
          <Route path="/ventas-del-turno" element={<p>ventas del turno</p>} />
        </Routes>
      </ProveedorDeAccionesDePos>
    </MemoryRouter>,
  )
}

describe('ProveedorDeAccionesDePos', () => {
  it('con la pantalla de venta lista, el pedido se ejecuta de inmediato y una sola vez', async () => {
    const ejecutores = ejecutoresFixture()
    montar(<PantallaDeVenta estado={estadoFixture()} ejecutores={ejecutores} />)

    await userEvent.click(screen.getByText('pedir cierre'))

    expect(ejecutores.cerrarTurno).toHaveBeenCalledTimes(1)
    expect(ejecutores.abrirTurno).not.toHaveBeenCalled()
  })

  it('un pedido de una acción que la pantalla informa deshabilitada no se ejecuta', async () => {
    const ejecutores = ejecutoresFixture()
    const sinAcciones = estadoFixture({ habilitadas: { abrirTurno: false, cerrarTurno: false, retirar: false, sincronizar: false } })
    montar(<PantallaDeVenta estado={sinAcciones} ejecutores={ejecutores} />)

    await userEvent.click(screen.getByText('pedir cierre'))

    expect(ejecutores.cerrarTurno).not.toHaveBeenCalled()
  })

  it('desde otra pantalla navega a la venta y ejecuta el pedido una sola vez al montarse lista', async () => {
    const ejecutores = ejecutoresFixture()
    montar(<PantallaDeVenta estado={estadoFixture()} ejecutores={ejecutores} />)
    await userEvent.click(screen.getByText('ir a ventas'))
    expect(screen.getByTestId('ruta')).toHaveTextContent('/ventas-del-turno')
    expect(ejecutores.cerrarTurno).not.toHaveBeenCalled()

    await userEvent.click(screen.getByText('pedir cierre'))

    expect(screen.getByTestId('ruta')).toHaveTextContent('/vender')
    expect(ejecutores.cerrarTurno).toHaveBeenCalledTimes(1)
  })

  it('un pedido que espera a una pantalla todavía no lista corre al quedar lista, y un cambio de estado posterior no lo repite', async () => {
    const ejecutores = ejecutoresFixture()
    let avanzar: () => void = () => {}
    function PantallaQueSeCarga() {
      const [fase, setFase] = useState(0)
      avanzar = () => setFase((f) => f + 1)
      const estado = fase === 0 ? estadoFixture(ESTADO_SIN_CARGAR) : estadoFixture({ sincronizando: fase > 1 })
      return <PantallaDeVenta estado={estado} ejecutores={ejecutores} />
    }
    montar(<PantallaQueSeCarga />)

    await userEvent.click(screen.getByText('pedir cierre'))
    expect(ejecutores.cerrarTurno).not.toHaveBeenCalled()

    act(() => avanzar())
    expect(ejecutores.cerrarTurno).toHaveBeenCalledTimes(1)

    act(() => avanzar())
    expect(ejecutores.cerrarTurno).toHaveBeenCalledTimes(1)
  })

  it('el pedido pendiente se descarta si el cajero se va a otra pantalla: al volver a la venta, ya lista, no se ejecuta', async () => {
    const ejecutores = ejecutoresFixture()
    let montajes = 0
    function PantallaQueMontaSinCargar() {
      const [numeroDeMontaje] = useState(() => ++montajes)
      return <PantallaDeVenta estado={numeroDeMontaje === 1 ? estadoFixture(ESTADO_SIN_CARGAR) : estadoFixture()} ejecutores={ejecutores} />
    }
    montar(<PantallaQueMontaSinCargar />)

    await userEvent.click(screen.getByText('pedir cierre'))
    await userEvent.click(screen.getByText('ir a ventas'))
    await userEvent.click(screen.getByText('volver a vender'))

    expect(screen.getByTestId('listo')).toHaveTextContent('true')
    expect(ejecutores.cerrarTurno).not.toHaveBeenCalled()
  })

  it('al desmontarse la pantalla de venta, el header conserva el último estado en reposo: sin "listo" y sin inercias pasajeras', async () => {
    const ejecutores = ejecutoresFixture()
    const inerte = estadoFixture({ habilitadas: { abrirTurno: false, cerrarTurno: false, retirar: false, sincronizar: false } })
    montar(<PantallaDeVenta estado={inerte} ejecutores={ejecutores} />)
    expect(screen.getByTestId('cerrar-habilitada')).toHaveTextContent('false')

    await userEvent.click(screen.getByText('ir a ventas'))

    expect(screen.getByTestId('listo')).toHaveTextContent('false')
    expect(screen.getByTestId('cerrar-habilitada')).toHaveTextContent('true')
  })

  it('enfocar el escaneo no espera a que el turno esté resuelto', async () => {
    const ejecutores = ejecutoresFixture()
    montar(<PantallaDeVenta estado={estadoFixture(ESTADO_SIN_CARGAR)} ejecutores={ejecutores} />)

    await userEvent.click(screen.getByText('pedir foco'))

    expect(ejecutores.enfocarEscaneo).toHaveBeenCalledTimes(1)
  })
})

describe('ProveedorDeAccionesDePos — pedidos que no deben correr tarde', () => {
  it('si la pantalla informa que no puede quedar lista, el pedido se descarta con un aviso y un reintento exitoso posterior no lo ejecuta', async () => {
    const ejecutores = ejecutoresFixture()
    const { Pantalla, control } = pantallaPorFases([estadoFixture(ESTADO_SIN_CARGAR), estadoFixture(ESTADO_CON_ERROR), estadoFixture()], ejecutores)
    montar(<Pantalla />)

    await userEvent.click(screen.getByText('pedir cierre'))
    act(() => control.avanzar())

    expect(screen.getByTestId('aviso')).toHaveTextContent('No se pudo cerrar el turno')
    act(() => control.avanzar())
    expect(screen.getByTestId('listo')).toHaveTextContent('true')
    expect(ejecutores.cerrarTurno).not.toHaveBeenCalled()
  })

  it('si la pantalla ya informa que no puede quedar lista, el pedido no queda armado', async () => {
    const ejecutores = ejecutoresFixture()
    const { Pantalla, control } = pantallaPorFases([estadoFixture(ESTADO_CON_ERROR), estadoFixture()], ejecutores)
    montar(<Pantalla />)

    await userEvent.click(screen.getByText('pedir cierre'))
    expect(screen.getByTestId('aviso')).toHaveTextContent('No se pudo cerrar el turno')
    act(() => control.avanzar())

    expect(ejecutores.cerrarTurno).not.toHaveBeenCalled()
  })

  it('un pedido que espera más del plazo se descarta con un aviso, y quedar lista después no lo ejecuta', () => {
    vi.useFakeTimers()
    const ejecutores = ejecutoresFixture()
    const { Pantalla, control } = pantallaPorFases([estadoFixture(ESTADO_SIN_CARGAR), estadoFixture()], ejecutores)
    montar(<Pantalla />)

    fireEvent.click(screen.getByText('pedir cierre'))
    act(() => {
      vi.advanceTimersByTime(PLAZO_DEL_PEDIDO_PENDIENTE_MS - 1)
    })
    expect(screen.getByTestId('aviso')).toBeEmptyDOMElement()

    act(() => {
      vi.advanceTimersByTime(1)
    })
    expect(screen.getByTestId('aviso')).toHaveTextContent('No se pudo cerrar el turno')
    act(() => control.avanzar())

    expect(ejecutores.cerrarTurno).not.toHaveBeenCalled()
  })

  it('un pedido que se cumple dentro del plazo no deja un aviso de vencimiento después', () => {
    vi.useFakeTimers()
    const ejecutores = ejecutoresFixture()
    const { Pantalla, control } = pantallaPorFases([estadoFixture(ESTADO_SIN_CARGAR), estadoFixture()], ejecutores)
    montar(<Pantalla />)

    fireEvent.click(screen.getByText('pedir cierre'))
    act(() => control.avanzar())
    act(() => {
      vi.advanceTimersByTime(PLAZO_DEL_PEDIDO_PENDIENTE_MS * 2)
    })

    expect(ejecutores.cerrarTurno).toHaveBeenCalledTimes(1)
    expect(screen.getByTestId('aviso')).toBeEmptyDOMElement()
  })

  it('"Sincronizar" pedido desde otra pantalla, cuando la venta queda lista con su sincronización ya en curso, se da por cumplido: no la repite ni avisa error', async () => {
    const ejecutores = ejecutoresFixture()
    const enCurso = estadoFixture({ sincronizando: true, habilitadas: { abrirTurno: false, cerrarTurno: true, retirar: true, sincronizar: false } })
    const { Pantalla, control } = pantallaPorFases([estadoFixture(ESTADO_SIN_CARGAR), enCurso], ejecutores)
    montar(<Pantalla />)

    await userEvent.click(screen.getByText('pedir sincronizacion'))
    act(() => control.avanzar())

    expect(screen.getByTestId('listo')).toHaveTextContent('true')
    expect(ejecutores.sincronizar).not.toHaveBeenCalled()
    expect(screen.getByTestId('aviso')).toBeEmptyDOMElement()
  })

  it('"Sincronizar" sin instantánea local posible avisa que no se pudo, en vez de perderse en silencio', async () => {
    const ejecutores = ejecutoresFixture()
    const sinInstantanea = estadoFixture({ puedeSincronizar: false, habilitadas: { abrirTurno: false, cerrarTurno: true, retirar: true, sincronizar: false } })
    montar(<PantallaDeVenta estado={sinInstantanea} ejecutores={ejecutores} />)

    await userEvent.click(screen.getByText('pedir sincronizacion'))

    expect(ejecutores.sincronizar).not.toHaveBeenCalled()
    expect(screen.getByTestId('aviso')).toHaveTextContent('No se pudo sincronizar')
  })
})
