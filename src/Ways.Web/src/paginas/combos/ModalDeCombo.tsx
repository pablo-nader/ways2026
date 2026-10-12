import {
  resumenDeFormulario,
  validarCombo,
  type CampoDeError,
  type ErrorDeServidorDeCombo,
  type FormularioCombo,
} from '../../api/combosAbm'
import type { EmpresaListado, FamiliaListado } from '../../api/tipos'
import { CampoImporte } from '../../componentes/CampoImporte'
import { ErrorDeFormulario } from '../../componentes/ErrorDeFormulario'
import { Modal } from '../../componentes/Modal'
import { EditorDeComponentes } from './EditorDeComponentes'

const ID_FORMULARIO_COMBO = 'formulario-combo'

type Props = {
  valor: FormularioCombo
  familias: FamiliaListado[]
  empresas: EmpresaListado[]
  /** `false` mientras el listado correspondiente no cargó o falló: lo que no figura no se rotula "no disponible". */
  familiasListas: boolean
  empresasListas: boolean
  guardando: boolean
  ocupado: boolean
  /** Los errores de validación solo se ven después de un intento de guardar y se recalculan con cada cambio. */
  mostrarErrores: boolean
  /** Rechazo del servidor ya traducido a su lugar; se descarta con el siguiente cambio del formulario. */
  errorDeServidor: ErrorDeServidorDeCombo | null
  error: string
  disparoDelError: number
  focoDeReserva: React.RefObject<HTMLElement | null>
  onCambio: (f: FormularioCombo) => void
  onGuardar: () => void
  onCancelar: () => void
}

function MensajeDeCampo({ id, mensaje }: { id: string; mensaje?: string }) {
  return mensaje ? (
    <div id={id} className="text-danger small mt-1">
      {mensaje}
    </div>
  ) : null
}

