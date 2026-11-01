import { useEffect, useRef, useState } from 'react'
import {
  aSolicitudDeAjuste,
  clienteDeCuentasTesoreria,
  errorAlAjustar,
  esResultadoIncierto,
  formularioDeAjusteVacio,
  importeDeVistaPrevia,
  saldoResultanteDeVistaPrevia,
  validarAjuste,
  type ErrorDeAjuste,
  type FormularioDeAjuste,
  type ResultadoDeAjusteDeCuenta,
} from '../api/cuentasTesoreria'
import { formatearImporte } from '../formato/importes'
import { CampoImporte } from './CampoImporte'
import { Modal } from './Modal'

type Props = {
  cuenta: { id: number; nombre: string; saldoActual: number }
  onCerrar: () => void
  /** Se llama una sola vez con el ajuste ya registrado; quien abrió el modal lo cierra y refresca. */
  onAjustado: (resultado: ResultadoDeAjusteDeCuenta) => void
  /** Se llama una sola vez cuando NO se sabe si el ajuste se escribió: quien abrió el modal lo cierra, relee y avisa. */
  onIncierto: () => void
}

/**
 * Ajuste manual del saldo de una cuenta de tesorería (solo Admin). Solo ofrece el importe con signo: el saldo de la tarjeta del
 * libro se calcula por fuentes y no coincide con la cadena de la cuenta, así que nunca se envía `saldoObjetivo`. El concepto es
 * obligatorio y el saldo resultante que se muestra es una estimación.
 *
 * `react-async-state`: guarda de reentrancia de primera línea (ref, cubre el doble clic del mismo tick) más ventana completa
 * deshabilitada y cierre bloqueado mientras `ajustando`; por eso la respuesta propia de la escritura no lleva generación: nada
 * puede reemplazar ni reabrir lo que escribe. El ajuste no lleva clave de idempotencia, así que nunca se reintenta solo y un
 * resultado incierto (red cortada, 5xx) no deja el formulario abierto para reenviar a ciegas: se avisa al llamador.
 */
export function ModalDeAjusteDeCuenta({ cuenta, onCerrar, onAjustado, onIncierto }: Props) {
  const [formulario, setFormulario] = useState<FormularioDeAjuste>(() => ({
    ...formularioDeAjusteVacio(),
    modo: 'importe',
  }))
  const [error, setError] = useState<ErrorDeAjuste | null>(null)
  const [ajustando, setAjustando] = useState(false)
  const ajustandoRef = useRef(false)
  const vigenteRef = useRef(true)

  useEffect(() => {
    vigenteRef.current = true
    return () => {
      vigenteRef.current = false
    }
  }, [])

  function cambiar(cambios: Partial<FormularioDeAjuste>) {
    setFormulario((previo) => ({ ...previo, ...cambios }))
    setError(null)
  }

  async function ajustar() {
    if (ajustandoRef.current) return

    const rechazo = validarAjuste(formulario)
    if (rechazo) {
      setError(rechazo)
      return
    }

    ajustandoRef.current = true
    setAjustando(true)
    setError(null)

    try {
      const resultado = await clienteDeCuentasTesoreria.ajustar(cuenta.id, aSolicitudDeAjuste(formulario))
      if (vigenteRef.current) onAjustado(resultado)
    } catch (e) {
      if (!vigenteRef.current) return

      if (esResultadoIncierto(e)) {
        onIncierto()
        return
      }

      ajustandoRef.current = false
      setAjustando(false)
      setError(errorAlAjustar(e))
    }
  }

  const importe = importeDeVistaPrevia(formulario, cuenta.saldoActual)
  const saldoResultante = importe === null ? null : saldoResultanteDeVistaPrevia(cuenta.saldoActual, importe)

  const errorDe = (campo: ErrorDeAjuste['campo']) => (error?.campo === campo ? error.mensaje : null)

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
    <Modal titulo={`Ajustar saldo de ${cuenta.nombre}`} ocupado={ajustando} pie={pie} onCerrar={onCerrar}>
      {errorDe('general') && <div className="alert alert-danger py-2 px-3 small">{errorDe('general')}</div>}

      <div className="small text-muted mb-3">
        Saldo actual de la cuenta: <strong>{formatearImporte(cuenta.saldoActual, { simbolo: true })}</strong>
      </div>

      <fieldset disabled={ajustando} className="border-0 p-0 m-0">
        <div className="mb-3">
          <label className="form-label" htmlFor="ajuste-importe">
            Importe (positivo suma, negativo resta)
          </label>
          <CampoImporte
            id="ajuste-importe"
            className="form-control"
            admiteNegativos
            valor={formulario.importe}
            onChange={(valor) => cambiar({ importe: valor })}
          />
          {errorDe('importe') && <div className="text-danger small mt-1">{errorDe('importe')}</div>}
          {saldoResultante !== null && importe !== null && importe !== 0 && (
            <div className="small mt-2">
              Saldo resultante (estimación): <strong>{formatearImporte(saldoResultante, { simbolo: true })}</strong>
              <div className="text-muted">Estimación: el sistema recalcula el saldo al guardar.</div>
            </div>
          )}
        </div>

        <div className="mb-1">
          <label className="form-label" htmlFor="ajuste-concepto">
            Concepto
          </label>
          <input
            id="ajuste-concepto"
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
