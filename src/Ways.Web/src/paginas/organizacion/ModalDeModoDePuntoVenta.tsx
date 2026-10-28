import { MODOS_PUNTO_VENTA } from '../../api/tipos'
import type { ModoPuntoVenta } from '../../api/tipos'
import { ErrorDeFormulario } from '../../componentes/ErrorDeFormulario'
import { Modal } from '../../componentes/Modal'
import type { FormularioDeModoDePuntoVenta } from './puntoVentaFormulario'

const ID_FORMULARIO_MODO = 'formulario-modo-punto-venta'

type Props = {
  valor: FormularioDeModoDePuntoVenta
  /** Hay una escritura en vuelo: cambia el rótulo del botón. */
  guardando: boolean
  /** Todo el modal queda inerte: la escritura en vuelo o una confirmación de baja abierta encima. */
  bloqueado: boolean
  error: string
  focoDeReserva: React.RefObject<HTMLElement | null>
  onCambio: (f: FormularioDeModoDePuntoVenta) => void
  onGuardar: () => void
  onCancelar: () => void
}

/** Cambio de modo de un punto de venta en un modal: tiene su propio endpoint y precondiciones. */
export function ModalDeModoDePuntoVenta({
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
      <button type="submit" form={ID_FORMULARIO_MODO} className="btn btn-success" disabled={bloqueado}>
        {guardando ? 'Guardando…' : 'Guardar'}
      </button>
    </>
  )

  return (
    <Modal
      titulo={`Modo de ${valor.nombre}`}
      tamano="lg"
      ocupado={bloqueado}
      focoDeReserva={focoDeReserva}
      pie={pie}
      onCerrar={onCancelar}
    >
      <ErrorDeFormulario mensaje={error} />
      <form
        id={ID_FORMULARIO_MODO}
        autoComplete="off"
        onSubmit={(e) => {
          e.preventDefault()
          onGuardar()
        }}
      >
        <fieldset disabled={bloqueado} className="row g-3 border-0 p-0 m-0">
          <div className="col-12">
            <p className="text-muted mb-0">
              Escritorio exige a lo sumo un dispositivo vinculado y solo ese dispositivo puede vender contra este
              punto de venta; Web exige sesión sin dispositivo. Si tiene un dispositivo vinculado, revocalo primero
              desde la pantalla de Dispositivos.
            </p>
          </div>
          <div className="col-md-4">
            <label className="form-label" htmlFor="pv-modo">
              Modo
            </label>
            <select
              id="pv-modo"
              className="form-select"
              value={valor.modo}
              onChange={(e) => onCambio({ ...valor, modo: e.target.value as ModoPuntoVenta })}
            >
              {MODOS_PUNTO_VENTA.map((modo) => (
                <option key={modo} value={modo}>
                  {modo}
                </option>
              ))}
            </select>
          </div>
        </fieldset>
      </form>
    </Modal>
  )
}
