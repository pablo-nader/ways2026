import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { HojaDeEtiquetas } from './HojaDeEtiquetas'
import type { FilaDeEtiqueta } from './HojaDeEtiquetas'
import { A4_3X8 } from './formatos'
// `?raw` (soportado por Vite, `vite/client`): lee el CSS como texto sin tocar `fs`/`path` de
// Node, que el `tsconfig.app.json` de la app no tipa (evita ensanchar los `types` compartidos
// solo para un test).
import cssTexto from '../estilos/etiquetas.css?raw'

function filaFixture(sobrescribir: Partial<FilaDeEtiqueta> = {}): FilaDeEtiqueta {
  return {
    idArticulo: 1,
    codigoInterno: 'ART-001',
    codigoBarra: null,
    nombre: 'Artículo de prueba',
    unidadVenta: 'un',
    precioOriginal: 100,
    precioFinal: 100,
    ofertas: [],
    ...sobrescribir,
  }
}

// mutation target 7: la regla de tachado es `ofertas.length > 0`, NUNCA
// `precioOriginal !== precioFinal` — producción jamás emite ese par, así que solo un DTO
// construido a mano prueba la diferencia entre las dos reglas.
describe('HojaDeEtiquetas — regla de tachado (mutation target 7)', () => {
  it('precios DISTINTOS + ofertas VACÍAS ⇒ sin tachado (el mutante `precioOriginal !== precioFinal` tacharía esto)', () => {
    const fila = filaFixture({ precioOriginal: 150, precioFinal: 120, ofertas: [] })
    render(<HojaDeEtiquetas descriptor={A4_3X8} celdas={[fila]} nombreDeLista="Lista general" />)

    expect(screen.queryByTestId(`precio-original-tachado-${fila.idArticulo}-0`)).not.toBeInTheDocument()
    expect(screen.getByText('$120,00')).toBeInTheDocument()
  })

  it('precios IGUALES + ofertas NO VACÍAS ⇒ tachado presente (el mutante `precioOriginal !== precioFinal` lo ocultaría)', () => {
    const fila = filaFixture({
      precioOriginal: 100,
      precioFinal: 100,
      ofertas: [{ idOferta: 9, nombre: '2x1', descuentoUnitario: 0 }],
    })
    render(<HojaDeEtiquetas descriptor={A4_3X8} celdas={[fila]} nombreDeLista="Lista general" />)

    const tachado = screen.getByTestId(`precio-original-tachado-${fila.idArticulo}-0`)
    expect(tachado).toBeInTheDocument()
    expect(tachado).toHaveStyle({ textDecoration: 'line-through' })
  })
})

// mutation target 6: el spike usa el MISMO descriptor que la hoja real — una calibración con
// números propios no prueba nada. La geometría emitida como custom properties debe ser IDÉNTICA
// en ambos modos para el mismo descriptor.
describe('HojaDeEtiquetas — geometría compartida entre modos (mutation target 6)', () => {
  it('modo="calibracion" emite exactamente los mismos custom properties que modo="normal" para el mismo descriptor', () => {
    const { container: contenedorNormal } = render(
      <HojaDeEtiquetas descriptor={A4_3X8} celdas={[filaFixture()]} nombreDeLista="Lista general" modo="normal" />,
    )
    const hojaNormal = contenedorNormal.querySelector('.hoja-de-etiquetas')
    const estiloNormal = hojaNormal?.getAttribute('style')

    const { container: contenedorCalibracion } = render(
      <HojaDeEtiquetas descriptor={A4_3X8} celdas={[]} nombreDeLista="Lista general" modo="calibracion" />,
    )
    const hojaCalibracion = contenedorCalibracion.querySelector('.hoja-de-etiquetas')
    const estiloCalibracion = hojaCalibracion?.getAttribute('style')

    expect(estiloNormal).toBeTruthy()
    expect(estiloCalibracion).toBe(estiloNormal)
  })

  it('la grilla de calibración dibuja columnas×filas celdas con etiqueta f{row}c{col} y cruz de registro', () => {
    render(<HojaDeEtiquetas descriptor={A4_3X8} celdas={[]} nombreDeLista="Lista general" modo="calibracion" />)

    const grilla = screen.getByTestId('grilla-de-calibracion')
    expect(grilla.children).toHaveLength(A4_3X8.columnas * A4_3X8.filas)

    expect(screen.getByTestId('celda-calibracion-f0c0')).toHaveTextContent('f0c0')
    expect(screen.getByTestId('celda-calibracion-f7c2')).toHaveTextContent('f7c2')
    expect(screen.getByTestId('cuadrado-de-escala')).toBeInTheDocument()
    expect(screen.getByTestId('etiqueta-cuadrado-de-escala')).toHaveTextContent('100.0 × 100.0 mm')
    expect(screen.getByTestId('regla-horizontal')).toBeInTheDocument()
    expect(screen.getByTestId('regla-vertical')).toBeInTheDocument()
  })
})

// mutation target 8: el bloque `d-print-none` de instrucciones de impresión.
describe('HojaDeEtiquetas — bloque de instrucciones de impresión (mutation target 8)', () => {
  it('modo="calibracion" muestra el bloque d-print-none con A4/100%/sin márgenes/gráficos de fondo', () => {
    render(<HojaDeEtiquetas descriptor={A4_3X8} celdas={[]} nombreDeLista="Lista general" modo="calibracion" />)

    const bloque = screen.getByTestId('instrucciones-de-impresion')
    expect(bloque).toHaveClass('d-print-none')
    expect(bloque).toHaveTextContent(/A4/)
    expect(bloque).toHaveTextContent(/100%/)
    expect(bloque).toHaveTextContent(/ajustar a la página/)
    expect(bloque).toHaveTextContent(/ninguno/)
    expect(bloque).toHaveTextContent(/gráficos de fondo: activados/i)
  })
})

// mutation target 1 (S — estructural): jsdom no implementa `@page`, así que la prueba es sobre el
// archivo/estado, no sobre un comportamiento en tiempo de ejecución (honest-limits, design.md:327).
// Combina el componente (lleva la clase que la regla de CSS usa como selector) con el contrato del
// propio stylesheet (declara `page: etiquetas` bajo esa clase, dentro de la named page).
describe('HojaDeEtiquetas — named page (mutation target 1, estructural)', () => {
  it('el contenedor lleva la clase `hoja-de-etiquetas` que etiquetas.css usa para declarar `page: etiquetas`', () => {
    const { container } = render(<HojaDeEtiquetas descriptor={A4_3X8} celdas={[]} nombreDeLista="Lista general" modo="calibracion" />)
    expect(container.querySelector('.hoja-de-etiquetas')).toBeInTheDocument()

    expect(cssTexto).toMatch(/@page\s+etiquetas\s*\{[^}]*margin:\s*0/)

    // El bloque `.hoja-de-etiquetas { ... }` declara `page: etiquetas` — buscado por posición en
    // vez de un `[^}]*` acotado por la próxima `}`, porque el comentario que documenta la regla
    // contiene un ejemplo con llaves (`@page { margin: 12mm }`) que rompería esa clase de regex.
    const inicioDelBloque = cssTexto.indexOf('.hoja-de-etiquetas {')
    expect(inicioDelBloque).toBeGreaterThanOrEqual(0)
    const finDelBloque = cssTexto.indexOf('\n}', inicioDelBloque)
    const bloque = cssTexto.slice(inicioDelBloque, finDelBloque)
    expect(bloque).toMatch(/page:\s*etiquetas/)
  })
})
