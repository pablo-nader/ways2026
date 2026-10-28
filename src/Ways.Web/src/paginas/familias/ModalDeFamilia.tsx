import { ErrorDeFormulario } from '../../componentes/ErrorDeFormulario'
import { Modal } from '../../componentes/Modal'

export type FormularioDeFamilia = { id: number; nombre: string; activo: boolean }

const ID_FORMULARIO_FAMILIA = 'formulario-familia'

type Props = {
  valor: FormularioDeFamilia
  guardando: boolean
  error: string
  focoDeReserva: React.RefObject<HTMLElement | null>
  onCambio: (f: FormularioDeFamilia) => void
  onGuardar: () => void
  onCancelar: () => void
}

/** Renombrado y activación de una familia en un modal: mientras guarda queda inerte entero (campos, pie y cierre). */
export function ModalDeFamilia({ valor, guardando, error, focoDeReserva, onCambio, onGuardar, onCancelar }: Props) {
  const pie = (
    <>
      <button type="button" className="btn btn-outline-secondary" onClick={onCancelar} disabled={guardando}>
        Cancelar
      </button>
      <button type="submit" form={ID_FORMULARIO_FAMILIA} className="btn btn-success" disabled={guardando}>
        {guardando ? 'Guardando…' : 'Guardar'}
      </button>
    </>
  )

  return (
    <Modal titulo={`Editando familia ${valor.id}`} ocupado={guardando} focoDeReserva={focoDeReserva} pie={pie} onCerrar={onCancelar}>
      <ErrorDeFormulario mensaje={error} />
      <form
        id={ID_FORMULARIO_FAMILIA}
        autoComplete="off"
        onSubmit={(e) => {
          e.preventDefault()
          onGuardar()
        }}
      >
        <fieldset disabled={guardando} className="row g-3 border-0 p-0 m-0">
          <div className="col-md-8">
            <label className="form-label" htmlFor="ff-nombre">
              Nombre
            </label>
            <input
              id="ff-nombre"
              className="form-control"
              maxLength={150}
              value={valor.nombre}
              onChange={(e) => onCambio({ ...valor, nombre: e.target.value })}
              required
            />
          </div>

          <div className="col-md-4">
            <label className="form-label" htmlFor="ff-estado">
              Estado
            </label>
            <select
              id="ff-estado"
              className="form-select"
              value={valor.activo ? 'activa' : 'inactiva'}
              aria-describedby="ff-estado-ayuda"
              onChange={(e) => onCambio({ ...valor, activo: e.target.value === 'activa' })}
            >
              <option value="activa">Activa</option>
              <option value="inactiva">Inactiva</option>
            </select>
            <div id="ff-estado-ayuda" className="form-text">
              Una familia inactiva no admite artículos nuevos.
            </div>
          </div>
        </fieldset>
      </form>
    </Modal>
  )
}
