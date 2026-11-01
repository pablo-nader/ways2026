import { useEffect, useId, useRef, useState } from 'react'
import {
  LARGO_MAXIMO_DE_NOTA_DE_CONCILIACION,
  aSolicitudDeConciliacion,
  clienteDeTesoreria,
  errorAlConciliar,
  esResultadoInciertoDeConciliacion,
  formularioDeConciliacionInicial,
  validarConciliacion,
  type ErrorDeConciliacion,
  type FormularioDeConciliacion,
  type ResultadoDeConciliacion,
} from '../api/tesoreria'
import { Modal } from './Modal'

type Props = {
  idEmpresa: number
  /** Marca vigente que mostraba el libro: el rechazo por una fecha anterior la nombra. */
  conciliadoHasta: string | null
  onCerrar: () => void
  /** Se llama una sola vez con la marca ya registrada; quien abrió el modal lo cierra y recarga. */
  onConciliado: (resultado: ResultadoDeConciliacion) => void
  /** Se llama una sola vez cuando NO se sabe si la marca se escribió: quien abrió el modal lo cierra, recarga y avisa. */
  onIncierto: () => void
}

/**
 * Conciliación de la tesorería (solo Admin): congela todo lo anterior al instante elegido y no se puede deshacer.
 *
 * `react-async-state`: guarda de reentrancia de primera línea (ref) más ventana completa deshabilitada y cierre bloqueado mientras
 * `conciliando`; por eso la respuesta propia no lleva generación. No hay clave de idempotencia: un resultado incierto nunca deja el
 * formulario abierto para reenviar a ciegas, se avisa al llamador. La confirmación es una casilla obligatoria, no un segundo paso.
 */
export function ModalDeConciliacion({ idEmpresa, conciliadoHasta, onCerrar, onConciliado, onIncierto }: Props) {
  const [formulario, setFormulario] = useState<FormularioDeConciliacion>(() => formularioDeConciliacionInicial())
  const [entiendo, setEntiendo] = useState(false)
  const [error, setError] = useState<ErrorDeConciliacion | null>(null)
  const [conciliando, setConciliando] = useState(false)
  const conciliandoRef = useRef(false)
  const vigenteRef = useRef(true)
  const idAviso = useId()

  useEffect(() => {
    vigenteRef.current = true
    return () => {
      vigenteRef.current = false
    }
  }, [])

  function cambiar(cambios: Partial<FormularioDeConciliacion>) {
    setFormulario((previo) => ({ ...previo, ...cambios }))
    setError(null)
  }

  async function conciliar() {
    if (conciliandoRef.current || !entiendo) return

    const rechazo = validarConciliacion(formulario)
    if (rechazo) {
      setError(rechazo)
      return
    }

    conciliandoRef.current = true
    setConciliando(true)
    setError(null)

    try {
      const resultado = await clienteDeTesoreria.conciliar(aSolicitudDeConciliacion(idEmpresa, formulario))
      if (vigenteRef.current) onConciliado(resultado)
    } catch (e) {
      if (!vigenteRef.current) return

      if (esResultadoInciertoDeConciliacion(e)) {
        onIncierto()
        return
      }

      conciliandoRef.current = false
      setConciliando(false)
      setError(errorAlConciliar(e, conciliadoHasta))
    }
  }

  const errorDe = (campo: ErrorDeConciliacion['campo']) => (error?.campo === campo ? error.mensaje : null)

  const pie = (
    <>
      <button type="button" className="btn btn-outline-secondary" onClick={onCerrar} disabled={conciliando}>
        Cancelar
      </button>
      <button type="button" className="btn btn-primary" onClick={() => void conciliar()} disabled={conciliando || !entiendo}>
        {conciliando ? 'Conciliando…' : 'Conciliar'}
      </button>
    </>
  )

  return (
    <Modal titulo="Conciliar tesorería" ocupado={conciliando} pie={pie} onCerrar={onCerrar}>
      {errorDe('general') && <div className="alert alert-danger py-2 px-3 small">{errorDe('general')}</div>}

      <div id={idAviso} className="alert alert-warning py-2 px-3 small">
        Al conciliar, todo lo anterior a ese instante queda congelado y no se puede modificar. Las correcciones solo se hacen con ajustes.
        La conciliación no se puede deshacer.
      </div>

      <fieldset disabled={conciliando} className="border-0 p-0 m-0">
        <div className="mb-3">
          <label className="form-label" htmlFor="conciliacion-hasta">
            Conciliar hasta
          </label>
          <input
            id="conciliacion-hasta"
            className="form-control"
            type="datetime-local"
            value={formulario.hasta}
            onChange={(e) => cambiar({ hasta: e.target.value })}
          />
          {errorDe('hasta') && <div className="text-danger small mt-1">{errorDe('hasta')}</div>}
        </div>

        <div className="mb-3">
          <label className="form-label" htmlFor="conciliacion-nota">
            Nota (opcional)
          </label>
          <textarea
            id="conciliacion-nota"
            className="form-control"
            rows={3}
            value={formulario.nota}
            onChange={(e) => cambiar({ nota: e.target.value })}
          />
          <div className="small text-muted mt-1" aria-live="off">
            {formulario.nota.length}/{LARGO_MAXIMO_DE_NOTA_DE_CONCILIACION}
          </div>
          {errorDe('nota') && <div className="text-danger small mt-1">{errorDe('nota')}</div>}
        </div>

        <div className="form-check mb-1">
          <input
            id="conciliacion-entiendo"
            className="form-check-input"
            type="checkbox"
            checked={entiendo}
            aria-describedby={idAviso}
            onChange={(e) => setEntiendo(e.target.checked)}
          />
          <label className="form-check-label" htmlFor="conciliacion-entiendo">
            Entiendo que no se puede deshacer
          </label>
        </div>
      </fieldset>
    </Modal>
  )
}
