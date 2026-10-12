import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { ErrorApi } from '../api/cliente'
import type { VitrinaPublica } from '../api/tipos'
import '../publico/publico.css'
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
  document.body.style.overflow = ''
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

  it('usa el tema y el acento de la vitrina y el título de la tienda con el nombre del comercio', async () => {
    await verTienda()

    expect(document.title).toBe('Tienda online · Ferretería Sur')
    expect(document.documentElement.dataset.bsTheme).toBe('light')
    expect(document.documentElement.style.getPropertyValue('--vitrina-acento')).toBe('#2a9d8f')
  })

  it('el botón del carrito muestra la cuenta en 0 y no abre nada todavía', async () => {
    await verTienda()

    const boton = screen.getByRole('button', { name: 'Carrito, vacío' })
    expect(boton).toHaveTextContent('Carrito')
    expect(boton).toHaveTextContent('0')
    expect(boton).toHaveAttribute('aria-expanded', 'false')
    expect(boton).not.toHaveAttribute('aria-controls')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('el botón abre el panel con el carrito vacío y Cerrar lo cierra devolviendo el foco al botón', async () => {
    await verTienda()
    const boton = screen.getByRole('button', { name: 'Carrito, vacío' })

    await userEvent.click(boton)

    const panel = screen.getByRole('dialog', { name: 'Carrito' })
    expect(boton).toHaveAttribute('aria-expanded', 'true')
    expect(boton).toHaveAttribute('aria-controls', panel.id)
    expect(panel).toHaveAttribute('aria-modal', 'true')
    expect(within(panel).getByText('Tu carrito está vacío')).toBeInTheDocument()
    expect(within(panel).getByRole('button', { name: 'Cerrar' })).toHaveFocus()

    await userEvent.click(within(panel).getByRole('button', { name: 'Cerrar' }))

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(boton).toHaveAttribute('aria-expanded', 'false')
    expect(boton).toHaveFocus()
  })

  describe('panel modal del carrito', () => {
    async function abrirPanel() {
      await verTienda()
      const boton = screen.getByRole('button', { name: 'Carrito, vacío' })
      await userEvent.click(boton)

      return { boton, panel: screen.getByRole('dialog', { name: 'Carrito' }) }
    }

    it('Escape cierra el panel aunque el foco no esté dentro de él', async () => {
      const { boton } = await abrirPanel()
      ;(document.activeElement as HTMLElement).blur()
      expect(document.body).toHaveFocus()

      await userEvent.keyboard('{Escape}')

      expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
      expect(boton).toHaveFocus()
    })

    it('el clic en el velo cierra el panel y devuelve el foco al botón', async () => {
      const { boton } = await abrirPanel()

      await userEvent.click(document.querySelector('.publico-carrito-velo') as HTMLElement)

      expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
      expect(boton).toHaveFocus()
    })

    it('el foco circula dentro del panel: Tab y Shift+Tab no salen hacia la página', async () => {
      const { panel } = await abrirPanel()
      const cerrar = within(panel).getByRole('button', { name: 'Cerrar' })

      await userEvent.tab()
      expect(cerrar).toHaveFocus()
      await userEvent.tab({ shift: true })
      expect(cerrar).toHaveFocus()

      ;(document.activeElement as HTMLElement).blur()
      await userEvent.tab()
      expect(cerrar).toHaveFocus()
    })

    it('con el foco fuera del panel, Shift+Tab lo lleva al último elemento del panel', async () => {
      const { panel } = await abrirPanel()
      const ultimo = document.createElement('button')
      ultimo.textContent = 'Último'
      panel.appendChild(ultimo)
      const fuera = document.createElement('button')
      fuera.textContent = 'Fuera del panel'
      document.body.appendChild(fuera)
      ;(document.activeElement as HTMLElement).blur()
      expect(document.body).toHaveFocus()

      await userEvent.tab({ shift: true })

      expect(ultimo).toHaveFocus()
      fuera.remove()
    })

    it('con varios elementos, Tab desde el último vuelve al primero y Shift+Tab desde el primero va al último', async () => {
      const { panel } = await abrirPanel()
      const cerrar = within(panel).getByRole('button', { name: 'Cerrar' })
      const ultimo = document.createElement('button')
      ultimo.textContent = 'Último'
      panel.appendChild(ultimo)

      await userEvent.tab()
      expect(ultimo).toHaveFocus()
      await userEvent.tab()
      expect(cerrar).toHaveFocus()
      await userEvent.tab({ shift: true })
      expect(ultimo).toHaveFocus()
    })

    it('mientras está abierto el scroll de la página queda bloqueado y al cerrar se restaura lo que había', async () => {
      document.body.style.overflow = 'scroll'
      const { panel } = await abrirPanel()
      expect(document.body.style.overflow).toBe('hidden')

      await userEvent.click(within(panel).getByRole('button', { name: 'Cerrar' }))

      expect(document.body.style.overflow).toBe('scroll')
    })

    it('al desmontar con el panel abierto no deja el scroll de la página bloqueado', async () => {
      obtenerVitrinaMock.mockResolvedValue(vitrina)
      const { unmount } = render(<Tienda />)
      await userEvent.click(await screen.findByRole('button', { name: 'Carrito, vacío' }))
      expect(document.body.style.overflow).toBe('hidden')

      unmount()

      expect(document.body.style.overflow).toBe('')
    })
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
