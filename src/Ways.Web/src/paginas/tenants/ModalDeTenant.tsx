import { useEffect, useRef } from 'react'
import { ErrorDeFormulario } from '../../componentes/ErrorDeFormulario'
import { Modal } from '../../componentes/Modal'

export type FormularioDeTenant = { id: number; nombre: string }

const ID_FORMULARIO_TENANT = 'formulario-tenant'

type Props = {
  valor: FormularioDeTenant
  guardando: boolean
  error: string
  focoDeReserva: React.RefObject<HTMLElement | null>
  onCambio: (f: FormularioDeTenant) => void
  onGuardar: () => void
  onCancelar: () => void
}

/** Renombrado de un tenant en un modal: mientras guarda queda inerte entero (campo, pie y cierre). */
export function ModalDeTenant({ valor, guardando, error, focoDeReserva, onCambio, onGuardar, onCancelar }: Props) {
  const errorRef = useRef<HTMLDivElement>(null)

  // El botón que se envió queda deshabilitado y el foco caería en <body>: ante un rechazo pasa al mensaje.
  useEffect(() => {
    if (error) errorRef.current?.focus()
  }, [error])

  const pie = (
    <>
      <button type="button" className="btn btn-outline-secondary" onClick={onCancelar} disabled={guardando}>
        Cancelar
      </button>
      <button type="submit" form={ID_FORMULARIO_TENANT} className="btn btn-success" disabled={guardando}>
        {guardando ? 'Guardando…' : 'Guardar'}
      </button>
    </>
  )

  return (
    <Modal titulo={`Editando tenant ${valor.id}`} ocupado={guardando} focoDeReserva={focoDeReserva} pie={pie} onCerrar={onCancelar}>
      <div ref={errorRef} tabIndex={-1}>
        <ErrorDeFormulario mensaje={error} />
      </div>
      <form
        id={ID_FORMULARIO_TENANT}
        autoComplete="off"
        onSubmit={(e) => {
          e.preventDefault()
          onGuardar()
        }}
      >
        <fieldset disabled={guardando} className="border-0 p-0 m-0">
          <label className="form-label" htmlFor="t-nombre">
            Nombre
          </label>
          <input
            id="t-nombre"
            className="form-control"
            maxLength={150}
            value={valor.nombre}
            onChange={(e) => onCambio({ ...valor, nombre: e.target.value })}
            required
          />
        </fieldset>
      </form>
    </Modal>
  )
}
