import type { SucursalListado } from '../../api/tipos'
import { ErrorDeFormulario } from '../../componentes/ErrorDeFormulario'
import { Modal } from '../../componentes/Modal'

const ID_FORMULARIO_FUSION = 'formulario-fusion-sucursal'

export type FormularioDeFusion = {
  origen: SucursalListado
  /** Id de la sucursal destino como texto del `<select>`; `''` mientras no se eligió. */
  idDestino: string
  confirmado: boolean
}

type Props = {
  valor: FormularioDeFusion
  /** Todas las sucursales del listado: el modal ofrece solo las de la empresa del origen, sin el origen. */
  sucursales: SucursalListado[]
  /** Hay una escritura en vuelo: cambia el rótulo del botón. */
  guardando: boolean
  /** Deshabilita los campos, los botones del pie y el cierre del modal; el estado lo decide la pantalla. */
  bloqueado: boolean
  error: string
  focoDeReserva: React.RefObject<HTMLElement | null>
  onCambio: (f: FormularioDeFusion) => void
  onConfirmar: () => void
  onCancelar: () => void
}

/** Destinos posibles de una fusión: las otras sucursales de la empresa del origen. */
function destinosDeFusion(origen: SucursalListado, sucursales: SucursalListado[]): SucursalListado[] {
  return sucursales.filter((s) => s.idEmpresa === origen.idEmpresa && s.id !== origen.id)
}

/** Fusionar una sucursal en otra de su empresa: las cajas y el stock del origen pasan al destino y el origen se elimina. */
export function ModalDeFusionDeSucursal({
  valor,
  sucursales,
  guardando,
  bloqueado,
  error,
  focoDeReserva,
  onCambio,
  onConfirmar,
  onCancelar,
}: Props) {
  const destinos = destinosDeFusion(valor.origen, sucursales)
  const destino = destinos.find((s) => String(s.id) === valor.idDestino)
  const origen = valor.origen.nombre

  const pie = (
    <>
      <button type="button" className="btn btn-outline-secondary" onClick={onCancelar} disabled={bloqueado}>
        Cancelar
      </button>
      <button
        type="submit"
        form={ID_FORMULARIO_FUSION}
        className="btn btn-danger"
        disabled={bloqueado || destino === undefined || !valor.confirmado}
      >
        {guardando ? 'Fusionando…' : 'Fusionar'}
      </button>
    </>
  )

  return (
    <Modal
      titulo={`Fusionar "${origen}"`}
      tamano="lg"
      ocupado={bloqueado}
      focoDeReserva={focoDeReserva}
      pie={pie}
      onCerrar={onCancelar}
    >
      <ErrorDeFormulario mensaje={error} />
      <form
        id={ID_FORMULARIO_FUSION}
        autoComplete="off"
        onSubmit={(e) => {
          e.preventDefault()
          onConfirmar()
        }}
      >
        <fieldset disabled={bloqueado} className="row g-3 border-0 p-0 m-0">
          <div className="col-12">
            <p className="text-muted mb-0">
              Para dos cajas de un mismo local que quedaron con el stock separado. Las cajas de &quot;{origen}&quot; y todo
              su stock (incluidos lotes y saldos negativos) pasan a la sucursal destino, y &quot;{origen}&quot; se elimina.
              Los mínimos y puntos de reposición del destino se conservan; donde el destino no tiene, se toman los del origen.
            </p>
          </div>
          {destinos.length === 0 && (
            <div className="col-12 alert alert-warning mb-0">
              No hay otra sucursal de la misma empresa para recibir esta.
            </div>
          )}
          <div className="col-md-6">
            <label className="form-label" htmlFor="fusion-destino">
              Sucursal destino
            </label>
            <select
              id="fusion-destino"
              className="form-select"
              value={valor.idDestino}
              onChange={(e) => onCambio({ ...valor, idDestino: e.target.value })}
            >
              <option value="">Elegir…</option>
              {destinos.map((s) => (
                <option key={s.id} value={String(s.id)}>
                  {s.nombre}
                </option>
              ))}
            </select>
          </div>
          <div className="col-12">
            <div className="form-check">
              <input
                id="fusion-confirmacion"
                type="checkbox"
                className="form-check-input"
                checked={valor.confirmado}
                onChange={(e) => onCambio({ ...valor, confirmado: e.target.checked })}
              />
              <label className="form-check-label" htmlFor="fusion-confirmacion">
                {destino
                  ? `Confirmo que las cajas y el stock de "${origen}" pasan a "${destino.nombre}" y que "${origen}" se elimina. No se puede deshacer.`
                  : `Confirmo que las cajas y el stock de "${origen}" pasan a la sucursal destino y que "${origen}" se elimina. No se puede deshacer.`}
              </label>
            </div>
          </div>
        </fieldset>
      </form>
    </Modal>
  )
}
