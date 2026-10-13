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
  type ModoDeAjuste,
  type ResultadoDeAjusteDeCuenta,
} from '../api/cuentasTesoreria'
import { clienteDeReportes } from '../api/reportes'
import { formatearImporte, formatearImporteConSigno } from '../formato/importes'
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
 * Ajuste manual del saldo de una cuenta de tesorería (solo Admin). Dos modos: "Tengo el saldo real" (el servidor calcula la
 * diferencia contra el saldo que tenga la cuenta al escribir; la que se muestra acá es una vista previa) y "Sumar o restar un
 * importe". El concepto es obligatorio.
 *
 * `react-async-state`: guarda de reentrancia de primera línea (ref, cubre el doble clic del mismo tick) más ventana completa
 * deshabilitada y cierre bloqueado mientras `ajustando`; por eso la respuesta propia de la escritura no lleva generación: nada
 * puede reemplazar ni reabrir lo que escribe. El ajuste no lleva clave de idempotencia, así que nunca se reintenta solo y un
 * resultado incierto (red cortada, 5xx) no deja el formulario abierto para reenviar a ciegas: se avisa al llamador.
 */
export function ModalDeAjusteDeCuenta({ cuenta, onCerrar, onAjustado, onIncierto }: Props) {
  const [formulario, setFormulario] = useState<FormularioDeAjuste>(formularioDeAjusteVacio)
  const [error, setError] = useState<ErrorDeAjuste | null>(null)
  // El saldo que llega por props sale de un listado que pudo quedar viejo: se relee al abrir y la vista previa parte de ese.
  const [saldoActual, setSaldoActual] = useState(cuenta.saldoActual)
  const [ajustando, setAjustando] = useState(false)
  const ajustandoRef = useRef(false)
  const vigenteRef = useRef(true)

  useEffect(() => {
    vigenteRef.current = true
    return () => {
      vigenteRef.current = false
    }
  }, [])

  useEffect(() => {
    let vigente = true
    clienteDeReportes
      .libroDeCuenta(cuenta.id, { desde: '', hasta: '', pagina: 1, tamanio: 1 })
      .then((libro) => {
        if (vigente) setSaldoActual(libro.saldoActual)
      })
      .catch(() => {
        // Sin lectura fresca la vista previa sigue con el saldo conocido; el servidor decide igual.
      })

    return () => {
      vigente = false
    }
  }, [cuenta.id])

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

  const importe = importeDeVistaPrevia(formulario, saldoActual)
  const saldoResultante = importe === null ? null : saldoResultanteDeVistaPrevia(saldoActual, importe)

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
        Saldo actual de la cuenta: <strong>{formatearImporte(saldoActual, { simbolo: true })}</strong>
      </div>

      <fieldset disabled={ajustando} className="border-0 p-0 m-0">
        <div className="mb-3">
          {(
            [
              ['saldoObjetivo', 'Tengo el saldo real'],
              ['importe', 'Sumar o restar un importe'],
            ] as [ModoDeAjuste, string][]
          ).map(([modo, etiqueta]) => (
            <div className="form-check" key={modo}>
              <input
                className="form-check-input"
                type="radio"
                name="modo-de-ajuste"
                id={`ajuste-modo-${modo}`}
                checked={formulario.modo === modo}
                onChange={() => cambiar({ modo })}
              />
              <label className="form-check-label" htmlFor={`ajuste-modo-${modo}`}>
                {etiqueta}
              </label>
            </div>
          ))}
        </div>

        {formulario.modo === 'saldoObjetivo' ? (
          <div className="mb-3">
            <label className="form-label" htmlFor="ajuste-saldo-objetivo">
              Saldo real de la plataforma
            </label>
            <CampoImporte
              id="ajuste-saldo-objetivo"
              className="form-control"
              admiteNegativos
              valor={formulario.saldoObjetivo}
              onChange={(valor) => cambiar({ saldoObjetivo: valor })}
            />
            {errorDe('saldoObjetivo') && <div className="text-danger small mt-1">{errorDe('saldoObjetivo')}</div>}
            {importe !== null && (
              <div className="small mt-2">
                Diferencia (vista previa): <strong>{formatearImporteConSigno(importe, { simbolo: true })}</strong>
                <div className="text-muted">
                  Es una estimación con el saldo que ves ahora: al registrar, el servidor la vuelve a calcular con el saldo
                  vigente de la cuenta.
                </div>
              </div>
            )}
          </div>
        ) : (
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
                Saldo resultante (vista previa): <strong>{formatearImporte(saldoResultante, { simbolo: true })}</strong>
              </div>
            )}
          </div>
        )}

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
