import { useEffect, useRef, useState, type ReactNode } from 'react'
import { UNIDADES_VENTA } from '../../api/tipos'
import type {
  AlcanceDeFamilia,
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
import {
  altaConFamiliaLista,
  familiaDelArticulo,
  type AccionesDeFamiliaDelFormulario,
  type EstadoDeFamiliaDelFormulario,
} from './familia'
import { GestorDeCodigosBarra } from './GestorDeCodigosBarra'
import { etiquetaDeProveedor, opcionesConValorActual } from './helpers'
import { SeccionDeFamilia } from './SeccionDeFamilia'

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
  acumulaEnVenta: boolean
  /** En un alta, la familia elegida (`''` = sin familia); al editar, la familia a la que pertenece el
   * artículo. No se cambia editando: sale con `sacar` o con el alcance de un cambio. */
  idFamilia: number | ''
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
    acumulaEnVenta: true,
    idFamilia: '',
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
    acumulaEnVenta: a.acumulaEnVenta,
    idFamilia: a.idFamilia ?? '',
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
    acumulaEnVenta: f.acumulaEnVenta,
  }
}

export function aAlta(f: Formulario): AltaArticulo {
  return { codigoInterno: aVacioNulo(f.codigoInterno), ...camposComunes(f), idFamilia: f.idFamilia === '' ? null : f.idFamilia }
}

/** La familia no viaja en una edición: la pertenencia no se cambia editando. Con `alcance` el servidor
 * decide qué escribir cuando el artículo es miembro y la edición cambia un campo compartido; sin él, un
 * miembro con cambios compartidos se rechaza con 409 `alcance_requerido`. */
