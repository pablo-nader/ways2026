import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { ErrorApi } from '../api/cliente'
import type { ComboParaCargar } from '../pos/cargaDeCombos'
import type { ComboDeLista, FuenteDeCombos } from '../pos/fuenteDeCombos'
import { ModalDeBusquedaDeCombos } from './ModalDeBusquedaDeCombos'

function deLista(idCombo: number, codigo: number, nombre: string, extra: Partial<ComboDeLista> = {}): ComboDeLista {
  return { idCombo, codigo, nombre, precio: 5000, resumen: 'Pancho × 1 + Coca × 1', motivoNoCargable: null, ...extra }
}

function completo(idCombo: number, codigo = idCombo): ComboParaCargar {
  const articulo = { idArticulo: 1, codigoInterno: 'A1', nombre: 'Pancho', codigoBarra: null, cantidad: 1, acumulaEnVenta: true, unidadVenta: 'Unidad' as const }
  return {
    idCombo,
    codigo,
    nombre: `Combo ${idCombo}`,
    precio: 5000,
    componentes: [
      { idArticulo: 1, idFamilia: null, etiqueta: 'Pancho', cantidad: 1, candidatos: [articulo] },
      { idArticulo: 2, idFamilia: null, etiqueta: 'Coca', cantidad: 1, candidatos: [{ ...articulo, idArticulo: 2, nombre: 'Coca' }] },
    ],
  }
}

function fuenteDe(lista: ComboDeLista[], sobrescribir: Partial<FuenteDeCombos> = {}): FuenteDeCombos {
  return {
    origen: 'api',
    listar: vi.fn().mockResolvedValue(lista),
    buscarPorCodigo: vi.fn(),
    cargar: vi.fn((id: number) => Promise.resolve(completo(id))),
    ...sobrescribir,
  }
}

function props(fuente: FuenteDeCombos, extra: { motivoSinAgregar?: string } = {}) {
  return { fuente, onCargar: vi.fn<(combo: ComboParaCargar) => void>(), onCerrar: vi.fn<() => void>(), ...extra }
}

async function esperarLaLista() {
  await screen.findByRole('listbox', { name: 'Combos' })
  await waitFor(() => expect(screen.queryByText('Cargando combos…')).not.toBeInTheDocument())
}

