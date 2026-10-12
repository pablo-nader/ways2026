import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { CatalogoPublico, ConsultaDeCatalogo } from '../api/publico'
import { ErrorApi } from '../api/cliente'
import type { VitrinaPublica } from '../api/tipos'
import '../publico/publico.css'
import { articuloPublico, catalogoPublico, categoriaPublica, diferida } from './datosDePrueba'
import { Tienda } from './Tienda'

const obtenerVitrinaMock = vi.fn()
const obtenerCatalogoMock = vi.fn<(consulta: ConsultaDeCatalogo) => Promise<CatalogoPublico>>()

const urlDeLaImagenMock = vi.fn<(id: number, version: number | null) => void>()

vi.mock('../api/publico', async (importarOriginal) => {
  const original = await importarOriginal<typeof import('../api/publico')>()

  return {
    ...original,
    // Pasa la llamada al original y la cuenta: cada dibujo de la imagen de una tarjeta es una llamada.
    urlDeLaImagenDelArticulo: (id: number, version: number | null) => {
      urlDeLaImagenMock(id, version)

      return original.urlDeLaImagenDelArticulo(id, version)
    },
    clienteDePublico: {
      obtenerVitrina: () => obtenerVitrinaMock(),
      obtenerCatalogo: (consulta: ConsultaDeCatalogo) => obtenerCatalogoMock(consulta),
    },
  }
})

const vitrina: VitrinaPublica = {
  nombre: 'Ferretería Sur',
  tagline: null,
  descripcion: null,
  colorAcento: null,
  tema: 'claro',
  tieneLogo: false,
  logoVersion: null,
  sucursales: [],
}

const martillo = articuloPublico(1, {
  nombre: 'Martillo',
  descripcion: 'Mango de madera',
  precio: 1234.56,
  idCategoria: 5,
  tieneImagen: true,
  imagenVersion: 42,
})
const clavos = articuloPublico(2, { nombre: 'Clavos', precio: 2000, unidadVenta: 'Peso', idCategoria: 6 })
const categorias = [categoriaPublica(5, 'Herramientas', 1), categoriaPublica(6, 'Fijaciones', 1)]

function llamadas() {
  return obtenerCatalogoMock.mock.calls.map(([consulta]) => consulta)
}

async function verCatalogo(catalogo: CatalogoPublico = catalogoPublico({ articulos: [martillo, clavos], categorias })) {
  obtenerVitrinaMock.mockResolvedValue(vitrina)
  obtenerCatalogoMock.mockResolvedValue(catalogo)
  render(<Tienda />)
  await screen.findByRole('heading', { level: 2, name: catalogo.articulos[0]?.nombre ?? '' })
}

beforeEach(() => {
  obtenerVitrinaMock.mockReset()
  obtenerCatalogoMock.mockReset()
  urlDeLaImagenMock.mockClear()
})

afterEach(() => {
  vi.useRealTimers()
  localStorage.clear()
  document.documentElement.removeAttribute('data-bs-theme')
  document.documentElement.style.removeProperty('--vitrina-acento')
  document.title = ''
  document.body.style.overflow = ''
  document.head.querySelector('meta[name="description"]')?.remove()
})

