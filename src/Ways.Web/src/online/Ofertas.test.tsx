import { cleanup, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it } from 'vitest'

import type { ArticuloPublico } from '../api/publico'
import '../publico/publico.css'
import { claveDelCarrito } from './carrito'
import { CarritoProvider } from './CarritoProvider'
import { articuloPublico, itemsGuardados } from './datosDePrueba'
import { PanelCarrito } from './PanelCarrito'
import { PrecioConOferta } from './PrecioConOferta'
import { TarjetaDeArticulo } from './TarjetaDeArticulo'

const HOST = 'sur.ways.app'

afterEach(() => {
  cleanup()
  localStorage.clear()
})

function verTarjeta(articulo: ArticuloPublico, cantidad = 0) {
  render(
    <CarritoProvider host={HOST}>
      <ul>
        <TarjetaDeArticulo articulo={articulo} cantidad={cantidad} />
      </ul>
    </CarritoProvider>,
  )

  return screen.getByRole('listitem')
}

const enOferta = articuloPublico(1, {
  nombre: 'Martillo',
  precio: 80,
  precioOriginal: 100,
  oferta: 'Semana de la herramienta',
})

describe('precio con oferta en la tarjeta', () => {
  it('con precioOriginal muestra el de lista tachado con "Antes", el final y el cartelito "Oferta"', () => {
    const tarjeta = verTarjeta(enOferta)

    const tachado = tarjeta.querySelector('s') as HTMLElement
    expect(tachado).toHaveTextContent('Antes $ 100,00')
    expect(tachado.querySelector('.publico-solo-lectura')).toHaveTextContent('Antes')
    expect(within(tarjeta).getByText('$ 80,00')).toBeInTheDocument()
    expect(tarjeta.querySelector('.publico-precio .publico-solo-lectura')).toHaveTextContent('Ahora')
    expect(tarjeta.querySelector('.publico-etiqueta-oferta')).toHaveTextContent('Oferta')
  })

  it('el nombre de la oferta va de title y como texto para lectores, no a la vista del cartelito', () => {
    const tarjeta = verTarjeta(enOferta)

    const etiqueta = tarjeta.querySelector('.publico-etiqueta-oferta') as HTMLElement
    expect(etiqueta).toHaveAttribute('title', 'Semana de la herramienta')
    expect(etiqueta.querySelector('.publico-solo-lectura')).toHaveTextContent(': Semana de la herramienta')
  })

  it('sin precioOriginal la tarjeta no tiene tachado, ni cartelito, ni "Antes", ni "Ahora"', () => {
    const tarjeta = verTarjeta(articuloPublico(1, { nombre: 'Martillo', precio: 100 }))

    expect(within(tarjeta).getByText('$ 100,00')).toBeInTheDocument()
    expect(tarjeta.querySelector('s')).toBeNull()
    expect(tarjeta.querySelector('.publico-etiqueta-oferta')).toBeNull()
    expect(tarjeta).not.toHaveTextContent('Antes')
    expect(tarjeta).not.toHaveTextContent('Ahora')
    expect(tarjeta).not.toHaveTextContent('Oferta')
  })

  it('una oferta sin nombre muestra el cartelito sin title ni texto extra', () => {
    const tarjeta = verTarjeta(articuloPublico(1, { nombre: 'Martillo', precio: 80, precioOriginal: 100 }))

    const etiqueta = tarjeta.querySelector('.publico-etiqueta-oferta') as HTMLElement
    expect(etiqueta).not.toHaveAttribute('title')
    expect(etiqueta.textContent).toBe('Oferta')
  })

  it('un artículo por peso en oferta conserva el "por kg"', () => {
    const tarjeta = verTarjeta(articuloPublico(2, { nombre: 'Clavos', unidadVenta: 'Peso', precio: 1800, precioOriginal: 2000 }))

    expect(tarjeta).toHaveTextContent('Antes $ 2.000,00')
    expect(tarjeta).toHaveTextContent('por kg')
  })

  it('el componente con un precio de lista de cero también lo muestra: la presencia, no el valor, decide', () => {
    render(<PrecioConOferta precio={0} precioOriginal={0} unidadVenta="Unidad" />)

    expect(document.querySelector('s')).toHaveTextContent('Antes $ 0,00')
  })

  it('el texto plano (copiar y pegar, modo lectura) separa cartelito, precio de lista y precio final con espacios', () => {
    const { container } = render(<PrecioConOferta precio={80} precioOriginal={100} unidadVenta="Unidad" />)

    expect(container.textContent).toBe('Oferta Antes $ 100,00 Ahora $ 80,00')
  })

  it('con el nombre de la oferta y por peso, el texto plano sigue separado', () => {
    const { container } = render(<PrecioConOferta precio={1800} precioOriginal={2000} oferta="Semana" unidadVenta="Peso" />)

    expect(container.textContent).toBe('Oferta: Semana Antes $ 2.000,00 Ahora $ 1.800,00 por kg')
  })

  it('la etiqueta usa el acento del comercio y el texto que el tema calcula sobre él', () => {
    const hoja = Array.from(document.styleSheets).flatMap((h) => Array.from(h.cssRules))
    const regla = hoja.find((r) => r instanceof CSSStyleRule && r.selectorText === '.publico-etiqueta-oferta') as CSSStyleRule

    expect(regla.style.getPropertyValue('background')).toBe('var(--pub-acento)')
    expect(regla.style.getPropertyValue('color')).toBe('var(--pub-tinta-sobre-acento, var(--pub-tinta))')
  })
})

describe('oferta en el carrito', () => {
  it('agregar un artículo en oferta guarda el precio de lista y la oferta, y el total sale del precio final', async () => {
    const tarjeta = verTarjeta(enOferta)

    await userEvent.click(within(tarjeta).getByRole('button', { name: 'Agregar Martillo al carrito' }))

    expect(itemsGuardados(localStorage.getItem(claveDelCarrito(HOST)))).toEqual([
      expect.objectContaining({ tipo: 'articulo', precio: 80, precioOriginal: 100, oferta: 'Semana de la herramienta', cantidad: 1 }),
    ])
  })

  it('el panel muestra la línea con el precio de lista tachado y el cartelito, y el total con el precio final', () => {
    localStorage.setItem(
      claveDelCarrito(HOST),
      JSON.stringify({
        version: 2,
        items: [
          { tipo: 'articulo', idArticulo: 1, nombre: 'Martillo', precio: 80, precioOriginal: 100, oferta: 'Semana de la herramienta', unidadVenta: 'Unidad', cantidad: 3, imagenVersion: null },
          { tipo: 'articulo', idArticulo: 2, nombre: 'Clavos', precio: 50, unidadVenta: 'Unidad', cantidad: 1, imagenVersion: null },
        ],
      }),
    )
    render(
      <CarritoProvider host={HOST}>
        <PanelCarrito alCerrar={() => {}} />
      </CarritoProvider>,
    )

    const conOferta = screen.getByText('Martillo').closest('li') as HTMLElement
    const sinOferta = screen.getByText('Clavos').closest('li') as HTMLElement

    expect(conOferta.querySelector('s')).toHaveTextContent('Antes $ 100,00')
    expect(conOferta.querySelector('.publico-etiqueta-oferta')).toHaveAttribute('title', 'Semana de la herramienta')
    expect(conOferta).toHaveTextContent('$ 240,00')
    expect(sinOferta.querySelector('s')).toBeNull()
    expect(sinOferta.querySelector('.publico-etiqueta-oferta')).toBeNull()
    expect((screen.getByText('Total').parentElement as HTMLElement).textContent).toBe('Total$ 290,00')
  })
})
