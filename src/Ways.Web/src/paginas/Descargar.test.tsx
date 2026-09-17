import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { describe, expect, it } from 'vitest'
import { Descargar } from './Descargar'
import { URL_DESCARGA_POS_POR_DEFECTO } from '../config/descargaPos'

/** Se renderiza sin `AuthProvider` a propósito: prueba que la página no depende de sesión, igual
 * que `/login` (paridad con "route reachable without session"). */
function renderDescargar() {
  return render(
    <MemoryRouter initialEntries={['/descargar']}>
      <Routes>
        <Route path="/descargar" element={<Descargar />} />
        <Route path="/login" element={<div>Login</div>} />
      </Routes>
    </MemoryRouter>,
  )
}

describe('Descargar', () => {
  it('muestra el botón de descarga apuntando a la URL resuelta', () => {
    renderDescargar()

    const boton = screen.getByRole('link', { name: 'Descargar para Windows' })
    expect(boton).toHaveAttribute('href', URL_DESCARGA_POS_POR_DEFECTO)
  })

  it('incluye el paso de SmartScreen sin firma digital', () => {
    renderDescargar()

    expect(screen.getByText(/Windows protegió su PC/)).toBeInTheDocument()
    expect(screen.getByText(/Ejecutar de todas formas/)).toBeInTheDocument()
  })

  it('renderiza todos los encabezados de sección', () => {
    renderDescargar()

    const titulos = [
      'Requisitos',
      'Instalación',
      'Primera configuración del equipo',
      'Vincular el equipo',
      'Uso diario del cajero',
      'Cambiar servidor o impresora',
      'Revocar un equipo',
      'Problemas frecuentes',
    ]

    for (const titulo of titulos) {
      expect(screen.getByRole('heading', { name: titulo })).toBeInTheDocument()
    }
  })

  it('manda a revocar un equipo desde la pantalla Equipos POS, sin pasar por soporte', () => {
    renderDescargar()

    expect(screen.getByRole('link', { name: 'Equipos POS' })).toHaveAttribute('href', '/organizacion/equipos-pos')
    expect(screen.getByText(/sigue bloqueando la baja de su punto de venta, del tenant y del usuario que lo vinculó/)).toBeInTheDocument()
    expect(screen.queryByText(/soporte/)).not.toBeInTheDocument()
  })

  it('tiene un link de vuelta a /login', () => {
    renderDescargar()

    expect(screen.getByRole('link', { name: 'Volver a iniciar sesión' })).toHaveAttribute(
      'href',
      '/login',
    )
  })
})
