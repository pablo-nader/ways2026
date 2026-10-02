import { act, cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { AvisoDeActualizacion } from './AvisoDeActualizacion'

type ManejadorDeEvento = (evento: { payload: unknown }) => void
type GlobalConTauri = typeof globalThis & {
  __TAURI__?: {
    core: { invoke: ReturnType<typeof vi.fn> }
    event: { listen: ReturnType<typeof vi.fn> }
  }
}

const invokeMock = vi.fn()
const listenMock = vi.fn()
const desuscribirMock = vi.fn()
let manejadorDeEvento: ManejadorDeEvento | null = null

function instalarPuenteTauri(disponible: unknown) {
  invokeMock.mockImplementation((comando: string) =>
    comando === 'estado_actualizacion' ? Promise.resolve(disponible) : Promise.resolve(undefined),
  )
  listenMock.mockImplementation((_evento: string, manejador: ManejadorDeEvento) => {
    manejadorDeEvento = manejador
    return Promise.resolve(desuscribirMock)
  })
  ;(globalThis as GlobalConTauri).__TAURI__ = { core: { invoke: invokeMock }, event: { listen: listenMock } }
}

beforeEach(() => {
  invokeMock.mockReset()
  listenMock.mockReset()
  desuscribirMock.mockReset()
  manejadorDeEvento = null
})

afterEach(() => {
  cleanup()
  delete (globalThis as GlobalConTauri).__TAURI__
})

const botonInstalar = () => screen.getByRole('button', { name: 'Instalar y reiniciar' })

describe('AvisoDeActualizacion', () => {
  it('en la app web (sin Tauri) no renderiza nada ni intenta ninguna IPC', async () => {
    const { container } = render(<AvisoDeActualizacion motivoDeBloqueo={null} />)

    await act(async () => {})

    expect(container).toBeEmptyDOMElement()
    expect(invokeMock).not.toHaveBeenCalled()
  })

  it('sin actualización descargada no muestra el banner', async () => {
    instalarPuenteTauri(null)
    const { container } = render(<AvisoDeActualizacion motivoDeBloqueo={null} />)

    await waitFor(() => expect(invokeMock).toHaveBeenCalledWith('estado_actualizacion'))

    expect(container).toBeEmptyDOMElement()
  })

  it('con una actualización ya descargada al montar, muestra la versión y el botón habilitado', async () => {
    instalarPuenteTauri({ version: '0.4.0', notas: null })
    render(<AvisoDeActualizacion motivoDeBloqueo={null} />)

    expect(await screen.findByText(/Actualización 0\.4\.0 disponible/)).toBeInTheDocument()
    expect(botonInstalar()).toBeEnabled()
  })

  it('el evento de descarga muestra el banner aunque la consulta inicial no haya encontrado nada', async () => {
    instalarPuenteTauri(null)
    render(<AvisoDeActualizacion motivoDeBloqueo={null} />)
    await waitFor(() => expect(manejadorDeEvento).not.toBeNull())

    act(() => manejadorDeEvento!({ payload: { version: '0.5.0', notas: 'Mejoras' } }))

    expect(await screen.findByText(/Actualización 0\.5\.0 disponible/)).toBeInTheDocument()
  })

  it('con un motivo de bloqueo el botón queda deshabilitado y lo explica', async () => {
    instalarPuenteTauri({ version: '0.4.0', notas: null })
    render(<AvisoDeActualizacion motivoDeBloqueo="Terminá la venta en curso para instalarla." />)

    await screen.findByText(/Terminá la venta en curso para instalarla/)
    expect(botonInstalar()).toBeDisabled()

    await userEvent.click(botonInstalar())
    expect(invokeMock).not.toHaveBeenCalledWith('instalar_actualizacion')
  })

  it('dos clicks en el mismo tick invocan una sola instalación y tapan el POS con "Instalando…"', async () => {
    instalarPuenteTauri({ version: '0.4.0', notas: null })
    const alCambiarInstalando = vi.fn()
    render(<AvisoDeActualizacion motivoDeBloqueo={null} alCambiarInstalando={alCambiarInstalando} />)
    const boton = await screen.findByRole('button', { name: 'Instalar y reiniciar' })
    invokeMock.mockImplementation(() => new Promise(() => {}))

    act(() => {
      boton.click()
      boton.click()
    })

    expect(invokeMock.mock.calls.filter(([comando]) => comando === 'instalar_actualizacion')).toHaveLength(1)
    expect(alCambiarInstalando.mock.calls).toEqual([[true]])
    expect(screen.getByText(/Instalando la actualización 0\.4\.0/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Instalar y reiniciar' })).not.toBeInTheDocument()
  })

  it('si la instalación falla, muestra el error, libera el POS y permite reintentar', async () => {
    instalarPuenteTauri({ version: '0.4.0', notas: null })
    const alCambiarInstalando = vi.fn()
    render(<AvisoDeActualizacion motivoDeBloqueo={null} alCambiarInstalando={alCambiarInstalando} />)
    await screen.findByRole('button', { name: 'Instalar y reiniciar' })
    invokeMock.mockImplementation(() => Promise.reject('No se pudo instalar la actualizacion: acceso denegado'))

    await userEvent.click(botonInstalar())

    expect(await screen.findByText(/acceso denegado/)).toBeInTheDocument()
    expect(botonInstalar()).toBeEnabled()
    expect(alCambiarInstalando.mock.calls).toEqual([[true], [false]])
  })

  it('al desmontar se desuscribe del evento', async () => {
    instalarPuenteTauri(null)
    const { unmount } = render(<AvisoDeActualizacion motivoDeBloqueo={null} />)
    await waitFor(() => expect(listenMock).toHaveBeenCalled())
    await act(async () => {})

    unmount()

    expect(desuscribirMock).toHaveBeenCalled()
  })
})
