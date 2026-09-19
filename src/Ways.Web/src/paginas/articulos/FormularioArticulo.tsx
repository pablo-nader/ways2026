import { useRef, useState } from 'react'
import { UNIDADES_VENTA } from '../../api/tipos'
import type {
  AlicuotaIvaListado,
  AltaArticulo,
  AreaListado,
  ArticuloListado,
  CategoriaListado,
  EdicionArticulo,
  EmpresaListado,
  GrupoListado,
  ListaPrecioListado,
  MarcaListado,
  ProveedorListado,
  UnidadVenta,
} from '../../api/tipos'
import { CampoImporte } from '../../componentes/CampoImporte'
import { AltaRapidaCategoria } from './AltaRapidaCategoria'
import { AltaRapidaGrupo } from './AltaRapidaGrupo'
import { AltaRapidaMarca } from './AltaRapidaMarca'
import { AltaRapidaProveedor } from './AltaRapidaProveedor'
import { EditorDePrecios } from './EditorDePrecios'
import { GestorDeCodigosBarra } from './GestorDeCodigosBarra'
import { etiquetaDeProveedor, opcionesConValorActual } from './helpers'

export type Formulario = {
  id: number | null
  codigoInterno: string
  nombre: string
  descripcion: string
  idArea: number | ''
  idCategoria: number | ''
  idMarca: number | ''
  idGrupo: number | ''
  idProveedorHabitual: number | ''
  idAlicuotaIva: number | ''
  unidadVenta: UnidadVenta
  unidadesPorBulto: string
  esProducto: boolean
  costoLista: string
  descuentoProveedor: string
  costoNominal: string
  disponibleParaTodas: boolean
  idsEmpresas: number[]
  activo: boolean
  controlaLote: boolean
}

export function formularioVacio(): Formulario {
  return {
    id: null,
    codigoInterno: '',
    nombre: '',
    descripcion: '',
    idArea: '',
    idCategoria: '',
    idMarca: '',
    idGrupo: '',
    idProveedorHabitual: '',
    idAlicuotaIva: '',
    unidadVenta: 'Unidad',
    unidadesPorBulto: '',
    esProducto: true,
    costoLista: '',
    descuentoProveedor: '',
    costoNominal: '',
    disponibleParaTodas: true,
    idsEmpresas: [],
    activo: true,
    controlaLote: false,
  }
}

export function aFormulario(a: ArticuloListado): Formulario {
  return {
    id: a.id,
    codigoInterno: a.codigoInterno,
    nombre: a.nombre,
    descripcion: a.descripcion ?? '',
    idArea: a.idArea,
    idCategoria: a.idCategoria ?? '',
    idMarca: a.idMarca ?? '',
    idGrupo: a.idGrupo ?? '',
    idProveedorHabitual: a.idProveedorHabitual ?? '',
    idAlicuotaIva: a.idAlicuotaIva,
    unidadVenta: a.unidadVenta,
    unidadesPorBulto: a.unidadesPorBulto === null ? '' : String(a.unidadesPorBulto),
    esProducto: a.esProducto,
    costoLista: a.costoLista === null ? '' : String(a.costoLista),
    descuentoProveedor: a.descuentoProveedor === null ? '' : String(a.descuentoProveedor),
    costoNominal: a.costoNominal === null ? '' : String(a.costoNominal),
    disponibleParaTodas: a.disponibleParaTodas,
    idsEmpresas: a.idsEmpresas,
    activo: a.activo,
    controlaLote: a.controlaLote,
  }
}

function aVacioNulo(valor: string): string | null {
  const limpio = valor.trim()
  return limpio === '' ? null : limpio
}

function numeroOpcional(valor: string): number | null {
  const limpio = valor.trim()
  return limpio === '' ? null : Number(limpio)
}

