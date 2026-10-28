import type { FormEvent } from 'react'
import type { FormularioDeGastoDeAdministracion } from '../../api/gastos'
import { CATEGORIAS_GASTO } from '../../api/tipos'
import type {
  AreaListado,
  CategoriaGasto,
  EmpresaListado,
  MedioPagoListado,
  OpcionDeProveedor,
  PuntoVentaListado,
} from '../../api/tipos'
import { CampoFecha } from '../../componentes/CampoFecha'
import { CampoImporte } from '../../componentes/CampoImporte'
import { ErrorDeFormulario } from '../../componentes/ErrorDeFormulario'
import { Modal } from '../../componentes/Modal'
import { etiquetaDeProveedor } from '../articulos/helpers'

const ID_FORMULARIO_GASTO = 'formulario-gasto-admin'

type Props = {
  valor: FormularioDeGastoDeAdministracion
  /** Se pasa en lugar de un `setFormulario` crudo para que cada cambio construya desde el estado previo. */
  onCambio: (cambios: (prev: FormularioDeGastoDeAdministracion) => FormularioDeGastoDeAdministracion) => void
  onProveedorCambio: (valorCrudo: string) => void
  empresas: EmpresaListado[]
  puntosVenta: PuntoVentaListado[]
  medios: MedioPagoListado[]
  areas: AreaListado[]
  proveedores: OpcionDeProveedor[]
  fechaMaxima: string
  /** Validación local o rechazo del servidor: se rinde dentro del modal. */
  error: string
  /** Alta en vuelo: el modal queda inerte entero y no se puede cerrar. */
  guardando: boolean
  focoDeReserva: React.RefObject<HTMLElement | null>
  onRegistrar: () => void
  onCancelar: () => void
}

