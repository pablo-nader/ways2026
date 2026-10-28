import { ErrorDeFormulario } from '../../componentes/ErrorDeFormulario'
import { Modal } from '../../componentes/Modal'
import { atributosDeCampoDecimal } from '../../formato/numeros'
import { LARGO_MAXIMO_INGRESOS_BRUTOS, type FormularioDeEmpresa } from '../empresaFormulario'

const ID_FORMULARIO_EMPRESA = 'formulario-empresa'

type Props = {
  valor: FormularioDeEmpresa
  /** Hay una escritura en vuelo: cambia el rótulo del botón. */
  guardando: boolean
  /** Todo el modal queda inerte: la escritura en vuelo o una confirmación de baja abierta encima. */
  bloqueado: boolean
  error: string
  /** Cambia en cada rechazo local, para anunciar de nuevo un mensaje idéntico al anterior. */
  disparoDeError: number
  focoDeReserva: React.RefObject<HTMLElement | null>
  onCambio: (f: FormularioDeEmpresa) => void
  onGuardar: () => void
  onCancelar: () => void
}

/** Edición de una empresa en un modal: el alta es plataforma-only vía aprovisionamiento. */
export function ModalDeEmpresa({
  valor,
  guardando,
  bloqueado,
  error,
  disparoDeError,
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
      <button type="submit" form={ID_FORMULARIO_EMPRESA} className="btn btn-success" disabled={bloqueado}>
        {guardando ? 'Guardando…' : 'Guardar'}
      </button>
    </>
  )

  return (
    <Modal
      titulo={`Editando empresa ${valor.id}`}
      tamano="lg"
      desplazable
      ocupado={bloqueado}
      focoDeReserva={focoDeReserva}
      pie={pie}
      onCerrar={onCancelar}
    >
      <ErrorDeFormulario mensaje={error} disparo={disparoDeError} />
      <form
        id={ID_FORMULARIO_EMPRESA}
        autoComplete="off"
        onSubmit={(e) => {
          e.preventDefault()
          onGuardar()
        }}
      >
        <fieldset disabled={bloqueado} className="row g-3 border-0 p-0 m-0">
          <div className="col-md-4">
            <label className="form-label" htmlFor="e-razon">
              Razón social
            </label>
            <input
              id="e-razon"
              className="form-control"
              maxLength={150}
              value={valor.razonSocial}
              onChange={(e) => onCambio({ ...valor, razonSocial: e.target.value })}
              required
            />
          </div>
          <div className="col-md-4">
            <label className="form-label" htmlFor="e-fantasia">
              Nombre de fantasía
            </label>
            <input
              id="e-fantasia"
              className="form-control"
              maxLength={150}
              value={valor.nombreFantasia}
              onChange={(e) => onCambio({ ...valor, nombreFantasia: e.target.value })}
            />
          </div>
          <div className="col-md-4">
            <label className="form-label" htmlFor="e-cuit">
              CUIT
            </label>
            <input
              id="e-cuit"
              className="form-control"
              maxLength={13}
              value={valor.cuit}
              onChange={(e) => onCambio({ ...valor, cuit: e.target.value })}
            />
          </div>
          <div className="col-12">
            <strong>Datos fiscales</strong>
          </div>
          <div className="col-md-6">
            <label className="form-label" htmlFor="e-ingresos-brutos">
              Ingresos brutos
            </label>
            <input
              id="e-ingresos-brutos"
              className="form-control"
              maxLength={LARGO_MAXIMO_INGRESOS_BRUTOS}
              value={valor.ingresosBrutos}
              onChange={(e) => onCambio({ ...valor, ingresosBrutos: e.target.value })}
            />
          </div>
          <div className="col-md-6">
            <label className="form-label" htmlFor="e-inicio-actividades">
              Inicio de actividades
            </label>
            <input
              id="e-inicio-actividades"
              type="date"
              className="form-control"
              value={valor.inicioActividades}
              onChange={(e) => onCambio({ ...valor, inicioActividades: e.target.value })}
            />
          </div>
          <div className="col-12">
            <strong>Percepciones que le aplican los proveedores</strong>
          </div>
          <div className="col-md-6">
            <label className="form-label" htmlFor="e-percepcion-iibb">
              Alícuota de percepción de IIBB (%)
            </label>
            <input
              id="e-percepcion-iibb"
              {...atributosDeCampoDecimal({ decimales: 3 })}
              className="form-control"
              value={valor.alicuotaPercepcionIibb}
              onChange={(e) => onCambio({ ...valor, alicuotaPercepcionIibb: e.target.value })}
            />
          </div>
          <div className="col-md-6">
            <label className="form-label" htmlFor="e-percepcion-iva">
              Alícuota de percepción de IVA (%)
            </label>
            <input
              id="e-percepcion-iva"
              {...atributosDeCampoDecimal({ decimales: 3 })}
              className="form-control"
              value={valor.alicuotaPercepcionIva}
              onChange={(e) => onCambio({ ...valor, alicuotaPercepcionIva: e.target.value })}
            />
          </div>
        </fieldset>
      </form>
    </Modal>
  )
}
