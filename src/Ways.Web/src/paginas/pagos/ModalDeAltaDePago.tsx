import type { FormEvent } from 'react'
import type { FormularioDePagoDeAdministracion } from '../../api/registroDePagos'
import { CATEGORIAS_PAGO } from '../../api/tipos'
import type {
  AreaListado,
  CategoriaPago,
  EmpresaListado,
  MedioPagoListado,
  OpcionDeProveedor,
  PuntoVentaListado,
} from '../../api/tipos'
import { CampoFecha } from '../../componentes/CampoFecha'
import { EditorDeLineasDePago } from '../../componentes/EditorDeLineasDePago'
import { ErrorDeFormulario } from '../../componentes/ErrorDeFormulario'
import { Modal } from '../../componentes/Modal'
import { etiquetaDeProveedor } from '../articulos/helpers'

const ID_FORMULARIO_PAGO = 'formulario-pago-admin'

type Props = {
  valor: FormularioDePagoDeAdministracion
  /** Se pasa en lugar de un `setFormulario` crudo para que cada cambio construya desde el estado previo. */
  onCambio: (cambios: (prev: FormularioDePagoDeAdministracion) => FormularioDePagoDeAdministracion) => void
  onProveedorCambio: (valorCrudo: string) => void
  empresas: EmpresaListado[]
  puntosVenta: PuntoVentaListado[]
  medios: MedioPagoListado[]
  areas: AreaListado[]
  proveedores: OpcionDeProveedor[]
  fechaMaxima: string
  /** Validación local o rechazo del servidor: se rinde dentro del modal. */
  error: string
  /** Cambia en cada rechazo, para que un mismo mensaje repetido se vuelva a anunciar. */
  disparoDeError: number
  /** Alta en vuelo: el modal queda inerte entero y no se puede cerrar. */
  guardando: boolean
  focoDeReserva: React.RefObject<HTMLElement | null>
  onRegistrar: () => void
  onCancelar: () => void
}

/** Alta de un pago de administración (sin turno, pagado de la tesorería) en un modal. */
export function ModalDeAltaDePago({
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
  disparoDeError,
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
      <button type="submit" form={ID_FORMULARIO_PAGO} className="btn btn-primary" disabled={guardando}>
        {guardando ? 'Guardando…' : 'Registrar'}
      </button>
    </>
  )

  return (
    <Modal
      titulo="Nuevo pago"
      tamano="lg"
      desplazable
      ocupado={guardando}
      focoDeReserva={focoDeReserva}
      pie={pie}
      onCerrar={onCancelar}
    >
      <ErrorDeFormulario mensaje={error} disparo={disparoDeError} />
      <form id={ID_FORMULARIO_PAGO} onSubmit={alEnviar}>
        <fieldset disabled={guardando} className="row g-2 border-0 p-0 m-0">
          <div className="col-md-4">
            <label className="form-label" htmlFor="pago-admin-fecha">
              Fecha
            </label>
            <CampoFecha
              id="pago-admin-fecha"
              className="form-control"
              max={fechaMaxima}
              value={valor.fecha}
              onChange={(fecha) => onCambio((prev) => ({ ...prev, fecha }))}
            />
          </div>

          <div className="col-md-4">
            <label className="form-label" htmlFor="pago-admin-empresa">
              Empresa
            </label>
            <select
              id="pago-admin-empresa"
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
            <label className="form-label" htmlFor="pago-admin-pv">
              Punto de venta (opcional)
            </label>
            <select
              id="pago-admin-pv"
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
            <label className="form-label" htmlFor="pago-admin-categoria">
              Categoría
            </label>
            <select
              id="pago-admin-categoria"
              className="form-select"
              value={valor.categoria}
              onChange={(e) => onCambio((prev) => ({ ...prev, categoria: e.target.value as CategoriaPago }))}
            >
              {CATEGORIAS_PAGO.map((c) => (
                <option key={c.valor} value={c.valor}>
                  {c.etiqueta}
                </option>
              ))}
            </select>
          </div>

          <div className="col-md-4">
            <label className="form-label" htmlFor="pago-admin-proveedor">
              Proveedor (opcional)
            </label>
            <select
              id="pago-admin-proveedor"
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
            <label className="form-label" htmlFor="pago-admin-area">
              Área
            </label>
            <select
              id="pago-admin-area"
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
            <label className="form-label" htmlFor="pago-admin-concepto">
              Concepto
            </label>
            <input
              id="pago-admin-concepto"
              type="text"
              className="form-control"
              value={valor.concepto}
              onChange={(e) => onCambio((prev) => ({ ...prev, concepto: e.target.value }))}
            />
          </div>

          <div className="col-md-6">
            <label className="form-label" htmlFor="pago-admin-detalle">
              Detalle (opcional)
            </label>
            <input
              id="pago-admin-detalle"
              type="text"
              className="form-control"
              value={valor.detalle}
              onChange={(e) => onCambio((prev) => ({ ...prev, detalle: e.target.value }))}
            />
          </div>

          <div className="col-md-4">
            <label className="form-label" htmlFor="pago-admin-factura">
              N° de factura (opcional)
            </label>
            <input
              id="pago-admin-factura"
              type="text"
              className="form-control"
              value={valor.numeroFactura}
              onChange={(e) => onCambio((prev) => ({ ...prev, numeroFactura: e.target.value }))}
            />
          </div>

          <div className="col-12">
            <EditorDeLineasDePago
              lineas={valor.lineas}
              medios={medios}
              onCambio={(cambios) => onCambio((prev) => ({ ...prev, lineas: cambios(prev.lineas) }))}
            />
          </div>
        </fieldset>
      </form>
    </Modal>
  )
}
