import type { ComponentProps, ReactNode } from 'react'
import { Link, type LinkProps } from 'react-router'

export type IconoDeAccion =
  | 'eliminar'
  | 'anular'
  | 'editar'
  | 'agregar'
  | 'ajuste'
  | 'cerrar'
  | 'actualizar'
  | 'detalle'
  | 'imprimir'
  | 'limpiar'

const ETIQUETA_POR_DEFECTO: Record<IconoDeAccion, string> = {
  eliminar: 'Eliminar',
  anular: 'Anular',
  editar: 'Editar',
  agregar: 'Agregar',
  ajuste: 'Ajuste manual',
  cerrar: 'Cerrar',
  actualizar: 'Actualizar',
  detalle: 'Detalle',
  imprimir: 'Imprimir',
  limpiar: 'Limpiar filtros',
}

const VARIANTE: Record<IconoDeAccion, string> = {
  eliminar: 'btn-outline-danger',
  anular: 'btn-outline-danger',
  editar: 'btn-outline-primary',
  agregar: 'btn-primary',
  ajuste: 'btn-outline-secondary',
  cerrar: 'btn-outline-secondary',
  actualizar: 'btn-outline-secondary',
  detalle: 'btn-outline-secondary',
  imprimir: 'btn-outline-secondary',
  limpiar: 'btn-outline-secondary',
}

const PAPELERA = (
  <>
    <path d="M4 7h16" />
    <path d="M10 11v6" />
    <path d="M14 11v6" />
    <path d="M5 7l1 12a2 2 0 0 0 2 2h8a2 2 0 0 0 2-2l1-12" />
    <path d="M9 7V4a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v3" />
  </>
)

const TRAZADOS: Record<IconoDeAccion, ReactNode> = {
  eliminar: PAPELERA,
  anular: PAPELERA,
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
  ajuste: (
    <>
      <path d="M19 5L5 19" />
      <circle cx="7" cy="7" r="2" />
      <circle cx="17" cy="17" r="2" />
    </>
  ),
  cerrar: (
    <>
      <path d="M6 6l12 12" />
      <path d="M18 6L6 18" />
    </>
  ),
  actualizar: (
    <>
      <path d="M20 11a8 8 0 0 0-14.9-3" />
      <path d="M4 4v4h4" />
      <path d="M4 13a8 8 0 0 0 14.9 3" />
      <path d="M20 20v-4h-4" />
    </>
  ),
  detalle: (
    <>
      <path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z" />
      <circle cx="12" cy="12" r="3" />
    </>
  ),
  imprimir: (
    <>
      <path d="M6 9V3h12v6" />
      <path d="M6 18H4a1 1 0 0 1-1-1v-6a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2v6a1 1 0 0 1-1 1h-2" />
      <rect x="6" y="14" width="12" height="7" />
    </>
  ),
  limpiar: (
    <>
      <path d="M3 4h18l-7 8v6l-4 2v-8L3 4z" />
      <path d="M4 20L20 4" />
    </>
  ),
}

/** El mismo trazado de los botones de ícono, para los botones que además llevan texto visible. */
export function Icono({ icono }: { icono: IconoDeAccion }) {
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
  Omit<ComponentProps<'button'>, 'children' | 'aria-label'> & {
    /** Hay una operación en curso: marca `aria-busy`, y solo el ícono de actualizar gira. El nombre y el
     * tooltip no cambian, así que quien usa lector de pantalla no ve un botón que se renombra a mitad de camino. */
    ocupado?: boolean
  }

/** Botón de acción de fila/listado que muestra solo un ícono. `title` queda como tooltip nativo y
 * puede sobrescribirse (p. ej. para explicar por qué la acción está deshabilitada). */
export function BotonIcono({ icono, etiqueta, className, title, type = 'button', ocupado = false, ...resto }: PropsBotonIcono) {
  const nombre = etiqueta ?? ETIQUETA_POR_DEFECTO[icono]
  return (
    <button
      {...resto}
      type={type}
      className={clases(icono, ocupado && icono === 'actualizar' ? [className, 'btn-icono-ocupado'].filter(Boolean).join(' ') : className)}
      aria-label={nombre}
      aria-busy={ocupado || undefined}
      title={title ?? ETIQUETA_POR_DEFECTO[icono]}
    >
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
