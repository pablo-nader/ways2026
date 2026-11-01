import { useEffect, useRef, useState } from 'react'
import {
  aSolicitudDeAjusteDeEfectivo,
  clienteDeTesoreria,
  errorAlAjustarEfectivo,
  esResultadoInciertoDeEfectivo,
  formularioDeAjusteDeEfectivoVacio,
  validarAjusteDeEfectivo,
  type ErrorDeAjusteDeEfectivo,
  type FormularioDeAjusteDeEfectivo,
  type ResultadoDeAjusteDeEfectivo,
} from '../api/tesoreria'
import { CampoImporte } from './CampoImporte'
import { Modal } from './Modal'

type Props = {
  idEmpresa: number
  onCerrar: () => void
  /** Se llama una sola vez con el ajuste ya registrado; quien abrió el modal lo cierra y recarga. */
  onAjustado: (resultado: ResultadoDeAjusteDeEfectivo) => void
  /** Se llama una sola vez cuando NO se sabe si el ajuste se escribió: quien abrió el modal lo cierra, recarga y avisa. */
  onIncierto: () => void
}

/**
 * Ajuste manual del efectivo de tesorería (solo Admin): importe con signo y concepto obligatorio.
 *
 * `react-async-state`: guarda de reentrancia de primera línea (ref) más ventana completa deshabilitada y cierre bloqueado mientras
 * `ajustando`; por eso la respuesta propia no lleva generación. No hay clave de idempotencia: un resultado incierto nunca deja el
 * formulario abierto para reenviar a ciegas, se avisa al llamador.
 */
export function ModalDeAjusteDeEfectivo({ idEmpresa, onCerrar, onAjustado, onIncierto }: Props) {
  const [formulario, setFormulario] = useState<FormularioDeAjusteDeEfectivo>(formularioDeAjusteDeEfectivoVacio)
  const [error, setError] = useState<ErrorDeAjusteDeEfectivo | null>(null)
  const [ajustando, setAjustando] = useState(false)
  const ajustandoRef = useRef(false)
  const vigenteRef = useRef(true)

  useEffect(() => {
    vigenteRef.current = true
    return () => {
      vigenteRef.current = false
    }
  }, [])

  function cambiar(cambios: Partial<FormularioDeAjusteDeEfectivo>) {
    setFormulario((previo) => ({ ...previo, ...cambios }))
    setError(null)
  }

  async function ajustar() {
    if (ajustandoRef.current) return

    const rechazo = validarAjusteDeEfectivo(formulario)
    if (rechazo) {
      setError(rechazo)
      return
    }

    ajustandoRef.current = true
    setAjustando(true)
    setError(null)

    try {
      const resultado = await clienteDeTesoreria.ajustarEfectivo(aSolicitudDeAjusteDeEfectivo(idEmpresa, formulario))
      if (vigenteRef.current) onAjustado(resultado)
    } catch (e) {
      if (!vigenteRef.current) return

      if (esResultadoInciertoDeEfectivo(e)) {
        onIncierto()
        return
      }

      ajustandoRef.current = false
      setAjustando(false)
      setError(errorAlAjustarEfectivo(e))
    }
  }

  const errorDe = (campo: ErrorDeAjusteDeEfectivo['campo']) => (error?.campo === campo ? error.mensaje : null)

  const pie = (
    <>
      <button type="button" className="btn btn-outline-secondary" onClick={onCerrar} disabled={ajustando}>
        Cancelar
      </button>
      <button type="button" className="btn btn-primary" onClick={() => void ajustar()} disabled={ajustando}>
        {ajustando ? 'Registrando…' : 'Registrar ajuste'}
      </button>
    </>
  )

  return (
    <Modal titulo="Ajustar efectivo" ocupado={ajustando} pie={pie} onCerrar={onCerrar}>
      {errorDe('general') && <div className="alert alert-danger py-2 px-3 small">{errorDe('general')}</div>}

      <fieldset disabled={ajustando} className="border-0 p-0 m-0">
        <div className="mb-3">
          <label className="form-label" htmlFor="ajuste-efectivo-importe">
            Importe (positivo suma, negativo resta)
          </label>
          <CampoImporte
            id="ajuste-efectivo-importe"
            className="form-control"
            admiteNegativos
            valor={formulario.importe}
            onChange={(valor) => cambiar({ importe: valor })}
          />
          {errorDe('importe') && <div className="text-danger small mt-1">{errorDe('importe')}</div>}
        </div>

        <div className="mb-1">
          <label className="form-label" htmlFor="ajuste-efectivo-concepto">
            Concepto
          </label>
          <input
            id="ajuste-efectivo-concepto"
            className="form-control"
            type="text"
            value={formulario.concepto}
            onChange={(e) => cambiar({ concepto: e.target.value })}
          />
          {errorDe('concepto') && <div className="text-danger small mt-1">{errorDe('concepto')}</div>}
        </div>
      </fieldset>
    </Modal>
  )
}
