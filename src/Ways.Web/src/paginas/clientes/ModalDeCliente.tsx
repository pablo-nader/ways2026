import { TIPOS_DOCUMENTO } from '../../api/tipos'
import type { CondicionFiscalListado, ListaPrecioAsignable } from '../../api/tipos'
import { CampoFecha } from '../../componentes/CampoFecha'
import { CampoImporte } from '../../componentes/CampoImporte'
import { ErrorDeFormulario } from '../../componentes/ErrorDeFormulario'
import { Modal } from '../../componentes/Modal'
import type { Formulario } from './clienteFormulario'

const ID_FORMULARIO_CLIENTE = 'formulario-cliente'

type Props = {
  valor: Formulario
  condiciones: CondicionFiscalListado[]
  listas: ListaPrecioAsignable[]
  guardando: boolean
  error: string
  focoDeReserva: React.RefObject<HTMLElement | null>
  onCambio: (f: Formulario) => void
  onGuardar: () => void
  onCancelar: () => void
}

/** Alta y edición de un cliente en un modal: mientras guarda queda inerte entera (campos, pie y cierre). */
export function ModalDeCliente({
  valor,
  condiciones,
  listas,
  guardando,
  error,
  focoDeReserva,
  onCambio,
  onGuardar,
  onCancelar,
}: Props) {
  const esNuevo = valor.id === null

  const pie = (
    <>
      <button type="button" className="btn btn-outline-secondary" onClick={onCancelar} disabled={guardando}>
        Cancelar
      </button>
      <button type="submit" form={ID_FORMULARIO_CLIENTE} className="btn btn-success" disabled={guardando}>
        {guardando ? 'Guardando…' : 'Guardar'}
      </button>
    </>
  )

  return (
    <Modal
      titulo={esNuevo ? 'Nuevo cliente' : `Editando cliente ${valor.id}`}
      tamano="xl"
      desplazable
      ocupado={guardando}
      focoDeReserva={focoDeReserva}
      pie={pie}
      onCerrar={onCancelar}
    >
      <ErrorDeFormulario mensaje={error} />
      <form
        id={ID_FORMULARIO_CLIENTE}
        autoComplete="off"
        onSubmit={(e) => {
          e.preventDefault()
          onGuardar()
        }}
      >
        <fieldset disabled={guardando} className="row g-3 border-0 p-0 m-0">
          <div className="col-md-3">
            <label className="form-label" htmlFor="c-nombre">
              Nombre
            </label>
            <input
              id="c-nombre"
              className="form-control"
              maxLength={150}
              value={valor.nombre}
              onChange={(e) => onCambio({ ...valor, nombre: e.target.value })}
              required
            />
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="c-apellido">
              Apellido
            </label>
            <input
              id="c-apellido"
              className="form-control"
              maxLength={150}
              value={valor.apellido}
              onChange={(e) => onCambio({ ...valor, apellido: e.target.value })}
            />
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="c-razon-social">
              Razón social
            </label>
            <input
              id="c-razon-social"
              className="form-control"
              maxLength={150}
              value={valor.razonSocial}
              onChange={(e) => onCambio({ ...valor, razonSocial: e.target.value })}
            />
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="c-condicion-fiscal">
              Condición fiscal
            </label>
            <select
              id="c-condicion-fiscal"
              className="form-select"
              value={valor.idCondicionFiscal}
              onChange={(e) => onCambio({ ...valor, idCondicionFiscal: Number(e.target.value) })}
              required
            >
              <option value="" disabled>
                Elegir…
              </option>
              {condiciones.map((cf) => (
                <option key={cf.id} value={cf.id}>
                  {cf.nombre}
                </option>
              ))}
            </select>
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="c-tipo-documento">
              Tipo de documento
            </label>
            <select
              id="c-tipo-documento"
              className="form-select"
              value={valor.tipoDocumento}
              onChange={(e) => onCambio({ ...valor, tipoDocumento: e.target.value as Formulario['tipoDocumento'] })}
            >
              <option value="">Sin especificar</option>
              {TIPOS_DOCUMENTO.map((t) => (
                <option key={t} value={t}>
                  {t}
                </option>
              ))}
            </select>
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="c-numero-documento">
              N° de documento
            </label>
            <input
              id="c-numero-documento"
              className="form-control"
              maxLength={30}
              value={valor.numeroDocumento}
              onChange={(e) => onCambio({ ...valor, numeroDocumento: e.target.value })}
            />
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="c-nacimiento">
              Nacimiento
            </label>
            <CampoFecha
              id="c-nacimiento"
              className="form-control"
              value={valor.nacimiento}
              onChange={(fecha) => onCambio({ ...valor, nacimiento: fecha })}
            />
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="c-lista-precio">
              Lista de precios
            </label>
            <select
              id="c-lista-precio"
              className="form-select"
              value={valor.idListaPrecio}
              onChange={(e) => onCambio({ ...valor, idListaPrecio: Number(e.target.value) })}
              required
            >
              <option value="" disabled>
                Elegir…
              </option>
              {listas.map((l) => (
                <option key={l.id} value={l.id}>
                  {l.nombre}
                  {l.esDefault ? ' (default)' : ''}
                </option>
              ))}
            </select>
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="c-domicilio">
              Domicilio
            </label>
            <input
              id="c-domicilio"
              className="form-control"
              maxLength={255}
              value={valor.domicilio}
              onChange={(e) => onCambio({ ...valor, domicilio: e.target.value })}
            />
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="c-telefono">
              Teléfono
            </label>
            <input
              id="c-telefono"
              className="form-control"
              maxLength={50}
              value={valor.telefono}
              onChange={(e) => onCambio({ ...valor, telefono: e.target.value })}
            />
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="c-celular">
              Celular
            </label>
            <input
              id="c-celular"
              className="form-control"
              maxLength={50}
              value={valor.celular}
              onChange={(e) => onCambio({ ...valor, celular: e.target.value })}
            />
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="c-email">
              Email
            </label>
            <input
              id="c-email"
              type="email"
              className="form-control"
              maxLength={255}
              value={valor.email}
              onChange={(e) => onCambio({ ...valor, email: e.target.value })}
            />
          </div>

          <div className="col-12">
            <label className="form-label" htmlFor="c-observaciones">
              Observaciones
            </label>
            <textarea
              id="c-observaciones"
              className="form-control"
              rows={2}
              value={valor.observaciones}
              onChange={(e) => onCambio({ ...valor, observaciones: e.target.value })}
            />
          </div>

          <div className="col-12">
            <strong>Crédito</strong>
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="c-limite-credito">
              Límite de crédito
            </label>
            <CampoImporte
              id="c-limite-credito"
              className="form-control"
              valor={valor.limiteCredito}
              disabled={valor.creditoIlimitado}
              onChange={(limiteCredito) => onCambio({ ...valor, limiteCredito })}
            />
          </div>

          <div className="col-md-3 d-flex align-items-end">
            <div className="form-check">
              <input
                id="c-credito-ilimitado"
                type="checkbox"
                className="form-check-input"
                checked={valor.creditoIlimitado}
                onChange={(e) => onCambio({ ...valor, creditoIlimitado: e.target.checked })}
              />
              <label className="form-check-label" htmlFor="c-credito-ilimitado">
                Crédito ilimitado
              </label>
            </div>
          </div>

          <div className="col-md-3 d-flex align-items-end">
            <div className="form-check">
              <input
                id="c-activo"
                type="checkbox"
                className="form-check-input"
                checked={valor.activo}
                onChange={(e) => onCambio({ ...valor, activo: e.target.checked })}
              />
              <label className="form-check-label" htmlFor="c-activo">
                Activo
              </label>
            </div>
          </div>
        </fieldset>
      </form>
    </Modal>
  )
}
