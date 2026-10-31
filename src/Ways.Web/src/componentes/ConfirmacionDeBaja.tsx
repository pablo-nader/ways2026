import { useEffect, useId, useRef, useState } from 'react'
import type { RefObject } from 'react'
import { Modal } from './Modal'

/**
 * Confirmación MODAL (etapa 20, slice 5), compartida por las pantallas que dan de baja o cambian
 * el estado de algo — `react-async-state` regla 10: una sola puerta para todas. Sus valores por
 * defecto son los de la baja lógica (de ahí el nombre), pero es genérica: `Tenants` la reusa para
 * suspender y reactivar.
 *
 * Se rinde dentro de `Modal` (portal, trampa de foco, apilado): la confirmación nunca vive
 * incrustada en la página. Mientras `ocupado`, el modal no se cierra (cruz, Cancelar, Escape y
 * fondo quedan inertes). Los anfitriones pueden seguir bloqueando sus propios controles.
 *
 * NO usa `window.confirm`: tiene que NOMBRAR qué se va a borrar, incluidos sus hijos, y no se
 * puede dejar inerte mientras el DELETE está en vuelo.
 */
type Props = {
  /** Qué se está por tocar, ya nombrado ("el tenant \"Comercio Sur\""). */
  titulo: string
  /** Verbo de la acción, ya conjugado en infinitivo. Por defecto, el de la baja. */
  pregunta?: string
  /** Lo que se va con él. Vacío cuando la acción no arrastra nada. */
  arrastra?: readonly string[]
  /** Aclaración bajo la pregunta. Por defecto, la de la baja lógica; `null` la omite. */
  nota?: string | null
  /** Etiqueta del botón que confirma, y nombre accesible de la puerta. */
  etiquetaConfirmar?: string
  /** Etiqueta de ese mismo botón mientras la escritura está en vuelo. */
  etiquetaEnCurso?: string
  /** `true` mientras la escritura (y su refresco) están en vuelo: la puerta entera queda inerte. */
  ocupado: boolean
  /**
   * Control que abrió la puerta, capturado SÍNCRONAMENTE en su `onClick` (antes de cualquier
   * `setState`). No se puede leer acá con `document.activeElement`: para cuando el efecto de
   * montaje corre, el commit que abrió la puerta ya dejó ese control `disabled` y el navegador
   * aplicó la *focus fixup rule* —el foco ya está en el `<body>`—, así que la captura tardía
   * devolvía el foco a la nada. jsdom no reproduce esa corrección, por eso el defecto pasaba los
   * tests: el del componente lo fuerza a mano.
   */
  disparador?: HTMLElement | null
  /**
   * Destino explícito del foco cuando el disparador ya no sirve, para una puerta que no vive dentro
   * de una `Box` (p. ej. dentro de un `Modal`, en un portal, donde no hay un `.box` al que subir). Es
   * un `RefObject` y se lee recién al cerrar. Sin él, o con su `.current` en `null`, rige el respaldo
   * de la `Box`.
   */
  regreso?: RefObject<HTMLElement | null> | null
  /** Motivo del rechazo de la acción confirmada; se muestra dentro del modal, que sigue abierto. */
  error?: string | null
  onConfirmar: () => void
  onCancelar: () => void
}

/** Un disparador sirve para devolverle el foco solo si sigue en el documento y sigue siendo
 * operable: la fila puede haber desaparecido (la baja salió bien) o quedar inerte. */
function esAlcanzable(elemento: HTMLElement | null): elemento is HTMLElement {
  return (
    elemento !== null &&
    elemento.isConnected &&
    !elemento.matches(':disabled') &&
    elemento.getAttribute('aria-disabled') !== 'true'
  )
}

export const NOTA_DE_BAJA_LOGICA = 'La baja es lógica: los datos dejan de verse, pero no se borran de la base.'

export function ConfirmacionDeBaja({
  titulo,
  pregunta = 'Dar de baja',
  arrastra = [],
  nota = NOTA_DE_BAJA_LOGICA,
  etiquetaConfirmar = 'Confirmar baja',
  etiquetaEnCurso = 'Dando de baja…',
  ocupado,
  disparador = null,
  regreso: regresoExplicito = null,
  error = null,
  onConfirmar,
  onCancelar,
}: Props) {
  /** Foco al abrir, capturado en el render (antes de que el commit deshabilite el disparador). */
  const [focoPrevio] = useState<HTMLElement | null>(() => document.activeElement as HTMLElement | null)
  /** Punto de referencia estable de la pantalla (título de la `Box` o tabla) para cuando el
   * disparador se fue con la baja. Se resuelve al abrir porque, ya en el portal, no hay `.box`
   * ancestro al cual subir al cerrar. */
  const [respaldo] = useState<HTMLElement | null>(
    () => disparador?.closest('.box')?.querySelector<HTMLElement>('header h5, table') ?? null,
  )
  const confirmandoRef = useRef(false)
  const idDescripcion = useId()

  useEffect(() => {
    return () => {
      if (esAlcanzable(disparador)) {
        disparador.focus()

        return
      }

      const regreso = regresoExplicito?.current ?? respaldo
      if (regreso?.isConnected) {
        // Un encabezado o una tabla no son tabulables: necesitan un `tabindex` que React no rinde.
        if (!regreso.hasAttribute('tabindex')) regreso.setAttribute('tabindex', '-1')
        regreso.focus()

        return
      }

      if (esAlcanzable(focoPrevio) && focoPrevio !== document.body) focoPrevio.focus()
    }
  }, [disparador, regresoExplicito, respaldo, focoPrevio])

  function alConfirmar() {
    if (confirmandoRef.current) return
    confirmandoRef.current = true
    queueMicrotask(() => {
      confirmandoRef.current = false
    })
    onConfirmar()
  }

  return (
    <Modal
      titulo={etiquetaConfirmar}
      tamano="sm"
      rol="alertdialog"
      idDescripcion={idDescripcion}
      ocupado={ocupado}
      restaurarFoco={false}
      onCerrar={onCancelar}
      pie={
        <>
          <button type="button" className="btn btn-danger" onClick={alConfirmar} disabled={ocupado}>
            {ocupado ? etiquetaEnCurso : etiquetaConfirmar}
          </button>
          <button
            type="button"
            className="btn btn-outline-secondary"
            onClick={onCancelar}
            disabled={ocupado}
            autoFocus
          >
            Cancelar
          </button>
        </>
      }
    >
      <div id={idDescripcion}>
        <p className="mb-2">
          <strong>
            ¿{pregunta} {titulo}?
          </strong>
        </p>
        {arrastra.length > 0 && (
          <>
            <p className="mb-1">También se dan de baja:</p>
            <ul className="mb-2">
              {arrastra.map((linea) => (
                <li key={linea}>{linea}</li>
              ))}
            </ul>
          </>
        )}
        {nota && <p className="mb-0">{nota}</p>}
      </div>
      {error && (
        <div className="alert alert-danger mt-3 mb-0" role="alert">
          {error}
        </div>
      )}
    </Modal>
  )
}
