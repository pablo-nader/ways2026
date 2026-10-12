import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { CatalogoPublico, ConsultaDeCatalogo } from '../api/publico'
import { ErrorApi } from '../api/cliente'
import type { VitrinaPublica } from '../api/tipos'
import '../publico/publico.css'
import { claveDelCarrito } from './carrito'
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

/** Lo que el carrito tiene guardado en el navegador: la verdad del carrito aunque el campo muestre otra cosa. */
function cantidadesGuardadas() {
  const crudo = localStorage.getItem(claveDelCarrito(window.location.host))
  const lineas = JSON.parse(crudo ?? '[]') as { idArticulo: number; cantidad: number }[]

  return lineas.map(({ idArticulo, cantidad }) => ({ idArticulo, cantidad }))
}

/** La región de avisos del selector de cantidad al que pertenece el campo. */
function avisoDe(campo: HTMLElement) {
  return campo.closest('.publico-cantidad')?.querySelector('.publico-cantidad-ayuda') as HTMLElement
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
  // Primero se desmonta: el desmontaje vacía los efectos pendientes y uno de ellos puede guardar el
  // carrito. Si el almacenamiento se limpiara antes, esa escritura le llegaría al test siguiente.
  cleanup()
  vi.useRealTimers()
  localStorage.clear()
  window.history.replaceState(null, '', '/')
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

  it('si la categoría elegida llega sin categorías en la respuesta, "Todos" sigue ahí para salir del filtro', async () => {
    await verCatalogo()
    obtenerCatalogoMock.mockResolvedValue(catalogoPublico({ articulos: [], categorias: [], total: 0 }))

    await userEvent.click(screen.getByRole('button', { name: 'Fijaciones 1' }))

    expect(await screen.findByText('No encontramos artículos para esa búsqueda.')).toBeInTheDocument()
    const todos = screen.getByRole('button', { name: 'Todos' })
    expect(todos).toHaveAttribute('aria-pressed', 'false')
    expect(screen.queryByRole('button', { name: 'Fijaciones 1' })).not.toBeInTheDocument()
    obtenerCatalogoMock.mockResolvedValue(catalogoPublico({ articulos: [martillo, clavos], categorias }))

    await userEvent.click(todos)

    expect(await screen.findByRole('heading', { name: 'Martillo' })).toBeInTheDocument()
    expect(llamadas().at(-1)).toEqual({ buscar: '', idCategoria: null, pagina: 1, tamano: 24 })
    expect(screen.getByRole('button', { name: 'Todos' })).toHaveAttribute('aria-pressed', 'true')
  })

  describe('búsqueda', () => {
    // El reloj falso se instala con la barra ya montada. No queda un temporizador real colgado
    // porque la barra no agenda nada al montarse (lo prueba `BarraDelCatalogo.test.tsx`).
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

  describe('filtros que vienen en la URL', () => {
    function abrirCon(consulta: string) {
      window.history.replaceState(null, '', `/online${consulta}`)
    }

    it('?categoria=3 arranca filtrado por esa categoría, con una sola lectura', async () => {
      abrirCon('?categoria=3')
      await verCatalogo(catalogoPublico({ articulos: [martillo], categorias: [...categorias, categoriaPublica(3, 'Pinturas', 4)] }))

      expect(llamadas()).toEqual([{ buscar: '', idCategoria: 3, pagina: 1, tamano: 24 }])
      expect(screen.getByRole('button', { name: /Pinturas/ })).toHaveAttribute('aria-pressed', 'true')
      expect(screen.getByRole('button', { name: 'Todos' })).toHaveAttribute('aria-pressed', 'false')
    })

    it('?buscar=yerba arranca con esa búsqueda en la caja, con una sola lectura', async () => {
      abrirCon('?buscar=yerba')
      await verCatalogo()

      expect(llamadas()).toEqual([{ buscar: 'yerba', idCategoria: null, pagina: 1, tamano: 24 }])
      expect(screen.getByRole('searchbox', { name: 'Buscar artículos' })).toHaveValue('yerba')
    })

    it('los dos parámetros juntos se piden en la misma lectura', async () => {
      abrirCon('?categoria=3&buscar=yerba')
      await verCatalogo()

      expect(llamadas()).toEqual([{ buscar: 'yerba', idCategoria: 3, pagina: 1, tamano: 24 }])
    })

    it('una categoría que no es un id se ignora y se pide el catálogo completo', async () => {
      abrirCon('?categoria=abc')
      await verCatalogo()

      expect(llamadas()).toEqual([{ buscar: '', idCategoria: null, pagina: 1, tamano: 24 }])
    })

    it('sin parámetros el catálogo arranca sin filtros', async () => {
      await verCatalogo()

      expect(llamadas()).toEqual([{ buscar: '', idCategoria: null, pagina: 1, tamano: 24 }])
      expect(screen.getByRole('searchbox', { name: 'Buscar artículos' })).toHaveValue('')
    })
  })

  describe('limpiar una búsqueda sin resultados', () => {
    async function conUnaBusquedaSinResultados() {
      await verCatalogo()
      const sinFiltros = diferida<CatalogoPublico>()
      obtenerCatalogoMock.mockImplementation(({ buscar }) =>
        buscar === 'zzz' ? Promise.resolve(catalogoPublico({ articulos: [], categorias: [], total: 0 })) : sinFiltros.promesa,
      )
      const campo = screen.getByRole('searchbox', { name: 'Buscar artículos' })
      campo.focus()
      fireEvent.change(campo, { target: { value: 'zzz' } })
      fireEvent.submit(campo.closest('form') as HTMLFormElement)
      await screen.findByText('No encontramos artículos para esa búsqueda.')

      return { campo, sinFiltros }
    }

    /**
     * Entre que el filtro vuelve a "sin filtros" y llega la respuesta nueva, la pantalla se dibuja una
     * vez más con la respuesta vieja (la vacía, pedida con "zzz"). Ese dibujo no se puede observar
     * desde afuera, pero si reemplazara la barra por la pantalla de "se está preparando" la barra
     * volvería a montarse: se afirma que sigue siendo el mismo elemento, con foco, mientras la
     * respuesta nueva está en vuelo.
     */
    async function afirmarQueLaBarraSobrevive(campo: HTMLElement) {
      await waitFor(() => expect(llamadas().at(-1)?.buscar).toBe(''))
      expect(screen.queryByText('El catálogo se está preparando.')).not.toBeInTheDocument()
      expect(screen.getByRole('searchbox', { name: 'Buscar artículos' })).toBe(campo)
      expect(campo).toBeInTheDocument()
      expect(campo).toHaveFocus()
    }

    it('con Enter sobre la caja vacía la barra sigue, con foco, y llega el catálogo completo', async () => {
      const { campo, sinFiltros } = await conUnaBusquedaSinResultados()

      fireEvent.change(campo, { target: { value: '' } })
      fireEvent.submit(campo.closest('form') as HTMLFormElement)

      await afirmarQueLaBarraSobrevive(campo)
      await act(async () => sinFiltros.resolver(catalogoPublico({ articulos: [martillo, clavos], categorias })))
      expect(await screen.findByRole('heading', { name: 'Martillo' })).toBeInTheDocument()
      expect(screen.getByRole('searchbox', { name: 'Buscar artículos' })).toBe(campo)
      expect(campo).toHaveFocus()
      expect(screen.queryByText('El catálogo se está preparando.')).not.toBeInTheDocument()
    })

    it('borrando con Backspace hasta vaciar la caja pasa lo mismo, después de la pausa', async () => {
      const { campo, sinFiltros } = await conUnaBusquedaSinResultados()

      await userEvent.type(campo, '{Backspace}{Backspace}{Backspace}')
      expect(campo).toHaveValue('')

      await afirmarQueLaBarraSobrevive(campo)
      await act(async () => sinFiltros.resolver(catalogoPublico({ articulos: [martillo, clavos], categorias })))
      expect(await screen.findByRole('heading', { name: 'Martillo' })).toBeInTheDocument()
      expect(screen.getByRole('searchbox', { name: 'Buscar artículos' })).toBe(campo)
    })

    it('si el comercio quedó sin artículos mientras tanto, avisa sin reemplazar la barra', async () => {
      const { campo, sinFiltros } = await conUnaBusquedaSinResultados()

      fireEvent.change(campo, { target: { value: '' } })
      fireEvent.submit(campo.closest('form') as HTMLFormElement)
      await act(async () => sinFiltros.resolver(catalogoPublico({ articulos: [], categorias: [], total: 0 })))

      expect(await screen.findByText('Por ahora no hay artículos publicados.')).toBeInTheDocument()
      expect(screen.queryByText('No encontramos artículos para esa búsqueda.')).not.toBeInTheDocument()
      expect(screen.queryByText('El catálogo se está preparando.')).not.toBeInTheDocument()
      expect(screen.getByRole('searchbox', { name: 'Buscar artículos' })).toBe(campo)
    })

    it('si la primera lectura sin filtros viene vacía sí muestra la pantalla de "se está preparando", sin barra', async () => {
      obtenerVitrinaMock.mockResolvedValue(vitrina)
      obtenerCatalogoMock.mockResolvedValue(catalogoPublico())
      render(<Tienda />)

      expect(await screen.findByRole('heading', { level: 1, name: 'El catálogo se está preparando.' })).toBeInTheDocument()
      expect(screen.queryByRole('searchbox')).not.toBeInTheDocument()
    })
  })

  describe('"Ver más"', () => {
    const primera =[articuloPublico(1, { nombre: 'Uno' }), articuloPublico(2, { nombre: 'Dos' })]

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

      const cargando = await screen.findByRole('status', { name: 'Cargando el catálogo' })

      expect(cargando).toHaveAttribute('aria-busy', 'true')
      const principal = screen.getByRole('main')
      expect(principal).not.toHaveAttribute('role')
      expect(principal).toContainElement(cargando)
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

      const region = document.querySelector('.publico-solo-lectura[aria-live="polite"]') as HTMLElement
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
      await userEvent.type(cantidad, '0,25{Enter}')

      expect(cantidad).toHaveValue('0,25')
      expect(cantidadesGuardadas()).toEqual([{ idArticulo: 2, cantidad: 0.25 }])
      expect(avisoDe(cantidad)).toBeEmptyDOMElement()
    })

    /**
     * jsdom no aplica el "focus fixup" del navegador (mandar el foco a `<body>` cuando se quita el
     * elemento enfocado): se afirma el destino que el código elige, no la pérdida de foco real.
     */
    describe('el foco en la tarjeta', () => {
      it('"Agregar" lleva el foco al + del selector de cantidad', async () => {
        await verCatalogo()

        await userEvent.click(screen.getByRole('button', { name: 'Agregar Martillo al carrito' }))

        expect(screen.getByRole('button', { name: 'Agregar uno más de Martillo' })).toHaveFocus()
      })

      it('lo mismo con Enter sobre "Agregar" en un artículo por peso', async () => {
        await verCatalogo()
        screen.getByRole('button', { name: 'Agregar Clavos al carrito' }).focus()

        await userEvent.keyboard('{Enter}')

        expect(screen.getByRole('button', { name: 'Agregar medio kilo más de Clavos' })).toHaveFocus()
      })

      it('"−" que quita la línea lleva el foco al "Agregar" de esa misma tarjeta', async () => {
        await verCatalogo()
        await userEvent.click(screen.getByRole('button', { name: 'Agregar Martillo al carrito' }))
        await userEvent.click(screen.getByRole('button', { name: 'Agregar Clavos al carrito' }))

        await userEvent.click(screen.getByRole('button', { name: 'Quitar uno de Martillo' }))

        expect(screen.getByRole('button', { name: 'Agregar Martillo al carrito' })).toHaveFocus()
        expect(screen.getByRole('textbox', { name: 'Cantidad de Clavos' })).toBeInTheDocument()
      })

      it('"−" que no quita la línea deja el foco donde estaba', async () => {
        await verCatalogo()
        await userEvent.click(screen.getByRole('button', { name: 'Agregar Clavos al carrito' }))
        await userEvent.click(screen.getByRole('button', { name: 'Agregar medio kilo más de Clavos' }))
        const menos = screen.getByRole('button', { name: 'Quitar medio kilo de Clavos' })

        await userEvent.click(menos)

        expect(menos).toHaveFocus()
      })

      it('un selector que aparece sin que se haya pulsado "Agregar" aquí (carrito guardado) no roba el foco', async () => {
        localStorage.setItem(
          claveDelCarrito(window.location.host),
          JSON.stringify([{ idArticulo: 1, nombre: 'Martillo', precio: 100, unidadVenta: 'Unidad', cantidad: 2, imagenVersion: null }]),
        )

        await verCatalogo()

        expect(screen.getByRole('textbox', { name: 'Cantidad de Martillo' })).toBeInTheDocument()
        expect(document.body).toHaveFocus()
      })

      it('quitar la línea desde el panel no manda el foco al "Agregar" de la tarjeta', async () => {
        await verCatalogo()
        await userEvent.click(screen.getByRole('button', { name: 'Agregar Martillo al carrito' }))
        await userEvent.click(screen.getByRole('button', { name: /^Carrito/ }))
        const panel = screen.getByRole('dialog', { name: 'Carrito' })

        await userEvent.click(within(panel).getByRole('button', { name: 'Quitar Martillo del carrito' }))

        expect(screen.getByRole('button', { name: 'Agregar Martillo al carrito' })).not.toHaveFocus()
        expect(within(panel).getByRole('button', { name: 'Cerrar' })).toHaveFocus()
      })
    })

    describe('cantidad tipeada', () => {
      async function conMartilloEnDos() {
        await verCatalogo()
        await userEvent.click(screen.getByRole('button', { name: 'Agregar Martillo al carrito' }))
        await userEvent.click(screen.getByRole('button', { name: 'Agregar uno más de Martillo' }))

        return screen.getByRole('textbox', { name: 'Cantidad de Martillo' })
      }

      const AVISO = 'No se cambió la cantidad. Ingresá una cantidad entera, hasta 999.'

      it('no se aplica ni se avisa mientras se escribe: "0" y "2," son pasos de una cantidad válida', async () => {
        const cantidad = await conMartilloEnDos()

        await userEvent.clear(cantidad)
        await userEvent.type(cantidad, '0')
        expect(avisoDe(cantidad)).toBeEmptyDOMElement()
        await userEvent.type(cantidad, '{Backspace}2,')

        expect(cantidad).toHaveValue('2,')
        expect(avisoDe(cantidad)).toBeEmptyDOMElement()
        expect(screen.queryByRole('alert')).not.toBeInTheDocument()
        expect(cantidadesGuardadas()).toEqual([{ idArticulo: 1, cantidad: 2 }])
      })

      it('al salir del campo se aplica lo tipeado', async () => {
        const cantidad = await conMartilloEnDos()

        await userEvent.clear(cantidad)
        await userEvent.type(cantidad, '7')
        expect(cantidadesGuardadas()).toEqual([{ idArticulo: 1, cantidad: 2 }])
        await userEvent.tab()

        expect(cantidadesGuardadas()).toEqual([{ idArticulo: 1, cantidad: 7 }])
        expect(cantidad).toHaveValue('7')
        expect(avisoDe(cantidad)).toBeEmptyDOMElement()
      })

      it('con Enter se aplica sin salir del campo', async () => {
        const cantidad = await conMartilloEnDos()

        await userEvent.clear(cantidad)
        await userEvent.type(cantidad, '5{Enter}')

        expect(cantidad).toHaveFocus()
        expect(cantidadesGuardadas()).toEqual([{ idArticulo: 1, cantidad: 5 }])
        await userEvent.click(screen.getByRole('button', { name: 'Agregar uno más de Martillo' }))
        expect(cantidad).toHaveValue('6')
      })

      it.each([
        ['al salir del campo', '{Tab}'],
        ['con Enter', '{Enter}'],
      ])('una cantidad que la unidad no admite, %s, vuelve a la última buena, no cambia el carrito y lo avisa con cortesía', async (_cuando, tecla) => {
        const cantidad = await conMartilloEnDos()

        await userEvent.clear(cantidad)
        await userEvent.type(cantidad, `2,5${tecla}`)

        expect(cantidad).toHaveValue('2')
        expect(cantidadesGuardadas()).toEqual([{ idArticulo: 1, cantidad: 2 }])
        expect(avisoDe(cantidad)).toHaveAttribute('aria-live', 'polite')
        expect(avisoDe(cantidad)).toHaveTextContent(AVISO)
        expect(screen.queryByRole('alert')).not.toBeInTheDocument()
      })

      it('dejar el campo vacío tampoco vale: vuelve a la última buena y avisa', async () => {
        const cantidad = await conMartilloEnDos()

        await userEvent.clear(cantidad)
        await userEvent.tab()

        expect(cantidad).toHaveValue('2')
        expect(avisoDe(cantidad)).toHaveTextContent(AVISO)
      })

      it('el aviso se va con la próxima acción: volver a escribir, + o −', async () => {
        const cantidad = await conMartilloEnDos()
        const provocarElAviso = async () => {
          await userEvent.clear(cantidad)
          await userEvent.type(cantidad, 'x{Enter}')
          expect(avisoDe(cantidad)).toHaveTextContent(AVISO)
        }

        await provocarElAviso()
        await userEvent.type(cantidad, '3')
        expect(avisoDe(cantidad)).toBeEmptyDOMElement()
        await userEvent.type(cantidad, '{Enter}')

        await provocarElAviso()
        await userEvent.click(screen.getByRole('button', { name: 'Agregar uno más de Martillo' }))
        expect(avisoDe(cantidad)).toBeEmptyDOMElement()

        await provocarElAviso()
        await userEvent.click(screen.getByRole('button', { name: 'Quitar uno de Martillo' }))
        expect(avisoDe(cantidad)).toBeEmptyDOMElement()
      })

      it('salir del campo sin haber escrito nada no cambia nada ni avisa', async () => {
        const cantidad = await conMartilloEnDos()

        cantidad.focus()
        await userEvent.tab()

        expect(avisoDe(cantidad)).toBeEmptyDOMElement()
        expect(cantidadesGuardadas()).toEqual([{ idArticulo: 1, cantidad: 2 }])
      })
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
      await userEvent.type(cantidad, '999{Enter}')

      expect(screen.getByRole('button', { name: 'Agregar uno más de Martillo' })).toBeDisabled()
    })
  })

  describe('las líneas del carrito siguen al catálogo', () => {
    function buscarAhora(texto: string) {
      const campo = screen.getByRole('searchbox', { name: 'Buscar artículos' })
      fireEvent.change(campo, { target: { value: texto } })
      fireEvent.submit(campo.closest('form') as HTMLFormElement)
    }

    async function abrirElPanel() {
      await userEvent.click(screen.getByRole('button', { name: /^Carrito/ }))

      return screen.getByRole('dialog', { name: 'Carrito' })
    }

    async function lineaDelPanel(nombre: string) {
      return within(await abrirElPanel()).getByText(nombre).closest('li') as HTMLElement
    }

    it('una línea guardada con un precio viejo se pone al día con la primera lectura del catálogo', async () => {
      localStorage.setItem(
        claveDelCarrito(window.location.host),
        JSON.stringify([{ idArticulo: 1, nombre: 'Martillo', precio: 100, unidadVenta: 'Unidad', cantidad: 2, imagenVersion: null }]),
      )
      await verCatalogo()

      const linea = await lineaDelPanel('Martillo')

      expect(linea).toHaveTextContent('$ 1.234,56')
      expect(linea).toHaveTextContent('$ 2.469,12')
      expect(JSON.parse(localStorage.getItem(claveDelCarrito(window.location.host)) as string)).toEqual([
        { idArticulo: 1, nombre: 'Martillo', precio: 1234.56, unidadVenta: 'Unidad', cantidad: 2, imagenVersion: 42 },
      ])
    })

    it('un artículo agregado a 100 pasa a 120 cuando otra búsqueda lo trae a 120', async () => {
      await verCatalogo(catalogoPublico({ articulos: [articuloPublico(1, { nombre: 'Martillo', precio: 100 })] }))
      await userEvent.click(screen.getByRole('button', { name: 'Agregar Martillo al carrito' }))
      obtenerCatalogoMock.mockResolvedValue(
        catalogoPublico({ articulos: [articuloPublico(1, { nombre: 'Martillo', precio: 120 })] }),
      )

      buscarAhora('mart')
      await waitFor(() => expect(screen.getAllByText('$ 120,00')).toHaveLength(1))
      const linea = await lineaDelPanel('Martillo')

      expect(linea).toHaveTextContent('$ 120,00')
      expect(linea).not.toHaveTextContent('$ 100,00')
    })

    it('un artículo que pasó de Peso a Unidad redondea la cantidad de su línea', async () => {
      await verCatalogo(catalogoPublico({ articulos: [articuloPublico(2, { nombre: 'Clavos', unidadVenta: 'Peso' })] }))
      await userEvent.click(screen.getByRole('button', { name: 'Agregar Clavos al carrito' }))
      for (let i = 0; i < 3; i++) await userEvent.click(screen.getByRole('button', { name: 'Agregar medio kilo más de Clavos' }))
      expect(screen.getByRole('textbox', { name: 'Cantidad de Clavos' })).toHaveValue('2,5')
      obtenerCatalogoMock.mockResolvedValue(
        catalogoPublico({ articulos: [articuloPublico(2, { nombre: 'Clavos', unidadVenta: 'Unidad' })] }),
      )

      buscarAhora('clavos')

      await waitFor(() => expect(screen.getByRole('textbox', { name: 'Cantidad de Clavos' })).toHaveValue('3'))
      expect(screen.getByRole('button', { name: 'Agregar uno más de Clavos' })).toBeInTheDocument()
    })

    it('un carrito adoptado de otra pestaña con un precio viejo se pone al día con la lista que esta pestaña ya tiene, sin escribirlo de vuelta', async () => {
      await verCatalogo()
      const clave = claveDelCarrito(window.location.host)
      const viejo = [{ idArticulo: 1, nombre: 'Martillo', precio: 100, unidadVenta: 'Unidad', cantidad: 2, imagenVersion: null }]
      localStorage.setItem(clave, JSON.stringify(viejo))
      const escritura = vi.spyOn(Storage.prototype, 'setItem')

      act(() => {
        window.dispatchEvent(new StorageEvent('storage', { key: clave, storageArea: localStorage }))
      })
      const linea = await lineaDelPanel('Martillo')

      expect(linea).toHaveTextContent('$ 1.234,56')
      expect(linea).toHaveTextContent('$ 2.469,12')
      expect(linea).not.toHaveTextContent('$ 100,00')
      expect(escritura).not.toHaveBeenCalled()
      expect(localStorage.getItem(clave)).toBe(JSON.stringify(viejo))
      escritura.mockRestore()
    })

    it('"Ver más" pone al día las líneas de los artículos que trae y no toca las de los que no están', async () => {
      localStorage.setItem(
        claveDelCarrito(window.location.host),
        JSON.stringify([
          { idArticulo: 3, nombre: 'Tres', precio: 100, unidadVenta: 'Unidad', cantidad: 1, imagenVersion: null },
          { idArticulo: 4, nombre: 'Cuatro', precio: 50, unidadVenta: 'Unidad', cantidad: 1, imagenVersion: null },
        ]),
      )
      await verCatalogo(
        catalogoPublico({ articulos: [articuloPublico(1, { nombre: 'Uno' }), articuloPublico(2, { nombre: 'Dos' })], total: 30 }),
      )
      expect(await lineaDelPanel('Tres')).toHaveTextContent('$ 100,00')
      await userEvent.click(screen.getByRole('button', { name: 'Cerrar' }))
      obtenerCatalogoMock.mockResolvedValue(
        catalogoPublico({ articulos: [articuloPublico(3, { nombre: 'Tres', precio: 120 })], total: 30, pagina: 2 }),
      )

      await userEvent.click(screen.getByRole('button', { name: 'Ver más' }))
      await screen.findByRole('heading', { name: 'Tres' })

      const panel = await abrirElPanel()
      expect(within(panel).getByText('Tres').closest('li')).toHaveTextContent('$ 120,00')
      expect(within(panel).getByText('Cuatro').closest('li')).toHaveTextContent('$ 50,00')
    })
  })
})
