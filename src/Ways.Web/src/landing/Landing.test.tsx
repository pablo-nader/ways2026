import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { ErrorApi } from '../api/cliente'
import type { SucursalPublica, VitrinaPublica } from '../api/tipos'
import '../publico/publico.css'
import { Landing } from './Landing'

const obtenerVitrinaMock = vi.fn()

vi.mock('../api/publico', async (importarOriginal) => ({
  ...(await importarOriginal<typeof import('../api/publico')>()),
  clienteDePublico: { obtenerVitrina: () => obtenerVitrinaMock() },
}))

const sucursalCompleta: SucursalPublica = {
  nombre: 'Casa Central',
  domicilio: 'Av. Siempreviva 742',
  horario: 'Lun a Vie 9 a 18',
  whatsapp: '+54 9 11 5555-1234',
  instagram: '@ferreteriasur',
  facebook: 'https://www.facebook.com/FerreteriaSur',
  web: 'www.ferreteriasur.com.ar',
}

const sucursalSinContacto: SucursalPublica = {
  nombre: 'Depósito',
  domicilio: null,
  horario: null,
  whatsapp: null,
  instagram: null,
  facebook: null,
  web: null,
}

function vitrina(cambios: Partial<VitrinaPublica> = {}): VitrinaPublica {
  return {
    nombre: 'Ferretería Sur',
    tagline: 'Todo para tu obra',
    descripcion: 'Atendemos desde 1980.\nEnvíos en el día.',
    colorAcento: '#2a9d8f',
    tema: 'oscuro',
    tieneLogo: false,
    logoVersion: null,
    sucursales: [sucursalCompleta, sucursalSinContacto],
    ...cambios,
  }
}