describe('ModalDeBusquedaDeCombos', () => {
  it('lista los combos con código, nombre, precio y resumen, y el foco arranca en el filtro', async () => {
    render(<ModalDeBusquedaDeCombos {...props(fuenteDe([deLista(1, 12, 'Pancho + coca')]))} />)

    await esperarLaLista()

    const opcion = screen.getByRole('option', { name: /C12/ })
    expect(opcion).toHaveTextContent('Pancho + coca')
    expect(opcion).toHaveTextContent('Pancho × 1 + Coca × 1')
    expect(opcion).toHaveTextContent('5.000')
    expect(screen.getByRole('combobox', { name: 'Buscar combo por nombre o código' })).toHaveFocus()
  })

  it('filtra por nombre sin importar tildes y mayúsculas, y por código', async () => {
    const usuario = userEvent.setup()
    render(<ModalDeBusquedaDeCombos {...props(fuenteDe([deLista(1, 12, 'Café + medialuna'), deLista(2, 30, 'Pancho + coca')]))} />)
    await esperarLaLista()
    const filtro = screen.getByRole('combobox')

    await usuario.type(filtro, 'CAFE')
    expect(screen.getAllByRole('option')).toHaveLength(1)
    expect(screen.getByRole('option')).toHaveTextContent('Café + medialuna')

    await usuario.clear(filtro)
    await usuario.type(filtro, 'c30')
    expect(screen.getByRole('option')).toHaveTextContent('Pancho + coca')

    await usuario.clear(filtro)
    await usuario.type(filtro, 'nada')
    expect(screen.queryAllByRole('option')).toHaveLength(0)
    expect(screen.getByText('Sin resultados')).toBeInTheDocument()
  })

  it('las flechas recorren la lista (con vuelta) y Enter elige el activo', async () => {
    const usuario = userEvent.setup()
    const p = props(fuenteDe([deLista(1, 1, 'Uno'), deLista(2, 2, 'Dos'), deLista(3, 3, 'Tres')]))
    render(<ModalDeBusquedaDeCombos {...p} />)
    await esperarLaLista()

    expect(screen.getByRole('option', { name: /Uno/ })).toHaveAttribute('aria-selected', 'true')
    await usuario.keyboard('{ArrowDown}{ArrowDown}')
    expect(screen.getByRole('option', { name: /Tres/ })).toHaveAttribute('aria-selected', 'true')
    await usuario.keyboard('{ArrowDown}')
    expect(screen.getByRole('option', { name: /Uno/ })).toHaveAttribute('aria-selected', 'true')
    await usuario.keyboard('{ArrowUp}')
    expect(screen.getByRole('option', { name: /Tres/ })).toHaveAttribute('aria-selected', 'true')

    await usuario.keyboard('{Enter}')

    await waitFor(() => expect(p.onCargar).toHaveBeenCalledTimes(1))
    expect(p.onCargar.mock.calls[0][0].idCombo).toBe(3)
  })

  it('un clic elige y entrega el combo con sus candidatos', async () => {
    const usuario = userEvent.setup()
    const p = props(fuenteDe([deLista(1, 1, 'Uno')]))
    render(<ModalDeBusquedaDeCombos {...p} />)
    await esperarLaLista()

    await usuario.click(screen.getByRole('option', { name: /Uno/ }))

    await waitFor(() => expect(p.onCargar).toHaveBeenCalledTimes(1))
    expect(p.onCargar.mock.calls[0][0].componentes).toHaveLength(2)
  })

  it('un combo no cargable se ve deshabilitado con el motivo y no se puede elegir', async () => {
    const usuario = userEvent.setup()
    const fuente = fuenteDe([deLista(1, 1, 'Roto', { motivoNoCargable: 'No hay artículos disponibles para «Gaseosas».' })])
    const p = props(fuente)
    render(<ModalDeBusquedaDeCombos {...p} />)
    await esperarLaLista()

    const opcion = screen.getByRole('option', { name: /Roto/ })
    expect(opcion).toHaveAttribute('aria-disabled', 'true')
    expect(opcion).toHaveTextContent('No se puede cargar: No hay artículos disponibles para «Gaseosas».')
    await usuario.click(opcion)
    await usuario.keyboard('{Enter}')

    expect(fuente.cargar).not.toHaveBeenCalled()
    expect(p.onCargar).not.toHaveBeenCalled()
  })

  it('con la API el motivo se descubre al elegir: marca la fila, avisa y no entrega nada', async () => {
    const usuario = userEvent.setup()
    const sinArticulos = { ...completo(1), componentes: [{ ...completo(1).componentes[0], candidatos: [] }, completo(1).componentes[1]] }
    const fuente = fuenteDe([deLista(1, 1, 'Uno')], { cargar: vi.fn().mockResolvedValue(sinArticulos) })
    const p = props(fuente)
    render(<ModalDeBusquedaDeCombos {...p} />)
    await esperarLaLista()

    await usuario.click(screen.getByRole('option', { name: /Uno/ }))

    await waitFor(() => expect(screen.getByRole('option', { name: /Uno/ })).toHaveAttribute('aria-disabled', 'true'))
    expect(screen.getByRole('option', { name: /Uno/ })).toHaveTextContent('No hay artículos disponibles para «Pancho».')
    expect(p.onCargar).not.toHaveBeenCalled()
  })

  it('si el detalle falla (404) lo avisa y deja elegir de nuevo', async () => {
    const usuario = userEvent.setup()
    const cargar = vi
      .fn()
      .mockRejectedValueOnce(new ErrorApi(404, 'no_encontrado', 'No existe el combo 1.'))
      .mockResolvedValueOnce(completo(1))
    const p = props(fuenteDe([deLista(1, 1, 'Uno')], { cargar }))
    render(<ModalDeBusquedaDeCombos {...p} />)
    await esperarLaLista()

    await usuario.click(screen.getByRole('option', { name: /Uno/ }))
    expect(await screen.findByText('No existe el combo 1.')).toBeInTheDocument()
    expect(p.onCargar).not.toHaveBeenCalled()

    await usuario.click(screen.getByRole('option', { name: /Uno/ }))
    await waitFor(() => expect(p.onCargar).toHaveBeenCalledTimes(1))
  })

  it('un doble Enter mientras se lee el detalle entrega el combo una sola vez', async () => {
    const usuario = userEvent.setup()
    let resolver: (c: ComboParaCargar) => void = () => {}
    const cargar = vi.fn(() => new Promise<ComboParaCargar>((r) => (resolver = r)))
    const p = props(fuenteDe([deLista(1, 1, 'Uno')], { cargar }))
    render(<ModalDeBusquedaDeCombos {...p} />)
    await esperarLaLista()

    await usuario.keyboard('{Enter}{Enter}')
    expect(cargar).toHaveBeenCalledTimes(1)
    resolver(completo(1))

    await waitFor(() => expect(p.onCargar).toHaveBeenCalledTimes(1))
    await usuario.keyboard('{Enter}')
    expect(cargar).toHaveBeenCalledTimes(1)
  })

  it('una respuesta que llega después de cerrar el modal no entrega nada', async () => {
    const usuario = userEvent.setup()
    let resolver: (c: ComboParaCargar) => void = () => {}
    const cargar = vi.fn(() => new Promise<ComboParaCargar>((r) => (resolver = r)))
    const p = props(fuenteDe([deLista(1, 1, 'Uno')], { cargar }))
    const { unmount } = render(<ModalDeBusquedaDeCombos {...p} />)
    await esperarLaLista()
    await usuario.keyboard('{Enter}')
    expect(cargar).toHaveBeenCalledTimes(1)

    unmount()
    resolver(completo(1))
    await Promise.resolve()
    await Promise.resolve()

    expect(p.onCargar).not.toHaveBeenCalled()
  })

  it('una lista que falla ofrece reintentar y la segunda lectura la reemplaza', async () => {
    const usuario = userEvent.setup()
    const listar = vi
      .fn()
      .mockRejectedValueOnce(new ErrorApi(500, 'error', 'Falló la lectura.'))
      .mockResolvedValueOnce([deLista(1, 1, 'Uno')])
    render(<ModalDeBusquedaDeCombos {...props(fuenteDe([], { listar }))} />)

    expect(await screen.findByText('Falló la lectura.')).toBeInTheDocument()
    await usuario.click(screen.getByRole('button', { name: 'Reintentar' }))

    expect(await screen.findByRole('option', { name: /Uno/ })).toBeInTheDocument()
    expect(screen.queryByText('Falló la lectura.')).not.toBeInTheDocument()
  })

  it('sin permiso para agregar (caja cerrada) avisa el motivo y no elige', async () => {
    const usuario = userEvent.setup()
    const fuente = fuenteDe([deLista(1, 1, 'Uno')])
    const p = props(fuente, { motivoSinAgregar: 'Caja cerrada: abrí la caja para vender.' })
    render(<ModalDeBusquedaDeCombos {...p} />)
    await esperarLaLista()

    expect(screen.getByText('Caja cerrada: abrí la caja para vender.')).toBeInTheDocument()
    await usuario.click(screen.getByRole('option', { name: /Uno/ }))
    await usuario.keyboard('{Enter}')

    expect(fuente.cargar).not.toHaveBeenCalled()
  })

  it('sin combos activos lo dice', async () => {
    render(<ModalDeBusquedaDeCombos {...props(fuenteDe([]))} />)

    expect(await screen.findByText('No hay combos activos')).toBeInTheDocument()
  })

  it('Escape cierra el modal', async () => {
    const usuario = userEvent.setup()
    const p = props(fuenteDe([deLista(1, 1, 'Uno')]))
    render(<ModalDeBusquedaDeCombos {...p} />)
    await esperarLaLista()

    await usuario.keyboard('{Escape}')

    expect(p.onCerrar).toHaveBeenCalledTimes(1)
  })
})
