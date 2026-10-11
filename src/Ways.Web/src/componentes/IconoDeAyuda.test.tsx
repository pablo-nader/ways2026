import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { IconoDeAyuda } from './IconoDeAyuda'
import { Modal } from './Modal'

afterEach(() => {
  cleanup()
  vi.restoreAllMocks()
})

const TEXTO = 'Desmarcado, cada vez que se agrega suma una línea nueva.'
const ETIQUETA = 'Ayuda sobre la acumulación'
const ALTO_DEL_TEXTO = 40

function montar() {
  return render(<IconoDeAyuda id="ayuda-1" texto={TEXTO} etiqueta={ETIQUETA} />)
}

function tooltip() {
  return screen.getByRole('tooltip', { hidden: true })
}

function rectangulo(top: number, left: number, ancho: number, alto: number): DOMRect {
  return { top, left, width: ancho, height: alto, bottom: top + alto, right: left + ancho, x: left, y: top, toJSON: () => ({}) }
}

/**
 * jsdom no calcula layout: `getBoundingClientRect` devuelve ceros. Acá se lo reemplaza por la geometría que el test
 * declara (el ícono en `boton`, el texto con `ALTO_DEL_TEXTO` de alto) y se devuelve una función para moverlo. Con
 * esto se prueba la cuenta de la posición, no que el navegador dibuje el texto donde se la calcula.
 */
function simularLayout(inicial: { top: number; left: number }) {
  const boton = { ...inicial }
  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function (this: HTMLElement) {
    return this.getAttribute('role') === 'tooltip' ? rectangulo(0, 0, 200, ALTO_DEL_TEXTO) : rectangulo(boton.top, boton.left, 16, 16)
  })
  return (nuevo: Partial<typeof boton>) => Object.assign(boton, nuevo)
}

