import { useState } from 'react'
import {
  aFormularioDeEdicion,
  aSolicitudDeEdicionDePago,
  categoriaAlElegirProveedorAdministracion,
  formularioDeEdicionCompleto,
} from '../api/registroDePagos'
import type { FormularioDeEdicionDePago, PagoEditable } from '../api/registroDePagos'
import { CATEGORIAS_PAGO } from '../api/tipos'
import type { AreaListado, MedioPagoListado, OpcionDeProveedor, SolicitudDeEdicionDePago } from '../api/tipos'
import { CampoImporte } from '../componentes/CampoImporte'
import { Modal } from '../componentes/Modal'
import { etiquetaDeProveedor, ordenarProveedoresPorEtiqueta } from './articulos/helpers'

type Props = {
  pago: PagoEditable
  medios: MedioPagoListado[]
  proveedores: OpcionDeProveedor[]
  areas: AreaListado[]
  /** Con una compra ligada el servidor exige categoría proveedor y el mismo proveedor (400). */
  ligadoACompra?: boolean
  /** Avisos de efectos (arqueo recalculado, ajuste compensatorio) mostrados sobre el formulario. */
  advertencias?: readonly string[]
  /** `true` mientras la escritura (y su refresco) están en vuelo: todo el diálogo queda inerte. */
  guardando: boolean
  /** Error de la escritura, dueño: la pantalla. */
  error: string
  onGuardar: (solicitud: SolicitudDeEdicionDePago) => void
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
  advertencias = [],
  guardando,
  error,
  onGuardar,
  onCancelar,
}: Props) {
  const [formulario, setFormulario] = useState<FormularioDeEdicionDePago>(() => aFormularioDeEdicion(pago))
  const [errorLocal, setErrorLocal] = useState('')

  const proveedoresOrdenados = ordenarProveedoresPorEtiqueta(proveedores)
  // Un catálogo dado de baja (o que no cargó) no puede cambiar el valor del pago en silencio: la
  // opción actual se conserva rotulada como no disponible.
  const medioFaltante = formulario.idMedioPago !== '' && !medios.some((m) => m.id === formulario.idMedioPago)
  const proveedorFaltante = formulario.idProveedor !== '' && !proveedores.some((p) => p.id === formulario.idProveedor)
  const areaFaltante = formulario.idArea !== '' && !areas.some((a) => a.id === formulario.idArea)

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
    if (!formularioDeEdicionCompleto(formulario)) {
      setErrorLocal('Completá el medio de pago, el concepto y un importe mayor a 0.')
      return
    }
    setErrorLocal('')
    onGuardar(aSolicitudDeEdicionDePago(formulario))
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
        <div className="col-md-4">
          <label className="form-label" htmlFor="pago-edicion-importe">
            Importe
          </label>
          <CampoImporte
            id="pago-edicion-importe"
            className="form-control"
            valor={formulario.importe}
            onChange={(v) => setFormulario((prev) => ({ ...prev, importe: v }))}
          />
        </div>

        <div className="col-md-4">
          <label className="form-label" htmlFor="pago-edicion-medio-pago">
            Medio de pago
          </label>
          <select
            id="pago-edicion-medio-pago"
            className="form-select"
            value={formulario.idMedioPago}
            onChange={(e) => setFormulario((prev) => ({ ...prev, idMedioPago: e.target.value === '' ? '' : Number(e.target.value) }))}
          >
            <option value="">Elegir…</option>
            {medioFaltante && <option value={formulario.idMedioPago}>Medio #{formulario.idMedioPago} (no disponible)</option>}
            {medios.map((m) => (
              <option key={m.id} value={m.id}>
                {m.nombre}
              </option>
            ))}
          </select>
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
            onChange={(e) => setFormulario((prev) => ({ ...prev, categoria: e.target.value as FormularioDeEdicionDePago['categoria'] }))}
          >
            {CATEGORIAS_PAGO.map((c) => (
              <option key={c.valor} value={c.valor}>
                {c.etiqueta}
              </option>
            ))}
          </select>
        </div>

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
            value={formulario.concepto}
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
