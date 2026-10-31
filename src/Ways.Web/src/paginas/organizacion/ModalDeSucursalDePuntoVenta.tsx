import type { SucursalListado } from '../../api/tipos'
import { ErrorDeFormulario } from '../../componentes/ErrorDeFormulario'
import { Modal } from '../../componentes/Modal'
import type { FormularioDeSucursalDePuntoVenta } from './puntoVentaFormulario'

const ID_FORMULARIO_SUCURSAL = 'formulario-sucursal-punto-venta'

type Props = {
  valor: FormularioDeSucursalDePuntoVenta
  /** Sucursales ofrecidas (solo las de la empresa del punto de venta); `null` mientras la lectura no aterrizó. */
  sucursales: SucursalListado[] | null
  errorSucursales: string
  /** Hay una escritura en vuelo: cambia el rótulo del botón. */
  guardando: boolean
  /** Deshabilita los campos, los botones del pie y el cierre del modal; el estado lo decide la pantalla. */
  bloqueado: boolean
  error: string
  focoDeReserva: React.RefObject<HTMLElement | null>
  onCambio: (f: FormularioDeSucursalDePuntoVenta) => void
  onGuardar: () => void
  onCancelar: () => void
}

/** Mover un punto de venta a otra sucursal de su empresa: el stock no viaja con la caja, queda en la sucursal. */
export function ModalDeSucursalDePuntoVenta({
  valor,
  sucursales,
  errorSucursales,
  guardando,
  bloqueado,
  error,
  focoDeReserva,
  onCambio,
  onGuardar,
  onCancelar,
}: Props) {
  const delaEmpresa = (sucursales ?? []).filter((s) => s.idEmpresa === valor.idEmpresa)
  const sinCambio = valor.idSucursal === '' || Number(valor.idSucursal) === valor.idSucursalActual

  const pie = (
    <>
      <button type="button" className="btn btn-outline-secondary" onClick={onCancelar} disabled={bloqueado}>
        Cancelar
      </button>
      <button
        type="submit"
        form={ID_FORMULARIO_SUCURSAL}
        className="btn btn-success"
        disabled={bloqueado || sucursales === null || sinCambio}
      >
        {guardando ? 'Guardando…' : 'Guardar'}
      </button>
    </>
  )

  return (
    <Modal
      titulo={`Sucursal de ${valor.nombre}`}
      tamano="lg"
      ocupado={bloqueado}
      focoDeReserva={focoDeReserva}
      pie={pie}
      onCerrar={onCancelar}
    >
      <ErrorDeFormulario mensaje={error} />
      <form
        id={ID_FORMULARIO_SUCURSAL}
        autoComplete="off"
        onSubmit={(e) => {
          e.preventDefault()
          onGuardar()
        }}
      >
        <fieldset disabled={bloqueado} className="row g-3 border-0 p-0 m-0">
          <div className="col-12">
            <p className="text-muted mb-0">
              El stock pertenece a la sucursal, no a la caja: al moverla, esta caja pasa a vender contra el stock de la
              sucursal nueva. Si era la última caja de su sucursal y esa sucursal tiene stock o movimientos, no se puede
              mover.
            </p>
          </div>
          {errorSucursales && <div className="col-12 alert alert-danger mb-0">{errorSucursales}</div>}
          <div className="col-md-6">
            <label className="form-label" htmlFor="pv-sucursal">
              Sucursal
            </label>
            <select
              id="pv-sucursal"
              className="form-select"
              value={valor.idSucursal}
              disabled={sucursales === null}
              onChange={(e) => onCambio({ ...valor, idSucursal: e.target.value })}
            >
              {sucursales === null && !errorSucursales && <option value="">Cargando…</option>}
              {delaEmpresa.map((s) => (
                <option key={s.id} value={String(s.id)}>
                  {s.nombre}
                </option>
              ))}
            </select>
          </div>
        </fieldset>
      </form>
    </Modal>
  )
}
