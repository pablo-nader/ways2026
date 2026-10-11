import { useState, type CSSProperties } from 'react'

const ANCHO_MAXIMO = 280

/**
 * Ícono "i" que muestra un texto de ayuda al pasar el mouse o al enfocarlo con el teclado. El texto vive
 * siempre en el DOM (`role="tooltip"` con `id`), de modo que el control que ayuda puede apuntarlo con
 * `aria-describedby`; solo se oculta visualmente. Se posiciona con `fixed` para no quedar recortado por
 * el scroll de un modal.
 */
export function IconoDeAyuda({ id, texto, etiqueta = 'Ayuda' }: { id: string; texto: string; etiqueta?: string }) {
  const [posicion, setPosicion] = useState<CSSProperties | null>(null)

  function mostrar(elemento: HTMLElement) {
    const caja = elemento.getBoundingClientRect()
    const mitad = ANCHO_MAXIMO / 2 + 8
    const centro = Math.min(Math.max(caja.left + caja.width / 2, mitad), Math.max(window.innerWidth - mitad, mitad))
    setPosicion({ top: caja.top - 8, left: centro })
  }

  return (
    <span className="icono-de-ayuda">
      <button
        type="button"
        className="icono-de-ayuda__boton"
        aria-label={etiqueta}
        aria-describedby={id}
        onMouseEnter={(e) => mostrar(e.currentTarget)}
        onMouseLeave={() => setPosicion(null)}
        onFocus={(e) => mostrar(e.currentTarget)}
        onBlur={() => setPosicion(null)}
        onKeyDown={(e) => {
          if (e.key === 'Escape' && posicion !== null) {
            e.stopPropagation()
            setPosicion(null)
          }
        }}
      >
        <svg
          width="16"
          height="16"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          strokeWidth="2"
          strokeLinecap="round"
          strokeLinejoin="round"
          aria-hidden="true"
          focusable="false"
        >
          <circle cx="12" cy="12" r="9" />
          <path d="M12 11v5" />
          <path d="M12 8h.01" />
        </svg>
      </button>
      <span
        id={id}
        role="tooltip"
        className="icono-de-ayuda__texto"
        data-visible={posicion !== null}
        style={{ ...posicion, maxWidth: ANCHO_MAXIMO }}
      >
        {texto}
      </span>
    </span>
  )
}
