import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type {
  DatosDeEtiquetas,
  FilaDeEtiqueta,
  FilaDeGrillaDeArticulos,
  ListaPrecioAsignable,
  PaginaDeGrillaDeArticulos,
  PuntoVentaListado,
  SolicitudDeEtiquetas,
} from '../api/tipos'
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
const listas: ListaPrecioAsignable[] = [
  { id: 4, nombre: 'Mayorista', esDefault: false },
  { id: 3, nombre: 'General', esDefault: true },
]
const oferta = { idOferta: 1, nombre: '15%', descuentoUnitario: 15 }

function conOferta(id: number): boolean {
  return id % 10 === 0
}

function fila(id: number, idLista: number): FilaDeEtiqueta {
  const precio = idLista === 3 ? 100 * id : 80 * id
  return {
    idArticulo: id,
    codigoInterno: `A${id}`,
    codigoBarra: null,
    nombre: `Artículo ${id}`,
    unidadVenta: 'Unidad',
    precioOriginal: precio,
    precioFinal: conOferta(id) ? precio - 15 : precio,
    ofertas: conOferta(id) ? [oferta] : [],
    promociones: [],
  }
}

function datosDe(solicitud: SolicitudDeEtiquetas): DatosDeEtiquetas {
  const ids = solicitud.idsArticulo ?? []
  return { idListaPrecio: solicitud.idListaPrecio, nombreDeLista: 'General', momento: '2026-11-27T10:00:00Z', filas: ids.map((id) => fila(id, solicitud.idListaPrecio)), excluidos: [], truncado: false }
}

function filaDeGrilla(id: number): FilaDeGrillaDeArticulos {
  return {
    id,
    codigoInterno: `A${id}`,
    nombre: `Artículo ${id}`,
    precio: 100 * id,
    costoNominal: 50 * id,
    idProveedorHabitual: null,
    proveedor: null,
    activo: true,
    disponibleOnline: false,
    disponibleVenta: true,
    tieneImagen: false,
  }
}

function grillaDe(ruta: string): PaginaDeGrillaDeArticulos {
  const consulta = new URLSearchParams(ruta.split('?')[1])
  const pagina = Number(consulta.get('pagina'))
  const tamanio = Number(consulta.get('tamanio'))
  const soloOfertas = consulta.get('conOfertaVigente') === 'true'
  const todos = Array.from({ length: TOTAL_DE_ARTICULOS }, (_, i) => i + 1).filter((id) => !soloOfertas || conOferta(id))
  const desde = (pagina - 1) * tamanio
  return { items: todos.slice(desde, desde + tamanio).map(filaDeGrilla), total: todos.length, pagina, tamanio, nombreListaPrecio: 'General' }
}

function rutasDeGrilla(): string[] {
  return apiGetMock.mock.calls.map((llamada) => llamada[0] as string).filter((ruta) => ruta.startsWith('/articulos/grilla'))
}

