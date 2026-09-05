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
  onConfirmar: () => void
  onCancelar: () => void
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
  onConfirmar,
  onCancelar,
}: Props) {
  const cancelarRef = useRef<HTMLButtonElement>(null)
  /** Quién tenía el foco cuando la puerta se abrió, para devolvérselo al cerrarla. Si esa fila ya
   * no existe (la baja salió bien), enfocar un nodo desprendido es un no-op inofensivo. */
  const disparadorRef = useRef<HTMLElement | null>(null)

  useEffect(() => {
    disparadorRef.current = document.activeElement instanceof HTMLElement ? document.activeElement : null
    cancelarRef.current?.focus()

    return () => disparadorRef.current?.focus()
  }, [])

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
