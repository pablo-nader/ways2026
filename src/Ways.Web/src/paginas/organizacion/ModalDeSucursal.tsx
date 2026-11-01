import type { EmpresaListado } from '../../api/tipos'
import { ETIQUETA_SIN_DUENIO } from '../../api/organizacion'
import { ErrorDeFormulario } from '../../componentes/ErrorDeFormulario'
import { Modal } from '../../componentes/Modal'
import { atributosDeCampoDecimal } from '../../formato/numeros'
import { PROVINCIAS_ARGENTINAS, type DireccionDelFormulario } from './direccionDeSucursal'

const ID_FORMULARIO_SUCURSAL = 'formulario-sucursal'

type CampoDeTexto = {
  clave: 'calle' | 'altura' | 'ciudad' | 'codigoPostal'
  etiqueta: string
  largo: number
  columnas: string
}

/** Los largos son los de las columnas (`DireccionDeSucursal.LargoMaximo*`). */
const CAMPOS_DE_TEXTO: CampoDeTexto[] = [
  { clave: 'calle', etiqueta: 'Calle', largo: 120, columnas: 'col-md-6' },
  { clave: 'altura', etiqueta: 'Altura', largo: 20, columnas: 'col-md-3' },
  { clave: 'codigoPostal', etiqueta: 'Código postal', largo: 10, columnas: 'col-md-3' },
  { clave: 'ciudad', etiqueta: 'Ciudad', largo: 80, columnas: 'col-md-6' },
]

const CAMPOS_DE_COORDENADA: { clave: 'latitud' | 'longitud'; etiqueta: string }[] = [
  { clave: 'latitud', etiqueta: 'Latitud' },
  { clave: 'longitud', etiqueta: 'Longitud' },
]

/** `id` es `null` en el alta; `idEmpresa` es '' hasta que se elige y no se edita (es estructural). La dirección es
 * opcional: `''` es "sin informar". */
export type FormularioDeSucursal = { id: number | null; idEmpresa: string; nombre: string } & DireccionDelFormulario

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

/** Alta y edición de una sucursal en un modal. La empresa se elige al crearla y no se cambia después. */
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
          <h3 className="col-12 h6 mb-0 mt-4">Dirección</h3>
          {CAMPOS_DE_TEXTO.map((campo) => (
            <div key={campo.clave} className={campo.columnas}>
              <label className="form-label" htmlFor={`sucursal-${campo.clave}`}>
                {campo.etiqueta}
              </label>
              <input
                id={`sucursal-${campo.clave}`}
                className="form-control"
                maxLength={campo.largo}
                value={valor[campo.clave]}
                onChange={(e) => onCambio({ ...valor, [campo.clave]: e.target.value })}
              />
            </div>
          ))}
          <div className="col-md-6">
            <label className="form-label" htmlFor="sucursal-provincia">
              Provincia
            </label>
            <select
              id="sucursal-provincia"
              className="form-select"
              value={valor.provincia}
              onChange={(e) => onCambio({ ...valor, provincia: e.target.value })}
            >
              <option value="">Sin informar</option>
              {PROVINCIAS_ARGENTINAS.map((p) => (
                <option key={p} value={p}>
                  {p}
                </option>
              ))}
            </select>
          </div>
          {CAMPOS_DE_COORDENADA.map((campo) => (
            <div key={campo.clave} className="col-md-3">
              <label className="form-label" htmlFor={`sucursal-${campo.clave}`}>
                {campo.etiqueta}
              </label>
              <input
                id={`sucursal-${campo.clave}`}
                className="form-control"
                {...atributosDeCampoDecimal({ decimales: 6, permitirNegativo: true })}
                value={valor[campo.clave]}
                onChange={(e) => onCambio({ ...valor, [campo.clave]: e.target.value })}
              />
            </div>
          ))}
          <div className="col-12">
            <label className="form-label" htmlFor="sucursal-referencia">
              Referencia
            </label>
            <input
              id="sucursal-referencia"
              className="form-control"
              maxLength={200}
              value={valor.referencia}
              onChange={(e) => onCambio({ ...valor, referencia: e.target.value })}
            />
          </div>
        </fieldset>
      </form>
    </Modal>
  )
}
