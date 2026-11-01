import { useState } from 'react'
import { errorDeLineasDePago, type LineaDePagoEditable } from '../api/lineasDePago'
import {
  aFormularioDeEdicion,
  aSolicitudDeEdicionConLineas,
  categoriaAlElegirProveedorAdministracion,
  lineasIniciales,
} from '../api/registroDePagos'
import type { FormularioDeEdicionDePago, PagoEditable } from '../api/registroDePagos'
import { conEmpleadoAsignado, ofreceElegirPersonal } from '../api/sueldoConPersonal'
import { CATEGORIAS_PAGO } from '../api/tipos'
import type {
  AreaListado,
  LineaDePagoListada,
  MedioPagoListado,
  OpcionDeProveedor,
  SolicitudDeEdicionDePagoConLineas,
} from '../api/tipos'
import { EditorDeLineasDePago } from '../componentes/EditorDeLineasDePago'
import { Modal } from '../componentes/Modal'
import { etiquetaDeProveedor, ordenarProveedoresPorEtiqueta } from './articulos/helpers'

type Props = {
  pago: PagoEditable & { lineas?: readonly LineaDePagoListada[] }
  medios: MedioPagoListado[]
  proveedores: OpcionDeProveedor[]
  areas: AreaListado[]
  /** Con una compra ligada el servidor exige categoría proveedor y el mismo proveedor (400). */
  ligadoACompra?: boolean
  /**
   * Empleados (solo id y nombre) a los que se puede asignar un pago de sueldos común, que pasa a ser un pago de personal.
   * Sin esta lista (`undefined`) el modal no ofrece elegir un empleado; con una lista vacía lo ofrece y solo queda "Sin empleado".
   */
  personal?: readonly { id: number; nombre: string }[]
  /** Si el personal se puede asignar ahora (el pago de personal necesita conexión) y, si no, el motivo. */
  personalDisponible?: { habilitado: boolean; motivo: string }
  /** Motivo por el que no se pudo leer el personal; el pago sigue editable sin empleado. */
  errorDePersonal?: string
  /** Recibe la solicitud con el reparto por medio ya armada y validada de forma local. */
  onGuardarConLineas: (solicitud: SolicitudDeEdicionDePagoConLineas) => void
  /** Avisos de efectos (arqueo recalculado, ajuste compensatorio) mostrados sobre el formulario. */
  advertencias?: readonly string[]
  /** `true` mientras la escritura (y su refresco) están en vuelo: todo el diálogo queda inerte. */
  guardando: boolean
  /** Error de la escritura, dueño: la pantalla. */
  error: string
  onCancelar: () => void
}

/**
 * Edición de un pago ya registrado, compartida por el POS (`PagosDelTurno`) y la administración
 * (`Pagos`). El formulario es de ESTE componente (se precarga una sola vez del pago; la pantalla
 * lo monta con `key={pago.id}` para que cambiar de pago lo reinicie) y la escritura de la
 * pantalla: acá solo se valida lo mínimo y se entrega la solicitud ya armada.
 */
