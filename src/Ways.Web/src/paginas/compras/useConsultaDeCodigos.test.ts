import { act, renderHook } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { lineaDeCompraVacia, type LineaDeCompraFormulario } from '../../api/lineaDeCompra'
import type { ArticuloListado } from '../../api/tipos'
import { useConsultaDeCodigos } from './useConsultaDeCodigos'

const listarMock = vi.fn()

vi.mock('../../api/articulos', () => ({
  clienteDeArticulos: { listar: (...args: unknown[]) => listarMock(...args) },
}))

const ENCONTRADO = {
  id: 20,
  nombre: 'Leche',
  codigoProveedor: 'AB-1',
  costoLista: 200,
  descuentoProveedor: null,
  costoNominal: null,
  idAlicuotaIva: 3,
  unidadesPorBulto: null,
  controlaLote: false,
  unidadVenta: 'Unidad',
} as unknown as ArticuloListado

function opciones(lineas: LineaDeCompraFormulario[], aplicar: (clave: number, cambios: Partial<LineaDeCompraFormulario>) => void) {
  return {
    lineas,
    idProveedor: 1,
    alicuotas: [{ id: 3, nombre: '21%', porcentaje: 21, codigoAfip: null, activo: true }],
    modoDePrecios: { discriminaIva: false, preciosIncluyenIva: false },
    disabled: false,
    cambiosDeProveedor: 0,
    aplicar,
    alResolver: vi.fn(),
  }
}

describe('useConsultaDeCodigos — respuesta sobre una fila que ya tiene artículo', () => {
  beforeEach(() => {
    listarMock.mockReset()
  })

  it('si el padre le puso un artículo a la fila mientras la consulta estaba en vuelo, la respuesta no se aplica', async () => {
    let responder: (r: unknown) => void = () => {}
    listarMock.mockImplementation(() => new Promise((r) => (responder = r)))
    const aplicar = vi.fn()
    const sinArticulo = { ...lineaDeCompraVacia(1), codigoProveedor: 'AB-1' }
    const { result, rerender } = renderHook((props) => useConsultaDeCodigos(props), { initialProps: opciones([sinArticulo], aplicar) })

    act(() => result.current.alSalirDelCodigo(1))
    rerender(opciones([{ ...sinArticulo, idArticulo: 99, descripcion: 'Elegido por el padre' }], aplicar))
    await act(async () => {
      responder({ items: [ENCONTRADO], total: 1, pagina: 1, tamanio: 25 })
    })

    expect(aplicar).not.toHaveBeenCalled()
  })

  it('con la fila todavía sin artículo la respuesta sí se aplica', async () => {
    listarMock.mockResolvedValue({ items: [ENCONTRADO], total: 1, pagina: 1, tamanio: 25 })
    const aplicar = vi.fn()
    const sinArticulo = { ...lineaDeCompraVacia(1), codigoProveedor: 'AB-1' }
    const { result } = renderHook(() => useConsultaDeCodigos(opciones([sinArticulo], aplicar)))

    await act(async () => result.current.alSalirDelCodigo(1))

    expect(aplicar).toHaveBeenCalledWith(1, expect.objectContaining({ idArticulo: 20 }))
  })
})