beforeEach(() => {
  apiGetMock.mockReset()
  apiPostMock.mockReset()
  apiGetMock.mockImplementation((ruta: string) => {
    if (ruta === '/listas-precio') return Promise.resolve(listas)
    if (ruta.startsWith('/proveedores')) return Promise.resolve({ items: [], total: 0, pagina: 1, tamanio: 200 })
    if (ruta.startsWith('/articulos/grilla')) return Promise.resolve(grillaDe(ruta))
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
}

describe('EtiquetasGondola', () => {
  it('lista los artículos con la grilla de artículos: columnas, filtros y selección, sin acciones', async () => {
    await renderYEsperarGrilla()
    expect(screen.getByLabelText('Filtrar por código')).toBeInTheDocument()
    expect(screen.getByLabelText('Filtrar por proveedor')).toBeInTheDocument()
    expect(screen.getByRole('columnheader', { name: /Costo/ })).toBeInTheDocument()
    expect(screen.queryByRole('columnheader', { name: 'Acciones' })).not.toBeInTheDocument()
    expect(screen.getByLabelText('Seleccionar Artículo 1')).toBeInTheDocument()
    expect(rutasDeGrilla()[0]).toBe('/articulos/grilla?pagina=1&tamanio=25')
  })

  it('elige la lista por defecto y pide los precios con ella', async () => {
    await renderYEsperarGrilla()
    await userEvent.click(screen.getByLabelText('Seleccionar Artículo 1'))
    await waitFor(() => expect(solicitudes().some((s) => s.idListaPrecio === 3 && s.idPuntoVenta === 7)).toBe(true))
    expect(solicitudes().every((s) => s.idListaPrecio === 3)).toBe(true)
  })

  it('ofrece las 10 plantillas en una sola lista', async () => {
    await renderYEsperarGrilla()
    const plantilla = screen.getByLabelText('Plantilla')
    expect(within(plantilla).getAllByRole('option')).toHaveLength(10)
    expect(within(plantilla).queryByRole('group')).not.toBeInTheDocument()
  })

  it('Imprimir y Vista previa están deshabilitados sin selección', async () => {
    await renderYEsperarGrilla()
    expect(screen.getByRole('button', { name: 'Imprimir' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Vista previa' })).toBeDisabled()
  })

  it('las hojas no se muestran en la página: solo aparecen al abrir la vista previa, y se cierra', async () => {
    await renderYEsperarGrilla()
    await userEvent.click(screen.getByLabelText('Seleccionar Artículo 1'))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Imprimir' })).toBeEnabled())
    expect(screen.queryByTestId('gondola-hoja')).not.toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Vista previa' }))
    const dialogo = await screen.findByRole('dialog')
    expect(within(dialogo).getAllByTestId('gondola-hoja')).toHaveLength(1)
    expect(within(dialogo).getAllByTestId('gondola-etiqueta')).toHaveLength(1)

    await userEvent.click(within(dialogo).getByRole('button', { name: 'Cerrar vista previa' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.queryByTestId('gondola-hoja')).not.toBeInTheDocument()
  })

  it('conserva la selección entre páginas y arma la vista previa con los elegidos', async () => {
    await renderYEsperarGrilla()

    await userEvent.click(screen.getByLabelText('Seleccionar Artículo 1'))
    await userEvent.click(screen.getByRole('button', { name: 'Siguiente' }))
    await userEvent.click(await screen.findByLabelText('Seleccionar Artículo 26'))
    expect(screen.getByText('2 seleccionados')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Anterior' }))
    expect(await screen.findByLabelText('Seleccionar Artículo 1')).toBeChecked()

    await waitFor(() => expect(solicitudes().some((s) => s.idsArticulo?.join(',') === '1,26')).toBe(true))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Vista previa' })).toBeEnabled())
    await userEvent.click(screen.getByRole('button', { name: 'Vista previa' }))
    const dialogo = await screen.findByRole('dialog')
    expect(within(dialogo).getAllByTestId('gondola-etiqueta')).toHaveLength(2)
  })

  it('Imprimir desde la vista previa imprime las hojas por el portal del body y no deja nada colgado', async () => {
    let hojasEnLaImpresion = 0
    let modalEnLaImpresion = false
    const imprimir = vi.spyOn(window, 'print').mockImplementation(() => {
      hojasEnLaImpresion = document.querySelectorAll('body > .gondola-impresion [data-testid="gondola-hoja"]').length
      modalEnLaImpresion = document.querySelector('body > .gondola-impresion [role="dialog"]') !== null
    })
    await renderYEsperarGrilla()
    await userEvent.click(screen.getByLabelText('Seleccionar Artículo 1'))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Vista previa' })).toBeEnabled())
    await userEvent.click(screen.getByRole('button', { name: 'Vista previa' }))
    const dialogo = await screen.findByRole('dialog')
    const imprimirBoton = within(dialogo).getByRole('button', { name: 'Imprimir' })
    await waitFor(() => expect(imprimirBoton).toBeEnabled())

    await userEvent.click(imprimirBoton)

    expect(imprimir).toHaveBeenCalledTimes(1)
    expect(hojasEnLaImpresion).toBe(1)
    expect(modalEnLaImpresion).toBe(false)
    expect(document.querySelector('.gondola-impresion')).toBeNull()
  })

  it('Imprimir de la página sigue imprimiendo sin abrir la vista previa', async () => {
    const imprimir = vi.spyOn(window, 'print').mockImplementation(() => undefined)
    await renderYEsperarGrilla()
    await userEvent.click(screen.getByLabelText('Seleccionar Artículo 1'))
    const boton = screen.getByRole('button', { name: 'Imprimir' })
    await waitFor(() => expect(boton).toBeEnabled())
    await userEvent.click(boton)
    expect(imprimir).toHaveBeenCalledTimes(1)
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('seleccionar la página elige las 25 filas y limpiar vacía la selección', async () => {
    await renderYEsperarGrilla()
    await userEvent.click(screen.getByLabelText('Seleccionar la página'))
    expect(screen.getByText('25 seleccionados')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Limpiar selección' }))
    expect(screen.getByText('0 seleccionados')).toBeInTheDocument()
  })

  it('con Ofertas pide el filtro de oferta vigente al servidor con el punto de venta y la lista', async () => {
    await renderYEsperarGrilla()
    await userEvent.click(screen.getByLabelText('Ofertas'))

    await waitFor(() => expect(screen.queryByText('Artículo 1')).not.toBeInTheDocument())
    expect(rutasDeGrilla().at(-1)).toBe('/articulos/grilla?conOfertaVigente=true&idPuntoVenta=7&idListaPrecio=3&pagina=1&tamanio=25')
    expect(screen.getByText('Artículo 10')).toBeInTheDocument()
    expect(screen.getByText('Artículo 20')).toBeInTheDocument()
    expect(screen.getByText('Artículo 30')).toBeInTheDocument()
  })

  it('con Ofertas la hoja imprime el precio de oferta y el original tachado', async () => {
    await renderYEsperarGrilla()
    await userEvent.click(screen.getByLabelText('Ofertas'))
    await screen.findByText('Artículo 10')
    await userEvent.click(screen.getByLabelText('Seleccionar Artículo 10'))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Vista previa' })).toBeEnabled())
    await userEvent.click(screen.getByRole('button', { name: 'Vista previa' }))

    expect(await screen.findByTestId('gondola-precio-tachado')).toHaveTextContent('$ 1.000')
    expect(screen.getByTestId('gondola-precio')).toHaveTextContent('$ 985')
  })
})
