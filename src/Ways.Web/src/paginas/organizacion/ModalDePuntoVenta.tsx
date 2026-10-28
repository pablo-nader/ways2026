import { ErrorDeFormulario } from '../../componentes/ErrorDeFormulario'
import { Modal } from '../../componentes/Modal'
import type { FormularioDePuntoVenta } from './puntoVentaFormulario'

const ID_FORMULARIO_PUNTO_VENTA = 'formulario-punto-venta'

type Props = {
  valor: FormularioDePuntoVenta
  /** Hay una escritura en vuelo: cambia el rótulo del botón. */
  guardando: boolean
  /** Deshabilita los campos, los botones del pie y el cierre del modal; el estado lo decide la pantalla. */
  bloqueado: boolean
  error: string
  focoDeReserva: React.RefObject<HTMLElement | null>
  onCambio: (f: FormularioDePuntoVenta) => void
  onGuardar: () => void
  onCancelar: () => void
}

/** Edición descriptiva de un punto de venta en un modal. La empresa y el modo no se editan acá. */
export function ModalDePuntoVenta({
  valor,
  guardando,
  bloqueado,
  error,
  focoDeReserva,
  onCambio,
  onGuardar,
  onCancelar,
}: Props) {
  const pie = (
    <>
      <button type="button" className="btn btn-outline-secondary" onClick={onCancelar} disabled={bloqueado}>
        Cancelar
      </button>
      <button type="submit" form={ID_FORMULARIO_PUNTO_VENTA} className="btn btn-success" disabled={bloqueado}>
        {guardando ? 'Guardando…' : 'Guardar'}
      </button>
    </>
  )

  return (
    <Modal
      titulo={`Editando punto de venta ${valor.id}`}
      tamano="lg"
      desplazable
      ocupado={bloqueado}
      focoDeReserva={focoDeReserva}
      pie={pie}
      onCerrar={onCancelar}
    >
      <ErrorDeFormulario mensaje={error} />
      <form
        id={ID_FORMULARIO_PUNTO_VENTA}
        autoComplete="off"
        onSubmit={(e) => {
          e.preventDefault()
          onGuardar()
        }}
      >
        <fieldset disabled={bloqueado} className="row g-3 border-0 p-0 m-0">
          <div className="col-md-4">
            <label className="form-label" htmlFor="pv-nombre">
              Nombre
            </label>
            <input
              id="pv-nombre"
              className="form-control"
              maxLength={150}
              value={valor.nombre}
              onChange={(e) => onCambio({ ...valor, nombre: e.target.value })}
              required
            />
          </div>
          <div className="col-md-8">
            <label className="form-label" htmlFor="pv-domicilio">
              Domicilio
            </label>
            <input
              id="pv-domicilio"
              className="form-control"
              maxLength={255}
              value={valor.domicilio}
              onChange={(e) => onCambio({ ...valor, domicilio: e.target.value })}
            />
          </div>
          <div className="col-md-4">
            <label className="form-label" htmlFor="pv-horario">
              Horario
            </label>
            <input
              id="pv-horario"
              className="form-control"
              maxLength={255}
              value={valor.horario}
              onChange={(e) => onCambio({ ...valor, horario: e.target.value })}
            />
          </div>
          <div className="col-md-4">
            <label className="form-label" htmlFor="pv-whatsapp">
              WhatsApp
            </label>
            <input
              id="pv-whatsapp"
              className="form-control"
              maxLength={30}
              value={valor.whatsapp}
              onChange={(e) => onCambio({ ...valor, whatsapp: e.target.value })}
            />
          </div>
          <div className="col-md-4">
            <label className="form-label" htmlFor="pv-instagram">
              Instagram
            </label>
            <input
              id="pv-instagram"
              className="form-control"
              maxLength={150}
              value={valor.instagram}
              onChange={(e) => onCambio({ ...valor, instagram: e.target.value })}
            />
          </div>
          <div className="col-md-6">
            <label className="form-label" htmlFor="pv-facebook">
              Facebook
            </label>
            <input
              id="pv-facebook"
              className="form-control"
              maxLength={150}
              value={valor.facebook}
              onChange={(e) => onCambio({ ...valor, facebook: e.target.value })}
            />
          </div>
          <div className="col-md-6">
            <label className="form-label" htmlFor="pv-web">
              Sitio web
            </label>
            <input
              id="pv-web"
              className="form-control"
              maxLength={255}
              value={valor.web}
              onChange={(e) => onCambio({ ...valor, web: e.target.value })}
            />
          </div>
          <div className="col-12">
            <div className="form-check">
              <input
                id="pv-envio-por-defecto"
                type="checkbox"
                className="form-check-input"
                checked={valor.envioPorDefecto}
                onChange={(e) => onCambio({ ...valor, envioPorDefecto: e.target.checked })}
              />
              <label className="form-check-label" htmlFor="pv-envio-por-defecto">
                Envío por defecto en el POS
              </label>
            </div>
          </div>
        </fieldset>
      </form>
    </Modal>
  )
}
