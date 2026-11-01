import { MODOS_PUNTO_VENTA } from '../../api/tipos'
import type { EmpresaListado, ModoPuntoVenta, SucursalListado } from '../../api/tipos'
import { ETIQUETA_SIN_DUENIO } from '../../api/organizacion'
import { ErrorDeFormulario } from '../../componentes/ErrorDeFormulario'
import { Modal } from '../../componentes/Modal'
import {
  CAMPOS_DESCRIPTIVOS_DE_ALTA,
  type FormularioDeAltaDePuntoVenta,
} from './puntoVentaFormulario'

const ID_FORMULARIO_ALTA = 'formulario-alta-punto-venta'

type Props = {
  valor: FormularioDeAltaDePuntoVenta
  /** Empresas ofrecidas; `null` mientras la lectura no aterrizó. */
  empresas: EmpresaListado[] | null
  errorEmpresas: string
  /** Sucursales ofrecidas (se filtran por la empresa elegida); `null` mientras la lectura no aterrizó. */
  sucursales: SucursalListado[] | null
  /** El actor de plataforma ve además el tenant dueño de cada empresa. */
  esPlataforma: boolean
  /** Hay una escritura en vuelo: cambia el rótulo del botón. */
  guardando: boolean
  /** Deshabilita los campos, los botones del pie y el cierre del modal; el estado lo decide la pantalla. */
  bloqueado: boolean
  error: string
  /** Cambia en cada rechazo local, para anunciar de nuevo un mensaje idéntico al anterior. */
  disparoDeError: number
  focoDeReserva: React.RefObject<HTMLElement | null>
  onCambio: (f: FormularioDeAltaDePuntoVenta) => void
  onGuardar: () => void
  onCancelar: () => void
}

/** Alta de un punto de venta en un modal: se crea sobre una empresa existente y el modo no se infiere. */
export function ModalDeAltaDePuntoVenta({
  valor,
  empresas,
  errorEmpresas,
  sucursales,
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
  const pie = (
    <>
      <button type="button" className="btn btn-outline-secondary" onClick={onCancelar} disabled={bloqueado}>
        Cancelar
      </button>
      <button
        type="submit"
        form={ID_FORMULARIO_ALTA}
        className="btn btn-success"
        disabled={bloqueado || empresas === null}
      >
        {guardando ? 'Guardando…' : 'Crear'}
      </button>
    </>
  )

  return (
    <Modal titulo="Nuevo punto de venta" tamano="lg" desplazable ocupado={bloqueado} focoDeReserva={focoDeReserva} pie={pie} onCerrar={onCancelar}>
      <ErrorDeFormulario mensaje={error} disparo={disparoDeError} />
      <form
        id={ID_FORMULARIO_ALTA}
        autoComplete="off"
        onSubmit={(e) => {
          e.preventDefault()
          onGuardar()
        }}
      >
        <fieldset disabled={bloqueado} className="row g-3 border-0 p-0 m-0">
          <div className="col-12">
            <p className="text-muted mb-0">
              Se crea sobre una empresa existente. El modo no tiene valor por defecto: Escritorio exige un
              dispositivo vinculado para vender; Web vende desde el navegador.
            </p>
          </div>
          {errorEmpresas && <div className="col-12 alert alert-danger mb-0">{errorEmpresas}</div>}
          <div className="col-md-6">
            <label className="form-label" htmlFor="pv-alta-empresa">
              Empresa destino
            </label>
            <select
              id="pv-alta-empresa"
              className="form-select"
              value={valor.idEmpresa}
              onChange={(e) => onCambio({ ...valor, idEmpresa: e.target.value, idSucursal: '' })}
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
          <div className="col-md-4">
            <label className="form-label" htmlFor="pv-alta-nombre">
              Nombre
            </label>
            <input
              id="pv-alta-nombre"
              className="form-control"
              maxLength={150}
              value={valor.nombre}
              onChange={(e) => onCambio({ ...valor, nombre: e.target.value })}
              required
            />
          </div>
          <div className="col-md-4">
            <label className="form-label" htmlFor="pv-alta-sucursal">
              Sucursal
            </label>
            <select
              id="pv-alta-sucursal"
              className="form-select"
              value={valor.idSucursal}
              onChange={(e) => onCambio({ ...valor, idSucursal: e.target.value })}
              disabled={sucursales === null || valor.idEmpresa === ''}
            >
              <option value="">Una nueva, con el nombre del punto de venta</option>
              {(sucursales ?? [])
                .filter((s) => String(s.idEmpresa) === valor.idEmpresa)
                .map((s) => (
                  <option key={s.id} value={String(s.id)}>
                    {s.nombre}
                  </option>
                ))}
            </select>
          </div>
          <div className="col-md-2">
            <label className="form-label" htmlFor="pv-alta-modo">
              Modo
            </label>
            <select
              id="pv-alta-modo"
              className="form-select"
              value={valor.modo}
              onChange={(e) => onCambio({ ...valor, modo: e.target.value as ModoPuntoVenta | '' })}
              required
            >
              <option value="">Elegí un modo…</option>
              {MODOS_PUNTO_VENTA.map((modo) => (
                <option key={modo} value={modo}>
                  {modo}
                </option>
              ))}
            </select>
          </div>
          {CAMPOS_DESCRIPTIVOS_DE_ALTA.map((campo) => (
            <div key={campo.clave} className={campo.columnas}>
              <label className="form-label" htmlFor={`pv-alta-${campo.clave}`}>
                {campo.etiqueta}
              </label>
              <input
                id={`pv-alta-${campo.clave}`}
                className="form-control"
                maxLength={campo.max}
                value={valor[campo.clave]}
                onChange={(e) => onCambio({ ...valor, [campo.clave]: e.target.value })}
              />
            </div>
          ))}
        </fieldset>
      </form>
    </Modal>
  )
}