/** Alta de un gasto de administración (sin turno, pagado de la tesorería) en un modal. */
export function ModalDeAltaDeGasto({
  valor,
  onCambio,
  onProveedorCambio,
  empresas,
  puntosVenta,
  medios,
  areas,
  proveedores,
  fechaMaxima,
  error,
  guardando,
  focoDeReserva,
  onRegistrar,
  onCancelar,
}: Props) {
  function alEnviar(evento: FormEvent) {
    evento.preventDefault()
    onRegistrar()
  }

  const pie = (
    <>
      <button type="button" className="btn btn-outline-secondary" onClick={onCancelar} disabled={guardando}>
        Cancelar
      </button>
      <button type="submit" form={ID_FORMULARIO_GASTO} className="btn btn-primary" disabled={guardando}>
        {guardando ? 'Guardando…' : 'Registrar'}
      </button>
    </>
  )

  return (
    <Modal
      titulo="Nuevo gasto"
      tamano="lg"
      desplazable
      ocupado={guardando}
      focoDeReserva={focoDeReserva}
      pie={pie}
      onCerrar={onCancelar}
    >
      <ErrorDeFormulario mensaje={error} />
      <form id={ID_FORMULARIO_GASTO} onSubmit={alEnviar}>
        <fieldset disabled={guardando} className="row g-2 border-0 p-0 m-0">
          <div className="col-md-4">
            <label className="form-label" htmlFor="gasto-admin-fecha">
              Fecha
            </label>
            <CampoFecha
              id="gasto-admin-fecha"
              className="form-control"
              max={fechaMaxima}
              value={valor.fecha}
              onChange={(fecha) => onCambio((prev) => ({ ...prev, fecha }))}
            />
          </div>

          <div className="col-md-4">
            <label className="form-label" htmlFor="gasto-admin-empresa">
              Empresa
            </label>
            <select
              id="gasto-admin-empresa"
              className="form-select"
              value={valor.idEmpresa}
              onChange={(e) =>
                onCambio((prev) => ({
                  ...prev,
                  idEmpresa: e.target.value === '' ? '' : Number(e.target.value),
                  idPuntoVenta: '',
                }))
              }
            >
              <option value="">Elegir…</option>
              {empresas.map((emp) => (
                <option key={emp.id} value={emp.id}>
                  {emp.razonSocial}
                </option>
              ))}
            </select>
          </div>

          <div className="col-md-4">
            <label className="form-label" htmlFor="gasto-admin-pv">
              Punto de venta (opcional)
            </label>
            <select
              id="gasto-admin-pv"
              className="form-select"
              value={valor.idPuntoVenta}
              onChange={(e) =>
                onCambio((prev) => ({ ...prev, idPuntoVenta: e.target.value === '' ? '' : Number(e.target.value) }))
              }
            >
              <option value="">Sin punto de venta</option>
              {puntosVenta.map((pv) => (
                <option key={pv.id} value={pv.id}>
                  {pv.nombre}
                </option>
              ))}
            </select>
          </div>

          <div className="col-md-4">
            <label className="form-label" htmlFor="gasto-admin-categoria">
              Categoría
            </label>
            <select
              id="gasto-admin-categoria"
              className="form-select"
              value={valor.categoria}
              onChange={(e) => onCambio((prev) => ({ ...prev, categoria: e.target.value as CategoriaGasto }))}
            >
              {CATEGORIAS_GASTO.map((c) => (
                <option key={c.valor} value={c.valor}>
                  {c.etiqueta}
                </option>
              ))}
            </select>
          </div>

          <div className="col-md-4">
            <label className="form-label" htmlFor="gasto-admin-proveedor">
              Proveedor (opcional)
            </label>
            <select
              id="gasto-admin-proveedor"
              className="form-select"
              value={valor.idProveedor}
              onChange={(e) => onProveedorCambio(e.target.value)}
            >
              <option value="">Sin proveedor</option>
              {proveedores.map((p) => (
                <option key={p.id} value={p.id}>
                  {etiquetaDeProveedor(p)}
                </option>
              ))}
            </select>
          </div>

          <div className="col-md-4">
            <label className="form-label" htmlFor="gasto-admin-area">
              Área
            </label>
            <select
              id="gasto-admin-area"
              className="form-select"
              value={valor.idArea}
              onChange={(e) => onCambio((prev) => ({ ...prev, idArea: Number(e.target.value) }))}
            >
              {areas.length === 0 && (
                <option value="" disabled>
                  Sin áreas
                </option>
              )}
              {areas.map((a) => (
                <option key={a.id} value={a.id}>
                  {a.nombre}
                </option>
              ))}
            </select>
          </div>

          <div className="col-md-6">
            <label className="form-label" htmlFor="gasto-admin-concepto">
              Concepto
            </label>
            <input
              id="gasto-admin-concepto"
              type="text"
              className="form-control"
              value={valor.concepto}
              onChange={(e) => onCambio((prev) => ({ ...prev, concepto: e.target.value }))}
            />
          </div>

          <div className="col-md-6">
            <label className="form-label" htmlFor="gasto-admin-detalle">
              Detalle (opcional)
            </label>
            <input
              id="gasto-admin-detalle"
              type="text"
              className="form-control"
              value={valor.detalle}
              onChange={(e) => onCambio((prev) => ({ ...prev, detalle: e.target.value }))}
            />
          </div>

          <div className="col-md-4">
            <label className="form-label" htmlFor="gasto-admin-medio-pago">
              Medio de pago
            </label>
            <select
              id="gasto-admin-medio-pago"
              className="form-select"
              value={valor.idMedioPago}
              onChange={(e) =>
                onCambio((prev) => ({ ...prev, idMedioPago: e.target.value === '' ? '' : Number(e.target.value) }))
              }
            >
              <option value="">Elegir…</option>
              {medios.map((m) => (
                <option key={m.id} value={m.id}>
                  {m.nombre}
                </option>
              ))}
            </select>
          </div>

          <div className="col-md-4">
            <label className="form-label" htmlFor="gasto-admin-factura">
              N° de factura (opcional)
            </label>
            <input
              id="gasto-admin-factura"
              type="text"
              className="form-control"
              value={valor.numeroFactura}
              onChange={(e) => onCambio((prev) => ({ ...prev, numeroFactura: e.target.value }))}
            />
          </div>

          <div className="col-md-4">
            <label className="form-label" htmlFor="gasto-admin-importe">
              Importe
            </label>
            <CampoImporte
              id="gasto-admin-importe"
              className="form-control"
              valor={valor.importe}
              onChange={(importe) => onCambio((prev) => ({ ...prev, importe }))}
            />
          </div>
        </fieldset>
      </form>
    </Modal>
  )
}
