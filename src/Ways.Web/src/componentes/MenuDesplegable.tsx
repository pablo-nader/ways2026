import { Fragment, useEffect, useId, useRef } from 'react'
import type { FocusEvent, KeyboardEvent } from 'react'
import { Link, useLocation } from 'react-router'
import { esRutaActiva } from './menu'
import type { GrupoDeMenu } from './menu'

type Props = {
  grupo: GrupoDeMenu
  abierto: boolean
  activo: boolean
  alAlternar: () => void
  alCerrar: () => void
}

function enlacesDe(item: HTMLLIElement | null): HTMLAnchorElement[] {
  return Array.from(item?.querySelectorAll<HTMLAnchorElement>('a.dropdown-item') ?? [])
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

/**
 * Patrón WAI-ARIA de navegación con disclosure: un botón que muestra u oculta una lista de links
 * comunes, sin `role="menu"`. La `<ul>` se renderiza siempre y se oculta con `hidden`, que es lo
 * que hace coincidir producción (Bootstrap) y jsdom sin depender de ninguna hoja de estilos.
 */
export function MenuDesplegable({ grupo, abierto, activo, alAlternar, alCerrar }: Props) {
  const idMenu = useId()
  const { pathname } = useLocation()
  const itemRef = useRef<HTMLLIElement>(null)
  const botonRef = useRef<HTMLButtonElement>(null)
  const enfocarPrimeroAlAbrirRef = useRef(false)

  useEffect(() => {
    if (!abierto) return
    function alPresionarAfuera(evento: PointerEvent) {
      if (evento.target instanceof Node && !itemRef.current?.contains(evento.target)) alCerrar()
    }
    document.addEventListener('pointerdown', alPresionarAfuera)
    return () => document.removeEventListener('pointerdown', alPresionarAfuera)
  }, [abierto, alCerrar])

  // Los ítems recién son enfocables cuando el commit que abre la lista les quitó `hidden`.
  useEffect(() => {
    if (abierto && enfocarPrimeroAlAbrirRef.current) {
      enfocarPrimeroAlAbrirRef.current = false
      enlacesDe(itemRef.current)[0]?.focus()
    }
  }, [abierto])

  function alTeclear(evento: KeyboardEvent<HTMLLIElement>) {
    if (evento.key === 'Escape') {
      if (!abierto) return
      evento.preventDefault()
      alCerrar()
      botonRef.current?.focus()
      return
    }
    const items = enlacesDe(itemRef.current)
    if (evento.target === botonRef.current) {
      if (evento.key !== 'ArrowDown') return
      evento.preventDefault()
      if (abierto) {
        items[0]?.focus()
      } else {
        enfocarPrimeroAlAbrirRef.current = true
        alAlternar()
      }
      return
    }
    const actual = items.indexOf(evento.target as HTMLAnchorElement)
    if (actual === -1) return
    const destino = destinoDe(evento.key, actual, items.length)
    if (destino === null) return
    evento.preventDefault()
    items[destino]?.focus()
  }

  function alPerderFoco(evento: FocusEvent<HTMLLIElement>) {
    if (abierto && !evento.currentTarget.contains(evento.relatedTarget)) alCerrar()
  }

  return (
    <li ref={itemRef} className="nav-item dropdown" onKeyDown={alTeclear} onBlur={alPerderFoco}>
      <button
        ref={botonRef}
        type="button"
        className={`nav-link dropdown-toggle${activo ? ' active' : ''}`}
        aria-expanded={abierto}
        aria-controls={idMenu}
        onClick={alAlternar}
      >
        {grupo.etiqueta}
      </button>
      <ul
        id={idMenu}
        className={abierto ? 'dropdown-menu dropdown-menu-dark show' : 'dropdown-menu dropdown-menu-dark'}
        hidden={!abierto}
      >
        {grupo.secciones.map((seccion, indice) => (
          <Fragment key={indice}>
            {indice > 0 && (
              <li>
                <hr className="dropdown-divider" />
              </li>
            )}
            {seccion.titulo && (
              <li>
                <h6 className="dropdown-header">{seccion.titulo}</h6>
              </li>
            )}
            {seccion.enlaces.map((enlace) => {
              const enlaceActivo = esRutaActiva(pathname, enlace)
              return (
                <li key={enlace.a}>
                  <Link
                    className={`dropdown-item${enlaceActivo ? ' active' : ''}`}
                    aria-current={enlaceActivo ? 'page' : undefined}
                    to={enlace.a}
                    onClick={alCerrar}
                  >
                    {enlace.etiqueta}
                  </Link>
                </li>
              )
            })}
          </Fragment>
        ))}
      </ul>
    </li>
  )
}
