import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { CompraListada, TipoComprobanteListado } from '../../api/tipos'
import { SelectorDeFacturaAsociada } from './SelectorDeFacturaAsociada'

const listarMock = vi.fn()

vi.mock('../../api/compras', async (importarOriginal) => ({
  ...(await importarOriginal<typeof import('../../api/compras')>()),
  clienteDeCompras: { listar: (...args: unknown[]) => listarMock(...args) },
}))

const TIPOS = [
  { id: 1, codigo: 'C-FA', signo: 1, registraLibroIva: true },
  { id: 2, codigo: 'C-RM', signo: 1, registraLibroIva: false },
  { id: 3, codigo: 'C-NCA', signo: -1, registraLibroIva: true },
] as TipoComprobanteListado[]

function compra(id: number, idTipoComprobante: number): CompraListada {
  return { id, idTipoComprobante, numeroExterno: `0001-${id}`, total: 100 } as CompraListada
}

beforeEach(() => listarMock.mockReset())
afterEach(cleanup)

describe('SelectorDeFacturaAsociada', () => {
  it('lista solo facturas confirmadas del proveedor, sin remitos ni notas', async () => {
    listarMock.mockResolvedValue({ items: [compra(10, 1), compra(11, 2), compra(12, 3)], total: 3, pagina: 1, tamanio: 200 })
    render(<SelectorDeFacturaAsociada idProveedor={5} idEmpresa={7} tipos={TIPOS} valor={null} disabled={false} onChange={() => {}} />)

    await waitFor(() => expect(screen.getAllByRole('option')).toHaveLength(2))
    expect(screen.getByRole('option', { name: /C-FA 0001-10/ })).toBeTruthy()
    const filtros = listarMock.mock.calls[0][0]
    expect(filtros).toMatchObject({ idProveedor: 5, idEmpresa: 7, estado: 'Confirmada', tamanio: 200 })
    expect(screen.queryByText(/últimas 200/)).toBeNull()
  })

  it('avisa que se listan solo las últimas 200 cuando la página no alcanza', async () => {
    listarMock.mockResolvedValue({ items: [compra(10, 1)], total: 350, pagina: 1, tamanio: 200 })
    render(<SelectorDeFacturaAsociada idProveedor={5} idEmpresa={7} tipos={TIPOS} valor={null} disabled={false} onChange={() => {}} />)

    expect(await screen.findByText(/últimas 200/)).toBeTruthy()
  })

  it('sin proveedor no consulta y queda deshabilitado', () => {
    render(<SelectorDeFacturaAsociada idProveedor="" idEmpresa={null} tipos={TIPOS} valor={null} disabled={false} onChange={() => {}} />)

    expect(listarMock).not.toHaveBeenCalled()
    expect((screen.getByRole('combobox') as HTMLSelectElement).disabled).toBe(true)
  })

  it('una respuesta tardía de otro proveedor no se muestra', async () => {
    let resolverViejo: (v: unknown) => void = () => {}
    listarMock.mockReturnValueOnce(new Promise((r) => (resolverViejo = r)))
    listarMock.mockResolvedValueOnce({ items: [compra(20, 1)], total: 1, pagina: 1, tamanio: 200 })
    const { rerender } = render(
      <SelectorDeFacturaAsociada idProveedor={5} idEmpresa={7} tipos={TIPOS} valor={null} disabled={false} onChange={() => {}} />,
    )
    rerender(<SelectorDeFacturaAsociada idProveedor={6} idEmpresa={7} tipos={TIPOS} valor={null} disabled={false} onChange={() => {}} />)
    await screen.findByRole('option', { name: /0001-20/ })

    resolverViejo({ items: [compra(10, 1)], total: 1, pagina: 1, tamanio: 200 })
    await Promise.resolve()
    expect(screen.queryByRole('option', { name: /0001-10/ })).toBeNull()
  })
})
