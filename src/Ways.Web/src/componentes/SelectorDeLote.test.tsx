import { act, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { SelectorDeLote } from './SelectorDeLote'
import type { LoteListado } from '../api/tipos'

const apiGetMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: vi.fn(),
    put: vi.fn(),
    delete: vi.fn(),
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

function loteFixture(sobrescribir: Partial<LoteListado> = {}): LoteListado {
  return {
    idLote: 1,
    idArticulo: 1,
    codigo: '2026-09-01',
    fechaVencimiento: '2026-09-01',
    esSinIdentificar: false,
    cantidad: 5,
    estado: 'Vigente',
    sugerido: false,
    ...sobrescribir,
  }
}

function propsDe(idPuntoVenta: number) {
  return {
    idPuntoVenta,
    idArticulo: 1,
    nombreArticulo: 'Coca Cola 1L',
    idLoteElegido: null,
    disabled: false,
    onElegir: vi.fn(),
  }
}

beforeEach(() => {
  apiGetMock.mockReset()
})

describe('SelectorDeLote — cambio de punto de venta con un fetch en vuelo (react-async-state regla 3)', () => {
  /**
   * Cláusula bajo prueba: el guard `tokenRef.current !== miToken` que sigue al `await` de
   * `listarLotes`, junto con el bump de `tokenRef` del efecto keyed en `[idPuntoVenta, idArticulo]`.
   * Escenario que vivía en `Pos.test.tsx` cuando el POS tenía su propio selector de punto de venta;
   * con el punto de venta en la sesión, `Pos()` remonta la pantalla entera por `key` y esta carrera
   * ya no es alcanzable desde ahí — se prueba acá, directamente contra el componente.
   *
   * Evidencia de mutación (mutation-proof-tests regla 2): con `if (tokenRef.current !== miToken)
   * return` borrado del camino de éxito de `cargar`, este test falla ("Lote de Coca Cola 1L" aparece
   * con el lote STALE y el botón "Elegir lote" desaparece); restaurado, vuelve a verde.
   */
  it('una respuesta stale que llega DESPUÉS de cambiar de punto de venta no pinta el picker (mutation-proof-tests regla 7)', async () => {
    let resolverLotesPv7: (valor: LoteListado[]) => void = () => {}
    let promesaLotesPv7: Promise<LoteListado[]> = Promise.resolve([])
    apiGetMock.mockImplementation((ruta: string) => {
      if (ruta === '/stock/lotes?idPuntoVenta=7&idArticulo=1') {
        promesaLotesPv7 = new Promise((resolve) => (resolverLotesPv7 = resolve))
        return promesaLotesPv7
      }
      if (ruta.startsWith('/stock/lotes?')) return Promise.resolve<LoteListado[]>([])
      return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
    })

    const { rerender } = render(<SelectorDeLote {...propsDe(7)} />)

    await userEvent.click(screen.getByRole('button', { name: 'Elegir lote' }))
    // el fetch del punto de venta 7 queda en vuelo — `resolverLotesPv7` todavía no se llamó.
    expect(screen.getByRole('button', { name: 'Cargando…' })).toBeDisabled()

    rerender(<SelectorDeLote {...propsDe(8)} />)
    expect(screen.getByRole('button', { name: 'Elegir lote' })).toBeEnabled()

    // regla 7: el flush del microtask va DENTRO de `act` — un `waitFor` pasaría en su primer
    // tick, antes de que el `.then` stale aterrice, y saldría verde sin probar nada.
    await act(async () => {
      resolverLotesPv7([loteFixture({ idLote: 99, codigo: 'STALE' })])
      await promesaLotesPv7
    })

    expect(screen.getByRole('button', { name: 'Elegir lote' })).toBeEnabled()
    expect(screen.queryByLabelText('Lote de Coca Cola 1L')).not.toBeInTheDocument()
    expect(apiGetMock).toHaveBeenCalledTimes(1)
  })
})