function camposComunes(f: Formulario) {
  return {
    nombre: f.nombre.trim(),
    descripcion: aVacioNulo(f.descripcion),
    idArea: f.idArea === '' ? 0 : f.idArea,
    idCategoria: f.idCategoria === '' ? null : f.idCategoria,
    idMarca: f.idMarca === '' ? null : f.idMarca,
    idGrupo: f.idGrupo === '' ? null : f.idGrupo,
    idProveedorHabitual: f.idProveedorHabitual === '' ? null : f.idProveedorHabitual,
    idAlicuotaIva: f.idAlicuotaIva === '' ? 0 : f.idAlicuotaIva,
    unidadVenta: f.unidadVenta,
    unidadesPorBulto: numeroOpcional(f.unidadesPorBulto),
    esProducto: f.esProducto,
    costoLista: numeroOpcional(f.costoLista),
    descuentoProveedor: numeroOpcional(f.descuentoProveedor),
    costoNominal: numeroOpcional(f.costoNominal),
    disponibleParaTodas: f.disponibleParaTodas,
    idsEmpresas: f.disponibleParaTodas ? null : f.idsEmpresas,
    activo: f.activo,
    controlaLote: f.controlaLote,
  }
}

export function aAlta(f: Formulario): AltaArticulo {
  return { codigoInterno: aVacioNulo(f.codigoInterno), ...camposComunes(f) }
}

export function aEdicion(f: Formulario): EdicionArticulo {
  return camposComunes(f)
}

