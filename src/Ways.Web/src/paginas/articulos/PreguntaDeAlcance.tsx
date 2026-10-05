import type { Ref } from 'react'
import type { AlcanceDeFamilia } from '../../api/tipos'
import { CAMPOS_PROPIOS, PREGUNTA_DE_ALCANCE } from './familia'

type Props = {
  /** Con qué se abre la pregunta: la familia del artículo si se la conoce, o el texto del servidor. */
  contexto: string
  /** Etiquetas de los campos compartidos que cambian. Vacío cuando lo que se escribe es un precio. */
  cambios?: string[]
  /** `true` mientras la escritura está en vuelo: las tres respuestas quedan inertes. */
  ocupado: boolean
  /** El botón "Cancelar", la respuesta que no escribe nada: a quien aloja la pregunta le sirve para enfocarlo al abrirla. */
  refDeCancelar?: Ref<HTMLButtonElement>
  onElegir: (alcance: AlcanceDeFamilia) => void
  onCancelar: () => void
}

const EXPLICACION =
  'Con "Toda la familia", el cambio llega a todos sus artículos. Con "Solo este artículo", este sale de la familia y el cambio queda solo en él.'

/**
 * La pregunta que se le hace a quien escribe un artículo miembro de una familia (doc 10 §3): aplicar el
 * cambio a toda la familia o dejarlo solo en este artículo, que entonces sale de ella. Es solo el
 * contenido: el formulario de artículo lo aloja en un `Modal` y el editor de precios en el panel de la
 * lista, así que la pregunta y sus tres respuestas son las mismas en los dos.
 */
export function PreguntaDeAlcance({ contexto, cambios = [], ocupado, refDeCancelar, onElegir, onCancelar }: Props) {
  return (
    <div>
      <p className="mb-2">
        {contexto} <strong>{PREGUNTA_DE_ALCANCE}</strong>
      </p>
      {cambios.length > 0 && <p className="mb-2">Campos compartidos que cambian: {cambios.join(', ')}.</p>}
      <p className="small text-body-secondary mb-3">
        {cambios.length > 0 ? `${EXPLICACION} Los campos propios (${CAMPOS_PROPIOS}) se guardan siempre solo en este artículo.` : EXPLICACION}
      </p>
      <div className="d-flex flex-wrap gap-2">
        <button type="button" className="btn btn-primary" disabled={ocupado} onClick={() => onElegir('Familia')}>
          Toda la familia
        </button>
        <button type="button" className="btn btn-outline-warning" disabled={ocupado} onClick={() => onElegir('SoloEste')}>
          Solo este artículo (sale de la familia)
        </button>
        <button ref={refDeCancelar} type="button" className="btn btn-outline-secondary" disabled={ocupado} onClick={onCancelar}>
          Cancelar
        </button>
      </div>
    </div>
  )
}
