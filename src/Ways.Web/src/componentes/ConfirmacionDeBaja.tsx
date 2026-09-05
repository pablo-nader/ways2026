import { useEffect, useRef } from 'react'

/**
 * Puerta MODAL de confirmación (etapa 20, slice 5), compartida por las CUATRO pantallas raíz —
 * `react-async-state` regla 10: el patrón se replica en todas las superficies hermanas, y la forma
 * más barata de garantizarlo es que haya una sola. Sus valores por defecto son los de la baja
 * lógica (de ahí el nombre), pero el panel es genérico: `Tenants` lo reusa para suspender y
 * reactivar, que estaban con `confirm()` nativo en el MISMO archivo que introdujo la puerta.
 *
 * MODAL DE VERDAD, y ahí está la seguridad: mientras esta puerta está montada, la pantalla que la
 * monta deja INERTE todo el resto de sus controles (`bloqueado = ocupado || puerta abierta`). Eso
 * es la regla 9 por construcción —nada puede supersederla— y de paso hace innecesaria una trampa
 * de foco: un control deshabilitado no es tabulable, así que no queda nada afuera para enfocar.
 *
 * NO usa `window.confirm`: la puerta tiene que NOMBRAR qué se va a borrar, y en el caso del
 * tenant eso incluye a sus hijos, que se van con él en la misma cascada. Un `confirm` nativo
 * bloquea el hilo, no se puede rendir con listas y —lo que importa acá— no se puede dejar
 * inerte mientras el DELETE está en vuelo.
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

const NOTA_DE_BAJA_LOGICA = 'La baja es lógica: los datos dejan de verse, pero no se borran de la base.'

export function ConfirmacionDeBaja({
  titulo,
  pregunta = 'Dar de baja',
  arrastra = [],
  nota = NOTA_DE_BAJA_LOGICA,
  etiquetaConfirmar = 'Confirmar baja',
  etiquetaEnCurso = 'Dando de baja…',
  ocupado,
  disparador = null,
  onConfirmar,
  onCancelar,
}: Props) {
  const cancelarRef = useRef<HTMLButtonElement>(null)
  const panelRef = useRef<HTMLDivElement>(null)
  /** Adónde va el foco cuando el disparador ya no sirve (la fila que lo contenía se fue con la
   * baja): un punto de referencia estable de la pantalla —el título de la `Box` o, si no hay, la
   * tabla— en vez del `<body>`, que deja a quien navega por teclado al principio de todo. */
  const regresoRef = useRef<HTMLElement | null>(null)

  useEffect(() => {
    regresoRef.current =
      panelRef.current?.closest('.box')?.querySelector<HTMLElement>('header h5, table') ?? null
    cancelarRef.current?.focus()

    return () => {
      if (esAlcanzable(disparador)) {
        disparador.focus()

        return
      }

      const regreso = regresoRef.current
      if (!regreso) return

      // Un encabezado o una tabla no son tabulables: para poder recibir el foco necesitan un
      // `tabindex` que React no rinde (no es un nodo suyo) y que se pone una sola vez.
      if (!regreso.hasAttribute('tabindex')) regreso.setAttribute('tabindex', '-1')
      regreso.focus()
    }
  }, [disparador])

  /** Escape = Cancelar, y se escucha en `document` y no en el panel a propósito: el foco puede
   * haberse ido a la nada si la fila que lo tenía desapareció. Mientras la escritura está en vuelo
   * no cancela nada, igual que el botón. */
  useEffect(() => {
    function alTeclado(evento: KeyboardEvent) {
      if (evento.key !== 'Escape' || ocupado) return

      evento.preventDefault()
      onCancelar()
    }

    document.addEventListener('keydown', alTeclado)

    return () => document.removeEventListener('keydown', alTeclado)
  }, [ocupado, onCancelar])

  return (
    <div
      ref={panelRef}
      className="alert alert-warning rounded-0"
      role="alertdialog"
      aria-modal="true"
      aria-label={etiquetaConfirmar}
    >
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
      {nota && <p className="mb-3">{nota}</p>}
      <div className="d-flex gap-2">
        <button type="button" className="btn btn-danger rounded-0" onClick={onConfirmar} disabled={ocupado}>
          {ocupado ? etiquetaEnCurso : etiquetaConfirmar}
        </button>
        <button
          ref={cancelarRef}
          type="button"
          className="btn btn-outline-secondary rounded-0"
          onClick={onCancelar}
          disabled={ocupado}
        >
          Cancelar
        </button>
      </div>
    </div>
  )
}
