import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { Box } from './Box'

describe('Box', () => {
  it('con título lo muestra como encabezado de nivel 5 y no es una región', () => {
    render(<Box titulo="Datos">contenido</Box>)
    expect(screen.getByRole('heading', { level: 5, name: 'Datos' })).toBeInTheDocument()
    expect(screen.queryByRole('region')).not.toBeInTheDocument()
  })

  it('con un encabezado propio lo dibuja en la cabecera, sin título, y la etiqueta da nombre a la región', () => {
    render(
      <Box etiqueta="Carrito" encabezado={<button type="button">Elegir</button>}>
        contenido
      </Box>,
    )
    const region = screen.getByRole('region', { name: 'Carrito' })
    expect(region.querySelector('header')).toContainElement(screen.getByRole('button', { name: 'Elegir' }))
    expect(screen.queryByRole('heading')).not.toBeInTheDocument()
    expect(region).toHaveTextContent('contenido')
  })

  it('el encabezado propio por sí solo alcanza para dibujar la cabecera', () => {
    render(<Box encabezado={<span>propio</span>}>contenido</Box>)
    expect(screen.getByText('propio').closest('header')).not.toBeNull()
  })
})
