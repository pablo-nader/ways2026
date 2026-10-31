import { useEffect, useState } from 'react'
import { ErrorApi } from '../../api/cliente'
import { clienteDePersonal, FILTROS_DE_PERSONAL_VACIOS, TAMANIO_MAXIMO_DE_PAGINA } from '../../api/personal'
import type { PagoDeAdministracionListado, PersonalListado } from '../../api/tipos'
import { Modal } from '../../componentes/Modal'
import { formatearImporte } from '../../formato/importes'

type Props = {
  pago: PagoDeAdministracionListado
  /** `true` mientras la escritura (y su refresco) están en vuelo: todo el diálogo queda inerte. */
  guardando: boolean
  /** Rechazo cierto del servidor, dueño: la pantalla. */
  error: string
  onConfirmar: (idPersonal: number) => void
  onCancelar: () => void
}

/**
 * Reasignación de un pago de sueldo a otro empleado. Carga los empleados por su cuenta (lectura con
 * token propio: una respuesta de un montaje anterior no pisa la actual) y entrega solo el id elegido;
 * la escritura y su manejo de errores son de la pantalla.
 */
export function ModalDeReasignacionDePago({ pago, guardando, error, onConfirmar, onCancelar }: Props) {
  const [personal, setPersonal] = useState<PersonalListado[] | null>(null)
  const [errorDeCarga, setErrorDeCarga] = useState('')
  const [elegido, setElegido] = useState<number | ''>('')

  useEffect(() => {
    let vigente = true
    clienteDePersonal
      .listarGrilla({ filtros: FILTROS_DE_PERSONAL_VACIOS, orden: null, pagina: 1, tamanio: TAMANIO_MAXIMO_DE_PAGINA })
      .then((respuesta) => {
        if (vigente) setPersonal([...respuesta.items].sort((a, b) => a.nombre.localeCompare(b.nombre, 'es')))
      })
      .catch((e: unknown) => {
        if (vigente) setErrorDeCarga(e instanceof ErrorApi ? e.message : 'No se pudo cargar el personal.')
      })
    return () => {
      vigente = false
    }
  }, [])

  function confirmar() {
    if (guardando || elegido === '') return
    onConfirmar(elegido)
  }

  const pie = (
    <>
      <button type="button" className="btn btn-outline-secondary" onClick={onCancelar} disabled={guardando}>
        Cancelar
      </button>
      <button type="button" className="btn btn-primary" onClick={confirmar} disabled={guardando || elegido === ''}>
        {guardando ? 'Reasignando…' : 'Reasignar'}
      </button>
    </>
  )

  return (
    <Modal titulo="Reasignar pago" ocupado={guardando} pie={pie} onCerrar={onCancelar}>
      {(error || errorDeCarga) && <div className="alert alert-danger py-2 px-3 small">{error || errorDeCarga}</div>}

      <fieldset disabled={guardando} className="border-0 p-0 m-0">
        <dl className="row small mb-3">
          <dt className="col-4">Fecha</dt>
          <dd className="col-8">{pago.fecha.slice(0, 10)}</dd>
          <dt className="col-4">Concepto</dt>
          <dd className="col-8">{pago.concepto}</dd>
          <dt className="col-4">Importe</dt>
          <dd className="col-8">{formatearImporte(pago.importe, { simbolo: true })}</dd>
        </dl>

        <p className="small text-muted">
          El pago pasa a la cuenta del empleado que elijas y sale de la cuenta del actual. El concepto se actualiza con el
          nombre del nuevo empleado.
        </p>

        <label className="form-label" htmlFor="reasignacion-personal">
          Empleado
        </label>
        <select
          id="reasignacion-personal"
          className="form-select"
          value={elegido}
          onChange={(e) => setElegido(e.target.value === '' ? '' : Number(e.target.value))}
        >
          <option value="">{personal === null ? 'Cargando…' : 'Elegí un empleado'}</option>
          {(personal ?? []).map((p) => (
            <option key={p.id} value={p.id}>
              {p.nombre}
            </option>
          ))}
        </select>
      </fieldset>
    </Modal>
  )
}