export function ModalDeEdicionDePago({
  pago,
  medios,
  proveedores,
  areas,
  ligadoACompra = false,
  personal,
  personalDisponible = { habilitado: true, motivo: '' },
  errorDePersonal = '',
  advertencias = [],
  guardando,
  error,
  onGuardarConLineas,
  onCancelar,
}: Props) {
  const [formulario, setFormulario] = useState<FormularioDeEdicionDePago>(() => aFormularioDeEdicion(pago))
  const [lineas, setLineas] = useState<LineaDePagoEditable[]>(() => lineasIniciales(pago))
  const [idPersonal, setIdPersonal] = useState<number | ''>('')
  const [errorLocal, setErrorLocal] = useState('')

  const proveedoresOrdenados = ordenarProveedoresPorEtiqueta(proveedores)
  // Un catálogo dado de baja (o que no cargó) no puede cambiar el valor del pago en silencio: la
  // opción actual se conserva rotulada como no disponible.
  const proveedorFaltante = formulario.idProveedor !== '' && !proveedores.some((p) => p.id === formulario.idProveedor)
  const areaFaltante = formulario.idArea !== '' && !areas.some((a) => a.id === formulario.idArea)

  // El empleado reemplaza al proveedor en Sueldos; un pago de sueldos que ya traía proveedor lo conserva a la vista.
  const ofrecePersonal =
    personal !== undefined && !ligadoACompra && ofreceElegirPersonal(formulario.categoria) && formulario.idProveedor === ''
  const conEmpleado = ofrecePersonal && idPersonal !== ''

  function alCambiarCategoria(categoria: FormularioDeEdicionDePago['categoria']) {
    // Al editar, el proveedor del pago no se descarta al entrar a Sueldos: solo asignar un empleado lo reemplaza.
    if (!ofreceElegirPersonal(categoria)) setIdPersonal('')
    setFormulario((prev) => ({ ...prev, categoria }))
  }

  function alCambiarProveedor(valorCrudo: string) {
    const nuevo = valorCrudo === '' ? null : Number(valorCrudo)
    setFormulario((prev) => ({
      ...prev,
      idProveedor: nuevo ?? '',
      categoria: categoriaAlElegirProveedorAdministracion(nuevo, prev.categoria),
    }))
  }

  function guardar() {
    if (guardando) return

    const errorDeLineas = errorDeLineasDePago(lineas)
    if (!conEmpleado && formulario.concepto.trim() === '') {
      setErrorLocal('Completá el concepto.')
      return
    }
    if (conEmpleado && !personalDisponible.habilitado) {
      setErrorLocal(personalDisponible.motivo)
      return
    }
    if (errorDeLineas !== null) {
      setErrorLocal(errorDeLineas)
      return
    }
    setErrorLocal('')
    const solicitud = aSolicitudDeEdicionConLineas(formulario, lineas)
    onGuardarConLineas(conEmpleado ? conEmpleadoAsignado(solicitud, idPersonal) : solicitud)
  }

  const pie = (
    <>
      <button type="button" className="btn btn-outline-secondary" onClick={onCancelar} disabled={guardando}>
        Cancelar
      </button>
      <button type="button" className="btn btn-primary" onClick={guardar} disabled={guardando}>
        {guardando ? 'Guardando…' : 'Guardar cambios'}
      </button>
    </>
  )

  return (
    <Modal titulo="Editar pago" tamano="lg" desplazable ocupado={guardando} pie={pie} onCerrar={onCancelar}>
      {advertencias.map((aviso) => (
        <div key={aviso} className="alert alert-warning py-2 px-3 small">
          {aviso}
        </div>
      ))}
      {ligadoACompra && (
        <div className="alert alert-info py-2 px-3 small">
          Este pago está ligado a una compra: la categoría y el proveedor no se pueden cambiar.
        </div>
      )}
      {(error || errorLocal) && <div className="alert alert-danger py-2 px-3 small">{error || errorLocal}</div>}

      <fieldset disabled={guardando} className="row g-2 border-0 p-0 m-0">
        <div className="col-12">
          <EditorDeLineasDePago
            lineas={lineas}
            medios={medios}
            onCambio={(cambio) => setLineas(cambio)}
          />
        </div>

        <div className="col-md-4">
          <label className="form-label" htmlFor="pago-edicion-categoria">
            Categoría
          </label>
          <select
            id="pago-edicion-categoria"
            className="form-select"
            value={formulario.categoria}
            disabled={ligadoACompra}
            onChange={(e) => alCambiarCategoria(e.target.value as FormularioDeEdicionDePago['categoria'])}
          >
            {CATEGORIAS_PAGO.map((c) => (
              <option key={c.valor} value={c.valor}>
                {c.etiqueta}
              </option>
            ))}
          </select>
        </div>

        {ofrecePersonal ? (
          <div className="col-md-6">
            <label className="form-label" htmlFor="pago-edicion-personal">
              Empleado (opcional)
            </label>
            <select
              id="pago-edicion-personal"
              className="form-select"
              value={idPersonal}
              onChange={(e) => setIdPersonal(e.target.value === '' ? '' : Number(e.target.value))}
            >
              <option value="">Sin empleado</option>
              {personal.map((p) => (
                <option key={p.id} value={p.id} disabled={!personalDisponible.habilitado}>
                  {p.nombre}
                </option>
              ))}
            </select>
            {!personalDisponible.habilitado && <div className="form-text text-danger">{personalDisponible.motivo}</div>}
            {errorDePersonal && personalDisponible.habilitado && <div className="form-text text-danger">{errorDePersonal}</div>}
            {conEmpleado && (
              <div className="form-text">Se registra como pago de personal y baja el saldo del empleado.</div>
            )}
          </div>
        ) : (
          <div className="col-md-6">
            <label className="form-label" htmlFor="pago-edicion-proveedor">
              Proveedor (opcional)
            </label>
            <select
              id="pago-edicion-proveedor"
              className="form-select"
              value={formulario.idProveedor}
              disabled={ligadoACompra}
              onChange={(e) => alCambiarProveedor(e.target.value)}
            >
              <option value="">Sin proveedor</option>
              {proveedorFaltante && <option value={formulario.idProveedor}>Proveedor #{formulario.idProveedor} (no disponible)</option>}
              {proveedoresOrdenados.map((p) => (
                <option key={p.id} value={p.id}>
                  {etiquetaDeProveedor(p)}
                </option>
              ))}
            </select>
          </div>
        )}

        <div className="col-md-6">
          <label className="form-label" htmlFor="pago-edicion-area">
            Área (opcional)
          </label>
          <select
            id="pago-edicion-area"
            className="form-select"
            value={formulario.idArea}
            onChange={(e) => setFormulario((prev) => ({ ...prev, idArea: e.target.value === '' ? '' : Number(e.target.value) }))}
          >
            <option value="">Sin área</option>
            {areaFaltante && <option value={formulario.idArea}>Área #{formulario.idArea} (no disponible)</option>}
            {areas.map((a) => (
              <option key={a.id} value={a.id}>
                {a.nombre}
              </option>
            ))}
          </select>
        </div>

        <div className="col-md-6">
          <label className="form-label" htmlFor="pago-edicion-concepto">
            Concepto
          </label>
          <input
            id="pago-edicion-concepto"
            type="text"
            className="form-control"
            value={conEmpleado ? '' : formulario.concepto}
            disabled={conEmpleado}
            placeholder={conEmpleado ? 'Se genera con el nombre del empleado' : undefined}
            onChange={(e) => setFormulario((prev) => ({ ...prev, concepto: e.target.value }))}
          />
        </div>

        <div className="col-md-6">
          <label className="form-label" htmlFor="pago-edicion-factura">
            N° de factura (opcional)
          </label>
          <input
            id="pago-edicion-factura"
            type="text"
            className="form-control"
            value={formulario.numeroFactura}
            onChange={(e) => setFormulario((prev) => ({ ...prev, numeroFactura: e.target.value }))}
          />
        </div>

        <div className="col-12">
          <label className="form-label" htmlFor="pago-edicion-detalle">
            Detalle (opcional)
          </label>
          <textarea
            id="pago-edicion-detalle"
            className="form-control"
            rows={2}
            value={formulario.detalle}
            onChange={(e) => setFormulario((prev) => ({ ...prev, detalle: e.target.value }))}
          />
        </div>
      </fieldset>
    </Modal>
  )
}
