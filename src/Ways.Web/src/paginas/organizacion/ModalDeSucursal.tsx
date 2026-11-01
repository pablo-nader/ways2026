import type { EmpresaListado } from '../../api/tipos'
import { ETIQUETA_SIN_DUENIO } from '../../api/organizacion'
import { ErrorDeFormulario } from '../../componentes/ErrorDeFormulario'
import { Modal } from '../../componentes/Modal'

const ID_FORMULARIO_SUCURSAL = 'formulario-sucursal'

/** `id` es `null` en el alta; `idEmpresa` es '' hasta que se elige y no se edita (es estructural). */
export type FormularioDeSucursal = { id: number | null; idEmpresa: string; nombre: string }

type Props = {
  valor: FormularioDeSucursal
  /** Empresas ofrecidas en el alta; `null` mientras la lectura no aterrizó. En la edición no se muestran. */
  empresas: EmpresaListado[] | null
  errorEmpresas: string
  esPlataforma: boolean
  /** Hay una escritura en vuelo: cambia el rótulo del botón. */
  guardando: boolean
  /** Deshabilita los campos, los botones del pie y el cierre del modal; el estado lo decide la pantalla. */
  bloqueado: boolean
  error: string
  /** Cambia en cada rechazo local, para anunciar de nuevo un mensaje idéntico al anterior. */
  disparoDeError: number
  focoDeReserva: React.RefObject<HTMLElement | null>
  onCambio: (f: FormularioDeSucursal) => void
  onGuardar: () => void
  onCancelar: () => void
}

/** Alta y renombre de una sucursal en un modal. La empresa se elige al crearla y no se cambia después. */
export function ModalDeSucursal({
  valor,
  empresas,
  errorEmpresas,
  esPlataforma,
  guardando,
  bloqueado,
  error,
  disparoDeError,
  focoDeReserva,
  onCambio,
  onGuardar,
  onCancelar,
}: Props) {
  const esAlta = valor.id === null

  const pie = (
    <>
      <button type="button" className="btn btn-outline-secondary" onClick={onCancelar} disabled={bloqueado}>
        Cancelar
      </button>
      <button
        type="submit"
        form={ID_FORMULARIO_SUCURSAL}
        className="btn btn-success"
        disabled={bloqueado || (esAlta && empresas === null)}
      >
        {guardando ? 'Guardando…' : esAlta ? 'Crear' : 'Guardar'}
      </button>
    </>
  )

  return (
    <Modal
      titulo={esAlta ? 'Nueva sucursal' : `Editando sucursal ${valor.id}`}
      tamano="lg"
      ocupado={bloqueado}
      focoDeReserva={focoDeReserva}
      pie={pie}
      onCerrar={onCancelar}
    >
      <ErrorDeFormulario mensaje={error} disparo={disparoDeError} />
      <form
        id={ID_FORMULARIO_SUCURSAL}
        autoComplete="off"
        onSubmit={(e) => {
          e.preventDefault()
          onGuardar()
        }}
      >
        <fieldset disabled={bloqueado} className="row g-3 border-0 p-0 m-0">
          {esAlta && (
            <>
              {errorEmpresas && <div className="col-12 alert alert-danger mb-0">{errorEmpresas}</div>}
              <div className="col-md-6">
                <label className="form-label" htmlFor="sucursal-empresa">
                  Empresa
                </label>
                <select
                  id="sucursal-empresa"
                  className="form-select"
                  value={valor.idEmpresa}
                  onChange={(e) => onCambio({ ...valor, idEmpresa: e.target.value })}
                  disabled={empresas === null}
                  required
                >
                  <option value="">{empresas === null && !errorEmpresas ? 'Cargando…' : 'Elegí una empresa…'}</option>
                  {(empresas ?? []).map((e) => (
                    <option key={e.id} value={String(e.id)}>
                      {esPlataforma ? `${e.razonSocial} (${e.nombreTenant ?? ETIQUETA_SIN_DUENIO})` : e.razonSocial}
                    </option>
                  ))}
                </select>
              </div>
            </>
          )}
          <div className="col-md-6">
            <label className="form-label" htmlFor="sucursal-nombre">
              Nombre
            </label>
            <input
              id="sucursal-nombre"
              className="form-control"
              maxLength={150}
              value={valor.nombre}
              onChange={(e) => onCambio({ ...valor, nombre: e.target.value })}
              required
            />
          </div>
        </fieldset>
      </form>
    </Modal>
  )
}
