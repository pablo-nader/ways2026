import { act, render, screen, waitFor, within } from '@testing-library/react'
import { StrictMode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { ErrorApi } from '../api/cliente'
import type { CatalogoPublico, ConsultaDeCatalogo } from '../api/publico'
import type { VitrinaPublica } from '../api/tipos'
import { articuloPublico, catalogoPublico, categoriaPublica, diferida } from '../online/datosDePrueba'
import '../publico/publico.css'
import { sucursalCompleta, sucursalSinContacto, vitrina } from './datosDePrueba'
import { Landing } from './Landing'

const obtenerVitrinaMock = vi.fn()
const obtenerCatalogoMock = vi.fn<(consulta: ConsultaDeCatalogo) => Promise<CatalogoPublico>>()

vi.mock('../api/publico', async (importarOriginal) => ({
  ...(await importarOriginal<typeof import('../api/publico')>()),
  clienteDePublico: {
    obtenerVitrina: () => obtenerVitrinaMock(),
    obtenerCatalogo: (consulta: ConsultaDeCatalogo) => obtenerCatalogoMock(consulta),
  },
}))

const martillo = articuloPublico(1, { nombre: 'Martillo', precio: 1234.56, tieneImagen: true, imagenVersion: 42 })
const clavos = articuloPublico(2, { nombre: 'Clavos', precio: 2000, unidadVenta: 'Peso' })
const categorias = [categoriaPublica(5, 'Herramientas', 12), categoriaPublica(6, 'Fijaciones', 3)]

async function verPublicada(cambios: Partial<VitrinaPublica> = {}) {
  obtenerVitrinaMock.mockResolvedValue(vitrina(cambios))
  render(<Landing />)
  await screen.findByRole('heading', { level: 1 })
}

/** Deja correr las promesas ya resueltas o rechazadas: sirve para afirmar que algo NO apareció. */
const reposar = () =>
  act(async () => {
    await new Promise((resolver) => setTimeout(resolver, 0))
  })

async function verConCatalogo(catalogo: CatalogoPublico, cambios: Partial<VitrinaPublica> = {}) {
  obtenerCatalogoMock.mockResolvedValue(catalogo)
  await verPublicada(cambios)
  await reposar()
}

function seccion(nombre: string) {
  return screen.getByRole('region', { name: nombre })
}

beforeEach(() => {
  obtenerVitrinaMock.mockReset()
  // Por defecto el comercio no tiene artículos publicados: las secciones del catálogo no se dibujan.
  obtenerCatalogoMock.mockReset()
  obtenerCatalogoMock.mockResolvedValue(catalogoPublico())
})

afterEach(() => {
  document.documentElement.removeAttribute('data-bs-theme')
  document.documentElement.style.removeProperty('--vitrina-acento')
  document.title = ''
  document.head.querySelector('meta[name="description"]')?.remove()
})

describe('secciones de la landing', () => {
  describe('portada', () => {
    function pildoras() {
      return within(seccion('Todo para tu obra')).queryAllByRole('listitem').map((li) => li.textContent)
    }

    function sello() {
      return document.body.querySelector('.publico-sello')
    }

    it('bajo los botones salen el horario, el domicilio y el pedido por WhatsApp de la primera sucursal', async () => {
      await verPublicada()

      expect(pildoras()).toEqual(['Lun a Vie 9 a 18', 'Av. Siempreviva 742', 'Pedidos por WhatsApp'])
    })

    it('el horario y el domicilio son los de la primera sucursal, no los de otra', async () => {
      await verPublicada({ sucursales: [sucursalSinContacto, sucursalCompleta] })

      expect(pildoras()).toEqual(['Pedidos por WhatsApp'])
      expect(sello()).toBeNull()
    })

    it('sin ningún número no hay píldora de WhatsApp', async () => {
      await verPublicada({ sucursales: [{ ...sucursalCompleta, whatsapp: null }] })

      expect(pildoras()).toEqual(['Lun a Vie 9 a 18', 'Av. Siempreviva 742'])
    })

    it('un dato en blanco no genera píldora', async () => {
      await verPublicada({ sucursales: [{ ...sucursalCompleta, whatsapp: null, horario: '  ', domicilio: ' \n ' }] })

      expect(pildoras()).toEqual([])
      expect(within(seccion('Todo para tu obra')).queryByRole('list')).not.toBeInTheDocument()
    })

    it('sin sucursales no hay píldoras ni sello y los botones siguen', async () => {
      await verPublicada({ sucursales: [] })

      expect(within(seccion('Todo para tu obra')).queryByRole('list')).not.toBeInTheDocument()
      expect(sello()).toBeNull()
      expect(within(seccion('Todo para tu obra')).getByRole('link', { name: 'Comprar online' })).toBeInTheDocument()
    })

    it('el sello repite el horario tal como lo cargó el comercio', async () => {
      await verPublicada()

      expect(sello()).toHaveTextContent('Lun a Vie 9 a 18')
      expect(sello()).toHaveAttribute('aria-hidden', 'true')
    })

    it('un horario de 24 horas se vuelve "Abierto 24 hs" en el sello y la píldora conserva el texto completo', async () => {
      await verPublicada({ sucursales: [{ ...sucursalCompleta, horario: 'Todos los días, 24 horas' }] })

      expect(sello()).toHaveTextContent(/^Abierto 24 hs$/)
      expect(pildoras()[0]).toBe('Todos los días, 24 horas')
    })

    it('un horario que solo llega hasta las 24 no se anuncia como abierto 24 hs', async () => {
      await verPublicada({ sucursales: [{ ...sucursalCompleta, horario: 'Lun a Sáb 8 a 24 hs' }] })

      expect(sello()).toHaveTextContent(/^Lun a Sáb 8 a 24 hs$/)
    })

    it('sin horario no hay sello', async () => {
      await verPublicada({ sucursales: [{ ...sucursalCompleta, horario: null }] })

      expect(sello()).toBeNull()
      expect(seccion('Todo para tu obra')).not.toHaveClass('publico-portada-con-sello')
    })
  })

  describe('destacados', () => {
    it('muestra los artículos del catálogo con nombre, precio, unidad y enlace a la tienda', async () => {
      await verConCatalogo(catalogoPublico({ articulos: [martillo, clavos], categorias }))

      const destacados = seccion('Lo que vas a encontrar')
      expect(within(destacados).getByRole('heading', { level: 2 })).toBeInTheDocument()
      const enlaceDelMartillo = within(destacados).getByRole('link', { name: /Martillo/ })
      expect(enlaceDelMartillo).toHaveAttribute('href', '/online')
      expect(enlaceDelMartillo).toHaveTextContent('$ 1.234,56')
      expect(enlaceDelMartillo).not.toHaveTextContent('por kg')
      const enlaceDeLosClavos = within(destacados).getByRole('link', { name: /Clavos/ })
      expect(enlaceDeLosClavos).toHaveAttribute('href', '/online')
      expect(enlaceDeLosClavos).toHaveTextContent('$ 2.000,00')
      expect(enlaceDeLosClavos).toHaveTextContent('por kg')
      expect(within(destacados).getAllByRole('listitem')).toHaveLength(2)
    })

    it('pide una sola página de 8 artículos sin filtros', async () => {
      await verConCatalogo(catalogoPublico({ articulos: [martillo] }))

      expect(obtenerCatalogoMock.mock.calls.map(([consulta]) => consulta)).toEqual([
        { buscar: '', idCategoria: null, pagina: 1, tamano: 8 },
      ])
    })

    it('la imagen sale de la API con su versión y es diferida; sin imagen se ve la marca de Ways', async () => {
      await verConCatalogo(catalogoPublico({ articulos: [martillo, clavos] }))

      const conImagen = screen.getByRole('link', { name: /Martillo/ })
      const imagen = conImagen.querySelector('img') as HTMLImageElement
      expect(imagen).toHaveAttribute('src', '/api/publico/articulos/1/imagen?v=42')
      expect(imagen).toHaveAttribute('loading', 'lazy')
      expect(imagen).toHaveAttribute('alt', '')
      const sinImagen = screen.getByRole('link', { name: /Clavos/ })
      expect(sinImagen.querySelector('img')).toBeNull()
      expect(sinImagen.querySelector('.publico-articulo-marca')).not.toBeNull()
    })

    it('"Ver toda la tienda" lleva a /online', async () => {
      await verConCatalogo(catalogoPublico({ articulos: [martillo] }))

      expect(within(seccion('Lo que vas a encontrar')).getByRole('link', { name: 'Ver toda la tienda' })).toHaveAttribute(
        'href',
        '/online',
      )
    })

    it.each([
      ['el catálogo da 404', () => Promise.reject(new ErrorApi(404, 'catalogo_no_disponible', 'No hay catálogo'))],
      ['el servidor falla', () => Promise.reject(new ErrorApi(500, 'error_interno', 'Falla'))],
      ['no hay red', () => Promise.reject(new TypeError('Failed to fetch'))],
      ['no hay artículos publicados', () => Promise.resolve(catalogoPublico())],
    ])('si %s, la landing sigue entera y sin destacados ni categorías ni avisos', async (_caso, respuesta) => {
      obtenerCatalogoMock.mockImplementation(respuesta)
      await verPublicada()
      await reposar()

      expect(screen.queryByRole('heading', { name: 'Lo que vas a encontrar' })).not.toBeInTheDocument()
      expect(screen.queryByRole('heading', { name: 'Explorá por categoría' })).not.toBeInTheDocument()
      expect(screen.queryByRole('link', { name: 'Ver toda la tienda' })).not.toBeInTheDocument()
      expect(screen.queryByRole('alert')).not.toBeInTheDocument()
      expect(screen.queryByText(/no pudimos/i)).not.toBeInTheDocument()
      expect(screen.getByRole('heading', { level: 1, name: 'Todo para tu obra' })).toBeInTheDocument()
      expect(screen.getByRole('heading', { name: 'Cómo comprar' })).toBeInTheDocument()
    })

    it('una respuesta vieja del catálogo que llega tarde no pisa a la más nueva', async () => {
      const primera = diferida<CatalogoPublico>()
      const segunda = diferida<CatalogoPublico>()
      obtenerCatalogoMock.mockReturnValueOnce(primera.promesa).mockReturnValueOnce(segunda.promesa)
      obtenerVitrinaMock.mockResolvedValue(vitrina())

      // El modo estricto monta, desmonta y vuelve a montar: dos lecturas, y la primera ya quedó invalidada.
      render(
        <StrictMode>
          <Landing />
        </StrictMode>,
      )
      await waitFor(() => expect(obtenerCatalogoMock).toHaveBeenCalledTimes(2))

      await act(async () => segunda.resolver(catalogoPublico({ articulos: [articuloPublico(2, { nombre: 'Artículo nuevo' })] })))
      await screen.findByRole('link', { name: /Artículo nuevo/ })
      await act(async () => primera.resolver(catalogoPublico({ articulos: [articuloPublico(1, { nombre: 'Artículo viejo' })] })))

      expect(screen.getByRole('link', { name: /Artículo nuevo/ })).toBeInTheDocument()
      expect(screen.queryByRole('link', { name: /Artículo viejo/ })).not.toBeInTheDocument()
    })
  })

  describe('categorías', () => {
    it('cada categoría es un enlace real a la tienda filtrada, con su cuenta', async () => {
      await verConCatalogo(catalogoPublico({ articulos: [martillo], categorias }))

      const seccionDeCategorias = seccion('Explorá por categoría')
      const herramientas = within(seccionDeCategorias).getByRole('link', { name: /Herramientas/ })
      expect(herramientas).toHaveAttribute('href', '/online?categoria=5')
      expect(herramientas).toHaveTextContent('12')
      expect(within(seccionDeCategorias).getByRole('link', { name: /Fijaciones/ })).toHaveAttribute('href', '/online?categoria=6')
      expect(within(seccionDeCategorias).getAllByRole('link')).toHaveLength(2)
    })

    it('sin categorías no hay sección, aunque haya artículos', async () => {
      await verConCatalogo(catalogoPublico({ articulos: [martillo], categorias: [] }))

      expect(screen.getByRole('heading', { name: 'Lo que vas a encontrar' })).toBeInTheDocument()
      expect(screen.queryByRole('heading', { name: 'Explorá por categoría' })).not.toBeInTheDocument()
    })
  })

  describe('quiénes somos', () => {
    it('el título a un lado y la descripción al otro', async () => {
      await verPublicada()

      const nosotros = seccion('Quiénes somos')
      expect(within(nosotros).getByRole('heading', { level: 2, name: 'Quiénes somos' })).toBeInTheDocument()
      expect(nosotros).toHaveTextContent('Atendemos desde 1980.')
      expect(nosotros).toHaveTextContent('Envíos en el día.')
    })
  })

  describe('cómo comprar', () => {
    function pasos() {
      return within(seccion('Cómo comprar')).getAllByRole('listitem')
    }

    it('son tres pasos numerados 01, 02 y 03', async () => {
      await verPublicada()

      expect(pasos()).toHaveLength(3)
      expect(pasos()[0]).toHaveTextContent(/^01Elegí tus productos en la tienda online$/)
      expect(pasos()[1]).toHaveTextContent(/^02/)
      expect(pasos()[2]).toHaveTextContent(/^03Retiralo en el local o coordiná la entrega$/)
    })

    it('con un número de WhatsApp el segundo paso manda a WhatsApp', async () => {
      await verPublicada()

      expect(pasos()[1]).toHaveTextContent('Confirmá tu pedido por WhatsApp')
      expect(screen.queryByText('Confirmá tu pedido con el comercio')).not.toBeInTheDocument()
    })

    it('sin número de WhatsApp el segundo paso es con el comercio', async () => {
      await verPublicada({ sucursales: [sucursalSinContacto] })

      expect(pasos()[1]).toHaveTextContent('Confirmá tu pedido con el comercio')
      expect(screen.queryByText('Confirmá tu pedido por WhatsApp')).not.toBeInTheDocument()
    })

    it('se muestra aunque el comercio no tenga nada más cargado', async () => {
      await verPublicada({ sucursales: [], descripcion: null, tagline: null })

      expect(pasos()).toHaveLength(3)
    })
  })

  describe('dónde estamos', () => {
    it('cada sucursal con domicilio lleva un mapa diferido, con título y la dirección codificada', async () => {
      await verPublicada()

      const mapa = screen.getByTitle('Mapa de Casa Central')
      expect(mapa.tagName).toBe('IFRAME')
      expect(mapa).toHaveAttribute('src', 'https://www.google.com/maps?q=Av.%20Siempreviva%20742&output=embed')
      expect(mapa).toHaveAttribute('loading', 'lazy')
      expect(mapa).toHaveAttribute('referrerpolicy', 'no-referrer-when-downgrade')
      expect(within(screen.getByText('Casa Central').closest('li') as HTMLElement).getByTitle('Mapa de Casa Central')).toBe(mapa)
    })

    it('una sucursal sin domicilio no tiene mapa', async () => {
      await verPublicada()

      expect(screen.queryByTitle('Mapa de Depósito')).not.toBeInTheDocument()
      expect(document.body.querySelectorAll('iframe')).toHaveLength(1)
    })

    it('un domicilio en blanco no genera mapa', async () => {
      await verPublicada({ sucursales: [{ ...sucursalCompleta, domicilio: '   ' }] })

      expect(document.body.querySelectorAll('iframe')).toHaveLength(0)
    })

    it('los caracteres que cortarían la consulta viajan codificados', async () => {
      await verPublicada({ sucursales: [{ ...sucursalCompleta, domicilio: 'Calle 9 & 10 #5, La Plata' }] })

      expect(screen.getByTitle('Mapa de Casa Central')).toHaveAttribute(
        'src',
        'https://www.google.com/maps?q=Calle%209%20%26%2010%20%235%2C%20La%20Plata&output=embed',
      )
    })
  })

  describe('cierre', () => {
    it('la banda final repite los dos botones', async () => {
      await verPublicada()

      const cierre = seccion('¿Listo para pedir?')
      expect(within(cierre).getByRole('heading', { level: 2, name: '¿Listo para pedir?' })).toBeInTheDocument()
      expect(within(cierre).getByRole('link', { name: 'Comprar online' })).toHaveAttribute('href', '/online')
      expect(within(cierre).getByRole('link', { name: 'Escribinos por WhatsApp' })).toHaveAttribute(
        'href',
        'https://wa.me/5491155551234',
      )
    })

    it('sin número de WhatsApp queda solo "Comprar online"', async () => {
      await verPublicada({ sucursales: [sucursalSinContacto] })

      const cierre = seccion('¿Listo para pedir?')
      expect(within(cierre).getAllByRole('link')).toHaveLength(1)
      expect(within(cierre).queryByText('Escribinos por WhatsApp')).not.toBeInTheDocument()
    })

    it('es lo último de la página: no hay pie', async () => {
      await verPublicada()

      expect(screen.queryByRole('contentinfo')).not.toBeInTheDocument()
      expect(screen.getByRole('main').lastElementChild).toBe(seccion('¿Listo para pedir?'))
    })
  })

  it('con todos los datos, las secciones salen en orden y debajo de un único h1', async () => {
    await verConCatalogo(catalogoPublico({ articulos: [martillo], categorias }))

    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1)
    expect(screen.getAllByRole('heading', { level: 2 }).map((titulo) => titulo.textContent)).toEqual([
      'Lo que vas a encontrar',
      'Explorá por categoría',
      'Quiénes somos',
      'Cómo comprar',
      'Dónde estamos',
      '¿Listo para pedir?',
    ])
  })

  it('un comercio con poco cargado muestra lo que tiene, sin huecos ni errores', async () => {
    await verPublicada({ tagline: null, descripcion: null, sucursales: [], colorAcento: null })

    expect(screen.getAllByRole('heading', { level: 2 }).map((titulo) => titulo.textContent)).toEqual([
      'Cómo comprar',
      '¿Listo para pedir?',
    ])
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
})
