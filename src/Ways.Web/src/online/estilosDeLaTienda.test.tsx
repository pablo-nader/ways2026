import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'

import '../publico/publico.css'
import { BarraDelCatalogo } from './BarraDelCatalogo'
import { BotonCarrito } from './BotonCarrito'
import { claveDelCarrito } from './carrito'
import { CarritoProvider } from './CarritoProvider'
import { categoriaPublica } from './datosDePrueba'
import { PanelCarrito } from './PanelCarrito'

/**
 * Estas pruebas leen el estilo calculado que jsdom obtiene de `publico.css` (Vitest corre con
 * `css: true`). Prueban lo que el stylesheet declara para esos elementos; no miden el dibujo en un
 * navegador, así que el aro de foco recortado se prueba por el relleno que le deja lugar.
 */

const LADOS = ['Top', 'Right', 'Bottom', 'Left'] as const

function lados(elemento: Element, propiedad: 'padding' | 'margin') {
  const estilo = getComputedStyle(elemento)

  return LADOS.map((lado) => estilo.getPropertyValue(`${propiedad}-${lado.toLowerCase()}`))
}

afterEach(() => {
  cleanup()
  localStorage.clear()
})

describe('aro de foco dentro de contenedores con desplazamiento', () => {
  it('las categorías, que se desplazan en horizontal, dejan 6 px de lugar al aro (3 px de trazo + 3 px de separación) y devuelven ese espacio con margen negativo', () => {
    render(
      <BarraDelCatalogo
        categorias={[categoriaPublica(5, 'Herramientas', 1)]}
        idCategoria={null}
        alBuscar={() => {}}
        alElegirCategoria={() => {}}
      />,
    )

    const chips = screen.getByRole('group', { name: 'Categorías' })

    expect(getComputedStyle(chips).overflowX).toBe('auto')
    expect(lados(chips, 'padding')).toEqual(['6px', '6px', '6px', '6px'])
    expect(lados(chips, 'margin')).toEqual(['-6px', '-6px', '-6px', '-6px'])
  })

  it('las líneas del panel del carrito, que se desplazan en vertical, hacen lo mismo', () => {
    localStorage.setItem(
      claveDelCarrito('sur.ways.app'),
      JSON.stringify([{ idArticulo: 1, nombre: 'Martillo', precio: 10, unidadVenta: 'Unidad', cantidad: 1, imagenVersion: null }]),
    )
    render(
      <CarritoProvider host="sur.ways.app">
        <PanelCarrito alCerrar={() => {}} />
      </CarritoProvider>,
    )

    const lineas = screen.getByRole('list')

    expect(getComputedStyle(lineas).overflowY).toBe('auto')
    expect(lados(lineas, 'padding')).toEqual(['6px', '6px', '6px', '6px'])
    expect(lados(lineas, 'margin')).toEqual(['-6px', '-6px', '-6px', '-6px'])
  })
})

describe('tamaño de los controles', () => {
  it('el botón del carrito de la cabecera y "Cerrar" del panel miden al menos 44 px de alto', () => {
    render(
      <CarritoProvider host="sur.ways.app">
        <BotonCarrito abierto={false} alAlternar={() => {}} />
        <PanelCarrito alCerrar={() => {}} />
      </CarritoProvider>,
    )

    for (const boton of [screen.getByRole('button', { name: 'Carrito, vacío' }), screen.getByRole('button', { name: 'Cerrar' })]) {
      expect(parseFloat(getComputedStyle(boton).minHeight), boton.textContent ?? '').toBeGreaterThanOrEqual(44)
    }
  })
})
