import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { DatosDeEtiquetas, FilaDeEtiqueta, ListaPrecioAsignable, PuntoVentaListado, SolicitudDeEtiquetas } from '../api/tipos'
import type { EstadoDePuntoVenta } from '../puntoVenta/PuntoVentaContext'
import { EtiquetasGondola } from './EtiquetasGondola'

const apiGetMock = vi.fn()
const apiPostMock = vi.fn()

vi.mock('../api/cliente', () => ({
  api: {
    get: (...args: unknown[]) => apiGetMock(...(args as [string])),
    post: (...args: unknown[]) => apiPostMock(...(args as [string, unknown?])),
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

const puntoVenta = { id: 7, idTenant: 1, idEmpresa: 1, nombre: 'Local Centro' } as PuntoVentaListado
const estadoDePuntoVenta: EstadoDePuntoVenta = {
  puntosVenta: [puntoVenta],
  puntoVenta,
  elegir: vi.fn(),
  recargar: vi.fn(() => Promise.resolve()),
}

vi.mock('../puntoVenta/usePuntoVenta', () => ({
  usePuntoVenta: () => estadoDePuntoVenta,
}))

const TOTAL_DE_ARTICULOS = 30
const TAMANIO = 25
const listas: ListaPrecioAsignable[] = [
  { id: 4, nombre: 'Mayorista', esDefault: false },
  { id: 3, nombre: 'General', esDefault: true },
]
const oferta = { idOferta: 1, nombre: '15%', descuentoUnitario: 15 }

function fila(id: number, idLista: number): FilaDeEtiqueta {
  const precio = idLista === 3 ? 100 * id : 80 * id
  return {
    idArticulo: id,
    codigoInterno: `A${id}`,
    codigoBarra: null,
    nombre: `Artículo ${id}`,
    unidadVenta: 'Unidad',
    precioOriginal: precio,
    precioFinal: id % 10 === 0 ? precio - 15 : precio,
    ofertas: id % 10 === 0 ? [oferta] : [],
    promociones: [],
  }
}

function datosDe(solicitud: SolicitudDeEtiquetas): DatosDeEtiquetas {
  const ids = solicitud.idsArticulo ?? Array.from({ length: TOTAL_DE_ARTICULOS }, (_, i) => i + 1)
  const filas = ids.map((id) => fila(id, solicitud.idListaPrecio)).filter((f) => !solicitud.filtro?.soloConOfertaVigente || f.ofertas.length > 0)
  return { idListaPrecio: solicitud.idListaPrecio, nombreDeLista: 'General', momento: '2026-11-27T10:00:00Z', filas, excluidos: [], truncado: false }
}

function articulosDe(ruta: string) {
  const consulta = new URLSearchParams(ruta.split('?')[1])
  const pagina = Number(consulta.get('pagina'))
  const desde = (pagina - 1) * TAMANIO
  const items = Array.from({ length: TOTAL_DE_ARTICULOS }, (_, i) => i + 1)
    .slice(desde, desde + TAMANIO)
    .map((id) => ({ id, codigoInterno: `A${id}`, nombre: `Artículo ${id}` }))
  return { items, total: TOTAL_DE_ARTICULOS, pagina, tamanio: TAMANIO }
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/listas-precio') return Promise.resolve(listas)
    if (ruta.startsWith('/articulos?')) return Promise.resolve(articulosDe(ruta))
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
  apiPostMock.mockImplementation((ruta: string, cuerpo: SolicitudDeEtiquetas) => {
    if (ruta === '/etiquetas/datos') return Promise.resolve(datosDe(cuerpo))
    return Promise.reject(new Error(`ruta no mockeada en el test: ${ruta}`))
  })
})

afterEach(() => {
  vi.restoreAllMocks()
})

function solicitudes(): SolicitudDeEtiquetas[] {
  return apiPostMock.mock.calls.map((llamada) => llamada[1] as SolicitudDeEtiquetas)
}

/** Espera el DATO: la lista por defecto elegida y la primera fila de la grilla, no el select vacío. */
async function renderYEsperarGrilla() {
  render(<EtiquetasGondola />)
  await screen.findByRole('option', { name: 'General' })
  await waitFor(() => expect(screen.getByLabelText('Lista de precios')).toHaveValue('3'))
  await screen.findByText('Artículo 1')
  await screen.findByText('$ 100')
}

describe('EtiquetasGondola', () => {
  it('elige la lista por defecto y pide los precios con ella', async () => {
    await renderYEsperarGrilla()
    await waitFor(() => expect(solicitudes().some((s) => s.idListaPrecio === 3 && s.idPuntoVenta === 7)).toBe(true))
    expect(solicitudes().every((s) => s.idListaPrecio === 3)).toBe(true)
  })

  it('al cambiar de lista la grilla muestra los precios de esa lista', async () => {
    await renderYEsperarGrilla()
    await userEvent.selectOptions(screen.getByLabelText('Lista de precios'), '4')
    await screen.findByText('$ 80')
  })

  it('ofrece 10 plantillas: 5 claras y 5 oscuras', async () => {
    await renderYEsperarGrilla()
    const plantilla = screen.getByLabelText('Plantilla')
    expect(within(plantilla).getAllByRole('option')).toHaveLength(10)
    expect(within(within(plantilla).getByRole('group', { name: 'Claras' })).getAllByRole('option')).toHaveLength(5)
    expect(within(within(plantilla).getByRole('group', { name: 'Oscuras' })).getAllByRole('option')).toHaveLength(5)
  })

  it('Imprimir está deshabilitado sin selección', async () => {
    await renderYEsperarGrilla()
    expect(screen.getByRole('button', { name: 'Imprimir' })).toBeDisabled()
  })

  it('conserva la selección entre páginas y arma la vista previa con los elegidos', async () => {
    let hojasEnLaImpresion = 0
    const imprimir = vi.spyOn(window, 'print').mockImplementation(() => {
      hojasEnLaImpresion = document.querySelectorAll('body > .gondola-impresion [data-testid="gondola-hoja"]').length
    })
    await renderYEsperarGrilla()

    await userEvent.click(screen.getByLabelText('Seleccionar Artículo 1'))
    await userEvent.click(screen.getByRole('button', { name: 'Siguiente' }))
    await userEvent.click(await screen.findByLabelText('Seleccionar Artículo 26'))
    expect(screen.getByText('2 seleccionados')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Anterior' }))
    expect(await screen.findByLabelText('Seleccionar Artículo 1')).toBeChecked()

    await waitFor(() => expect(solicitudes().some((s) => s.idsArticulo?.join(',') === '1,26')).toBe(true))
    const imprimirBoton = screen.getByRole('button', { name: 'Imprimir' })
    await waitFor(() => expect(imprimirBoton).toBeEnabled())
    expect(screen.getAllByTestId('gondola-hoja')).toHaveLength(1)
    expect(screen.getAllByTestId('gondola-etiqueta')).toHaveLength(2)

    await userEvent.click(imprimirBoton)
    expect(imprimir).toHaveBeenCalledTimes(1)
    expect(hojasEnLaImpresion).toBe(1)
    expect(document.querySelector('.gondola-impresion')).toBeNull()
  })

  it('seleccionar la página elige las 25 filas y limpiar vacía la selección', async () => {
    await renderYEsperarGrilla()
    await userEvent.click(screen.getByLabelText('Seleccionar la página'))
    expect(screen.getByText('25 seleccionados')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Limpiar selección' }))
    expect(screen.getByText('0 seleccionados')).toBeInTheDocument()
  })

  it('con Ofertas pide el filtro de ofertas vigentes y lista solo esos artículos', async () => {
    await renderYEsperarGrilla()
    await userEvent.click(screen.getByLabelText('Ofertas'))

    await waitFor(() => expect(solicitudes().some((s) => s.filtro?.soloConOfertaVigente === true)).toBe(true))
    await waitFor(() => expect(screen.queryByText('Artículo 1')).not.toBeInTheDocument())
    expect(screen.getByText('Artículo 10')).toBeInTheDocument()
    expect(screen.getByText('Artículo 20')).toBeInTheDocument()
    expect(screen.getByText('Artículo 30')).toBeInTheDocument()
  })

  it('con Ofertas la hoja imprime el precio de oferta y el original tachado', async () => {
    await renderYEsperarGrilla()
    await userEvent.click(screen.getByLabelText('Ofertas'))
    await screen.findByText('Artículo 10')
    await userEvent.click(screen.getByLabelText('Seleccionar Artículo 10'))

    expect(await screen.findByTestId('gondola-precio-tachado')).toHaveTextContent('$ 1.000')
    expect(screen.getByTestId('gondola-precio')).toHaveTextContent('$ 985')
  })
})