export function ModalDeCombo({
  valor,
  familias,
  empresas,
  familiasListas,
  empresasListas,
  guardando,
  ocupado,
  mostrarErrores,
  errorDeServidor,
  error,
  disparoDelError,
  focoDeReserva,
  onCambio,
  onGuardar,
  onCancelar,
}: Props) {
  const esNuevo = valor.id === null
  const validacion = mostrarErrores ? validarCombo(valor) : { filas: {} }
  const delServidor = (campo: CampoDeError) => (errorDeServidor?.campo === campo ? errorDeServidor.mensaje : undefined)
  const errorNombre = validacion.nombre ?? delServidor('nombre')
  const errorPrecio = validacion.precio ?? delServidor('precio')
  const errorCodigo = validacion.codigo ?? delServidor('codigo')
  const errorDelConjunto = validacion.componentes ?? delServidor('componentes')
  const empresaFaltante = valor.idEmpresa !== '' && !empresas.some((e) => e.id === valor.idEmpresa)

  const pie = (
    <>
      <button type="button" className="btn btn-outline-secondary" onClick={onCancelar} disabled={ocupado}>
        Cancelar
      </button>
      <button type="submit" form={ID_FORMULARIO_COMBO} className="btn btn-success" disabled={ocupado}>
        {guardando ? 'Guardando…' : 'Guardar'}
      </button>
    </>
  )

  return (
    <Modal
      titulo={esNuevo ? 'Nuevo combo' : 'Editar combo'}
      tamano="xl"
      desplazable
      ocupado={ocupado}
      focoDeReserva={focoDeReserva}
      pie={pie}
      onCerrar={onCancelar}
    >
      <ErrorDeFormulario mensaje={error} disparo={disparoDelError} />
      <form
        id={ID_FORMULARIO_COMBO}
        autoComplete="off"
        noValidate
        onSubmit={(e) => {
          e.preventDefault()
          if (ocupado) return
          onGuardar()
        }}
      >
        <fieldset disabled={ocupado} className="row g-3 border-0 p-0 m-0">
          <div className="col-md-6">
            <label className="form-label" htmlFor="cb-nombre">
              Nombre
            </label>
            <input
              id="cb-nombre"
              autoFocus
              className={`form-control ${errorNombre ? 'is-invalid' : ''}`}
              maxLength={150}
              aria-invalid={errorNombre ? true : undefined}
              aria-describedby={errorNombre ? 'cb-nombre-error' : undefined}
              value={valor.nombre}
              onChange={(e) => onCambio({ ...valor, nombre: e.target.value })}
            />
            <MensajeDeCampo id="cb-nombre-error" mensaje={errorNombre} />
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="cb-precio">
              Precio del combo ($)
            </label>
            <CampoImporte
              id="cb-precio"
              className={`form-control ${errorPrecio ? 'is-invalid' : ''}`}
              aria-invalid={errorPrecio ? true : undefined}
              aria-describedby={errorPrecio ? 'cb-precio-error' : undefined}
              valor={valor.precio}
              onChange={(n) => onCambio({ ...valor, precio: n })}
            />
            <MensajeDeCampo id="cb-precio-error" mensaje={errorPrecio} />
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="cb-codigo">
              Código
            </label>
            <div className="input-group">
              <span className="input-group-text">C</span>
              <input
                id="cb-codigo"
                inputMode="numeric"
                maxLength={6}
                className={`form-control ${errorCodigo ? 'is-invalid' : ''}`}
                aria-invalid={errorCodigo ? true : undefined}
                aria-describedby={errorCodigo ? 'cb-codigo-error' : 'cb-codigo-ayuda'}
                value={valor.codigo}
                onChange={(e) => onCambio({ ...valor, codigo: e.target.value })}
              />
            </div>
            <MensajeDeCampo id="cb-codigo-error" mensaje={errorCodigo} />
            <div id="cb-codigo-ayuda" className="form-text">
              {esNuevo
                ? 'Es lo que se tipea en la caja. Se asigna solo si lo dejás vacío.'
                : 'Es lo que se tipea en la caja. Si lo dejás vacío se conserva el actual.'}
            </div>
          </div>

          <div className="col-md-4">
            <label className="form-label" htmlFor="cb-empresa">
              Empresa
            </label>
            <select
              id="cb-empresa"
              className="form-select"
              value={valor.idEmpresa}
              onChange={(e) => onCambio({ ...valor, idEmpresa: e.target.value === '' ? '' : Number(e.target.value) })}
            >
              <option value="">Todo el tenant</option>
              {empresaFaltante && (
                <option value={valor.idEmpresa}>
                  Empresa #{valor.idEmpresa}
                  {empresasListas ? ' (no disponible)' : ''}
                </option>
              )}
              {empresas.map((e) => (
                <option key={e.id} value={e.id}>
                  {e.razonSocial}
                  {e.nombreFantasia ? ` (${e.nombreFantasia})` : ''}
                </option>
              ))}
            </select>
          </div>

          <div className="col-md-3 d-flex align-items-end">
            <div className="form-check">
              <input
                id="cb-activo"
                type="checkbox"
                className="form-check-input"
                checked={valor.activo}
                onChange={(e) => onCambio({ ...valor, activo: e.target.checked })}
              />
              <label className="form-check-label" htmlFor="cb-activo">
                Activo
              </label>
            </div>
          </div>

          <div className="col-md-3 d-flex align-items-end">
            <div className="form-check">
              <input
                id="cb-disponible-online"
                type="checkbox"
                className="form-check-input"
                checked={valor.disponibleOnline}
                onChange={(e) => onCambio({ ...valor, disponibleOnline: e.target.checked })}
                aria-describedby="cb-disponible-online-ayuda"
              />
              <label className="form-check-label" htmlFor="cb-disponible-online">
                Disponible online
              </label>
              <div id="cb-disponible-online-ayuda" className="form-text">
                Desmarcalo para que la tienda online no lo ofrezca.
              </div>
            </div>
          </div>

          <div className="col-12">
            <EditorDeComponentes
              filas={valor.componentes}
              familias={familias}
              familiasListas={familiasListas}
              errores={validacion.filas}
              errorDelConjunto={errorDelConjunto}
              idDelErrorDelConjunto="cb-componentes-error"
              resumen={resumenDeFormulario(valor)}
              disabled={ocupado}
              onCambio={(componentes) => onCambio({ ...valor, componentes })}
            />
          </div>
        </fieldset>
      </form>
    </Modal>
  )
}