export function aEdicion(f: Formulario, alcance?: AlcanceDeFamilia): EdicionArticulo {
  return alcance === undefined ? camposComunes(f) : { ...camposComunes(f), alcance }
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
  familia,
  accionesDeFamilia,
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
  /** La familia del artículo (la elegida en un alta o la del miembro que se edita) y qué hacer con ella. */
  familia: EstadoDeFamiliaDelFormulario
  accionesDeFamilia: AccionesDeFamiliaDelFormulario
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
  // Los campos compartidos de un alta dentro de una familia se toman de ella: quedan bloqueados desde que se la
  // elige, aunque sus valores todavía se estén cargando. Al editar un miembro no se bloquean: el alcance se
  // pregunta al guardar.
  const bloqueoDeFamilia = esNuevo && valor.idFamilia !== ''
  // Solo un alta depende de que la familia elegida esté cargada: un miembro que se edita se guarda aunque su
  // familia no haya cargado, y el servidor frena un cambio compartido sin alcance con `alcance_requerido`.
  const altaLista = !esNuevo || altaConFamiliaLista(valor.idFamilia, familia)
  // Salida de la familia pendiente de confirmación (solo al editar un miembro): mientras está abierta el resto
  // del formulario queda inerte, así que nada puede cambiar lo que se está por confirmar.
  const [salida, setSalida] = useState<{ disparador: HTMLElement } | null>(null)
  const focoDeSalidaRef = useRef<HTMLElement | null>(null)
  const confirmandoSalida = salida !== null
  const bloqueado = ocupado || confirmandoSalida

  // El disparador de la confirmación se captura en el click (react-async-state regla 12) y recién recupera el
  // foco una vez cerrada la confirmación, cuando el botón ya volvió a estar habilitado.
  useEffect(() => {
    if (salida !== null) return
    const destino = focoDeSalidaRef.current
    focoDeSalidaRef.current = null
    if (destino !== null && destino.isConnected && !destino.matches(':disabled')) destino.focus()
  }, [salida])

  function cancelarSalida() {
    focoDeSalidaRef.current = salida?.disparador ?? null
    setSalida(null)
  }

  async function confirmarSalida() {
    // Si la escritura no llegó a empezar (otra ya estaba en curso), la confirmación sigue abierta.
    if (await accionesDeFamilia.sacar()) setSalida(null)
  }

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
      {/* La sección de la familia va FUERA del fieldset: su confirmación de salida tiene que seguir operable
          mientras el resto del formulario está inerte. */}
      <SeccionDeFamilia
        esNuevo={esNuevo}
        idFamilia={valor.idFamilia}
        nombreDelArticulo={valor.nombre}
        ocupado={ocupado}
        listasPrecio={listasPrecio}
        familia={familia}
        acciones={accionesDeFamilia}
        confirmandoSalida={confirmandoSalida}
        onPedirSalida={(disparador) => setSalida({ disparador })}
        onCancelarSalida={cancelarSalida}
        onConfirmarSalida={() => void confirmarSalida()}
      />

      <form
        autoComplete="off"
        onSubmit={(e) => {
          e.preventDefault()
          if (bloqueadoPorCatalogos || bloqueado || !altaLista) return
          onGuardar()
        }}
      >
        {/* fieldset disabled: cascada nativa a todos los controles anidados mientras hay un guardado
            en vuelo, para que lo tipeado en la ventana de la request no se pise con la respuesta.
            Se le mueve acá la clase de grilla de Bootstrap (antes en el form) para no romper el
            layout de columnas; border-0/p-0/m-0 neutralizan el estilo por defecto del fieldset. */}
        <fieldset disabled={bloqueado} className="row g-3 border-0 p-0 m-0">
          <div className="col-12">
            <strong className="text-muted small text-uppercase">Identificación</strong>
          </div>

          <div className="col-md-2">
            <label className="form-label" htmlFor="art-codigo-interno">
              Código interno
            </label>
            <input
              id="art-codigo-interno"
              className="form-control"
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
              className="form-control"
              maxLength={150}
              value={valor.nombre}
              onChange={(e) => onCambio({ ...valor, nombre: e.target.value })}
              required
            />
          </div>

          <div className="col-md-3">
            <Etiqueta htmlFor="art-unidad-venta" deFamilia={bloqueoDeFamilia}>
              Unidad de venta
            </Etiqueta>
            <select
              id="art-unidad-venta"
              className="form-select"
              value={valor.unidadVenta}
              disabled={bloqueoDeFamilia}
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
            <Etiqueta htmlFor="art-unidades-por-bulto" deFamilia={bloqueoDeFamilia}>
              Unidades por bulto
            </Etiqueta>
            <input
              id="art-unidades-por-bulto"
              type="number"
              step="0.01"
              min="0"
              className="form-control"
              value={valor.unidadesPorBulto}
              disabled={bloqueoDeFamilia}
              onChange={(e) => onCambio({ ...valor, unidadesPorBulto: e.target.value })}
            />
          </div>

          <div className="col-md-6 d-flex align-items-end">
            <div className="form-check">
              <input
                id="art-es-producto"
                type="checkbox"
                className="form-check-input"
                checked={valor.esProducto}
                disabled={bloqueoDeFamilia}
                onChange={(e) => onCambio({ ...valor, esProducto: e.target.checked })}
              />
              <Etiqueta htmlFor="art-es-producto" deFamilia={bloqueoDeFamilia} className="form-check-label">
                Es producto (desmarcar si es un servicio)
              </Etiqueta>
            </div>
          </div>

          <div className="col-12">
            <label className="form-label" htmlFor="art-descripcion">
              Descripción
            </label>
            <textarea
              id="art-descripcion"
              className="form-control"
              rows={2}
              value={valor.descripcion}
              onChange={(e) => onCambio({ ...valor, descripcion: e.target.value })}
            />
          </div>

          <div className="col-12">
            <strong className="text-muted small text-uppercase">Clasificación</strong>
          </div>

          <div className="col-md-3">
            <Etiqueta htmlFor="art-area" deFamilia={bloqueoDeFamilia}>
              Área
            </Etiqueta>
            <select
              id="art-area"
              className="form-select"
              value={valor.idArea}
              disabled={bloqueoDeFamilia}
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
            <Etiqueta htmlFor="art-categoria" deFamilia={bloqueoDeFamilia}>
              Categoría
            </Etiqueta>
            <div className="input-group">
              <select
                id="art-categoria"
                ref={refSelectCategoria}
                className="form-select"
                value={valor.idCategoria}
                disabled={bloqueoDeFamilia}
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
                className="btn btn-outline-secondary"
                aria-label="Nueva categoría"
                disabled={ocupado || bloqueoDeFamilia}
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
                className="form-select"
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
                className="btn btn-outline-secondary"
                aria-label="Nueva marca"
                disabled={ocupado}
                onClick={() => abrirAltaRapida('marca', refSelectMarca.current)}
              >
                +
              </button>
            </div>
          </div>

          <div className="col-md-3">
            <Etiqueta htmlFor="art-grupo" deFamilia={bloqueoDeFamilia}>
              Grupo
            </Etiqueta>
            <div className="input-group">
              <select
                id="art-grupo"
                ref={refSelectGrupo}
                className="form-select"
                value={valor.idGrupo}
                disabled={bloqueoDeFamilia}
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
                className="btn btn-outline-secondary"
                aria-label="Nuevo grupo"
                disabled={ocupado || bloqueoDeFamilia}
                onClick={() => abrirAltaRapida('grupo', refSelectGrupo.current)}
              >
                +
              </button>
            </div>
          </div>

          <div className="col-md-4">
            <Etiqueta htmlFor="art-proveedor-habitual" deFamilia={bloqueoDeFamilia}>
              Proveedor habitual
            </Etiqueta>
            <div className="input-group">
              <select
                id="art-proveedor-habitual"
                ref={refSelectProveedor}
                className="form-select"
                value={valor.idProveedorHabitual}
                disabled={bloqueoDeFamilia}
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
                className="btn btn-outline-secondary"
                aria-label="Nuevo proveedor"
                disabled={ocupado || bloqueoDeFamilia}
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
            <Etiqueta htmlFor="art-alicuota-iva" deFamilia={bloqueoDeFamilia}>
              Alícuota de IVA
            </Etiqueta>
            <select
              id="art-alicuota-iva"
              className="form-select"
              value={valor.idAlicuotaIva}
              disabled={bloqueoDeFamilia}
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
            <Etiqueta htmlFor="art-costo-lista" deFamilia={bloqueoDeFamilia}>
              Costo de lista
            </Etiqueta>
            <CampoImporte
              id="art-costo-lista"
              className="form-control"
              valor={valor.costoLista === '' ? null : Number(valor.costoLista)}
              disabled={bloqueoDeFamilia}
              onChange={(n) => onCambio({ ...valor, costoLista: n === null ? '' : String(n) })}
            />
          </div>

          <div className="col-md-4">
            <Etiqueta htmlFor="art-descuento-proveedor" deFamilia={bloqueoDeFamilia}>
              Descuento de proveedor (%)
            </Etiqueta>
            <input
              id="art-descuento-proveedor"
              type="number"
              step="0.01"
              min="0"
              className="form-control"
              value={valor.descuentoProveedor}
              disabled={bloqueoDeFamilia}
              onChange={(e) => onCambio({ ...valor, descuentoProveedor: e.target.value })}
            />
          </div>

          <div className="col-md-4">
            <Etiqueta htmlFor="art-costo-nominal" deFamilia={bloqueoDeFamilia}>
              Costo nominal
            </Etiqueta>
            <CampoImporte
              id="art-costo-nominal"
              className="form-control"
              valor={valor.costoNominal === '' ? null : Number(valor.costoNominal)}
              disabled={bloqueoDeFamilia}
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
                      className="form-check-input"
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
                className="form-check-input"
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
                className="form-check-input"
                checked={valor.controlaLote}
                disabled={bloqueoDeFamilia}
                onChange={(e) => onCambio({ ...valor, controlaLote: e.target.checked })}
              />
              <Etiqueta htmlFor="art-controla-lote" deFamilia={bloqueoDeFamilia} className="form-check-label">
                Controla lote / vencimiento
              </Etiqueta>
            </div>
          </div>

          <div className="col-md-6 d-flex align-items-end">
            <div className="form-check">
              <input
                id="art-acumula-en-venta"
                type="checkbox"
                className="form-check-input"
                aria-describedby="art-acumula-en-venta-ayuda"
                checked={valor.acumulaEnVenta}
                disabled={bloqueoDeFamilia}
                onChange={(e) => onCambio({ ...valor, acumulaEnVenta: e.target.checked })}
              />
              <Etiqueta htmlFor="art-acumula-en-venta" deFamilia={bloqueoDeFamilia} className="form-check-label">
                Acumula en una sola línea al vender
              </Etiqueta>
              <div id="art-acumula-en-venta-ayuda" className="form-text">
                Desmarcado, cada vez que se agrega en el POS suma una línea nueva al ticket.
              </div>
            </div>
          </div>

          <div className="col-12 d-flex gap-2">
            <button type="submit" className="btn btn-success" disabled={bloqueado || bloqueadoPorCatalogos || !altaLista}>
              {guardando ? 'Guardando…' : 'Guardar'}
            </button>
            <button type="button" className="btn btn-outline-secondary" onClick={onCancelar} disabled={bloqueado}>
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
          <GestorDeCodigosBarra idArticulo={valor.id} bloqueadoPorPadre={bloqueado} alDeEscribir={alDeEscribir} />
          <hr />
          <EditorDePrecios
            idArticulo={valor.id}
            listasPrecio={listasPrecio.filter((l) => l.activo)}
            bloqueadoPorPadre={bloqueado}
            alDeEscribir={alDeEscribir}
            familia={familiaDelArticulo(valor.idFamilia, familia.detalle)}
            alSalirDeLaFamilia={accionesDeFamilia.alSalirDeLaFamilia}
            alCambiarLaFamilia={accionesDeFamilia.alCambiarLaFamilia}
          />
        </>
      )}
    </div>
  )
}

/** Etiqueta de un campo. Con `deFamilia` agrega "(de la familia)", la nota que acompaña a los campos
 * compartidos mientras el alta los toma de una familia y no se pueden cambiar. */
function Etiqueta({
  htmlFor,
  deFamilia,
  className = 'form-label',
  children,
}: {
  htmlFor: string
  deFamilia: boolean
  className?: string
  children: ReactNode
}) {
  return (
    <label className={className} htmlFor={htmlFor}>
      {children}
      {deFamilia && <span className="text-body-secondary small ms-1">(de la familia)</span>}
    </label>
  )
}
