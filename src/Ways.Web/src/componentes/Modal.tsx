import { useEffect, useId, useLayoutEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'

const Z_INDEX_BASE_MODAL = 1055
const Z_INDEX_BASE_BACKDROP = 1050
const Z_INDEX_PASO = 20

const SELECTOR_FOCUSABLE =
  'a[href], button:not([disabled]), textarea:not([disabled]), input:not([disabled]):not([type="hidden"]), select:not([disabled]), [tabindex]:not([tabindex="-1"])'

function obtenerFocusables(contenedor: HTMLElement): HTMLElement[] {
  return Array.from(contenedor.querySelectorAll<HTMLElement>(SELECTOR_FOCUSABLE))
}

/** Mismo criterio que `esAlcanzable` de `ConfirmacionDeBaja`: un elemento sirve como destino de
 * restauración de foco solo si sigue en el documento y sigue siendo operable — el trigger puede
 * haber desaparecido (la fila que lo tenía se fue) o haber quedado inerte. */
function esAlcanzable(elemento: HTMLElement | null): elemento is HTMLElement {
  return (
    elemento !== null &&
    elemento.isConnected &&
    !elemento.matches(':disabled') &&
    elemento.getAttribute('aria-disabled') !== 'true'
  )
}

/** Pila module-level de modales abiertos, en orden de apertura — el último id es el tope. Vive
 * fuera de React a propósito: Escape necesita saber cuál es el modal MÁS ARRIBA entre instancias
 * hermanas sin que ninguna dependa del estado de la otra. */
const pilaDeModales: string[] = []
let modalesAbiertos = 0

export type TamanoDeModal = 'sm' | 'lg' | 'xl'

const CLASE_POR_TAMANO: Record<TamanoDeModal, string> = {
  sm: 'modal-sm',
  lg: 'modal-lg',
  xl: 'modal-xl',
}

export type PropsModal = {
  titulo: string
  children: React.ReactNode
  pie?: React.ReactNode
  tamano?: TamanoDeModal
  /** `true` mientras una escritura está en vuelo: el botón de cerrar queda deshabilitado y
   * Escape/click en el fondo se ignoran — mismo criterio de "compuerta inerte mientras ocupado"
   * que el resto de las pantallas (react-async-state regla 13). */
  ocupado?: boolean
  /** `false` cuando el llamador es el único dueño del foco al cerrar (p. ej. el POS lo devuelve
   * siempre al input de código, y recién cuando ese input vuelve a estar habilitado): el modal no
   * devuelve el foco al control que lo tenía antes de abrir. Se lee al montar. */
  restaurarFoco?: boolean
  /** Nombre accesible del botón de cerrar del header — distinto de "Cerrar" cuando el pie ya tiene
   * un botón con ese nombre. */
  etiquetaCerrar?: string
  onCerrar: () => void
}

/**
 * Modal genérico reusable (Bootstrap markup, sin su JS — el proyecto no lo carga). Pensado para
 * apilarse: la próxima etapa va a alojar el formulario completo de artículo acá adentro, con un
 * modal de alta rápida abriéndose ENCIMA de ese. Cada instancia se registra en `pilaDeModales` al
 * montar y se da de baja al desmontar, así que Escape y el z-index siempre reflejan cuál es el
 * modal más arriba entre los que están abiertos en un momento dado — nunca un booleano "hay un
 * modal abierto" que no distinga cuál.
 */
export function Modal({
  titulo,
  children,
  pie,
  tamano,
  ocupado = false,
  restaurarFoco = true,
  etiquetaCerrar = 'Cerrar',
  onCerrar,
}: PropsModal) {
  const idTitulo = useId()
  const idPropio = useId()
  const contenidoRef = useRef<HTMLDivElement>(null)
  // Capturado con el inicializador perezoso de `useState` — corre durante el RENDER, antes de
  // cualquier commit. Es a propósito que NO sea un `useLayoutEffect`: React aplica `autoFocus` de
  // los hijos (si el contenido del modal tiene un campo con esa prop) durante la fase de mutación
  // del MISMO commit de montaje, que corre ANTES que cualquier layout effect — para cuando un
  // `useLayoutEffect` de acá llegara a leer `document.activeElement`, ese autoFocus ya lo habría
  // pisado, y "el foco anterior" terminaría siendo el campo del propio modal en vez del control
  // real que lo abrió (bug real, encontrado con un select enfocado a mano antes de abrir un modal
  // cuyo contenido tenía un input con `autoFocus`: el select nunca recuperaba el foco al cerrar).
  const [focoPrevio] = useState<HTMLElement | null>(() =>
    restaurarFoco ? (document.activeElement as HTMLElement | null) : null,
  )
  const [nivel, setNivel] = useState(0)

  // useLayoutEffect (no useEffect): el registro en la pila, el cálculo del nivel de apilado y el
  // foco inicial tienen que resolverse ANTES de que el navegador pinte — evita un frame con el
  // z-index o el foco todavía sin corregir. Para modales hermanos montados en el mismo commit, los
  // layout effects corren en orden de declaración: el primero empuja a la pila antes de que el
  // segundo lea su longitud, así que el nivel siempre sale bien incluso montados juntos.
  useLayoutEffect(() => {
    const nivelPropio = pilaDeModales.length
    pilaDeModales.push(idPropio)
    setNivel(nivelPropio)

    modalesAbiertos += 1
    if (modalesAbiertos === 1) document.body.classList.add('modal-open')

    const contenedor = contenidoRef.current
    // Si ya hay foco adentro del modal (p. ej. un campo con `autoFocus` que React ya enfocó al
    // montar), se respeta ese destino — nunca se lo pisa con el primer focusable a la fuerza.
    if (contenedor && !contenedor.contains(document.activeElement)) {
      obtenerFocusables(contenedor)[0]?.focus()
    }

    return () => {
      const indice = pilaDeModales.indexOf(idPropio)
      if (indice !== -1) pilaDeModales.splice(indice, 1)

      modalesAbiertos -= 1
      if (modalesAbiertos === 0) document.body.classList.remove('modal-open')

      if (esAlcanzable(focoPrevio)) focoPrevio.focus()
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // Escape cierra ÚNICAMENTE el modal tope de la pila — con dos modales apilados, el de abajo no
  // debe reaccionar a una tecla que ni siquiera puede ver (el de arriba se la robó visualmente).
  useEffect(() => {
    function alTeclado(evento: KeyboardEvent) {
      if (evento.key !== 'Escape') return
      if (ocupado) return
      if (pilaDeModales[pilaDeModales.length - 1] !== idPropio) return
      evento.preventDefault()
      onCerrar()
    }
    document.addEventListener('keydown', alTeclado)
    return () => document.removeEventListener('keydown', alTeclado)
  }, [idPropio, ocupado, onCerrar])

  function atraparTab(evento: React.KeyboardEvent<HTMLDivElement>) {
    if (evento.key !== 'Tab') return
    const contenedor = contenidoRef.current
    if (!contenedor) return

    const focusables = obtenerFocusables(contenedor)
    if (focusables.length === 0) {
      evento.preventDefault()
      return
    }

    const primero = focusables[0]
    const ultimo = focusables[focusables.length - 1]
    const activo = document.activeElement

    if (evento.shiftKey) {
      if (activo === primero || !contenedor.contains(activo)) {
        evento.preventDefault()
        ultimo.focus()
      }
    } else if (activo === ultimo || !contenedor.contains(activo)) {
      evento.preventDefault()
      primero.focus()
    }
  }

  function alHacerClickEnElFondo(evento: React.MouseEvent<HTMLDivElement>) {
    if (evento.target !== evento.currentTarget) return
    if (ocupado) return
    onCerrar()
  }

  const claseTamano = tamano ? CLASE_POR_TAMANO[tamano] : ''

  return createPortal(
    <>
      <div
        className="modal d-block"
        style={{ zIndex: Z_INDEX_BASE_MODAL + nivel * Z_INDEX_PASO }}
        tabIndex={-1}
        role="dialog"
        aria-modal="true"
        aria-labelledby={idTitulo}
        onKeyDown={atraparTab}
        onClick={alHacerClickEnElFondo}
      >
        <div className={`modal-dialog ${claseTamano}`.trim()} role="document">
          <div className="modal-content rounded-0" ref={contenidoRef}>
            <div className="modal-header">
              <h5 className="modal-title" id={idTitulo}>
                {titulo}
              </h5>
              <button type="button" className="btn-close" aria-label={etiquetaCerrar} disabled={ocupado} onClick={onCerrar} />
            </div>
            <div className="modal-body">{children}</div>
            {pie && <div className="modal-footer">{pie}</div>}
          </div>
        </div>
      </div>
      <div className="modal-backdrop show" style={{ zIndex: Z_INDEX_BASE_BACKDROP + nivel * Z_INDEX_PASO }} />
    </>,
    document.body,
  )
}
