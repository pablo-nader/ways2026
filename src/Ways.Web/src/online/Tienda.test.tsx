import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { ErrorApi } from '../api/cliente'
import type { VitrinaPublica } from '../api/tipos'
import { useCarrito } from './CarritoContext'
import { Tienda } from './Tienda'

const obtenerVitrinaMock = vi.fn()

vi.mock('../api/publico', async (importarOriginal) => ({
  ...(await importarOriginal<typeof import('../api/publico')>()),
  clienteDePublico: { obtenerVitrina: () => obtenerVitrinaMock() },
}))

const vitrina: VitrinaPublica = {
  nombre: 'Ferretería Sur',
  tagline: 'Todo para tu obra',
  descripcion: null,
  colorAcento: '#2a9d8f',
  tema: 'claro',
  tieneLogo: true,
  logoVersion: 7,
  sucursales: [],
}

async function verTienda() {
  obtenerVitrinaMock.mockResolvedValue(vitrina)
  render(<Tienda />)
  await screen.findByRole('heading', { level: 1, name: 'El catálogo se está preparando.' })
}

beforeEach(() => {
  obtenerVitrinaMock.mockReset()
})

afterEach(() => {
  document.documentElement.removeAttribute('data-bs-theme')
  document.documentElement.style.removeProperty('--vitrina-acento')
  document.title = ''
  document.head.querySelector('meta[name="description"]')?.remove()
})

describe('Tienda', () => {
  it('muestra la cabecera del comercio, el estado vacío y el camino de vuelta a la landing', async () => {
    await verTienda()

    expect(within(screen.getByRole('banner')).getByRole('img', { name: 'Ferretería Sur' })).toHaveAttribute(
      'src',
      '/api/publico/vitrina/logo?v=7',
    )
    expect(screen.getByRole('link', { name: 'Volver al inicio' })).toHaveAttribute('href', '/')
    expect(within(screen.getByRole('contentinfo')).getByRole('link', { name: 'Acceso para el comercio' })).toHaveAttribute(
      'href',
      '/admin/login',
    )
  })

  it('usa el tema y el acento de la vitrina y el nombre del comercio como título', async () => {
    await verTienda()

    expect(document.title).toBe('Ferretería Sur')
    expect(document.documentElement.dataset.bsTheme).toBe('light')
    expect(document.documentElement.style.getPropertyValue('--vitrina-acento')).toBe('#2a9d8f')
  })

  it('el botón del carrito muestra la cuenta en 0 y no abre nada todavía', async () => {
    await verTienda()

    const boton = screen.getByRole('button', { name: 'Carrito, vacío' })
    expect(boton).toHaveTextContent('Carrito')
    expect(boton).toHaveTextContent('0')
    expect(boton).toHaveAttribute('aria-expanded', 'false')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('el botón abre el panel con el carrito vacío y Cerrar lo cierra devolviendo el foco al botón', async () => {
    await verTienda()
    const boton = screen.getByRole('button', { name: 'Carrito, vacío' })

    await userEvent.click(boton)

    const panel = screen.getByRole('dialog', { name: 'Carrito' })
    expect(boton).toHaveAttribute('aria-expanded', 'true')
    expect(within(panel).getByText('Tu carrito está vacío')).toBeInTheDocument()
    expect(within(panel).getByRole('button', { name: 'Cerrar' })).toHaveFocus()

    await userEvent.click(within(panel).getByRole('button', { name: 'Cerrar' }))

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(boton).toHaveAttribute('aria-expanded', 'false')
    expect(boton).toHaveFocus()
  })

  it('Escape cierra el panel', async () => {
    await verTienda()
    await userEvent.click(screen.getByRole('button', { name: 'Carrito, vacío' }))
    expect(screen.getByRole('dialog')).toBeInTheDocument()

    await userEvent.keyboard('{Escape}')

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('un comercio sin publicar no tiene tienda: muestra la pantalla tranquila', async () => {
    obtenerVitrinaMock.mockRejectedValue(new ErrorApi(404, 'vitrina_no_publicada', 'No publicada'))

    render(<Tienda />)

    expect(await screen.findByText('Este comercio todavía no publicó su página.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /carrito/i })).not.toBeInTheDocument()
  })

  it('un fallo de carga muestra el aviso de error', async () => {
    obtenerVitrinaMock.mockRejectedValue(new ErrorApi(500, 'error_interno', 'Falla'))

    render(<Tienda />)

    expect(await screen.findByText('No pudimos cargar la página. Probá de nuevo en un momento.')).toBeInTheDocument()
  })
})

describe('useCarrito', () => {
  function Sonda() {
    useCarrito()
    return null
  }

  it('fuera de un CarritoProvider avisa con un error claro', () => {
    const consola = vi.spyOn(console, 'error').mockImplementation(() => {})

    expect(() => render(<Sonda />)).toThrow('useCarrito debe usarse dentro de un CarritoProvider')

    consola.mockRestore()
  })
})
