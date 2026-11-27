import { render, screen, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import type { FilaDeEtiqueta } from '../api/tipos'
import { HojasDeGondola } from './HojasDeGondola'
import { PLANTILLAS_DE_GONDOLA, plantillaDeGondolaPorId } from './plantillasDeGondola'

function filas(cantidad: number, sobrescribir: Partial<FilaDeEtiqueta> = {}): FilaDeEtiqueta[] {
  return Array.from({ length: cantidad }, (_, i) => ({
    idArticulo: i + 1,
    codigoInterno: `A${i + 1}`,
    codigoBarra: null,
    nombre: `Artículo ${i + 1}`,
    unidadVenta: 'Unidad',
    precioOriginal: 1000 + i,
    precioFinal: 1000 + i,
    ofertas: [],
    promociones: [],
    ...sobrescribir,
  }))
}

describe('HojasDeGondola', () => {
  it('N artículos dan ceil(N / porHoja) hojas, cada artículo una vez', () => {
    render(<HojasDeGondola filas={filas(9)} porHoja={4} plantilla={plantillaDeGondolaPorId('pizarra')} conOfertas={false} />)
    const hojas = screen.getAllByTestId('gondola-hoja')
    expect(hojas).toHaveLength(3)
    expect(within(hojas[0]).getAllByTestId('gondola-etiqueta')).toHaveLength(4)
    expect(within(hojas[2]).getAllByTestId('gondola-etiqueta')).toHaveLength(1)
    expect(screen.getAllByTestId('gondola-etiqueta')).toHaveLength(9)
  })

  it('emite la grilla de la disposición como variables CSS', () => {
    const { container } = render(<HojasDeGondola filas={filas(1)} porHoja={16} plantilla={plantillaDeGondolaPorId('pizarra')} conOfertas={false} />)
    const raiz = container.querySelector<HTMLElement>('.gondola-hojas')!
    expect(raiz.style.getPropertyValue('--et-columnas')).toBe('4')
    expect(raiz.style.getPropertyValue('--et-filas')).toBe('4')
  })

  it('todas las plantillas usan el logo de trazo oscuro, para papel blanco', () => {
    const { rerender } = render(<HojasDeGondola filas={filas(1)} porHoja={2} plantilla={plantillaDeGondolaPorId('pizarra')} conOfertas={false} />)
    expect(screen.getByAltText('Ways')).toHaveAttribute('src', '/marca/ways-logo-claro.png')
    rerender(<HojasDeGondola filas={filas(1)} porHoja={2} plantilla={plantillaDeGondolaPorId('doble')} conOfertas={false} />)
    expect(screen.getByAltText('Ways')).toHaveAttribute('src', '/marca/ways-logo-claro.png')
  })

  it('sin Ofertas imprime el precio de lista y nada de oferta', () => {
    render(
      <HojasDeGondola
        filas={filas(1, { precioFinal: 850, ofertas: [{ idOferta: 1, nombre: '15%', descuentoUnitario: 150 }] })}
        porHoja={2}
        plantilla={plantillaDeGondolaPorId('pizarra')}
        conOfertas={false}
      />,
    )
    expect(screen.getByTestId('gondola-precio')).toHaveTextContent('$ 1.000')
    expect(screen.queryByTestId('gondola-precio-tachado')).not.toBeInTheDocument()
    expect(screen.queryByTestId('gondola-descuento')).not.toBeInTheDocument()
  })

  it('con Ofertas muestra el precio final, el original tachado y el porcentaje', () => {
    render(
      <HojasDeGondola
        filas={filas(1, { precioFinal: 850, ofertas: [{ idOferta: 1, nombre: '15%', descuentoUnitario: 150 }] })}
        porHoja={2}
        plantilla={plantillaDeGondolaPorId('pizarra')}
        conOfertas
      />,
    )
    expect(screen.getByTestId('gondola-precio')).toHaveTextContent('$ 850')
    expect(screen.getByTestId('gondola-precio-tachado')).toHaveTextContent('$ 1.000')
    expect(screen.getByTestId('gondola-descuento')).toHaveTextContent('-15%')
  })

  it('con promoción por cantidad la oferta va en grande y el precio normal en chico', () => {
    render(
      <HojasDeGondola
        filas={filas(1, { promociones: [{ idOferta: 2, nombre: '3x', cantidad: 3, soloMultiplos: false, precioUnitarioFinal: 900 }] })}
        porHoja={2}
        plantilla={plantillaDeGondolaPorId('pizarra')}
        conOfertas
      />,
    )
    const oferta = screen.getByTestId('gondola-promocion')
    const normal = screen.getByTestId('gondola-precio-normal')
    expect(oferta).toHaveClass('gondola-precio')
    expect(normal).toHaveTextContent('$ 1.000')
    expect(parseFloat(oferta.style.fontSize)).toBeGreaterThan(parseFloat(normal.style.fontSize))
  })

  it('con Ofertas y promoción por cantidad muestra 3x total', () => {
    render(
      <HojasDeGondola
        filas={filas(1, { promociones: [{ idOferta: 2, nombre: '3x', cantidad: 3, soloMultiplos: false, precioUnitarioFinal: 900 }] })}
        porHoja={2}
        plantilla={plantillaDeGondolaPorId('pizarra')}
        conOfertas
      />,
    )
    expect(screen.getByTestId('gondola-promocion')).toHaveTextContent('3x $ 2.700')
  })

  it('el nombre largo baja la fuente respecto del corto', () => {
    const { rerender } = render(<HojasDeGondola filas={filas(1, { nombre: 'Sal' })} porHoja={8} plantilla={plantillaDeGondolaPorId('pizarra')} conOfertas={false} />)
    const corto = parseFloat(screen.getByRole('heading', { level: 3 }).style.fontSize)
    rerender(
      <HojasDeGondola
        filas={filas(1, { nombre: 'Aceite de girasol alto oleico Cocinero botella plástica 900 ml' })}
        porHoja={8}
        plantilla={plantillaDeGondolaPorId('pizarra')}
        conOfertas={false}
      />,
    )
    expect(parseFloat(screen.getByRole('heading', { level: 3 }).style.fontSize)).toBeLessThan(corto)
  })

  it('cada plantilla dibuja su propio modelo, solo con trazos sin relleno ni fondos', () => {
    const fila = filas(1, { precioFinal: 850, ofertas: [{ idOferta: 1, nombre: '15%', descuentoUnitario: 150 }] })
    for (const plantilla of PLANTILLAS_DE_GONDOLA) {
      const { container, unmount } = render(<HojasDeGondola filas={fila} porHoja={8} plantilla={plantilla} conOfertas />)
      const etiqueta = screen.getByTestId('gondola-etiqueta')
      expect(etiqueta).toHaveClass(`gondola-modelo-${plantilla.modelo}`)
      for (const path of container.querySelectorAll('path')) expect(path).toHaveAttribute('fill', 'none')
      for (const elemento of container.querySelectorAll<HTMLElement>('[style]')) expect(elemento.style.background + elemento.style.backgroundColor).toBe('')
      unmount()
    }
  })

  it('centavos arriba parte el precio en signo, entero y centavos', () => {
    const { container } = render(<HojasDeGondola filas={filas(1, { precioOriginal: 1234.5 })} porHoja={4} plantilla={plantillaDeGondolaPorId('centavos')} conOfertas={false} />)
    expect(container.querySelector('.gondola-precio-prefijo')).toHaveTextContent('$')
    expect(container.querySelector('.gondola-precio-entero')).toHaveTextContent('1.234')
    expect(container.querySelector('.gondola-precio-centavos')).toHaveTextContent('50')
  })

  it('centavos arriba sin decimales no muestra centavos', () => {
    const { container } = render(<HojasDeGondola filas={filas(1)} porHoja={4} plantilla={plantillaDeGondolaPorId('centavos')} conOfertas={false} />)
    expect(container.querySelector('.gondola-precio-entero')).toHaveTextContent('1.000')
    expect(container.querySelector('.gondola-precio-centavos')).toBeNull()
  })

  it('las cajas se posicionan en milímetros con la geometría del modelo', () => {
    render(<HojasDeGondola filas={filas(1)} porHoja={16} plantilla={plantillaDeGondolaPorId('sello')} conOfertas={false} />)
    const precio = screen.getByTestId('gondola-precio').parentElement!
    expect(precio.style.left).toMatch(/mm$/)
    expect(precio.style.width).toMatch(/mm$/)
  })
})
