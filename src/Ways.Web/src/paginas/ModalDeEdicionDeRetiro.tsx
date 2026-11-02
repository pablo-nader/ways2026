import { useId, useRef, useState } from 'react'
import { clienteDeCaja } from '../api/caja'
import type { FilaDeRetiro } from '../api/tipos'
import { CampoImporte } from '../componentes/CampoImporte'
import { ErrorDeFormulario } from '../componentes/ErrorDeFormulario'
import { Modal } from '../componentes/Modal'
import { fechaHoraATexto } from '../formato/fechas'
import { filaQuedoObsoleta, mensajeDeErrorDeRetiro, validarRetiro } from './utilidadesRetirosDeAdministracion'

type Props = {
  retiro: FilaDeRetiro
  /** Se llama una sola vez, con el retiro ya corregido en el servidor; la pantalla avisa y refresca su listado. */
  onGuardado: (retiro: FilaDeRetiro) => void
  /** El servidor dijo que la fila quedó vieja (404 o 409): la pantalla vuelve a pedir el listado. */
  onObsoleto: () => void
  onCerrar: () => void
}

/**
 * Corrección del importe y del motivo de un retiro (`PUT /api/caja/retiros/{id}`, solo Admin). Las validaciones locales son las del
 * alta del retiro; el servidor vuelve a validar todo. La escritura es de este modal: queda inerte mientras está en vuelo y una
 * guarda de ref evita el doble envío en el mismo tick.
 */
export function ModalDeEdicionDeRetiro({ retiro, onGuardado, onObsoleto, onCerrar }: Props) {
  const [importe, setImporte] = useState<number | null>(retiro.importe)
  const [motivo, setMotivo] = useState(retiro.motivo)
  const [guardando, setGuardando] = useState(false)
  const [errorDelServidor, setErrorDelServidor] = useState('')
  const guardandoRef = useRef(false)
  const idImporte = useId()
  const idMotivo = useId()

  const errores = validarRetiro(importe, motivo)
  const sinCambios = importe === retiro.importe && motivo.trim() === retiro.motivo
  const puedeGuardar = errores.importe === '' && errores.motivo === '' && !sinCambios && !guardando

  async function guardar() {
    if (guardandoRef.current || importe === null) return
    guardandoRef.current = true
    setGuardando(true)
    setErrorDelServidor('')
    try {
      const corregido = await clienteDeCaja.editarRetiro(retiro.id, { importe, motivo: motivo.trim() })
      onGuardado(corregido)
    } catch (e) {
      setErrorDelServidor(mensajeDeErrorDeRetiro(e, 'editar'))
      if (filaQuedoObsoleta(e)) onObsoleto()
    } finally {
      guardandoRef.current = false
      setGuardando(false)
    }
  }

  const pie = (
    <>
      <button type="button" className="btn btn-outline-secondary" disabled={guardando} onClick={onCerrar}>
        Cancelar
      </button>
      <button type="button" className="btn btn-primary" disabled={!puedeGuardar} onClick={() => void guardar()}>
        {guardando ? 'Guardando…' : 'Guardar cambio'}
      </button>
    </>
  )

  return (
    <Modal titulo="Editar retiro" tamano="sm" ocupado={guardando} restaurarFoco={false} etiquetaCerrar="Cerrar edición del retiro" pie={pie} onCerrar={onCerrar}>
      <p className="small text-muted">
        Retiro del {fechaHoraATexto(retiro.fecha)} en el turno {retiro.idTurnoCaja}.
        {retiro.turnoCerrado && ' El turno está cerrado: al guardar se recalcula el arqueo.'}
      </p>
      <fieldset disabled={guardando} className="border-0 p-0 m-0">
        <div className="mb-3">
          <label htmlFor={idImporte} className="form-label">
            Importe
          </label>
          <CampoImporte id={idImporte} className="form-control" valor={importe}
            onChange={(valor) => {
              setErrorDelServidor('')
              setImporte(valor)
            }} aria-invalid={errores.importe !== ''} />
          {errores.importe && <div className="form-text text-danger">{errores.importe}</div>}
        </div>
        <div className="mb-3">
          <label htmlFor={idMotivo} className="form-label">
            Motivo
          </label>
          <input
            id={idMotivo}
            type="text"
            className="form-control"
            value={motivo}
            onChange={(e) => {
              setErrorDelServidor('')
              setMotivo(e.target.value)
            }}
            aria-invalid={errores.motivo !== ''}
          />
          {errores.motivo && <div className="form-text text-danger">{errores.motivo}</div>}
        </div>
      </fieldset>
      <ErrorDeFormulario mensaje={errorDelServidor} />
    </Modal>
  )
}
