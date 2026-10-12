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

beforeEach(() => {
  obtenerVitrinaMock.mockReset()
  obtenerCatalogoMock.mockReset()
  obtenerCombosMock.mockReset()
})

afterEach(() => {
  cleanup()
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

    it('una respuesta tardía de una lectura vieja no pisa a la más nueva (primera pasada del modo estricto)', async () => {
      const primera = diferida<ComboPublico[]>()
      const segunda = diferida<ComboPublico[]>()
      obtenerCombosMock.mockReturnValueOnce(primera.promesa).mockReturnValueOnce(segunda.promesa)
      obtenerVitrinaMock.mockResolvedValue(vitrina)
      obtenerCatalogoMock.mockResolvedValue(catalogoPublico({ articulos: [martillo] }))
      render(
        <StrictMode>
          <Tienda />
        </StrictMode>,
      )
      await screen.findByRole('heading', { level: 2, name: 'Martillo' })
      await waitFor(() => expect(obtenerCombosMock).toHaveBeenCalledTimes(2))

      await act(async () => segunda.resolver([asado]))
      await screen.findByRole('heading', { level: 3, name: 'Combo asado' })
      await act(async () => primera.resolver([merienda]))
      await reposar()

      expect(screen.getAllByRole('heading', { level: 3 }).map((h) => h.textContent)).toEqual(['Combo asado'])
    })

    it('una respuesta que llega con la tienda ya desmontada no rompe nada', async () => {
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
      delete (Element.prototype as { scrollIntoView?: unknown }).scrollIntoView
    })

    it('sin el ancla no mueve la página', async () => {
      const llevar = vi.fn()
      Element.prototype.scrollIntoView = llevar

      await verTienda()
      await screen.findByRole('heading', { level: 3, name: 'Combo merienda' })

      expect(llevar).not.toHaveBeenCalled()
      delete (Element.prototype as { scrollIntoView?: unknown }).scrollIntoView
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
    expect(screen.getByRole('button', { name: 'Carrito, 1 artículo' })).toBeInTheDocument()
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

      expect(within(linea).getByText(/Ya no está disponible/)).toBeInTheDocument()
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
  })
})
