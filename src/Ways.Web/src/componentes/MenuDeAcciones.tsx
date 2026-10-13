import { Fragment, useEffect, useId, useRef, useState } from 'react'
import type { FocusEvent, KeyboardEvent } from 'react'
import { Link, useLocation } from 'react-router'

type ItemBase = {
  clave: string
  etiqueta: string
  /** Un ítem no disponible por una razón pasajera se muestra deshabilitado, no oculto. */
  deshabilitado?: boolean
  titulo?: string
  /** Línea divisoria antes del ítem. */
  separadorAntes?: boolean
}

export type ItemDeMenuDeAcciones =
  | (ItemBase & { a: string; alSeleccionar?: never })
  | (ItemBase & { alSeleccionar: () => void; a?: never })

type Props = {
  etiqueta: string
  items: ItemDeMenuDeAcciones[]
  /** El menú se abre hacia la izquierda del botón (los que quedan al borde derecho de la barra). */
  alineadoALaDerecha?: boolean
  className?: string
}

function destinoDe(tecla: string, actual: number, cantidad: number): number | null {
  switch (tecla) {
    case 'ArrowDown':
      return (actual + 1) % cantidad
    case 'ArrowUp':
      return (actual - 1 + cantidad) % cantidad
    case 'Home':
      return 0
    case 'End':
      return cantidad - 1
    default:
      return null
  }
}

function opcionesDe(contenedor: HTMLElement | null): HTMLElement[] {
  return Array.from(contenedor?.querySelectorAll<HTMLElement>('a.dropdown-item, button.dropdown-item:not(:disabled)') ?? [])
}

/**
 * Menú desplegable de acciones de la barra del POS (patrón de disclosure, sin `role="menu"` ni JS de
 * Bootstrap): mezcla enlaces de navegación y acciones. Se cierra con click afuera, Escape, al perder
 * el foco y al elegir un ítem; con el teclado se recorre con flechas, Inicio y Fin.
 */
export function MenuDeAcciones({ etiqueta, items, alineadoALaDerecha = false, className }: Props) {
  const idMenu = useId()
  const { pathname } = useLocation()
  const [abierto, setAbierto] = useState(false)
  const contenedorRef = useRef<HTMLDivElement>(null)
  const botonRef = useRef<HTMLButtonElement>(null)
  const enfocarPrimeroAlAbrirRef = useRef(false)

  useEffect(() => {
    if (!abierto) return
    function alPresionarAfuera(evento: PointerEvent) {
      if (evento.target instanceof Node && !contenedorRef.current?.contains(evento.target)) setAbierto(false)
    }
    document.addEventListener('pointerdown', alPresionarAfuera)
    return () => document.removeEventListener('pointerdown', alPresionarAfuera)
  }, [abierto])

  // Los ítems recién son enfocables cuando el commit que abre la lista les quitó `hidden`.
  useEffect(() => {
    if (abierto && enfocarPrimeroAlAbrirRef.current) {
      enfocarPrimeroAlAbrirRef.current = false
      opcionesDe(contenedorRef.current)[0]?.focus()
    }
  }, [abierto])

  function alTeclear(evento: KeyboardEvent<HTMLDivElement>) {
    if (evento.key === 'Escape') {
      if (!abierto) return
      evento.preventDefault()
      setAbierto(false)
      botonRef.current?.focus()
      return
    }
    const opciones = opcionesDe(contenedorRef.current)
    if (evento.target === botonRef.current) {
      if (evento.key !== 'ArrowDown') return
      evento.preventDefault()
      if (abierto) {
        opciones[0]?.focus()
      } else {
        enfocarPrimeroAlAbrirRef.current = true
        setAbierto(true)
      }
      return
    }
    const actual = opciones.indexOf(evento.target as HTMLElement)
    if (actual === -1) return
    const destino = destinoDe(evento.key, actual, opciones.length)
    if (destino === null) return
    evento.preventDefault()
    opciones[destino]?.focus()
  }

  function alPerderFoco(evento: FocusEvent<HTMLDivElement>) {
    if (abierto && !evento.currentTarget.contains(evento.relatedTarget)) setAbierto(false)
  }

  function elegir(item: ItemDeMenuDeAcciones) {
    setAbierto(false)
    if (item.alSeleccionar) {
      botonRef.current?.focus()
      item.alSeleccionar()
    }
  }

  const activo = items.some((item) => item.a !== undefined && pathname === item.a)

  return (
    <div ref={contenedorRef} className="dropdown" onKeyDown={alTeclear} onBlur={alPerderFoco}>
      <button
        ref={botonRef}
        type="button"
        className={`btn btn-outline-secondary dropdown-toggle${activo ? ' active' : ''}${className ? ` ${className}` : ''}`}
        aria-expanded={abierto}
        aria-controls={idMenu}
        onClick={() => setAbierto((previo) => !previo)}
      >
        {etiqueta}
      </button>
      <ul
        id={idMenu}
        data-bs-popper="static"
        className={`dropdown-menu${alineadoALaDerecha ? ' dropdown-menu-end' : ''}${abierto ? ' show' : ''}`}
        hidden={!abierto}
      >
        {items.map((item) => (
          <Fragment key={item.clave}>
            {item.separadorAntes && (
              <li>
                <hr className="dropdown-divider" />
              </li>
            )}
            <li>
              {item.a !== undefined ? (
                <Link
                  className={`dropdown-item${pathname === item.a ? ' active' : ''}`}
                  aria-current={pathname === item.a ? 'page' : undefined}
                  to={item.a}
                  title={item.titulo}
                  onClick={() => elegir(item)}
                >
                  {item.etiqueta}
                </Link>
              ) : (
                <button
                  type="button"
                  className="dropdown-item"
                  disabled={item.deshabilitado}
                  title={item.titulo}
                  onClick={() => elegir(item)}
                >
                  {item.etiqueta}
                </button>
              )}
            </li>
          </Fragment>
        ))}
      </ul>
    </div>
  )
}
