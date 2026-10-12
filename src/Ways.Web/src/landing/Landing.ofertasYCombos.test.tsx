import { act, cleanup, render, screen, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { CatalogoPublico, ConsultaDeCatalogo } from '../api/publico'
import { articuloPublico, catalogoPublico, comboPublico, componenteDeCombo } from '../online/datosDePrueba'
import '../publico/publico.css'
import { vitrina } from './datosDePrueba'
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

/** Deja correr las promesas ya resueltas o rechazadas: sirve para afirmar que algo NO apareció. */
const reposar = () =>
  act(async () => {
    await new Promise((resolver) => setTimeout(resolver, 0))
  })

async function verLanding(catalogo: CatalogoPublico | Error) {
  obtenerVitrinaMock.mockResolvedValue(vitrina())
  if (catalogo instanceof Error) obtenerCatalogoMock.mockRejectedValue(catalogo)
  else obtenerCatalogoMock.mockResolvedValue(catalogo)
  render(<Landing />)
  await screen.findByRole('heading', { level: 1 })
  await reposar()
}

beforeEach(() => {
  obtenerVitrinaMock.mockReset()
  obtenerCatalogoMock.mockReset()
})

afterEach(() => {
  cleanup()
  document.documentElement.removeAttribute('data-bs-theme')
  document.documentElement.style.removeProperty('--vitrina-acento')
  document.title = ''
  document.head.querySelector('meta[name="description"]')?.remove()
})

const merienda = comboPublico(1, {
  nombre: 'Combo merienda',
  precio: 500,
  componentes: [
    componenteDeCombo(10, { nombre: 'Coca Cola 1,5 L', cantidad: 2, imagenVersion: 7 }),
    componenteDeCombo(11, { nombre: 'Pan', cantidad: 1 }),
  ],
})

describe('landing: precios con oferta en "Lo que vas a encontrar"', () => {
  it('un artículo en oferta muestra el precio de lista tachado, el final y el cartelito; uno sin oferta queda igual', async () => {
    await verLanding(
      catalogoPublico({
        articulos: [
          articuloPublico(1, { nombre: 'Martillo', precio: 80, precioOriginal: 100, oferta: 'Semana de la herramienta' }),
          articuloPublico(2, { nombre: 'Clavos', precio: 2000 }),
        ],
      }),
    )

    const seccion = screen.getByRole('region', { name: 'Lo que vas a encontrar' })
    const conOferta = within(seccion).getByText('Martillo').closest('li') as HTMLElement
    const sinOferta = within(seccion).getByText('Clavos').closest('li') as HTMLElement

    expect(conOferta.querySelector('s')).toHaveTextContent('Antes $ 100,00')
    expect(within(conOferta).getByText('$ 80,00')).toBeInTheDocument()
    expect(conOferta.querySelector('.publico-etiqueta-oferta')).toHaveAttribute('title', 'Semana de la herramienta')
    expect(sinOferta.querySelector('s')).toBeNull()
    expect(sinOferta.querySelector('.publico-etiqueta-oferta')).toBeNull()
    expect(within(sinOferta).getByText('$ 2.000,00')).toBeInTheDocument()
  })
})

describe('landing: tira de combos', () => {
  it('con combos en la respuesta del catálogo muestra "Combos" con nombre, componentes y precio fijo, sin código ni ahorro', async () => {
    await verLanding(catalogoPublico({ articulos: [articuloPublico(1)], combos: [merienda] }))

    const seccion = screen.getByRole('region', { name: 'Combos' })
    const tarjeta = within(seccion).getByRole('heading', { level: 3, name: 'Combo merienda' }).closest('li') as HTMLElement

    expect(within(tarjeta).getByText('$ 500,00')).toBeInTheDocument()
    expect(within(tarjeta).getAllByRole('listitem').map((li) => li.textContent)).toEqual(['2 × Coca Cola 1,5 L', '1 × Pan'])
    expect(Array.from(tarjeta.querySelectorAll('img')).map((i) => i.getAttribute('src'))).toEqual(['/api/publico/articulos/10/imagen?v=7'])
    expect(tarjeta.textContent).not.toMatch(/\bC\d+\b/)
    expect(seccion.textContent).not.toMatch(/ahorr/i)
  })

  it('cada tarjeta y el botón llevan a la sección de combos de la tienda', async () => {
    await verLanding(catalogoPublico({ articulos: [articuloPublico(1)], combos: [merienda] }))

    const seccion = screen.getByRole('region', { name: 'Combos' })

    expect(within(seccion).getByRole('link', { name: /Combo merienda/ })).toHaveAttribute('href', '/online#combos')
    expect(within(seccion).getByRole('link', { name: 'Ver todos los combos' })).toHaveAttribute('href', '/online#combos')
    expect(within(seccion).queryByRole('button')).toBeNull()
  })

  it('muestra como máximo cuatro combos', async () => {
    const seis = [1, 2, 3, 4, 5, 6].map((n) => comboPublico(n))

    await verLanding(catalogoPublico({ articulos: [articuloPublico(1)], combos: seis }))

    const nombres = within(screen.getByRole('region', { name: 'Combos' })).getAllByRole('heading', { level: 3 }).map((h) => h.textContent)
    expect(nombres).toEqual(['Combo 1', 'Combo 2', 'Combo 3', 'Combo 4'])
  })

  it('con la lista de combos vacía no hay sección, aunque haya artículos', async () => {
    await verLanding(catalogoPublico({ articulos: [articuloPublico(1)], combos: [] }))

    expect(screen.queryByRole('region', { name: 'Combos' })).toBeNull()
    expect(screen.getByRole('region', { name: 'Lo que vas a encontrar' })).toBeInTheDocument()
  })

  it('una respuesta sin la clave de combos (un servidor anterior) tampoco la dibuja', async () => {
    const { combos: _combos, ...sinCombos } = catalogoPublico({ articulos: [articuloPublico(1)] })

    await verLanding(sinCombos as CatalogoPublico)

    expect(screen.queryByRole('region', { name: 'Combos' })).toBeNull()
  })

  it('si el catálogo falla no hay sección y la landing no muestra error', async () => {
    await verLanding(new Error('sin red'))

    expect(screen.queryByRole('region', { name: 'Combos' })).toBeNull()
    expect(screen.queryByRole('alert')).toBeNull()
  })
})