describe('IconoDeAyuda', () => {
  it('empieza oculto pero el texto está en el DOM como tooltip', () => {
    montar()

    expect(tooltip()).toHaveTextContent(TEXTO)
    expect(tooltip()).toHaveAttribute('data-visible', 'false')
  })

  it('el botón es focusable y su descripción accesible es el texto de ayuda', () => {
    montar()

    const boton = screen.getByRole('button', { name: ETIQUETA })
    expect(boton).toHaveAccessibleDescription(TEXTO)
  })

  it('se muestra al pasar el mouse y se oculta al salir', async () => {
    montar()
    const usuario = userEvent.setup()
    const boton = screen.getByRole('button', { name: ETIQUETA })

    await usuario.hover(boton)
    expect(tooltip()).toHaveAttribute('data-visible', 'true')

    await usuario.unhover(boton)
    expect(tooltip()).toHaveAttribute('data-visible', 'false')
  })

  it('se muestra al enfocar con el teclado y se oculta al perder el foco', async () => {
    montar()
    const usuario = userEvent.setup()

    await usuario.tab()
    expect(screen.getByRole('button', { name: ETIQUETA })).toHaveFocus()
    expect(tooltip()).toHaveAttribute('data-visible', 'true')

    await usuario.tab()
    expect(tooltip()).toHaveAttribute('data-visible', 'false')
  })

  describe('Escape', () => {
    it('con el foco en el botón lo oculta sin quitar el foco', async () => {
      montar()
      const usuario = userEvent.setup()
      await usuario.tab()

      await usuario.keyboard('{Escape}')

      expect(tooltip()).toHaveAttribute('data-visible', 'false')
      expect(screen.getByRole('button', { name: ETIQUETA })).toHaveFocus()
    })

    /** Cláusula bajo prueba: el `keydown` en captura sobre `window` con `stopPropagation`. `Modal` escucha en `document`,
     * así que sin él la primera tecla cerraría el modal además de ocultar el tooltip. Con el mouse encima el foco no
     * está en el botón: la tecla llega a otro control. */
    it('con el mouse encima lo oculta sin cerrar el modal que lo contiene, y la segunda tecla sí lo cierra', async () => {
      const alCerrar = vi.fn()
      render(
        <Modal titulo="Artículo" onCerrar={alCerrar}>
          <IconoDeAyuda id="ayuda-1" texto={TEXTO} etiqueta={ETIQUETA} />
        </Modal>,
      )
      const usuario = userEvent.setup()
      await usuario.hover(screen.getByRole('button', { name: ETIQUETA }))
      expect(tooltip()).toHaveAttribute('data-visible', 'true')
      expect(screen.getByRole('button', { name: ETIQUETA })).not.toHaveFocus()

      await usuario.keyboard('{Escape}')

      expect(tooltip()).toHaveAttribute('data-visible', 'false')
      expect(alCerrar).not.toHaveBeenCalled()

      await usuario.keyboard('{Escape}')

      expect(alCerrar).toHaveBeenCalledTimes(1)
    })

    it('queda oculto mientras el mouse sigue encima y vuelve a mostrarse cuando el mouse entra de nuevo', async () => {
      montar()
      const usuario = userEvent.setup()
      const boton = screen.getByRole('button', { name: ETIQUETA })
      await usuario.hover(boton)

      await usuario.keyboard('{Escape}')
      expect(tooltip()).toHaveAttribute('data-visible', 'false')

      await usuario.unhover(boton)
      await usuario.hover(boton)
      expect(tooltip()).toHaveAttribute('data-visible', 'true')
    })

    it('queda oculto mientras el foco sigue en el botón y vuelve a mostrarse cuando el foco llega de nuevo', async () => {
      montar()
      const usuario = userEvent.setup()
      await usuario.tab()

      await usuario.keyboard('{Escape}')
      expect(tooltip()).toHaveAttribute('data-visible', 'false')

      await usuario.tab()
      await usuario.tab()
      expect(screen.getByRole('button', { name: ETIQUETA })).toHaveFocus()
      expect(tooltip()).toHaveAttribute('data-visible', 'true')
    })

    it('oculto no consume Escape: la tecla llega al resto de la pantalla', async () => {
      montar()
      const llegoAlDocumento = vi.fn()
      document.addEventListener('keydown', llegoAlDocumento)
      const usuario = userEvent.setup()

      await usuario.keyboard('{Escape}')
      document.removeEventListener('keydown', llegoAlDocumento)

      expect(llegoAlDocumento).toHaveBeenCalledTimes(1)
    })
  })

  describe('mouse y foco', () => {
    /** Cláusula bajo prueba: estados separados para el mouse y el foco. Con uno solo, soltar el mouse ocultaría un
     * tooltip que el teclado todavía tiene enfocado. */
    it('salir con el mouse no lo oculta mientras el botón sigue enfocado', async () => {
      montar()
      const usuario = userEvent.setup()
      const boton = screen.getByRole('button', { name: ETIQUETA })
      await usuario.tab()
      await usuario.hover(boton)

      await usuario.unhover(boton)

      expect(boton).toHaveFocus()
      expect(tooltip()).toHaveAttribute('data-visible', 'true')
    })

    it('perder el foco no lo oculta mientras el mouse sigue encima', async () => {
      montar()
      const usuario = userEvent.setup()
      const boton = screen.getByRole('button', { name: ETIQUETA })
      await usuario.hover(boton)
      await usuario.tab()
      expect(boton).toHaveFocus()

      await usuario.tab()

      expect(boton).not.toHaveFocus()
      expect(tooltip()).toHaveAttribute('data-visible', 'true')

      await usuario.unhover(boton)
      expect(tooltip()).toHaveAttribute('data-visible', 'false')
    })

    /** Cláusula bajo prueba: el `onMouseEnter`/`onMouseLeave` en el contenedor y no en el botón. Lo que prueba jsdom es
     * que pasar del ícono al texto no lo oculta; que el puntero pueda cruzar la separación (el puente de CSS) y que un
     * tooltip oculto no intercepte clics (`pointer-events`) no se ve acá, porque jsdom no carga la hoja de estilos. */
    it('pasar del ícono al texto lo mantiene visible, y salir del texto lo oculta', async () => {
      montar()
      const usuario = userEvent.setup()
      await usuario.hover(screen.getByRole('button', { name: ETIQUETA }))

      await usuario.hover(tooltip())

      expect(tooltip()).toHaveAttribute('data-visible', 'true')

      await usuario.unhover(tooltip())

      expect(tooltip()).toHaveAttribute('data-visible', 'false')
    })
  })

  describe('posición', () => {
    /** Cláusulas bajo prueba: el `scroll` en captura y el `resize` mientras se ve. `scroll` no burbujea, así que solo la
     * captura en `window` ve el de un contenedor interno como el cuerpo de un modal. */
    it('va arriba del ícono, centrado, y lo sigue cuando el contenedor se desplaza o la ventana cambia de tamaño', async () => {
      const mover = simularLayout({ top: 200, left: 500 })
      render(
        <div data-testid="cuerpo-del-modal">
          <IconoDeAyuda id="ayuda-1" texto={TEXTO} etiqueta={ETIQUETA} />
        </div>,
      )
      await userEvent.setup().hover(screen.getByRole('button', { name: ETIQUETA }))

      expect(tooltip()).toHaveAttribute('data-lado', 'arriba')
      expect(tooltip()).toHaveStyle({ top: '192px', left: '508px' })

      mover({ top: 150 })
      fireEvent.scroll(screen.getByTestId('cuerpo-del-modal'))
      expect(tooltip()).toHaveStyle({ top: '142px', left: '508px' })

      mover({ left: 600 })
      fireEvent(window, new Event('resize'))
      expect(tooltip()).toHaveStyle({ top: '142px', left: '608px' })
    })

    /** Cláusula bajo prueba: el cálculo de si cabe arriba. Entra arriba si queda lugar para el texto y la separación
     * (`top - 8 >= alto`); si no, va abajo del ícono, a `bottom + 8`. */
    it.each<[string, number, 'arriba' | 'abajo', number]>([
      ['justo entra arriba', 48, 'arriba', 40],
      ['un píxel menos y va abajo', 47, 'abajo', 47 + 16 + 8],
      ['pegado al borde superior va abajo', 0, 'abajo', 24],
    ])('%s', async (_nombre, topDelIcono, lado, topEsperado) => {
      simularLayout({ top: topDelIcono, left: 500 })
      montar()

      await userEvent.setup().hover(screen.getByRole('button', { name: ETIQUETA }))

      expect(tooltip()).toHaveAttribute('data-lado', lado)
      expect(tooltip()).toHaveStyle({ top: `${topEsperado}px` })
    })

    it('cerca del borde derecho se corre hacia adentro para no salirse de la ventana', async () => {
      simularLayout({ top: 200, left: window.innerWidth - 8 })
      montar()

      await userEvent.setup().hover(screen.getByRole('button', { name: ETIQUETA }))

      expect(tooltip()).toHaveStyle({ left: `${window.innerWidth - 148}px` })
    })

    it('cerca del borde izquierdo se corre hacia adentro para no salirse de la ventana', async () => {
      simularLayout({ top: 200, left: 0 })
      montar()

      await userEvent.setup().hover(screen.getByRole('button', { name: ETIQUETA }))

      expect(tooltip()).toHaveStyle({ left: '148px' })
    })

    it('en una ventana más angosta que el texto queda pegado al margen izquierdo', async () => {
      const anchoOriginal = window.innerWidth
      Object.defineProperty(window, 'innerWidth', { configurable: true, value: 200 })
      try {
        simularLayout({ top: 200, left: 190 })
        montar()

        await userEvent.setup().hover(screen.getByRole('button', { name: ETIQUETA }))

        expect(tooltip()).toHaveStyle({ left: '148px' })
      } finally {
        Object.defineProperty(window, 'innerWidth', { configurable: true, value: anchoOriginal })
      }
    })
  })

  describe('escuchas mientras se ve', () => {
    /** Cláusula bajo prueba: el `return` del efecto que quita las escuchas. Después de ocultarse o desmontarse, un
     * `scroll` o un `resize` no miden nada y una tecla Escape llega al documento. */
    it('al ocultarse y al desmontarse quita el scroll, el resize y la tecla', async () => {
      const medir = vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect')
      const llegoAlDocumento = vi.fn()
      document.addEventListener('keydown', llegoAlDocumento)
      const { unmount } = montar()
      const usuario = userEvent.setup()
      const boton = screen.getByRole('button', { name: ETIQUETA })

      await usuario.hover(boton)
      await usuario.unhover(boton)
      medir.mockClear()
      fireEvent.scroll(document.body)
      fireEvent(window, new Event('resize'))
      fireEvent.keyDown(document.body, { key: 'Escape' })
      expect(medir).not.toHaveBeenCalled()
      expect(llegoAlDocumento).toHaveBeenCalledTimes(1)

      await usuario.hover(boton)
      unmount()
      medir.mockClear()
      fireEvent.scroll(document.body)
      fireEvent(window, new Event('resize'))
      fireEvent.keyDown(document.body, { key: 'Escape' })
      document.removeEventListener('keydown', llegoAlDocumento)

      expect(medir).not.toHaveBeenCalled()
      expect(llegoAlDocumento).toHaveBeenCalledTimes(2)
    })
  })
})