async function verPublicada(cambios: Partial<VitrinaPublica> = {}) {
  obtenerVitrinaMock.mockResolvedValue(vitrina(cambios))
  render(<Landing />)
  await screen.findByRole('heading', { level: 1 })
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

describe('Landing', () => {
  describe('cargando', () => {
    it('muestra bloques de carga hasta que llega la vitrina', async () => {
      let resolver: (valor: VitrinaPublica) => void = () => {}
      obtenerVitrinaMock.mockReturnValue(new Promise<VitrinaPublica>((r) => (resolver = r)))

      render(<Landing />)

      expect(screen.getByRole('status', { name: 'Cargando' })).toBeInTheDocument()
      expect(screen.queryByRole('heading', { level: 1 })).not.toBeInTheDocument()

      resolver(vitrina())

      expect(await screen.findByRole('heading', { level: 1, name: 'Todo para tu obra' })).toBeInTheDocument()
      expect(screen.queryByRole('status', { name: 'Cargando' })).not.toBeInTheDocument()
    })
  })

  describe('publicada', () => {
    it('el titular es la frase del comercio y el título y la descripción del documento salen de él', async () => {
      await verPublicada()

      expect(screen.getByRole('heading', { level: 1, name: 'Todo para tu obra' })).toBeInTheDocument()
      expect(document.title).toBe('Ferretería Sur')
      expect(document.head.querySelector('meta[name="description"]')).toHaveAttribute('content', 'Todo para tu obra')
    })

    it('sin frase el titular es el nombre del comercio', async () => {
      await verPublicada({ tagline: null })

      expect(screen.getByRole('heading', { level: 1, name: 'Ferretería Sur' })).toBeInTheDocument()
    })

    it('con logo la cabecera lo pide a la API con su versión; sin logo escribe el nombre', async () => {
      await verPublicada({ tieneLogo: true, logoVersion: 1700000000 })

      expect(screen.getByRole('img', { name: 'Ferretería Sur' })).toHaveAttribute(
        'src',
        '/api/publico/vitrina/logo?v=1700000000',
      )
      expect(screen.getByRole('img', { name: 'Ferretería Sur' })).toHaveAttribute('height', '44')
      expect(getComputedStyle(screen.getByRole('img', { name: 'Ferretería Sur' })).maxWidth).toBe('100%')
    })

    it('el wordmark de Ways reserva su tamaño para que el pie no se corra al cargar', async () => {
      await verPublicada()

      const wordmark = within(screen.getByRole('contentinfo')).getByRole('img', { name: 'Ways' })
      expect(wordmark).toHaveAttribute('height', '28')
      expect(wordmark).toHaveAttribute('width', '51')
    })

    it('sin logo la cabecera escribe el nombre y no hay imagen del comercio', async () => {
      await verPublicada({ tieneLogo: false })

      const cabecera = screen.getByRole('banner')
      expect(within(cabecera).getByText('Ferretería Sur')).toBeInTheDocument()
      expect(within(cabecera).queryByRole('img')).not.toBeInTheDocument()
    })

    it('"Comprar online" lleva a /online desde la cabecera y desde la portada', async () => {
      await verPublicada()

      const enlaces = screen.getAllByRole('link', { name: 'Comprar online' })
      expect(enlaces).toHaveLength(2)
      enlaces.forEach((enlace) => expect(enlace).toHaveAttribute('href', '/online'))
    })

    it('el CTA de WhatsApp usa el primer número usable de las sucursales', async () => {
      await verPublicada({ sucursales: [sucursalSinContacto, sucursalCompleta] })

      const cta = screen.getByRole('link', { name: 'Escribinos por WhatsApp' })
      expect(cta).toHaveAttribute('href', 'https://wa.me/5491155551234')
      expect(cta).toHaveAttribute('rel', 'noopener')
    })

    it('sin ningún número de WhatsApp no hay CTA de WhatsApp', async () => {
      await verPublicada({ sucursales: [sucursalSinContacto, { ...sucursalSinContacto, whatsapp: 'sin número' }] })

      expect(screen.queryByText('Escribinos por WhatsApp')).not.toBeInTheDocument()
      expect(screen.queryByRole('link', { name: /whatsapp/i })).not.toBeInTheDocument()
      expect(screen.getAllByRole('link', { name: 'Comprar online' })).toHaveLength(2)
    })

    it('la descripción conserva los saltos de línea y no interpreta HTML', async () => {
      await verPublicada({ descripcion: 'Primera línea\nSegunda <b>línea</b>' })

      const descripcion = screen.getByText(/Primera línea/)
      expect(descripcion.textContent).toBe('Primera línea\nSegunda <b>línea</b>')
      expect(descripcion.querySelector('b')).toBeNull()
    })

    it('un texto largo sin espacios se parte en vez de desbordar la página', async () => {
      const largo = (marca: string) => marca + 'x'.repeat(120)
      await verPublicada({
        descripcion: largo('d'),
        sucursales: [{ ...sucursalSinContacto, nombre: largo('n'), domicilio: largo('a'), horario: largo('h') }],
      })

      for (const marca of ['d', 'n', 'a', 'h']) {
        const elemento = screen.getByText(largo(marca))
        expect(getComputedStyle(elemento).overflowWrap, `texto ${marca}`).toBe('anywhere')
      }
      expect(getComputedStyle(screen.getByRole('listitem')).minWidth).toBe('0px')
    })

    it('el nombre de la cabecera se parte si es largo y la marca no recorta con overflow oculto', async () => {
      await verPublicada({ tieneLogo: false, nombre: 'N'.repeat(80) })

      const nombre = within(screen.getByRole('banner')).getByText('N'.repeat(80))
      expect(getComputedStyle(nombre).overflowWrap).toBe('anywhere')
      const marca = nombre.closest('a') as HTMLElement
      expect(getComputedStyle(marca).overflow).not.toBe('hidden')
      expect(getComputedStyle(marca).minWidth).toBe('0px')
    })

    it('sin descripción no hay sección de descripción', async () => {
      await verPublicada({ descripcion: '  ' })

      expect(screen.queryByRole('region', { name: 'Sobre el comercio' })).not.toBeInTheDocument()
    })

    it('cada sucursal es una tarjeta con sus datos y sus enlaces reales', async () => {
      await verPublicada()

      const tarjetas = within(screen.getByRole('list')).getAllByRole('listitem')
      expect(tarjetas).toHaveLength(2)

      const central = within(tarjetas[0])
      expect(central.getByRole('heading', { name: 'Casa Central' })).toBeInTheDocument()
      expect(central.getByText('Av. Siempreviva 742')).toBeInTheDocument()
      expect(central.getByText('Lun a Vie 9 a 18')).toBeInTheDocument()
      expect(central.getByRole('link', { name: 'WhatsApp de Casa Central' })).toHaveAttribute('href', 'https://wa.me/5491155551234')
      expect(central.getByRole('link', { name: 'Instagram de Casa Central' })).toHaveAttribute('href', 'https://www.instagram.com/ferreteriasur')
      expect(central.getByRole('link', { name: 'Facebook de Casa Central' })).toHaveAttribute('href', 'https://www.facebook.com/FerreteriaSur')
      expect(central.getByRole('link', { name: 'Web de Casa Central' })).toHaveAttribute('href', 'https://www.ferreteriasur.com.ar/')
      central.getAllByRole('link').forEach((enlace) => {
        expect(enlace).toHaveAttribute('target', '_blank')
        expect(enlace).toHaveAttribute('rel', 'noopener')
      })

      const deposito = within(tarjetas[1])
      expect(deposito.getByRole('heading', { name: 'Depósito' })).toBeInTheDocument()
      expect(deposito.queryByRole('link')).not.toBeInTheDocument()
    })

    it('un dato de contacto que no es un enlace válido no genera botón', async () => {
      await verPublicada({
        sucursales: [{ ...sucursalCompleta, instagram: 'dos palabras', facebook: 'javascript:alert(1)', web: 'sin punto' }],
      })

      expect(screen.queryByRole('link', { name: /instagram|facebook|web de/i })).not.toBeInTheDocument()
      expect(screen.getByRole('link', { name: 'WhatsApp de Casa Central' })).toBeInTheDocument()
    })

    it('sin sucursales no hay sección de sucursales', async () => {
      await verPublicada({ sucursales: [] })

      expect(screen.queryByRole('heading', { name: 'Sucursales' })).not.toBeInTheDocument()
    })

    it('el tema oscuro y el acento del comercio quedan en <html>', async () => {
      await verPublicada({ tema: 'oscuro', colorAcento: '#2a9d8f' })

      expect(document.documentElement.dataset.bsTheme).toBe('dark')
      expect(document.documentElement.style.getPropertyValue('--vitrina-acento')).toBe('#2a9d8f')
      expect(within(screen.getByRole('contentinfo')).getByRole('img', { name: 'Ways' })).toHaveAttribute(
        'src',
        '/marca/ways-logo-oscuro.png',
      )
    })

    it('el tema claro usa data-bs-theme light y el wordmark para fondos claros', async () => {
      await verPublicada({ tema: 'claro' })

      expect(document.documentElement.dataset.bsTheme).toBe('light')
      expect(within(screen.getByRole('contentinfo')).getByRole('img', { name: 'Ways' })).toHaveAttribute(
        'src',
        '/marca/ways-logo-claro.png',
      )
    })

    it('sin color de acento rige el naranja de Ways', async () => {
      await verPublicada({ colorAcento: null })

      expect(document.documentElement.style.getPropertyValue('--vitrina-acento')).toBe('#F7991C')
    })

    it('el pie dice quién lo hizo y deja el acceso del comercio al login del back office', async () => {
      await verPublicada()

      const pie = within(screen.getByRole('contentinfo'))
      expect(pie.getByText('Hecho con')).toBeInTheDocument()
      expect(pie.getByRole('link', { name: 'Acceso para el comercio' })).toHaveAttribute('href', '/admin/login')
    })
  })

  describe('sin publicar', () => {
    it('un 404 muestra la pantalla tranquila, con el wordmark de Ways y sin tocar el título del HTML', async () => {
      document.title = 'Ways'
      obtenerVitrinaMock.mockRejectedValue(new ErrorApi(404, 'vitrina_no_publicada', 'No publicada'))

      render(<Landing />)

      expect(await screen.findByText('Este comercio todavía no publicó su página.')).toBeInTheDocument()
      expect(screen.getByRole('img', { name: 'Ways' })).toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Reintentar' })).not.toBeInTheDocument()
      expect(getComputedStyle(screen.getByText('Este comercio todavía no publicó su página.')).overflowWrap).toBe('anywhere')
      expect(document.title).toBe('Ways')
      expect(document.head.querySelector('meta[name="description"]')).toBeNull()
    })

    it('un 404 con otro código no es "sin publicar": ofrece reintentar', async () => {
      obtenerVitrinaMock.mockRejectedValue(new ErrorApi(404, 'ruta_no_encontrada', 'No existe'))

      render(<Landing />)

      expect(await screen.findByRole('button', { name: 'Reintentar' })).toBeInTheDocument()
      expect(screen.queryByText('Este comercio todavía no publicó su página.')).not.toBeInTheDocument()
    })
  })

  describe('error', () => {
    it('un fallo del servidor o de red muestra el aviso y deja reintentar', async () => {
      obtenerVitrinaMock.mockRejectedValueOnce(new ErrorApi(500, 'error_interno', 'Falla'))

      render(<Landing />)

      expect(await screen.findByText('No pudimos cargar la página. Probá de nuevo en un momento.')).toBeInTheDocument()
      expect(screen.queryByText('Este comercio todavía no publicó su página.')).not.toBeInTheDocument()

      obtenerVitrinaMock.mockResolvedValueOnce(vitrina())
      await userEvent.click(screen.getByRole('button', { name: 'Reintentar' }))

      expect(await screen.findByRole('heading', { level: 1, name: 'Todo para tu obra' })).toBeInTheDocument()
      await waitFor(() => expect(obtenerVitrinaMock).toHaveBeenCalledTimes(2))
    })

    it('al reintentar el aviso desaparece y se ve el esqueleto mientras la lectura sigue en vuelo', async () => {
      obtenerVitrinaMock.mockRejectedValueOnce(new ErrorApi(500, 'error_interno', 'Falla'))
      render(<Landing />)
      const reintentar = await screen.findByRole('button', { name: 'Reintentar' })

      let resolver: (valor: VitrinaPublica) => void = () => {}
      obtenerVitrinaMock.mockReturnValueOnce(new Promise<VitrinaPublica>((r) => (resolver = r)))
      await userEvent.click(reintentar)

      expect(screen.getByRole('status', { name: 'Cargando' })).toBeInTheDocument()
      expect(screen.queryByText('No pudimos cargar la página. Probá de nuevo en un momento.')).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Reintentar' })).not.toBeInTheDocument()

      resolver(vitrina())
      expect(await screen.findByRole('heading', { level: 1 })).toBeInTheDocument()
    })

    it('un error que no es de la API (sin red) también es un error, no una vitrina sin publicar', async () => {
      obtenerVitrinaMock.mockRejectedValue(new TypeError('Failed to fetch'))

      render(<Landing />)

      expect(await screen.findByText('No pudimos cargar la página. Probá de nuevo en un momento.')).toBeInTheDocument()
    })
  })
})