describe('catálogo de la tienda', () => {
  it('muestra cada artículo con su nombre, descripción y precio formateado, y la cuenta de resultados', async () => {
    await verCatalogo()

    const tarjeta = screen.getByRole('heading', { level: 2, name: 'Martillo' }).closest('li') as HTMLElement
    expect(within(tarjeta).getByText('Mango de madera')).toBeInTheDocument()
    expect(within(tarjeta).getByText('$ 1.234,56')).toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('2 artículos')
    expect(screen.getAllByRole('listitem').filter((li) => li.classList.contains('publico-articulo'))).toHaveLength(2)
  })

  it('un artículo por peso aclara "por kg" y uno por unidad no', async () => {
    await verCatalogo()

    const porPeso = screen.getByRole('heading', { name: 'Clavos' }).closest('li') as HTMLElement
    const porUnidad = screen.getByRole('heading', { name: 'Martillo' }).closest('li') as HTMLElement
    expect(within(porPeso).getByText('$ 2.000,00')).toBeInTheDocument()
    expect(porPeso).toHaveTextContent('por kg')
    expect(porUnidad).not.toHaveTextContent('por kg')
  })

  it('la imagen sale de la API con su versión y es diferida; sin imagen se ve la marca de Ways', async () => {
    await verCatalogo()

    const conImagen = screen.getByRole('heading', { name: 'Martillo' }).closest('li') as HTMLElement
    const sinImagen = screen.getByRole('heading', { name: 'Clavos' }).closest('li') as HTMLElement
    const imagen = conImagen.querySelector('img') as HTMLImageElement
    expect(imagen).toHaveAttribute('src', '/api/publico/articulos/1/imagen?v=42')
    expect(imagen).toHaveAttribute('loading', 'lazy')
    expect(imagen).toHaveAttribute('alt', '')
    expect(sinImagen.querySelector('img')).toBeNull()
    expect(sinImagen.querySelector('.publico-articulo-marca')).not.toBeNull()
  })

  it('si la imagen falla al cargar, la tarjeta vuelve a la marca de Ways', async () => {
    await verCatalogo()
    const conImagen = screen.getByRole('heading', { name: 'Martillo' }).closest('li') as HTMLElement

    fireEvent.error(conImagen.querySelector('img') as HTMLImageElement)

    expect(conImagen.querySelector('img')).toBeNull()
    expect(conImagen.querySelector('.publico-articulo-marca')).not.toBeNull()
  })

  it('las categorías son filtros con su cuenta y "Todos" empieza elegido', async () => {
    await verCatalogo()

    const grupo = screen.getByRole('group', { name: 'Categorías' })
    expect(within(grupo).getByRole('button', { name: 'Todos' })).toHaveAttribute('aria-pressed', 'true')
    expect(within(grupo).getByRole('button', { name: 'Herramientas 1' })).toHaveAttribute('aria-pressed', 'false')
    expect(within(grupo).getByRole('button', { name: 'Fijaciones 1' })).toBeInTheDocument()
  })

  it('elegir una categoría pide ese filtro desde la página 1 y las cuentas de los chips se conservan', async () => {
    await verCatalogo()
    obtenerCatalogoMock.mockResolvedValue(catalogoPublico({ articulos: [clavos], categorias, total: 1 }))

    await userEvent.click(screen.getByRole('button', { name: 'Fijaciones 1' }))

    await waitFor(() => expect(screen.queryByRole('heading', { name: 'Martillo' })).not.toBeInTheDocument())
    expect(llamadas().at(-1)).toEqual({ buscar: '', idCategoria: 6, pagina: 1, tamano: 24 })
    expect(screen.getByRole('button', { name: 'Fijaciones 1' })).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByRole('button', { name: 'Herramientas 1' })).toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('1 artículo')

    await userEvent.click(screen.getByRole('button', { name: 'Todos' }))

    await waitFor(() => expect(llamadas().at(-1)).toEqual({ buscar: '', idCategoria: null, pagina: 1, tamano: 24 }))
  })

  describe('búsqueda', () => {
    async function conRelojFalso(accion: () => void | Promise<void>) {
      vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
      await act(async () => {
        await accion()
      })
    }

    it('espera a que se deje de escribir y hace una sola búsqueda con el último texto', async () => {
      await verCatalogo()
      const campo = screen.getByRole('searchbox', { name: 'Buscar artículos' })
      const antes = llamadas().length

      await conRelojFalso(() => {
        fireEvent.change(campo, { target: { value: 'ta' } })
        fireEvent.change(campo, { target: { value: 'tal' } })
        fireEvent.change(campo, { target: { value: 'taladro ' } })
      })
      expect(llamadas()).toHaveLength(antes)

      await act(async () => {
        await vi.advanceTimersByTimeAsync(299)
      })
      expect(llamadas()).toHaveLength(antes)
      await act(async () => {
        await vi.advanceTimersByTimeAsync(1)
      })

      expect(llamadas().slice(antes)).toEqual([{ buscar: 'taladro', idCategoria: null, pagina: 1, tamano: 24 }])
    })

    it('Enter busca de inmediato, sin esperar la pausa', async () => {
      await verCatalogo()
      const campo = screen.getByRole('searchbox', { name: 'Buscar artículos' })
      const antes = llamadas().length

      await conRelojFalso(() => {
        fireEvent.change(campo, { target: { value: 'sierra' } })
        fireEvent.submit(campo.closest('form') as HTMLFormElement)
      })

      expect(llamadas().slice(antes)).toEqual([{ buscar: 'sierra', idCategoria: null, pagina: 1, tamano: 24 }])
    })

    it('una respuesta tardía de una búsqueda anterior no pisa la de la actual', async () => {
      await verCatalogo()
      const campo = screen.getByRole('searchbox', { name: 'Buscar artículos' })
      const lenta = diferida<CatalogoPublico>()
      obtenerCatalogoMock.mockImplementation(({ buscar }) =>
        buscar === 'a'
          ? lenta.promesa
          : Promise.resolve(catalogoPublico({ articulos: [articuloPublico(9, { nombre: 'Resultado de ab' })], categorias })),
      )

      fireEvent.change(campo, { target: { value: 'a' } })
      fireEvent.submit(campo.closest('form') as HTMLFormElement)
      await waitFor(() => expect(llamadas().at(-1)?.buscar).toBe('a'))
      fireEvent.change(campo, { target: { value: 'ab' } })
      fireEvent.submit(campo.closest('form') as HTMLFormElement)
      await screen.findByRole('heading', { name: 'Resultado de ab' })

      await act(async () => lenta.resolver(catalogoPublico({ articulos: [articuloPublico(8, { nombre: 'Resultado de a' })] })))

      expect(screen.getByRole('heading', { name: 'Resultado de ab' })).toBeInTheDocument()
      expect(screen.queryByRole('heading', { name: 'Resultado de a' })).not.toBeInTheDocument()
    })

    it('sin resultados dice que no encontró nada y deja la barra para seguir buscando', async () => {
      await verCatalogo()
      obtenerCatalogoMock.mockResolvedValue(catalogoPublico({ articulos: [], categorias: [], total: 0 }))
      const campo = screen.getByRole('searchbox', { name: 'Buscar artículos' })

      fireEvent.change(campo, { target: { value: 'zzz' } })
      fireEvent.submit(campo.closest('form') as HTMLFormElement)

      expect(await screen.findByText('No encontramos artículos para esa búsqueda.')).toBeInTheDocument()
      expect(screen.getByRole('searchbox', { name: 'Buscar artículos' })).toHaveValue('zzz')
      expect(screen.queryByText('El catálogo se está preparando.')).not.toBeInTheDocument()
    })
  })

  describe('"Ver más"', () => {
    const primera = [articuloPublico(1, { nombre: 'Uno' }), articuloPublico(2, { nombre: 'Dos' })]

    it('trae la página siguiente y la agrega debajo, y desaparece cuando ya está todo', async () => {
      await verCatalogo(catalogoPublico({ articulos: primera, total: 30 }))
      obtenerCatalogoMock.mockResolvedValue(
        catalogoPublico({ articulos: [articuloPublico(3, { nombre: 'Tres' })], total: 30, pagina: 2 }),
      )

      await userEvent.click(screen.getByRole('button', { name: 'Ver más' }))

      await screen.findByRole('heading', { name: 'Tres' })
      expect(llamadas().at(-1)).toEqual({ buscar: '', idCategoria: null, pagina: 2, tamano: 24 })
      expect(screen.getByRole('heading', { name: 'Uno' })).toBeInTheDocument()
      expect(screen.getByRole('heading', { name: 'Dos' })).toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Ver más' })).not.toBeInTheDocument()
    })

    it('no ofrece "Ver más" si todo entró en la primera página', async () => {
      await verCatalogo(catalogoPublico({ articulos: primera, total: 2 }))

      expect(screen.queryByRole('button', { name: 'Ver más' })).not.toBeInTheDocument()
    })

    it('mientras carga queda deshabilitado, y si falla avisa y deja reintentar', async () => {
      await verCatalogo(catalogoPublico({ articulos: primera, total: 30 }))
      const pagina = diferida<CatalogoPublico>()
      obtenerCatalogoMock.mockReturnValue(pagina.promesa)

      await userEvent.click(screen.getByRole('button', { name: 'Ver más' }))
      expect(screen.getByRole('button', { name: 'Cargando…' })).toBeDisabled()
      await act(async () => pagina.rechazar(new ErrorApi(500, 'error_interno', 'Falla')))

      expect(await screen.findByText('No pudimos cargar más artículos. Probá de nuevo.')).toBeInTheDocument()
      expect(screen.getByRole('heading', { name: 'Uno' })).toBeInTheDocument()
      expect(screen.getByRole('button', { name: 'Ver más' })).toBeEnabled()
    })
  })

  describe('estados', () => {
    it('mientras carga por primera vez muestra un esqueleto accesible', async () => {
      obtenerVitrinaMock.mockResolvedValue(vitrina)
      obtenerCatalogoMock.mockReturnValue(new Promise(() => {}))
      render(<Tienda />)

      expect(await screen.findByRole('status', { name: 'Cargando el catálogo' })).toHaveAttribute('aria-busy', 'true')
      expect(screen.queryByRole('searchbox')).not.toBeInTheDocument()
    })

    it('si falla la primera lectura muestra el error y Reintentar la vuelve a pedir', async () => {
      obtenerVitrinaMock.mockResolvedValue(vitrina)
      obtenerCatalogoMock.mockRejectedValueOnce(new ErrorApi(500, 'error_interno', 'Falla'))
      obtenerCatalogoMock.mockResolvedValue(catalogoPublico({ articulos: [martillo], categorias }))
      render(<Tienda />)

      expect(await screen.findByText('No pudimos cargar el catálogo. Probá de nuevo en un momento.')).toBeInTheDocument()
      await userEvent.click(screen.getByRole('button', { name: 'Reintentar' }))

      expect(await screen.findByRole('heading', { name: 'Martillo' })).toBeInTheDocument()
    })

    it('si falla una búsqueda deja la barra y ofrece Reintentar con ese mismo filtro', async () => {
      await verCatalogo()
      obtenerCatalogoMock.mockRejectedValueOnce(new ErrorApi(500, 'error_interno', 'Falla'))
      const campo = screen.getByRole('searchbox', { name: 'Buscar artículos' })

      fireEvent.change(campo, { target: { value: 'sierra' } })
      fireEvent.submit(campo.closest('form') as HTMLFormElement)
      expect(await screen.findByText('No pudimos cargar los artículos. Probá de nuevo en un momento.')).toBeInTheDocument()
      expect(screen.queryByRole('heading', { name: 'Martillo' })).not.toBeInTheDocument()
      obtenerCatalogoMock.mockResolvedValue(catalogoPublico({ articulos: [clavos], categorias, total: 1 }))
      await userEvent.click(screen.getByRole('button', { name: 'Reintentar' }))

      expect(await screen.findByRole('heading', { name: 'Clavos' })).toBeInTheDocument()
      expect(llamadas().at(-1)?.buscar).toBe('sierra')
    })
  })

  describe('agregar al carrito', () => {
    it('"Agregar" suma el artículo, el botón del carrito cuenta la línea y la tarjeta pasa a selector de cantidad', async () => {
      await verCatalogo()

      await userEvent.click(screen.getByRole('button', { name: 'Agregar Martillo al carrito' }))

      expect(screen.getByRole('button', { name: 'Carrito, 1 artículo' })).toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Agregar Martillo al carrito' })).not.toBeInTheDocument()
      expect(screen.getByRole('textbox', { name: 'Cantidad de Martillo' })).toHaveValue('1')
      expect(screen.getByRole('button', { name: 'Agregar Clavos al carrito' })).toBeInTheDocument()
    })

    it('anuncia lo agregado en una región aria-live', async () => {
      await verCatalogo()

      await userEvent.click(screen.getByRole('button', { name: 'Agregar Martillo al carrito' }))

      const region = document.querySelector('[aria-live="polite"]') as HTMLElement
      expect(region).toHaveTextContent('Martillo agregado al carrito')
    })

    it('+ y − cambian de a uno en un artículo por unidad, y − desde 1 lo quita', async () => {
      await verCatalogo()
      await userEvent.click(screen.getByRole('button', { name: 'Agregar Martillo al carrito' }))
      const cantidad = screen.getByRole('textbox', { name: 'Cantidad de Martillo' })

      await userEvent.click(screen.getByRole('button', { name: 'Agregar uno más de Martillo' }))
      await userEvent.click(screen.getByRole('button', { name: 'Agregar uno más de Martillo' }))
      expect(cantidad).toHaveValue('3')
      await userEvent.click(screen.getByRole('button', { name: 'Quitar uno de Martillo' }))
      expect(cantidad).toHaveValue('2')
      await userEvent.click(screen.getByRole('button', { name: 'Quitar uno de Martillo' }))
      await userEvent.click(screen.getByRole('button', { name: 'Quitar uno de Martillo' }))

      expect(screen.getByRole('button', { name: 'Agregar Martillo al carrito' })).toBeInTheDocument()
      expect(screen.getByRole('button', { name: 'Carrito, vacío' })).toBeInTheDocument()
    })

    it('un artículo por peso suma de a medio kilo y acepta decimales tipeados con coma', async () => {
      await verCatalogo()
      await userEvent.click(screen.getByRole('button', { name: 'Agregar Clavos al carrito' }))
      const cantidad = screen.getByRole('textbox', { name: 'Cantidad de Clavos' })

      await userEvent.click(screen.getByRole('button', { name: 'Agregar medio kilo más de Clavos' }))
      expect(cantidad).toHaveValue('1,5')
      await userEvent.clear(cantidad)
      await userEvent.type(cantidad, '0,25')

      expect(cantidad).toHaveValue('0,25')
      expect(cantidad).toHaveAttribute('aria-invalid', 'false')
    })

    it('una cantidad tipeada que la unidad no admite avisa, no cambia el carrito y al salir vuelve a la última buena', async () => {
      await verCatalogo()
      await userEvent.click(screen.getByRole('button', { name: 'Agregar Martillo al carrito' }))
      await userEvent.click(screen.getByRole('button', { name: 'Agregar uno más de Martillo' }))
      const cantidad = screen.getByRole('textbox', { name: 'Cantidad de Martillo' })

      await userEvent.clear(cantidad)
      await userEvent.type(cantidad, '2,5')

      expect(cantidad).toHaveAttribute('aria-invalid', 'true')
      expect(screen.getByRole('alert')).toHaveTextContent('Ingresá una cantidad entera, hasta 999.')
      await userEvent.tab()
      expect(cantidad).toHaveValue('2')
      expect(cantidad).toHaveAttribute('aria-invalid', 'false')
      expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    })

    /**
     * Mide el redibujado por el efecto que deja: cada dibujo de una tarjeta con imagen llama a
     * `urlDeLaImagenDelArticulo`. Prueba que la tarjeta del Martillo no se vuelve a dibujar cuando el
     * carrito cambia por otro artículo; no mide el costo de dibujar las que sí cambian.
     */
    it('agregar un artículo no vuelve a dibujar la tarjeta de otro', async () => {
      await verCatalogo()
      const dibujosDelMartillo = () => urlDeLaImagenMock.mock.calls.filter(([id]) => id === 1).length
      const antes = dibujosDelMartillo()
      expect(antes).toBeGreaterThan(0)

      await userEvent.click(screen.getByRole('button', { name: 'Agregar Clavos al carrito' }))

      expect(screen.getByRole('textbox', { name: 'Cantidad de Clavos' })).toBeInTheDocument()
      expect(dibujosDelMartillo()).toBe(antes)
    })

    it('en 999 el + queda deshabilitado', async () => {
      await verCatalogo()
      await userEvent.click(screen.getByRole('button', { name: 'Agregar Martillo al carrito' }))
      const cantidad = screen.getByRole('textbox', { name: 'Cantidad de Martillo' })

      await userEvent.clear(cantidad)
      await userEvent.type(cantidad, '999')

      expect(screen.getByRole('button', { name: 'Agregar uno más de Martillo' })).toBeDisabled()
    })
  })
})
