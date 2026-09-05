/**
 * Puerta de confirmación de una baja lógica (etapa 20, slice 5), compartida por las CUATRO
 * pantallas raíz — `react-async-state` regla 10: el patrón se replica en todas las superficies
 * hermanas, y la forma más barata de garantizarlo es que haya una sola.
 *
 * NO usa `window.confirm`: la puerta tiene que NOMBRAR qué se va a borrar, y en el caso del
 * tenant eso incluye a sus hijos, que se van con él en la misma cascada. Un `confirm` nativo
 * bloquea el hilo, no se puede rendir con listas y —lo que importa acá— no se puede dejar
 * inerte mientras el DELETE está en vuelo.
 */
type Props = {
  /** Qué se está por dar de baja, ya nombrado ("el tenant \"Comercio Sur\""). */
  titulo: string
  /** Lo que se va con él. Vacío cuando la baja no arrastra nada. */
  arrastra?: readonly string[]
  /** `true` mientras el DELETE (y su refresco) están en vuelo: la puerta entera queda inerte. */
  ocupado: boolean
  onConfirmar: () => void
  onCancelar: () => void
}

export function ConfirmacionDeBaja({ titulo, arrastra = [], ocupado, onConfirmar, onCancelar }: Props) {
  return (
    <div className="alert alert-warning rounded-0" role="alertdialog" aria-label="Confirmar baja">
      <p className="mb-2">
        <strong>¿Dar de baja {titulo}?</strong>
      </p>
      {arrastra.length > 0 && (
        <>
          <p className="mb-1">Se dan de baja junto con él:</p>
          <ul className="mb-2">
            {arrastra.map((linea) => (
              <li key={linea}>{linea}</li>
            ))}
          </ul>
        </>
      )}
      <p className="mb-3">La baja es lógica: los datos dejan de verse, pero no se borran de la base.</p>
      <div className="d-flex gap-2">
        <button type="button" className="btn btn-danger rounded-0" onClick={onConfirmar} disabled={ocupado}>
          {ocupado ? 'Dando de baja…' : 'Confirmar baja'}
        </button>
        <button
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
