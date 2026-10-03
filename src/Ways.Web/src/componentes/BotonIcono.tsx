import type { ComponentProps, ReactNode } from 'react'
import { Link, type LinkProps } from 'react-router'

export type IconoDeAccion = 'eliminar' | 'editar' | 'agregar'

const ETIQUETA_POR_DEFECTO: Record<IconoDeAccion, string> = {
  eliminar: 'Eliminar',
  editar: 'Editar',
  agregar: 'Agregar',
}

const VARIANTE: Record<IconoDeAccion, string> = {
  eliminar: 'btn-outline-danger',
  editar: 'btn-outline-primary',
  agregar: 'btn-primary',
}

const TRAZADOS: Record<IconoDeAccion, ReactNode> = {
  eliminar: (
    <>
      <path d="M4 7h16" />
      <path d="M10 11v6" />
      <path d="M14 11v6" />
      <path d="M5 7l1 12a2 2 0 0 0 2 2h8a2 2 0 0 0 2-2l1-12" />
      <path d="M9 7V4a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v3" />
    </>
  ),
  editar: (
    <>
      <path d="M4 20h4L18.5 9.5a2.828 2.828 0 1 0-4-4L4 16v4" />
      <path d="M13.5 6.5l4 4" />
    </>
  ),
  agregar: (
    <>
      <path d="M12 5v14" />
      <path d="M5 12h14" />
    </>
  ),
}

function Icono({ icono }: { icono: IconoDeAccion }) {
  return (
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
      {TRAZADOS[icono]}
    </svg>
  )
}

function clases(icono: IconoDeAccion, className: string | undefined) {
  return ['btn', 'btn-sm', VARIANTE[icono], 'btn-icono', className].filter(Boolean).join(' ')
}

type PropsBase = {
  icono: IconoDeAccion
  /** Nombre accesible. Por defecto "Eliminar" / "Editar" / "Agregar"; usar uno específico por fila
   * ("Eliminar {nombre}") cuando varias filas comparten la misma acción. */
  etiqueta?: string
}

export type PropsBotonIcono = PropsBase &
  Omit<ComponentProps<'button'>, 'children' | 'aria-label'>

/** Botón de acción de fila/listado que muestra solo un ícono. `title` queda como tooltip nativo y
 * puede sobrescribirse (p. ej. para explicar por qué la acción está deshabilitada). */
export function BotonIcono({ icono, etiqueta, className, title, type = 'button', ...resto }: PropsBotonIcono) {
  const nombre = etiqueta ?? ETIQUETA_POR_DEFECTO[icono]
  return (
    <button {...resto} type={type} className={clases(icono, className)} aria-label={nombre} title={title ?? ETIQUETA_POR_DEFECTO[icono]}>
      <Icono icono={icono} />
    </button>
  )
}

export type PropsEnlaceIcono = PropsBase & Omit<LinkProps, 'children' | 'aria-label'>

/** Misma apariencia que `BotonIcono`, pero como `<Link>` real (navegación a una URL). */
export function EnlaceIcono({ icono, etiqueta, className, title, ...resto }: PropsEnlaceIcono) {
  const nombre = etiqueta ?? ETIQUETA_POR_DEFECTO[icono]
  return (
    <Link {...resto} className={clases(icono, className)} aria-label={nombre} title={title ?? ETIQUETA_POR_DEFECTO[icono]}>
      <Icono icono={icono} />
    </Link>
  )
}
