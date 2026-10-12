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

    it('con una sola sucursal salen el horario, el domicilio y el WhatsApp, cada uno con su rótulo para lectores y sin nombre de sucursal', async () => {
      await verPublicada({ sucursales: [sucursalCompleta] })

      expect(pildoras()).toEqual(['Horario: Lun a Vie 9 a 18', 'Dirección: Av. Siempreviva 742', 'WhatsApp: WhatsApp'])
    })

    it('los rótulos "Horario:", "Dirección:" y "WhatsApp:" son solo para lectores de pantalla', async () => {
      await verPublicada({ sucursales: [sucursalCompleta] })

      const rotulos = within(seccion('Todo para tu obra')).getAllByText(/^(Horario|Dirección|WhatsApp):$/)
      expect(rotulos).toHaveLength(3)
      for (const rotulo of rotulos) {
        expect(rotulo).toHaveClass('publico-solo-lectores')
        expect(getComputedStyle(rotulo).position).toBe('absolute')
      }
    })

    it('con varias sucursales cada píldora dice de cuál sale: horario, domicilio y WhatsApp de la primera', async () => {
      await verPublicada()

      expect(pildoras()).toEqual([
        'Horario: Casa Central · Lun a Vie 9 a 18',
        'Dirección: Casa Central · Av. Siempreviva 742',
        'WhatsApp: Casa Central · WhatsApp',
      ])
    })

    it('con varias sucursales con datos propios, ninguna píldora mezcla los de otra', async () => {
      const gascon = { ...sucursalCompleta, nombre: 'Gascón', domicilio: 'Gascón 100', horario: '24 hs', whatsapp: '+54 9 11 4444-0000' }
      await verPublicada({ sucursales: [gascon, sucursalCompleta] })

      expect(pildoras()).toEqual(['Horario: Gascón · 24 hs', 'Dirección: Gascón · Gascón 100', 'WhatsApp: Gascón · WhatsApp'])
      expect(within(seccion('Todo para tu obra')).getByRole('link', { name: 'Escribinos por WhatsApp' })).toHaveAttribute(
        'href',
        'https://wa.me/5491144440000',
      )
    })

    it('el horario y el domicilio son los de la primera sucursal, no los de otra; el WhatsApp cae a la primera que tiene número y lo dice', async () => {
      await verPublicada({ sucursales: [sucursalSinContacto, sucursalCompleta] })

      expect(pildoras()).toEqual(['WhatsApp: Casa Central · WhatsApp'])
      expect(sello()).toBeNull()
    })

    it('sin ningún número no hay píldora de WhatsApp', async () => {
      await verPublicada({ sucursales: [{ ...sucursalCompleta, whatsapp: null }] })

      expect(pildoras()).toEqual(['Horario: Lun a Vie 9 a 18', 'Dirección: Av. Siempreviva 742'])
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
      expect(pildoras()[0]).toBe('Horario: Todos los días, 24 horas')
    })

    it.each(['Lun a Sáb 8 a 24 hs', '9 hs a 24 hs', 'Lun a Vie 8 a 18 hs. Guardia 24 hs', 'Lun a Sáb 24 hs, Dom cerrado'])(
      'el horario %j no se anuncia como abierto 24 hs',
      async (horario) => {
        await verPublicada({ sucursales: [{ ...sucursalCompleta, horario }] })

        expect(sello()?.textContent).toBe(horario)
      },
    )

    it('el texto del sello no recorta las mayúsculas acentuadas: interlineado de al menos 1 y aire arriba', async () => {
      await verPublicada()

      const texto = document.body.querySelector('.publico-sello-texto') as HTMLElement
      const estilo = getComputedStyle(texto)
      expect(parseFloat(estilo.lineHeight)).toBeGreaterThanOrEqual(1)
      expect(parseFloat(estilo.paddingTop)).toBeGreaterThan(0)
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
      expect(enlaceDelMartillo).toHaveAttribute('href', '/online?buscar=Martillo')
      expect(enlaceDelMartillo).toHaveTextContent('$ 1.234,56')
      expect(enlaceDelMartillo).not.toHaveTextContent('por kg')
      const enlaceDeLosClavos = within(destacados).getByRole('link', { name: /Clavos/ })
      expect(enlaceDeLosClavos).toHaveAttribute('href', '/online?buscar=Clavos')
      expect(enlaceDeLosClavos).toHaveTextContent('$ 2.000,00')
      expect(enlaceDeLosClavos).toHaveTextContent('por kg')
      expect(within(destacados).getAllByRole('listitem')).toHaveLength(2)
    })

    it('el nombre del artículo viaja codificado en la búsqueda a la tienda', async () => {
      await verConCatalogo(catalogoPublico({ articulos: [articuloPublico(3, { nombre: 'Tornillo 1/4" & tuerca #5' })] }))

      expect(within(seccion('Lo que vas a encontrar')).getByRole('link', { name: /Tornillo/ })).toHaveAttribute(
        'href',
        '/online?buscar=Tornillo%201%2F4%22%20%26%20tuerca%20%235',
      )
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
      expect(pasos()[0]).toHaveTextContent(/^01Mirá lo que hay en la tienda online$/)
      expect(pasos()[1]).toHaveTextContent(/^02/)
      expect(pasos()[2]).toHaveTextContent(/^03Coordiná con el comercio el retiro en el local$/)
    })

    it('no promete un pedido en la tienda ni una entrega que hoy no existen', async () => {
      await verPublicada()

      const textoDeLosPasos = within(seccion('Cómo comprar')).getByRole('list').textContent ?? ''
      expect(textoDeLosPasos).not.toMatch(/confirm|entrega|envío|pedido/i)
    })

    it('con un número de WhatsApp el segundo paso manda a escribir por WhatsApp', async () => {
      await verPublicada()

      expect(pasos()[1]).toHaveTextContent(/^02Escribinos por WhatsApp con lo que necesitás$/)
      expect(screen.queryByText('Consultá al comercio por lo que necesitás')).not.toBeInTheDocument()
    })

    it('sin número de WhatsApp el segundo paso es consultar al comercio', async () => {
      await verPublicada({ sucursales: [sucursalSinContacto] })

      expect(pasos()[1]).toHaveTextContent(/^02Consultá al comercio por lo que necesitás$/)
      expect(screen.queryByText('Escribinos por WhatsApp con lo que necesitás')).not.toBeInTheDocument()
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
      expect(within(screen.getByText('Casa Central').closest('li') as HTMLElement).getByTitle('Mapa de Casa Central')).toBe(mapa)
    })

    it('el mapa corre en un sandbox que no deja navegar la página ni enviar formularios, y usa la política de referrer del navegador', async () => {
      await verPublicada()

      const mapa = screen.getByTitle('Mapa de Casa Central')
      expect(mapa.getAttribute('sandbox')?.split(' ').sort()).toEqual([
        'allow-popups',
        'allow-popups-to-escape-sandbox',
        'allow-same-origin',
        'allow-scripts',
      ])
      expect(mapa).not.toHaveAttribute('referrerpolicy')
    })

    it('bajo cada mapa hay un enlace de respaldo a Google Maps con la dirección codificada, en pestaña nueva', async () => {
      await verPublicada({ sucursales: [{ ...sucursalCompleta, domicilio: 'Calle 9 & 10 #5, La Plata' }] })

      const enlace = within(screen.getByText('Casa Central').closest('li') as HTMLElement).getByRole('link', {
        name: 'Abrir en Google Maps de Casa Central',
      })
      expect(enlace).toHaveAttribute(
        'href',
        'https://www.google.com/maps/search/?api=1&query=Calle%209%20%26%2010%20%235%2C%20La%20Plata',
      )
      expect(enlace).toHaveAttribute('target', '_blank')
      expect(enlace).toHaveAttribute('rel', 'noopener')
      expect(enlace).toHaveTextContent('Abrir en Google Maps')
    })

    it('una sucursal sin domicilio no tiene enlace a Google Maps', async () => {
      await verPublicada({ sucursales: [sucursalSinContacto] })

      expect(screen.queryByRole('link', { name: /Google Maps/ })).not.toBeInTheDocument()
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

      const cierre = seccion('¿Querés saber más?')
      expect(within(cierre).getAllByRole('link')).toHaveLength(1)
      expect(within(cierre).queryByText('Escribinos por WhatsApp')).not.toBeInTheDocument()
    })

    it('sin número de WhatsApp el título no invita a pedir, porque no hay por dónde', async () => {
      await verPublicada({ sucursales: [sucursalSinContacto] })

      expect(screen.queryByText('¿Listo para pedir?')).not.toBeInTheDocument()
      expect(within(seccion('¿Querés saber más?')).getByRole('heading', { level: 2, name: '¿Querés saber más?' })).toBeInTheDocument()
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
      '¿Querés saber más?',
    ])
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
})