export function FormularioArticulo({
  valor,
  areas,
  categorias,
  marcas,
  grupos,
  proveedores,
  proveedoresTruncados,
  alicuotasIva,
  empresas,
  listasPrecio,
  guardando,
  ocupado,
  bloqueadoPorCatalogos,
  onCambio,
  actualizarFormulario,
  onGuardar,
  onCancelar,
  alDeEscribir,
  onCategoriaCreada,
  onMarcaCreada,
  onGrupoCreada,
  onProveedorCreado,
}: {
  valor: Formulario
  areas: AreaListado[]
  categorias: CategoriaListado[]
  marcas: MarcaListado[]
  grupos: GrupoListado[]
  proveedores: ProveedorListado[]
  proveedoresTruncados: boolean
  alicuotasIva: AlicuotaIvaListado[]
  empresas: EmpresaListado[]
  listasPrecio: ListaPrecioListado[]
  guardando: boolean
  ocupado: boolean
  bloqueadoPorCatalogos: boolean
  onCambio: (f: Formulario) => void
  /** Actualización funcional, para el completado de las altas rápidas (react-async-state regla
   * 1): esos `onCreado` corren después de un `await` propio del mini-modal, así que un `valor`
   * capturado por closure en el momento de abrirlo puede quedar desactualizado si mientras tanto
   * el usuario tocó otro campo — o si otra alta rápida se completó primero. Construir siempre a
   * partir del `previo` real evita que un alta tardía resucite ese snapshot viejo. */
  actualizarFormulario: (actualizar: (previo: Formulario) => Formulario) => void
  onGuardar: () => void
  onCancelar: () => void
  alDeEscribir: (enCurso: boolean) => void
  onCategoriaCreada: (categoria: CategoriaListado) => void
  onMarcaCreada: (marca: MarcaListado) => void
  onGrupoCreada: (grupo: GrupoListado) => void
  onProveedorCreado: (proveedor: ProveedorListado) => void
}) {
  const esNuevo = valor.id === null
  // Alta rápida de padrones (Categoría/Marca/Grupo/Proveedor habitual): un solo estado porque solo
  // puede haber una abierta a la vez (cada "+" descarta cualquier otra). Los refs son el destino
  // de foco al abrir — enfocarlos ANTES de montar el modal hace que `Modal` los capture como el
  // foco previo y se lo devuelva solo al cerrar (éxito o cancelación), sin lógica extra.
  const [padronRapidoAbierto, setPadronRapidoAbierto] = useState<'categoria' | 'marca' | 'grupo' | 'proveedor' | null>(
    null,
  )
  const refSelectCategoria = useRef<HTMLSelectElement>(null)
  const refSelectMarca = useRef<HTMLSelectElement>(null)
  const refSelectGrupo = useRef<HTMLSelectElement>(null)
  const refSelectProveedor = useRef<HTMLSelectElement>(null)

  function abrirAltaRapida(padron: 'categoria' | 'marca' | 'grupo' | 'proveedor', select: HTMLSelectElement | null) {
    select?.focus()
    setPadronRapidoAbierto(padron)
  }

  function alternarEmpresa(id: number) {
    const yaEsta = valor.idsEmpresas.includes(id)
    onCambio({
      ...valor,
      idsEmpresas: yaEsta ? valor.idsEmpresas.filter((x) => x !== id) : [...valor.idsEmpresas, id],
    })
  }

  return (
    <div>
      <form
        autoComplete="off"
        onSubmit={(e) => {
          e.preventDefault()
          if (bloqueadoPorCatalogos || ocupado) return
          onGuardar()
        }}
      >
        {/* fieldset disabled: cascada nativa a todos los controles anidados mientras hay un guardado
            en vuelo, para que lo tipeado en la ventana de la request no se pise con la respuesta.
            Se le mueve acá la clase de grilla de Bootstrap (antes en el form) para no romper el
            layout de columnas; border-0/p-0/m-0 neutralizan el estilo por defecto del fieldset. */}
        <fieldset disabled={ocupado} className="row g-3 border-0 p-0 m-0">
          <div className="col-12">
            <strong className="text-muted small text-uppercase">Identificación</strong>
          </div>

          <div className="col-md-2">
            <label className="form-label" htmlFor="art-codigo-interno">
              Código interno
            </label>
            <input
              id="art-codigo-interno"
              className="form-control rounded-0"
              maxLength={30}
              placeholder={esNuevo ? 'Se autogenera si se omite' : undefined}
              value={valor.codigoInterno}
              disabled={!esNuevo}
              onChange={(e) => onCambio({ ...valor, codigoInterno: e.target.value })}
            />
          </div>

          <div className="col-md-4">
            <label className="form-label" htmlFor="art-nombre">
              Nombre
            </label>
            <input
              id="art-nombre"
              className="form-control rounded-0"
              maxLength={150}
              value={valor.nombre}
              onChange={(e) => onCambio({ ...valor, nombre: e.target.value })}
              required
            />
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="art-unidad-venta">
              Unidad de venta
            </label>
            <select
              id="art-unidad-venta"
              className="form-select rounded-0"
              value={valor.unidadVenta}
              onChange={(e) => onCambio({ ...valor, unidadVenta: e.target.value as UnidadVenta })}
            >
              {UNIDADES_VENTA.map((u) => (
                <option key={u.valor} value={u.valor}>
                  {u.etiqueta}
                </option>
              ))}
            </select>
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="art-unidades-por-bulto">
              Unidades por bulto
            </label>
            <input
              id="art-unidades-por-bulto"
              type="number"
              step="0.01"
              min="0"
              className="form-control rounded-0"
              value={valor.unidadesPorBulto}
              onChange={(e) => onCambio({ ...valor, unidadesPorBulto: e.target.value })}
            />
          </div>

          <div className="col-md-6 d-flex align-items-end">
            <div className="form-check">
              <input
                id="art-es-producto"
                type="checkbox"
                className="form-check-input rounded-0"
                checked={valor.esProducto}
                onChange={(e) => onCambio({ ...valor, esProducto: e.target.checked })}
              />
              <label className="form-check-label" htmlFor="art-es-producto">
                Es producto (desmarcar si es un servicio)
              </label>
            </div>
          </div>

          <div className="col-12">
            <label className="form-label" htmlFor="art-descripcion">
              Descripción
            </label>
            <textarea
              id="art-descripcion"
              className="form-control rounded-0"
              rows={2}
              value={valor.descripcion}
              onChange={(e) => onCambio({ ...valor, descripcion: e.target.value })}
            />
          </div>

          <div className="col-12">
            <strong className="text-muted small text-uppercase">Clasificación</strong>
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="art-area">
              Área
            </label>
            <select
              id="art-area"
              className="form-select rounded-0"
              value={valor.idArea}
              onChange={(e) => onCambio({ ...valor, idArea: Number(e.target.value) })}
              required
            >
              <option value="" disabled>
                Elegir…
              </option>
              {opcionesConValorActual(areas, valor.idArea).map((a) => (
                <option key={a.id} value={a.id}>
                  {a.nombre}
                  {!a.activo ? ' (inactiva)' : ''}
                </option>
              ))}
            </select>
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="art-categoria">
              Categoría
            </label>
            <div className="input-group">
              <select
                id="art-categoria"
                ref={refSelectCategoria}
                className="form-select rounded-0"
                value={valor.idCategoria}
                onChange={(e) => onCambio({ ...valor, idCategoria: e.target.value === '' ? '' : Number(e.target.value) })}
              >
                <option value="">Sin especificar</option>
                {opcionesConValorActual(categorias, valor.idCategoria).map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.nombre}
                    {!c.activo ? ' (inactiva)' : ''}
                  </option>
                ))}
              </select>
              <button
                type="button"
                className="btn btn-outline-secondary rounded-0"
                aria-label="Nueva categoría"
                disabled={ocupado}
                onClick={() => abrirAltaRapida('categoria', refSelectCategoria.current)}
              >
                +
              </button>
            </div>
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="art-marca">
              Marca
            </label>
            <div className="input-group">
              <select
                id="art-marca"
                ref={refSelectMarca}
                className="form-select rounded-0"
                value={valor.idMarca}
                onChange={(e) => onCambio({ ...valor, idMarca: e.target.value === '' ? '' : Number(e.target.value) })}
              >
                <option value="">Sin especificar</option>
                {opcionesConValorActual(marcas, valor.idMarca).map((m) => (
                  <option key={m.id} value={m.id}>
                    {m.nombre}
                    {!m.activo ? ' (inactiva)' : ''}
                  </option>
                ))}
              </select>
              <button
                type="button"
                className="btn btn-outline-secondary rounded-0"
                aria-label="Nueva marca"
                disabled={ocupado}
                onClick={() => abrirAltaRapida('marca', refSelectMarca.current)}
              >
                +
              </button>
            </div>
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="art-grupo">
              Grupo
            </label>
            <div className="input-group">
              <select
                id="art-grupo"
                ref={refSelectGrupo}
                className="form-select rounded-0"
                value={valor.idGrupo}
                onChange={(e) => onCambio({ ...valor, idGrupo: e.target.value === '' ? '' : Number(e.target.value) })}
              >
                <option value="">Sin especificar</option>
                {opcionesConValorActual(grupos, valor.idGrupo).map((g) => (
                  <option key={g.id} value={g.id}>
                    {g.nombre}
                    {g.margen !== null ? ` (margen ${g.margen}%)` : ''}
                    {!g.activo ? ' (inactivo)' : ''}
                  </option>
                ))}
              </select>
              <button
                type="button"
                className="btn btn-outline-secondary rounded-0"
                aria-label="Nuevo grupo"
                disabled={ocupado}
                onClick={() => abrirAltaRapida('grupo', refSelectGrupo.current)}
              >
                +
              </button>
            </div>
          </div>

          <div className="col-md-4">
            <label className="form-label" htmlFor="art-proveedor-habitual">
              Proveedor habitual
            </label>
            <div className="input-group">
              <select
                id="art-proveedor-habitual"
                ref={refSelectProveedor}
                className="form-select rounded-0"
                value={valor.idProveedorHabitual}
                onChange={(e) =>
                  onCambio({ ...valor, idProveedorHabitual: e.target.value === '' ? '' : Number(e.target.value) })
                }
              >
                <option value="">Sin especificar</option>
                {opcionesConValorActual(proveedores, valor.idProveedorHabitual).map((p) => (
                  <option key={p.id} value={p.id}>
                    {etiquetaDeProveedor(p)}
                    {!p.activo ? ' (inactivo)' : ''}
                  </option>
                ))}
              </select>
              <button
                type="button"
                className="btn btn-outline-secondary rounded-0"
                aria-label="Nuevo proveedor"
                disabled={ocupado}
                onClick={() => abrirAltaRapida('proveedor', refSelectProveedor.current)}
              >
                +
              </button>
            </div>
            {proveedoresTruncados && (
              <div className="form-text">Se muestran solo los primeros 200 proveedores.</div>
            )}
          </div>

          <div className="col-md-4">
            <label className="form-label" htmlFor="art-alicuota-iva">
              Alícuota de IVA
            </label>
            <select
              id="art-alicuota-iva"
              className="form-select rounded-0"
              value={valor.idAlicuotaIva}
              onChange={(e) => onCambio({ ...valor, idAlicuotaIva: Number(e.target.value) })}
              required
            >
              <option value="" disabled>
                Elegir…
              </option>
              {alicuotasIva.map((a) => (
                <option key={a.id} value={a.id}>
                  {a.nombre} ({a.porcentaje}%)
                </option>
              ))}
            </select>
          </div>

          <div className="col-12">
            <strong className="text-muted small text-uppercase">Costos</strong>
          </div>

          <div className="col-md-4">
            <label className="form-label" htmlFor="art-costo-lista">
              Costo de lista
            </label>
            <CampoImporte
              id="art-costo-lista"
              className="form-control rounded-0"
              valor={valor.costoLista === '' ? null : Number(valor.costoLista)}
              onChange={(n) => onCambio({ ...valor, costoLista: n === null ? '' : String(n) })}
            />
          </div>

          <div className="col-md-4">
            <label className="form-label" htmlFor="art-descuento-proveedor">
              Descuento de proveedor (%)
            </label>
            <input
              id="art-descuento-proveedor"
              type="number"
              step="0.01"
              min="0"
              className="form-control rounded-0"
              value={valor.descuentoProveedor}
              onChange={(e) => onCambio({ ...valor, descuentoProveedor: e.target.value })}
            />
          </div>

          <div className="col-md-4">
            <label className="form-label" htmlFor="art-costo-nominal">
              Costo nominal
            </label>
            <CampoImporte
              id="art-costo-nominal"
              className="form-control rounded-0"
              valor={valor.costoNominal === '' ? null : Number(valor.costoNominal)}
              onChange={(n) => onCambio({ ...valor, costoNominal: n === null ? '' : String(n) })}
            />
            <div className="form-text">Si se completa, tiene prioridad sobre costo de lista − descuento.</div>
          </div>

          <div className="col-12">
            <strong className="text-muted small text-uppercase">Disponibilidad</strong>
          </div>

          <div className="col-12">
            <div className="form-check form-switch">
              <input
                id="art-disponible-para-todas"
                type="checkbox"
                className="form-check-input"
                checked={valor.disponibleParaTodas}
                onChange={(e) => onCambio({ ...valor, disponibleParaTodas: e.target.checked })}
              />
              <label className="form-check-label" htmlFor="art-disponible-para-todas">
                Disponible para todas las empresas del tenant
              </label>
            </div>
          </div>

          {!valor.disponibleParaTodas && (
            <div className="col-12">
              <div className="form-text mb-1">
                Elegí al menos una empresa: sin ninguna marcada, el servidor rechaza el guardado.
              </div>
              <div className="d-flex flex-wrap gap-3">
                {empresas.map((e) => (
                  <div className="form-check" key={e.id}>
                    <input
                      id={`art-empresa-${e.id}`}
                      type="checkbox"
                      className="form-check-input rounded-0"
                      checked={valor.idsEmpresas.includes(e.id)}
                      onChange={() => alternarEmpresa(e.id)}
                    />
                    <label className="form-check-label" htmlFor={`art-empresa-${e.id}`}>
                      {e.razonSocial}
                      {e.nombreFantasia ? ` (${e.nombreFantasia})` : ''}
                    </label>
                  </div>
                ))}
              </div>
            </div>
          )}

          <div className="col-md-3 d-flex align-items-end">
            <div className="form-check">
              <input
                id="art-activo"
                type="checkbox"
                className="form-check-input rounded-0"
                checked={valor.activo}
                onChange={(e) => onCambio({ ...valor, activo: e.target.checked })}
              />
              <label className="form-check-label" htmlFor="art-activo">
                Activo
              </label>
            </div>
          </div>

          {/* stage-12-lotes-vencimientos (Slice 15, espejo de `Articulo.ControlaLote`): control
              efectivo de lote es este flag AND `lotes_habilitado` de la empresa (Parámetros) —
              acá solo se edita el flag propio del artículo, el servidor resuelve el AND. */}
          <div className="col-md-3 d-flex align-items-end">
            <div className="form-check">
              <input
                id="art-controla-lote"
                type="checkbox"
                className="form-check-input rounded-0"
                checked={valor.controlaLote}
                onChange={(e) => onCambio({ ...valor, controlaLote: e.target.checked })}
              />
              <label className="form-check-label" htmlFor="art-controla-lote">
                Controla lote / vencimiento
              </label>
            </div>
          </div>

          <div className="col-12 d-flex gap-2">
            <button type="submit" className="btn btn-success rounded-0" disabled={ocupado || bloqueadoPorCatalogos}>
              {guardando ? 'Guardando…' : 'Guardar'}
            </button>
            <button type="button" className="btn btn-outline-secondary rounded-0" onClick={onCancelar} disabled={ocupado}>
              Cancelar
            </button>
          </div>

          {/* Los cuatro modales de alta rápida se renderizan ACÁ ADENTRO del <form> a propósito
              (aunque `Modal` los saque del DOM vía createPortal): React sigue propagando sus
              eventos sintéticos por el árbol de COMPONENTES, no por el DOM físico, así que el
              submit de cada mini-formulario burbujearía hasta este <form> si no cortara la
              propagación (ver el comentario en cada `AltaRapida*`). */}
          {padronRapidoAbierto === 'categoria' && (
            <AltaRapidaCategoria
              // Filtrado acá, no en `AltaRapidaCategoria` (que ofrece la lista "tal cual" por
              // contrato, ver su doc-comment): `categorias` en este formulario trae activas e
              // inactivas (incluirInactivos: true) para el select de artículo, pero una categoría
              // nueva nunca debería poder quedar parentada bajo una ya desactivada.
              categorias={categorias.filter((c) => c.activo)}
              onCreado={(nueva) => {
                onCategoriaCreada(nueva)
                actualizarFormulario((previo) => ({ ...previo, idCategoria: nueva.id }))
                setPadronRapidoAbierto(null)
              }}
              onCancelar={() => setPadronRapidoAbierto(null)}
            />
          )}
          {padronRapidoAbierto === 'marca' && (
            <AltaRapidaMarca
              onCreado={(nueva) => {
                onMarcaCreada(nueva)
                actualizarFormulario((previo) => ({ ...previo, idMarca: nueva.id }))
                setPadronRapidoAbierto(null)
              }}
              onCancelar={() => setPadronRapidoAbierto(null)}
            />
          )}
          {padronRapidoAbierto === 'grupo' && (
            <AltaRapidaGrupo
              onCreado={(nuevo) => {
                onGrupoCreada(nuevo)
                actualizarFormulario((previo) => ({ ...previo, idGrupo: nuevo.id }))
                setPadronRapidoAbierto(null)
              }}
              onCancelar={() => setPadronRapidoAbierto(null)}
            />
          )}
          {padronRapidoAbierto === 'proveedor' && (
            <AltaRapidaProveedor
              onCreado={(nuevo) => {
                onProveedorCreado(nuevo)
                actualizarFormulario((previo) => ({ ...previo, idProveedorHabitual: nuevo.id }))
                setPadronRapidoAbierto(null)
              }}
              onCancelar={() => setPadronRapidoAbierto(null)}
            />
          )}
        </fieldset>
      </form>

      <hr />

      {valor.id === null ? (
        <p className="text-muted mb-0">Guardá el artículo para poder cargar códigos de barra y precios.</p>
      ) : (
        <>
          <GestorDeCodigosBarra idArticulo={valor.id} bloqueadoPorPadre={ocupado} alDeEscribir={alDeEscribir} />
          <hr />
          <EditorDePrecios
            idArticulo={valor.id}
            listasPrecio={listasPrecio.filter((l) => l.activo)}
            bloqueadoPorPadre={ocupado}
            alDeEscribir={alDeEscribir}
          />
        </>
      )}
    </div>
  )
}
