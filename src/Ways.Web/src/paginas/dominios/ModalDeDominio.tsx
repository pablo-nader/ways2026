import type { FormEvent } from 'react'
import { Modal } from '../../componentes/Modal'

const ID_FORMULARIO_DOMINIO = 'formulario-dominio'

type Props = {
  host: string
  esPrincipal: boolean
  /** Rechazo del servidor: se explica junto al campo del host. */
  error: string
  /** Escritura (y su refresco) en vuelo: el modal queda inerte entero y no se puede cerrar. */
  guardando: boolean
  focoDeReserva: React.RefObject<HTMLElement | null>
  onHostCambio: (host: string) => void
  onPrincipalCambio: (esPrincipal: boolean) => void
  onGuardar: (evento: FormEvent) => void
  onCancelar: () => void
}

/** Alta de un dominio de un tenant en un modal. */
export function ModalDeDominio({
  host,
  esPrincipal,
  error,
  guardando,
  focoDeReserva,
  onHostCambio,
  onPrincipalCambio,
  onGuardar,
  onCancelar,
}: Props) {
  const pie = (
    <>
      <button type="button" className="btn btn-outline-secondary" onClick={onCancelar} disabled={guardando}>
        Cancelar
      </button>
      <button type="submit" form={ID_FORMULARIO_DOMINIO} className="btn btn-success" disabled={guardando}>
        {guardando ? 'Guardando…' : 'Guardar'}
      </button>
    </>
  )

  return (
    <Modal
      titulo="Nuevo dominio"
      tamano="lg"
      ocupado={guardando}
      focoDeReserva={focoDeReserva}
      pie={pie}
      onCerrar={onCancelar}
    >
      <form id={ID_FORMULARIO_DOMINIO} onSubmit={onGuardar}>
        <fieldset disabled={guardando} className="row g-3 border-0 p-0 m-0">
          <div className="col-md-8">
            <label className="form-label" htmlFor="dom-host">
              Host
            </label>
            <input
              id="dom-host"
              className="form-control"
              placeholder="tienda.ejemplo.com"
              maxLength={253}
              value={host}
              onChange={(e) => onHostCambio(e.target.value)}
              aria-invalid={error !== ''}
              aria-describedby={error ? 'dom-host-error' : undefined}
              required
            />
            {error && (
              <div id="dom-host-error" className="text-danger small mt-1" role="alert">
                {error}
              </div>
            )}
          </div>
          <div className="col-md-4 d-flex align-items-end">
            <div className="form-check mb-2">
              <input
                id="dom-principal"
                type="checkbox"
                className="form-check-input"
                checked={esPrincipal}
                onChange={(e) => onPrincipalCambio(e.target.checked)}
              />
              <label className="form-check-label" htmlFor="dom-principal">
                Principal
              </label>
            </div>
          </div>
        </fieldset>
      </form>
    </Modal>
  )
}
