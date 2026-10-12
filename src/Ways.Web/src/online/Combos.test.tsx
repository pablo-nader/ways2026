import { act, cleanup, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { StrictMode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { ErrorApi } from '../api/cliente'
import type { CatalogoPublico, ComboPublico, ConsultaDeCatalogo } from '../api/publico'
import type { VitrinaPublica } from '../api/tipos'
import '../publico/publico.css'
import { claveDelCarrito } from './carrito'
import {
  articuloPublico,
  catalogoPublico,
  comboPublico,
  componenteDeCombo,
  diferida,
  itemsGuardados,
} from './datosDePrueba'
import { Tienda } from './Tienda'

const obtenerVitrinaMock = vi.fn()
const obtenerCatalogoMock = vi.fn<(consulta: ConsultaDeCatalogo) => Promise<CatalogoPublico>>()
const obtenerCombosMock = vi.fn<() => Promise<ComboPublico[]>>()

vi.mock('../api/publico', async (importarOriginal) => ({
  ...(await importarOriginal<typeof import('../api/publico')>()),
  clienteDePublico: {
    obtenerVitrina: () => obtenerVitrinaMock(),
    obtenerCatalogo: (consulta: ConsultaDeCatalogo) => obtenerCatalogoMock(consulta),
    obtenerCombos: () => obtenerCombosMock(),
  },
}))

const vitrina: VitrinaPublica = {
  nombre: 'Kiosco Sur',
  tagline: null,
  descripcion: null,
  colorAcento: null,
  tema: 'claro',
  tieneLogo: false,
  logoVersion: null,
  sucursales: [],
}

const gaseosa = componenteDeCombo(10, { nombre: 'Coca Cola 1,5 L', cantidad: 2, imagenVersion: 7 })
const pan = componenteDeCombo(11, { nombre: 'Pan', cantidad: 1, imagenVersion: 8 })
const merienda = comboPublico(1, { nombre: 'Combo merienda', precio: 500, componentes: [gaseosa, pan] })
const asado = comboPublico(2, {
  nombre: 'Combo asado',
  precio: 1200.5,
  componentes: [componenteDeCombo(20, { nombre: 'Carbón', cantidad: 1 })],
})
const martillo = articuloPublico(1, { nombre: 'Martillo', precio: 100 })

/** Deja correr las promesas ya resueltas o rechazadas: sirve para afirmar que algo NO apareció. */
const reposar = () =>
  act(async () => {
    await new Promise((resolver) => setTimeout(resolver, 0))
  })

async function verTienda(combos: ComboPublico[] | Error = [merienda, asado], envoltura: 'normal' | 'estricto' = 'normal') {
  obtenerVitrinaMock.mockResolvedValue(vitrina)
  obtenerCatalogoMock.mockResolvedValue(catalogoPublico({ articulos: [martillo] }))
  if (combos instanceof Error) obtenerCombosMock.mockRejectedValue(combos)
  else obtenerCombosMock.mockResolvedValue(combos)
  render(envoltura === 'estricto' ? <StrictMode><Tienda /></StrictMode> : <Tienda />)
  await screen.findByRole('heading', { level: 2, name: 'Martillo' })
}

function seccionDeCombos() {
  return screen.getByRole('region', { name: 'Combos' })
}

function regionDeAnuncios() {
  return document.querySelector('.publico-solo-lectura[aria-live="polite"]') as HTMLElement
}

function tarjetaDelCombo(nombre: string) {
  return screen.getByRole('heading', { level: 3, name: nombre }).closest('li') as HTMLElement
}

async function abrirElPanel() {
  await userEvent.click(screen.getByRole('button', { name: /^Carrito/ }))

  return screen.getByRole('dialog', { name: 'Carrito' })
}

function totalDelPanel(panel: HTMLElement) {
  return (within(panel).getByText('Total').parentElement as HTMLElement).textContent
}

const scrollIntoViewOriginal = Element.prototype.scrollIntoView

beforeEach(() => {
  obtenerVitrinaMock.mockReset()
  obtenerCatalogoMock.mockReset()
  obtenerCombosMock.mockReset()
})

afterEach(() => {
  cleanup()
  // jsdom no define `scrollIntoView`: los tests de `#combos` lo reemplazan y acá se vuelve a como estaba, pase o falle la prueba.
  if (scrollIntoViewOriginal === undefined) delete (Element.prototype as { scrollIntoView?: unknown }).scrollIntoView
  else Element.prototype.scrollIntoView = scrollIntoViewOriginal
  localStorage.clear()
  window.history.replaceState(null, '', '/')
  document.documentElement.removeAttribute('data-bs-theme')
  document.documentElement.style.removeProperty('--vitrina-acento')
  document.title = ''
  document.body.style.overflow = ''
  document.head.querySelector('meta[name="description"]')?.remove()
  vi.restoreAllMocks()
})

describe('sección de combos de la tienda', () => {
  it('muestra "Combos" sobre la grilla de artículos, después de la barra, con una tarjeta por combo', async () => {
    await verTienda()
    await screen.findByRole('heading', { level: 3, name: 'Combo merienda' })

    const seccion = seccionDeCombos()
    const grilla = screen.getByRole('heading', { level: 2, name: 'Martillo' }).closest('ul') as HTMLElement
    const barra = screen.getByRole('search', { hidden: false })

    expect(within(seccion).getByRole('heading', { level: 2, name: 'Combos' })).toBeInTheDocument()
    expect(within(seccion).getAllByRole('heading', { level: 3 }).map((h) => h.textContent)).toEqual(['Combo merienda', 'Combo asado'])
    expect(barra.compareDocumentPosition(seccion) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(seccion.compareDocumentPosition(grilla) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(seccion).toHaveAttribute('id', 'combos')
  })

  it('cada tarjeta lleva el nombre, el precio fijo, los componentes con su cantidad y ningún código', async () => {
    await verTienda()
    await screen.findByRole('heading', { level: 3, name: 'Combo merienda' })

    const tarjeta = tarjetaDelCombo('Combo merienda')

    expect(within(tarjeta).getByText('$ 500,00')).toBeInTheDocument()
    expect(within(tarjeta).getAllByRole('listitem').map((li) => li.textContent)).toEqual(['2 × Coca Cola 1,5 L', '1 × Pan'])
    expect(tarjeta.textContent).not.toMatch(/\bC\d+\b/)
    expect(within(tarjeta).queryByText(/ahorr/i)).toBeNull()
    expect(within(tarjetaDelCombo('Combo asado')).getByText('$ 1.200,50')).toBeInTheDocument()
  })

  it('la tira de imágenes usa las de los componentes con la versión en la URL, hasta tres', async () => {
    const cuatro = comboPublico(3, {
      nombre: 'Combo grande',
      componentes: [1, 2, 3, 4].map((n) => componenteDeCombo(30 + n, { nombre: `Cosa ${n}`, imagenVersion: n })),
    })
    await verTienda([merienda, cuatro])
    await screen.findByRole('heading', { level: 3, name: 'Combo grande' })

    const imagenes = (nombre: string) => Array.from(tarjetaDelCombo(nombre).querySelectorAll('.publico-combo-miniaturas img'))

    expect(imagenes('Combo merienda').map((i) => i.getAttribute('src'))).toEqual([
      '/api/publico/articulos/10/imagen?v=7',
      '/api/publico/articulos/11/imagen?v=8',
    ])
    expect(imagenes('Combo grande').map((i) => i.getAttribute('src'))).toEqual([
      '/api/publico/articulos/31/imagen?v=1',
      '/api/publico/articulos/32/imagen?v=2',
      '/api/publico/articulos/33/imagen?v=3',
    ])
    expect(imagenes('Combo grande')[0]).toHaveAttribute('loading', 'lazy')
  })

  it('los componentes sin imagen no aportan miniatura; sin ninguna se ve la marca de Ways', async () => {
    const mixto = comboPublico(3, {
      nombre: 'Combo mixto',
      componentes: [componenteDeCombo(40, { imagenVersion: null }), componenteDeCombo(41, { imagenVersion: 5 })],
    })
    await verTienda([asado, mixto])
    await screen.findByRole('heading', { level: 3, name: 'Combo mixto' })

    const sinNinguna = tarjetaDelCombo('Combo asado')
    const conUna = tarjetaDelCombo('Combo mixto')

    expect(sinNinguna.querySelector('.publico-combo-miniaturas img')).toBeNull()
    expect(sinNinguna.querySelector('.publico-combo-miniaturas .publico-articulo-marca')).not.toBeNull()
    expect(Array.from(conUna.querySelectorAll('.publico-combo-miniaturas img'))).toHaveLength(1)
  })

  it('una imagen que falla se reemplaza por la marca de Ways', async () => {
    await verTienda()
    await screen.findByRole('heading', { level: 3, name: 'Combo merienda' })
    const tarjeta = tarjetaDelCombo('Combo merienda')

    act(() => {
      tarjeta.querySelector('.publico-combo-miniaturas img')?.dispatchEvent(new Event('error'))
    })

    expect(tarjeta.querySelectorAll('.publico-combo-miniaturas img')).toHaveLength(1)
    expect(tarjeta.querySelector('.publico-combo-miniaturas .publico-articulo-marca')).not.toBeNull()
  })

  describe('cuándo no se dibuja', () => {
    it('con una lista vacía no hay sección', async () => {
      await verTienda([])
      await waitFor(() => expect(obtenerCombosMock).toHaveBeenCalledTimes(1))
      await reposar()

      expect(screen.queryByRole('region', { name: 'Combos' })).toBeNull()
      expect(screen.queryByRole('heading', { name: 'Combos' })).toBeNull()
    })

    it.each([
      ['la vitrina no publicada (404)', new ErrorApi(404, 'vitrina_no_publicada', 'No publicada')],
      ['un error de red', new Error('sin red')],
    ])('con %s no hay sección ni aviso de error', async (_, error) => {
      await verTienda(error)
      await waitFor(() => expect(obtenerCombosMock).toHaveBeenCalledTimes(1))
      await reposar()

      expect(screen.queryByRole('region', { name: 'Combos' })).toBeNull()
      expect(screen.queryByRole('alert')).toBeNull()
      expect(screen.getByRole('heading', { level: 2, name: 'Martillo' })).toBeInTheDocument()
    })

    it.each([
      ['una búsqueda', '?buscar=martillo'],
      ['una categoría', '?categoria=5'],
    ])('con %s activa los combos no se muestran, porque no se filtran', async (_, consulta) => {
      window.history.replaceState(null, '', `/online${consulta}`)
      await verTienda()
      await waitFor(() => expect(obtenerCombosMock).toHaveBeenCalledTimes(1))
      await reposar()

      expect(screen.queryByRole('region', { name: 'Combos' })).toBeNull()
    })

    it('una respuesta tardía de una lectura vieja no pisa a la más nueva (dos lecturas del catálogo seguidas)', async () => {
      const primera = diferida<ComboPublico[]>()
      const segunda = diferida<ComboPublico[]>()
      obtenerCombosMock.mockReturnValueOnce(primera.promesa).mockReturnValueOnce(segunda.promesa)
      obtenerVitrinaMock.mockResolvedValue(vitrina)
      obtenerCatalogoMock.mockResolvedValue(catalogoPublico({ articulos: [martillo], total: 30 }))
      render(<Tienda />)
      await userEvent.click(await screen.findByRole('button', { name: 'Ver más' }))
      await waitFor(() => expect(obtenerCombosMock).toHaveBeenCalledTimes(2))

      await act(async () => segunda.resolver([asado]))
      await screen.findByRole('heading', { level: 3, name: 'Combo asado' })
      await act(async () => primera.resolver([merienda]))
      await reposar()

      expect(screen.getAllByRole('heading', { level: 3 }).map((h) => h.textContent)).toEqual(['Combo asado'])
    })

    // Solo prueba que no se lanza ni se registra un error: React 19 no avisa de un `setState` sobre un componente desmontado,
    // así que el descarte por generación no es observable desde acá.
    it('una respuesta que llega con la tienda ya desmontada termina sin lanzar ni registrar errores', async () => {
      const lectura = diferida<ComboPublico[]>()
      obtenerCombosMock.mockReturnValue(lectura.promesa)
      obtenerVitrinaMock.mockResolvedValue(vitrina)
      obtenerCatalogoMock.mockResolvedValue(catalogoPublico({ articulos: [martillo] }))
      const vista = render(<Tienda />)
      await screen.findByRole('heading', { level: 2, name: 'Martillo' })
      const errores = vi.spyOn(console, 'error').mockImplementation(() => {})

      vista.unmount()
      await act(async () => lectura.resolver([merienda]))

      expect(errores).not.toHaveBeenCalled()
    })
  })

  describe('llegar con #combos', () => {
    it('lleva a la sección apenas se dibuja, una sola vez', async () => {
      const llevar = vi.fn()
      Element.prototype.scrollIntoView = llevar
      window.history.replaceState(null, '', '/online#combos')

      await verTienda()
      await screen.findByRole('heading', { level: 3, name: 'Combo merienda' })

      expect(llevar).toHaveBeenCalledTimes(1)
      expect(llevar.mock.contexts[0]).toBe(seccionDeCombos())
    })

    it('sin el ancla no mueve la página', async () => {
      const llevar = vi.fn()
      Element.prototype.scrollIntoView = llevar

      await verTienda()
      await screen.findByRole('heading', { level: 3, name: 'Combo merienda' })

      expect(llevar).not.toHaveBeenCalled()
    })
  })
})

describe('combos en el carrito', () => {
  it('"Agregar" crea una línea de combo: la tarjeta pasa al selector de cantidad entera, y el botón del carrito cuenta una línea', async () => {
    await verTienda()
    await screen.findByRole('heading', { level: 3, name: 'Combo merienda' })

    await userEvent.click(screen.getByRole('button', { name: 'Agregar Combo merienda al carrito' }))
    await userEvent.click(screen.getByRole('button', { name: 'Agregar uno más de Combo merienda' }))

    expect(screen.getByRole('textbox', { name: 'Cantidad de Combo merienda' })).toHaveValue('2')
    expect(screen.getByRole('button', { name: 'Carrito, 1 ítem' })).toBeInTheDocument()
    expect(itemsGuardados(localStorage.getItem(claveDelCarrito(window.location.host)))).toEqual([
      expect.objectContaining({ tipo: 'combo', idCombo: 1, nombre: 'Combo merienda', precio: 500, cantidad: 2 }),
    ])
  })

  it('el panel muestra el combo con sus componentes bajo el nombre, el subtotal y el total', async () => {
    await verTienda()
    await screen.findByRole('heading', { level: 3, name: 'Combo merienda' })
    await userEvent.click(screen.getByRole('button', { name: 'Agregar Combo merienda al carrito' }))
    await userEvent.click(screen.getByRole('button', { name: 'Agregar uno más de Combo merienda' }))
    await userEvent.click(screen.getByRole('button', { name: 'Agregar Martillo al carrito' }))

    const panel = await abrirElPanel()
    const linea = within(panel).getByText('Combo merienda').closest('li') as HTMLElement

    expect(linea).toHaveTextContent('2 × Coca Cola 1,5 L · 1 × Pan')
    expect(linea).toHaveTextContent('Combo · $ 500,00')
    expect(within(linea).getByText('$ 1.000,00')).toBeInTheDocument()
    expect(within(linea).getByRole('textbox', { name: 'Cantidad de Combo merienda' })).toHaveValue('2')
    expect(totalDelPanel(panel)).toBe('Total$ 1.100,00')
  })

  it('en el panel se cambia la cantidad del combo con tope en 99 y se lo quita', async () => {
    await verTienda()
    await screen.findByRole('heading', { level: 3, name: 'Combo merienda' })
    await userEvent.click(screen.getByRole('button', { name: 'Agregar Combo merienda al carrito' }))
    const panel = await abrirElPanel()
    const campo = within(panel).getByRole('textbox', { name: 'Cantidad de Combo merienda' })

    await userEvent.clear(campo)
    await userEvent.type(campo, '99')
    await userEvent.tab()

    expect(campo).toHaveValue('99')
    expect(within(panel).getByRole('button', { name: 'Agregar uno más de Combo merienda' })).toBeDisabled()
    expect(totalDelPanel(panel)).toBe('Total$ 49.500,00')

    await userEvent.clear(campo)
    await userEvent.type(campo, '100')
    await userEvent.tab()

    expect(campo).toHaveValue('99')
    expect(within(panel).getByText(/Ingresá una cantidad entera, hasta 99/)).toBeInTheDocument()

    await userEvent.click(within(panel).getByRole('button', { name: 'Quitar Combo merienda del carrito' }))

    expect(within(panel).getByText('Tu carrito está vacío')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Agregar Combo merienda al carrito' })).toBeInTheDocument()
  })

  it('una línea de combo guardada se pone al día con la lista de combos de la tienda', async () => {
    localStorage.setItem(
      claveDelCarrito(window.location.host),
      JSON.stringify({
        version: 2,
        items: [{ tipo: 'combo', idCombo: 1, nombre: 'Viejo', precio: 300, cantidad: 2, componentes: [{ idArticulo: 11, nombre: 'Pan', cantidad: 1, imagenVersion: null }] }],
      }),
    )
    await verTienda()
    await screen.findByRole('heading', { level: 3, name: 'Combo merienda' })

    const panel = await abrirElPanel()
    const linea = within(panel).getByText('Combo merienda').closest('li') as HTMLElement

    expect(within(panel).queryByText('Viejo')).toBeNull()
    expect(linea).toHaveTextContent('2 × Coca Cola 1,5 L · 1 × Pan')
    expect(totalDelPanel(panel)).toBe('Total$ 1.000,00')
  })

  describe('un combo que ya no está en la tienda', () => {
    const guardarDosCombos = () =>
      localStorage.setItem(
        claveDelCarrito(window.location.host),
        JSON.stringify({
          version: 2,
          items: [
            { tipo: 'combo', idCombo: 1, nombre: 'Combo merienda', precio: 500, cantidad: 1, componentes: [{ idArticulo: 11, nombre: 'Pan', cantidad: 1, imagenVersion: null }] },
            { tipo: 'combo', idCombo: 9, nombre: 'Combo de verano', precio: 800, cantidad: 3, componentes: [{ idArticulo: 90, nombre: 'Helado', cantidad: 2, imagenVersion: null }] },
          ],
        }),
      )

    it('queda en el panel con el aviso "Ya no está disponible", sin selector, y no suma al total', async () => {
      guardarDosCombos()
      await verTienda([merienda])
      await screen.findByRole('heading', { level: 3, name: 'Combo merienda' })

      const panel = await abrirElPanel()
      const linea = within(panel).getByText('Combo de verano').closest('li') as HTMLElement

      expect(within(linea).getByRole('status')).toHaveTextContent('Ya no está disponible. No se suma al total.')
      expect(linea).toHaveTextContent('2 × Helado')
      expect(within(linea).queryByRole('textbox')).toBeNull()
      expect(within(linea).queryByText('$ 2.400,00')).toBeNull()
      expect(totalDelPanel(panel)).toBe('Total$ 500,00')
      expect(within(panel).getByText('Combo merienda').closest('li')).not.toHaveTextContent('Ya no está disponible')
    })

    it('se puede quitar, y quitarlo no toca el otro', async () => {
      guardarDosCombos()
      await verTienda([merienda])
      await screen.findByRole('heading', { level: 3, name: 'Combo merienda' })
      const panel = await abrirElPanel()

      await userEvent.click(within(panel).getByRole('button', { name: 'Quitar Combo de verano del carrito' }))

      expect(within(panel).queryByText('Combo de verano')).toBeNull()
      expect(within(panel).getByText('Combo merienda')).toBeInTheDocument()
    })

    it('si la lista de combos viene vacía, todos los combos del carrito quedan no disponibles', async () => {
      guardarDosCombos()
      await verTienda([])
      await waitFor(() => expect(obtenerCombosMock).toHaveBeenCalledTimes(1))

      const panel = await abrirElPanel()

      await waitFor(() => expect(within(panel).getAllByText(/Ya no está disponible/)).toHaveLength(2))
      expect(totalDelPanel(panel)).toBe('Total$ 0,00')
    })

    it('si la lectura de combos falla no se sabe cuáles siguen: ninguno se marca', async () => {
      guardarDosCombos()
      await verTienda(new Error('sin red'))
      await waitFor(() => expect(obtenerCombosMock).toHaveBeenCalledTimes(1))
      await reposar()

      const panel = await abrirElPanel()

      expect(within(panel).queryByText(/Ya no está disponible/)).toBeNull()
      expect(totalDelPanel(panel)).toBe('Total$ 2.900,00')
    })

    it('la marca de no disponible queda guardada en el carrito del navegador', async () => {
      guardarDosCombos()
      await verTienda([merienda])
      await screen.findByRole('heading', { level: 3, name: 'Combo merienda' })

      await waitFor(() =>
        expect(itemsGuardados(localStorage.getItem(claveDelCarrito(window.location.host)))).toEqual([
          expect.not.objectContaining({ noDisponible: true }),
          expect.objectContaining({ idCombo: 9, noDisponible: true }),
        ]),
      )
    })

    it('lo anuncia en la región polite, con el nombre del combo', async () => {
      guardarDosCombos()
      await verTienda([merienda])

      await waitFor(() => expect(regionDeAnuncios()).toHaveTextContent('Combo de verano ya no está disponible'))
      expect(regionDeAnuncios()).not.toHaveTextContent('Combo merienda')
    })

    it('si hay varios, los anuncia en uno solo para que ninguno pise al otro', async () => {
      guardarDosCombos()
      await verTienda([])

      await waitFor(() =>
        expect(regionDeAnuncios()).toHaveTextContent('Combo merienda ya no está disponible. Combo de verano ya no está disponible'),
      )
    })

    it('el botón del carrito no cuenta la línea no disponible', async () => {
      guardarDosCombos()
      await verTienda([merienda])

      expect(await screen.findByRole('button', { name: 'Carrito, 1 ítem' })).toHaveTextContent('1')
      expect(screen.queryByRole('button', { name: 'Carrito, 2 ítems' })).toBeNull()
    })

    it('con todas las líneas no disponibles el carrito no está vacío, pero nada suma', async () => {
      guardarDosCombos()
      await verTienda([])

      const boton = await screen.findByRole('button', { name: 'Carrito, sin ítems disponibles' })

      expect(boton).toHaveTextContent('0')
      expect((await abrirElPanel()).textContent).not.toContain('Tu carrito está vacío')
    })
  })
})

describe('los combos se releen con cada lectura del catálogo', () => {
  function prepararCatalogoPaginado(combos: ComboPublico[][]) {
    obtenerVitrinaMock.mockResolvedValue(vitrina)
    obtenerCatalogoMock.mockResolvedValue(catalogoPublico({ articulos: [martillo], total: 30 }))
    for (const lectura of combos) obtenerCombosMock.mockResolvedValueOnce(lectura)
  }

  it('"Ver más" relee los combos y la sección muestra la lista nueva', async () => {
    prepararCatalogoPaginado([[merienda], [merienda, asado]])
    render(<Tienda />)
    await screen.findByRole('heading', { level: 3, name: 'Combo merienda' })
    expect(screen.queryByRole('heading', { level: 3, name: 'Combo asado' })).toBeNull()

    await userEvent.click(screen.getByRole('button', { name: 'Ver más' }))

    await screen.findByRole('heading', { level: 3, name: 'Combo asado' })
    expect(obtenerCombosMock).toHaveBeenCalledTimes(2)
  })

  it('"Reintentar" tras un error del catálogo también relee los combos', async () => {
    obtenerVitrinaMock.mockResolvedValue(vitrina)
    obtenerCatalogoMock.mockRejectedValueOnce(new Error('sin red')).mockResolvedValue(catalogoPublico({ articulos: [martillo] }))
    obtenerCombosMock.mockResolvedValueOnce([merienda]).mockResolvedValueOnce([asado])
    render(<Tienda />)

    await userEvent.click(await screen.findByRole('button', { name: 'Reintentar' }))

    await screen.findByRole('heading', { level: 3, name: 'Combo asado' })
    expect(screen.queryByRole('heading', { level: 3, name: 'Combo merienda' })).toBeNull()
    expect(obtenerCombosMock).toHaveBeenCalledTimes(2)
  })

  it('cambiar la búsqueda relee los combos aunque con el filtro activo no se vean', async () => {
    await verTienda([merienda])
    await screen.findByRole('heading', { level: 3, name: 'Combo merienda' })
    expect(obtenerCombosMock).toHaveBeenCalledTimes(1)

    await userEvent.type(screen.getByRole('searchbox', { name: 'Buscar artículos' }), 'mart{Enter}')

    await waitFor(() => expect(obtenerCombosMock).toHaveBeenCalledTimes(2))
    expect(screen.queryByRole('region', { name: 'Combos' })).toBeNull()
  })

  it('una relectura que falla conserva la última lista buena', async () => {
    prepararCatalogoPaginado([[merienda]])
    obtenerCombosMock.mockRejectedValueOnce(new Error('sin red'))
    render(<Tienda />)
    await screen.findByRole('heading', { level: 3, name: 'Combo merienda' })

    await userEvent.click(screen.getByRole('button', { name: 'Ver más' }))
    await waitFor(() => expect(obtenerCombosMock).toHaveBeenCalledTimes(2))
    await reposar()

    expect(screen.getByRole('heading', { level: 3, name: 'Combo merienda' })).toBeInTheDocument()
  })

  it('una relectura que sí llega pone al día las líneas del carrito y anuncia el cambio de disponibilidad en ambos sentidos', async () => {
    const verano = comboPublico(9, { nombre: 'Combo de verano', precio: 800, componentes: [componenteDeCombo(90, { nombre: 'Helado', cantidad: 2 })] })
    localStorage.setItem(
      claveDelCarrito(window.location.host),
      JSON.stringify({
        version: 2,
        items: [{ tipo: 'combo', idCombo: 9, nombre: 'Combo de verano', precio: 800, cantidad: 3, componentes: [{ idArticulo: 90, nombre: 'Helado', cantidad: 2, imagenVersion: null }] }],
      }),
    )
    prepararCatalogoPaginado([[merienda], [merienda, verano]])
    render(<Tienda />)
    await waitFor(() => expect(regionDeAnuncios()).toHaveTextContent('Combo de verano ya no está disponible'))

    await userEvent.click(screen.getByRole('button', { name: 'Ver más' }))

    await waitFor(() => expect(regionDeAnuncios()).toHaveTextContent('Combo de verano volvió a estar disponible'))
    const panel = await abrirElPanel()
    expect(within(panel).queryByText(/Ya no está disponible/)).toBeNull()
    expect(totalDelPanel(panel)).toBe('Total$ 2.400,00')
  })
})

describe('adoptar el carrito de otra pestaña con líneas de combo', () => {
  const clave = () => claveDelCarrito(window.location.host)
  const componentes = [{ idArticulo: 90, nombre: 'Helado', cantidad: 2, imagenVersion: null }]
  const lineaDeCombo = (idCombo: number, nombre: string, extra: Record<string, unknown> = {}) => ({
    tipo: 'combo',
    idCombo,
    nombre,
    precio: 800,
    cantidad: 2,
    componentes,
    ...extra,
  })

  function adoptar(items: unknown[]) {
    localStorage.setItem(clave(), JSON.stringify({ version: 2, items }))
    act(() => {
      window.dispatchEvent(new StorageEvent('storage', { key: clave(), storageArea: localStorage }))
    })
  }

  it('con una lista buena leída, las líneas adoptadas se ponen al día con ella y las que no figuran se marcan', async () => {
    await verTienda([merienda])
    await screen.findByRole('heading', { level: 3, name: 'Combo merienda' })

    adoptar([lineaDeCombo(1, 'Viejo', { precio: 300 }), lineaDeCombo(9, 'Combo de verano')])

    const panel = await abrirElPanel()
    expect(within(panel).queryByText('Viejo')).toBeNull()
    expect(within(panel).getByText('Combo merienda').closest('li')).toHaveTextContent('2 × Coca Cola 1,5 L · 1 × Pan')
    expect(within(within(panel).getByText('Combo de verano').closest('li') as HTMLElement).getByRole('status')).toHaveTextContent(
      'Ya no está disponible',
    )
    expect(totalDelPanel(panel)).toBe('Total$ 1.000,00')
  })

  it('una línea adoptada con la marca de no disponible se desmarca si el combo figura en la lista leída', async () => {
    await verTienda([merienda])
    await screen.findByRole('heading', { level: 3, name: 'Combo merienda' })

    adoptar([lineaDeCombo(1, 'Combo merienda', { precio: 500, noDisponible: true })])

    const panel = await abrirElPanel()
    expect(within(panel).queryByText(/Ya no está disponible/)).toBeNull()
    expect(within(panel).getByRole('textbox', { name: 'Cantidad de Combo merienda' })).toBeInTheDocument()
    expect(totalDelPanel(panel)).toBe('Total$ 1.000,00')
  })

  it('sin una lista buena leída no se marca ninguna línea adoptada', async () => {
    await verTienda(new Error('sin red'))
    await waitFor(() => expect(obtenerCombosMock).toHaveBeenCalledTimes(1))
    await reposar()

    adoptar([lineaDeCombo(9, 'Combo de verano')])

    const panel = await abrirElPanel()
    expect(within(panel).getByText('Combo de verano')).toBeInTheDocument()
    expect(within(panel).queryByText(/Ya no está disponible/)).toBeNull()
    expect(totalDelPanel(panel)).toBe('Total$ 1.600,00')
  })
})
