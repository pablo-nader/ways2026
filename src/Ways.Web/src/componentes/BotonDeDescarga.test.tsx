import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { BotonDeDescarga } from './BotonDeDescarga'

const descargarMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: { descargar: (...args: unknown[]) => descargarMock(...(args as [string])) },
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

beforeEach(() => {
  descargarMock.mockReset()
})

describe('BotonDeDescarga', () => {
  it('al hacer click descarga la ruta indicada', async () => {
    descargarMock.mockResolvedValue(undefined)
    render(<BotonDeDescarga ruta="/reportes/ventas/resumen/export?formato=xlsx" onError={vi.fn()} />)

    fireEvent.click(screen.getByRole('button', { name: 'Descargar' }))

    await waitFor(() => expect(descargarMock).toHaveBeenCalledWith('/reportes/ventas/resumen/export?formato=xlsx'))
  })

  // Prueba la guarda de re-entrancy (`if (enVueloRef.current) return`) de BotonDeDescarga
  // (mutation-proof-tests): quitando esa línea el doble click dispara dos `api.descargar` en vez
  // de uno — mutación aplicada, este test falló con 2 llamadas, revertida vuelve a pasar. Los dos
  // `dispatchEvent` viajan DENTRO de un mismo `act()` (no dos `fireEvent.click` separados) para
  // que ningún re-render de React corra entre ellos — si no, el segundo click ya vería el
  // `disabled` puesto por el primero y el test probaría el atributo, no la guarda del `ref`
  // (`react-async-state` regla 9: "a same-tick double click beats the disabled attribute re-render").
  it('un doble click en el mismo tick dispara exactamente un fetch y el botón queda deshabilitado durante la descarga', async () => {
    let resolverDescarga: () => void = () => {}
    descargarMock.mockImplementation(
      () =>
        new Promise<void>((resolve) => {
          resolverDescarga = resolve
        }),
    )
    render(<BotonDeDescarga ruta="/reportes/ventas/resumen/export?formato=xlsx" onError={vi.fn()} />)

    const boton = screen.getByRole('button', { name: 'Descargar' })
    act(() => {
      boton.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }))
      boton.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }))
    })

    expect(descargarMock).toHaveBeenCalledTimes(1)
    expect(screen.getByRole('button')).toBeDisabled()

    resolverDescarga()
    await waitFor(() => expect(screen.getByRole('button')).not.toBeDisabled())
  })

  it('un error de ErrorApi funnelea su mensaje vía onError, sin lanzar ni navegar', async () => {
    const { ErrorApi } = await import('../api/cliente')
    descargarMock.mockRejectedValue(new ErrorApi(403, 'prohibido', 'No tenés permiso para exportar este reporte.'))
    const onError = vi.fn()
    render(<BotonDeDescarga ruta="/reportes/ventas/resumen/export?formato=xlsx" onError={onError} />)

    fireEvent.click(screen.getByRole('button', { name: 'Descargar' }))

    await waitFor(() => expect(onError).toHaveBeenCalledWith('No tenés permiso para exportar este reporte.'))
    expect(screen.getByRole('button')).not.toBeDisabled()
  })

  it('un error no-ErrorApi cae al mensaje genérico', async () => {
    descargarMock.mockRejectedValue(new Error('boom'))
    const onError = vi.fn()
    render(<BotonDeDescarga ruta="/reportes/ventas/resumen/export?formato=xlsx" onError={onError} />)

    fireEvent.click(screen.getByRole('button', { name: 'Descargar' }))

    await waitFor(() => expect(onError).toHaveBeenCalledWith('No se pudo descargar el archivo.'))
  })

  it('acepta una etiqueta personalizada', () => {
    render(<BotonDeDescarga ruta="/x" etiqueta="Exportar a Excel" onError={vi.fn()} />)

    expect(screen.getByRole('button', { name: 'Exportar a Excel' })).toBeInTheDocument()
  })
})
