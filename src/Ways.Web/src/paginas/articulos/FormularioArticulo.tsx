import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { COSTOS_LISTA_POR, UNIDADES_VENTA } from '../../api/tipos'
import type {
  AlcanceDeFamilia,
  AlicuotaIvaListado,
  AltaArticulo,
  AreaListado,
  ArticuloListado,
  CategoriaListado,
  CostoListaPor,
  EdicionArticulo,
  EmpresaListado,
  GrupoListado,
  ListaPrecioListado,
  MarcaListado,
  ProveedorListado,
  UnidadVenta,
} from '../../api/tipos'
import { CampoImporte } from '../../componentes/CampoImporte'
import { IconoDeAyuda } from '../../componentes/IconoDeAyuda'
import { formatearImporte } from '../../formato/importes'
import { mapaDeNombres, type NombresDeCatalogo } from '../familias/valoresCompartidos'
import { AltaRapidaCategoria } from './AltaRapidaCategoria'
import { AltaRapidaGrupo } from './AltaRapidaGrupo'
import { AltaRapidaMarca } from './AltaRapidaMarca'
import { AltaRapidaProveedor } from './AltaRapidaProveedor'
import {
  bultoSinUnidades,
  costoDeListaSinYConIva,
  costoNominalDelFormulario,
  porcentajeDeAlicuota,
  type CostoGuardado,
} from './costoNominal'
import { EditorDePrecios } from './EditorDePrecios'
import {
  altaConFamiliaLista,
  familiaDelArticulo,
  type AccionesDeFamiliaDelFormulario,
  type EstadoDeFamiliaDelFormulario,
  type EstadoDeUnionAFamilia,
} from './familia'
import { GestorDeCodigosBarra } from './GestorDeCodigosBarra'
import { etiquetaDeProveedor, opcionesConValorActual } from './helpers'
import { SeccionDeFamilia } from './SeccionDeFamilia'
import { SeccionDeDisponibilidad } from './SeccionDeDisponibilidad'

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
  /** El costo de lista tal como se cargó: sin IVA o con IVA según `costoListaIncluyeIva`, por unidad o por bulto
   * según `costoListaPor`. El otro valor (con/sin IVA) se deriva al mostrarlo y nunca se guarda. */
  costoLista: string
  costoListaPor: CostoListaPor
  costoListaIncluyeIva: boolean
  descuentoProveedor: string
  /** El costo nominal guardado, solo de lectura: lo calcula el servidor y no viaja en un alta ni en una edición. */
  costoNominal: string
  /** Las entradas de costo con las que se cargó el artículo; `null` en un alta. Con ellas se decide si se muestra
   * el costo nominal guardado o el que daría el servidor al guardar. */
  costoGuardado: CostoGuardado | null
  /** El artículo llegó sin costos (quien lo lee no los ve): no se muestra ni se calcula ningún costo nominal y
   * una edición conserva la base y el IVA guardados. */
  costosOcultos: boolean
  disponibleParaTodas: boolean
  idsEmpresas: number[]
  activo: boolean
  controlaLote: boolean
  acumulaEnVenta: boolean
  /** En un alta, la familia elegida (`''` = sin familia); al editar, la familia a la que pertenece el
   * artículo. No se cambia editando: sale con `sacar` o con el alcance de un cambio. */
  idFamilia: number | ''
  /** En un alta, el nombre de la familia que el servidor crea junto con el artículo (él queda como su referencia); `''` si no
   * se pidió ninguna. Excluyente con `idFamilia`. */
  nombreFamiliaNueva: string
  /** Disponibilidad e imagen del artículo tal como se cargaron. Solo lectura: la sección "Venta y tienda online" los toma al
   * montar y escribe por su cuenta, así que no entran en `aAlta`/`aEdicion` ni cuentan como cambios sin guardar. */
  disponibleVenta: boolean
  disponibleOnline: boolean
  tieneImagen: boolean
}

/** El campo del código con el que el proveedor habitual conoce al artículo: no es un dato del artículo sino de su
 * relación con el proveedor, así que solo lo ofrece quien abre el alta desde una compra. */
export type CodigoDeProveedorDelFormulario = {
  valor: string
  onCambio: (codigo: string) => void
  /** El proveedor habitual elegido no es el dueño del código (el de la compra): el campo queda solo lectura. */
  deOtroProveedor?: boolean
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
    costoListaPor: 'Unidad',
    costoListaIncluyeIva: true,
    descuentoProveedor: '0',
    costoNominal: '',
    costoGuardado: null,
    costosOcultos: false,
    disponibleParaTodas: true,
    idsEmpresas: [],
    activo: true,
    controlaLote: false,
    acumulaEnVenta: true,
    idFamilia: '',
    nombreFamiliaNueva: '',
    disponibleVenta: true,
    disponibleOnline: true,
    tieneImagen: false,
  }
}

export function aFormulario(a: ArticuloListado): Formulario {
  const costos = {
    costoLista: a.costoLista === null ? '' : String(a.costoLista),
    costoListaPor: a.costoListaPor ?? 'Unidad',
    costoListaIncluyeIva: a.costoListaIncluyeIva ?? true,
    unidadesPorBulto: a.unidadesPorBulto === null ? '' : String(a.unidadesPorBulto),
    descuentoProveedor: a.descuentoProveedor === null ? (a.costoListaPor === null ? '' : '0') : String(a.descuentoProveedor),
    idAlicuotaIva: a.idAlicuotaIva,
  }

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
    unidadesPorBulto: costos.unidadesPorBulto,
    esProducto: a.esProducto,
    costoLista: costos.costoLista,
    costoListaPor: costos.costoListaPor,
    costoListaIncluyeIva: costos.costoListaIncluyeIva,
    descuentoProveedor: costos.descuentoProveedor,
    costoNominal: a.costoNominal === null ? '' : String(a.costoNominal),
    costoGuardado: costos,
    costosOcultos: a.costoListaPor === null,
    disponibleParaTodas: a.disponibleParaTodas,
    idsEmpresas: a.idsEmpresas,
    activo: a.activo,
    controlaLote: a.controlaLote,
    acumulaEnVenta: a.acumulaEnVenta,
    idFamilia: a.idFamilia ?? '',
    nombreFamiliaNueva: '',
    disponibleVenta: a.disponibleVenta,
    disponibleOnline: a.disponibleOnline,
    tieneImagen: a.tieneImagen,
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
    descuentoProveedor: f.costosOcultos ? null : (numeroOpcional(f.descuentoProveedor) ?? 0),
    disponibleParaTodas: f.disponibleParaTodas,
    idsEmpresas: f.disponibleParaTodas ? null : f.idsEmpresas,
    activo: f.activo,
    controlaLote: f.controlaLote,
    acumulaEnVenta: f.acumulaEnVenta,
  }
}

export function aAlta(f: Formulario): AltaArticulo {
  const alta = {
    codigoInterno: aVacioNulo(f.codigoInterno),
    ...camposComunes(f),
    costoListaPor: f.costoListaPor,
    costoListaIncluyeIva: f.costoListaIncluyeIva,
    idFamilia: f.idFamilia === '' ? null : f.idFamilia,
  }
  const nombreFamiliaNueva = f.nombreFamiliaNueva.trim()
  // El servidor rechaza una familia existente y una nueva a la vez: la nueva solo viaja cuando no hay una elegida.
  return f.idFamilia === '' && nombreFamiliaNueva !== '' ? { ...alta, nombreFamiliaNueva } : alta
}

/** Una edición manda la base y el IVA del costo de lista explícitos; solo si el artículo llegó sin costos manda
 * `null`, que el servidor lee como "conservar lo guardado". */
function camposDeCostoDeEdicion(f: Formulario) {
  return {
    costoListaPor: f.costosOcultos ? null : f.costoListaPor,
    costoListaIncluyeIva: f.costosOcultos ? null : f.costoListaIncluyeIva,
  }
}

/** La familia no viaja en una edición: la pertenencia no se cambia editando. Con `alcance` el servidor
 * decide qué escribir cuando el artículo es miembro y la edición cambia un campo compartido; sin él, un
 * miembro con cambios compartidos se rechaza con 409 `alcance_requerido`. */
export function aEdicion(f: Formulario, alcance?: AlcanceDeFamilia): EdicionArticulo {
  const comunes = { ...camposComunes(f), ...camposDeCostoDeEdicion(f) }
  return alcance === undefined ? comunes : { ...comunes, alcance }
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
  union,
  errorDeFamiliaNueva,
  sinGuardar,
  preguntaDeAlcanceAbierta,
  codigoProveedor,
  onCambio,
  actualizarFormulario,
  onGuardar,
  onCancelar,
  alDeEscribir,
  alCambiarTienda,
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
  union: EstadoDeUnionAFamilia
  /** El rechazo del servidor a la familia nueva de un alta: se muestra junto a la sección de la familia. */
  errorDeFamiliaNueva: string
  /** El formulario tiene cambios sin guardar: el servidor lee los valores guardados, así que con ellos no se elige ni se crea una familia de un artículo existente. */
  sinGuardar: boolean
  /** La pregunta de alcance (un modal hermano de este, no hijo) está abierta: al cerrarse, si el foco se perdió, vuelve acá. */
  preguntaDeAlcanceAbierta: boolean
  codigoProveedor?: CodigoDeProveedorDelFormulario
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
  /** La sección "Venta y tienda online" escribió algo que la grilla de artículos muestra (disponibilidad o imagen). */
  alCambiarTienda: () => void
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
  const refNombre = useRef<HTMLInputElement>(null)
  const nombresDeCatalogo = useMemo<NombresDeCatalogo>(
    () => ({
      areas: mapaDeNombres(areas, (a) => a.nombre),
      categorias: mapaDeNombres(categorias, (c) => c.nombre),
      grupos: mapaDeNombres(grupos, (g) => g.nombre),
      proveedores: mapaDeNombres(proveedores, etiquetaDeProveedor),
      alicuotas: mapaDeNombres(alicuotasIva, (a) => a.nombre),
    }),
    [areas, categorias, grupos, proveedores, alicuotasIva],
  )
  const nombresDeListas = useMemo(() => mapaDeNombres(listasPrecio, (l) => l.nombre), [listasPrecio])
  const confirmandoSalida = salida !== null
  const bloqueado = ocupado || confirmandoSalida

  // El disparador de la confirmación se captura en el click (react-async-state regla 12) y recién recupera el
  // foco una vez cerrada la confirmación, cuando el botón ya volvió a estar habilitado. Si salió bien, el botón ya no
  // existe (el artículo dejó de ser miembro) y el foco va al primer campo editable.
  useEffect(() => {
    if (salida !== null) return
    const destino = focoDeSalidaRef.current
    focoDeSalidaRef.current = null
    if (destino === null) return
    if (destino.isConnected && !destino.matches(':disabled')) destino.focus()
    else refNombre.current?.focus()
  }, [salida])

  // La pregunta de alcance vive en un modal hermano de este. Tras una respuesta, su cierre llega junto con el fin de la
  // escritura: el botón que la abrió todavía está deshabilitado cuando `Modal` intenta devolverle el foco, que queda en
  // `<body>`. Este cleanup corre después de ese commit.
  useEffect(() => {
    if (!preguntaDeAlcanceAbierta) return
    const nombre = refNombre.current
    return () => {
      if (document.activeElement === document.body) nombre?.focus()
    }
  }, [preguntaDeAlcanceAbierta])

  // El formulario que reemplaza a otro desmontado con el foco adentro (la recarga del artículo tras un rechazo de familia)
  // se monta con el foco en `<body>`: lo lleva a "Nombre".
  useEffect(() => {
    if (document.activeElement === document.body) refNombre.current?.focus()
  }, [])

  function cancelarSalida() {
    focoDeSalidaRef.current = salida?.disparador ?? null
    setSalida(null)
  }

  async function confirmarSalida() {
    const disparador = salida?.disparador ?? null
    // Si la escritura no llegó a empezar (otra ya estaba en curso), la confirmación sigue abierta.
    if (await accionesDeFamilia.sacar()) {
      focoDeSalidaRef.current = disparador
      setSalida(null)
    }
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

  const porcentajeIva = porcentajeDeAlicuota(alicuotasIva, valor.idAlicuotaIva)
  const costoDeLista = costoDeListaSinYConIva(
    valor.costoLista === '' ? null : Number(valor.costoLista),
    valor.costoListaIncluyeIva,
    porcentajeIva,
  )
  const costoNominal = costoNominalDelFormulario(valor, alicuotasIva, bloqueoDeFamilia)
  const bultoSinUnidadesPorBulto = bultoSinUnidades(
    valor.costoListaPor,
    valor.unidadesPorBulto.trim() === '' ? null : Number(valor.unidadesPorBulto),
  )

  // El campo que se tipeó pasa a ser la fuente del costo de lista; el otro se deriva. `CampoImporte` también emite al
  // perder el foco, y eso en el campo derivado repite su propio valor: no debe volverlo la fuente.
  function cargarCostoDeLista(n: number | null, incluyeIva: boolean, mostrado: number | null) {
    if (n === mostrado) return
    onCambio({ ...valor, costoLista: n === null ? '' : String(n), costoListaIncluyeIva: incluyeIva })
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
        nombreFamiliaNueva={valor.nombreFamiliaNueva}
        idDelArticulo={valor.id}
        codigoInterno={valor.codigoInterno}
        nombreDelArticulo={valor.nombre}
        ocupado={ocupado}
        listasPrecio={listasPrecio}
        familia={familia}
        acciones={accionesDeFamilia}
        union={union}
        errorDeFamiliaNueva={errorDeFamiliaNueva}
        sinGuardar={sinGuardar}
        nombresDeCatalogo={nombresDeCatalogo}
        nombresDeListas={nombresDeListas}
        confirmandoSalida={confirmandoSalida}
        onPedirSalida={(disparador) => setSalida({ disparador })}
        onCancelarSalida={cancelarSalida}
        onConfirmarSalida={() => void confirmarSalida()}
      />

      <form
        autoComplete="off"
        onSubmit={(e) => {
          e.preventDefault()
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
              ref={refNombre}
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

          {codigoProveedor && (
            <div className="col-md-4">
              <label className="form-label" htmlFor="art-codigo-proveedor">
                Código del proveedor
              </label>
              <input
                id="art-codigo-proveedor"
                className="form-control"
                maxLength={50}
                value={valor.idProveedorHabitual === '' ? '' : codigoProveedor.valor}
                disabled={valor.idProveedorHabitual === '' || codigoProveedor.deOtroProveedor === true}
                onChange={(e) => codigoProveedor.onCambio(e.target.value)}
              />
              <div className="form-text">
                {valor.idProveedorHabitual === ''
                  ? 'Elegí un proveedor habitual para cargar su código.'
                  : codigoProveedor.deOtroProveedor
                    ? 'Este código es del proveedor de la compra: se le asociará al artículo al confirmar la compra.'
                    : 'El código con el que el proveedor habitual identifica este artículo (opcional).'}
              </div>
            </div>
          )}

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

          {/* Quien no ve los costos no recibe ninguno: no se le ofrece cargarlos. */}
          {!valor.costosOcultos && (
            <>
            <div className="col-12">
              <strong className="text-muted small text-uppercase">Costos</strong>
            </div>

            <div className="col-md-3">
              <Etiqueta htmlFor="art-costo-lista-sin-iva" deFamilia={bloqueoDeFamilia}>
                Costo de lista sin IVA
              </Etiqueta>
              <CampoImporte
                id="art-costo-lista-sin-iva"
                className="form-control"
                valor={costoDeLista.sinIva}
                disabled={bloqueoDeFamilia}
                onChange={(n) => cargarCostoDeLista(n, false, costoDeLista.sinIva)}
              />
            </div>

            <div className="col-md-3">
              <Etiqueta htmlFor="art-costo-lista-con-iva" deFamilia={bloqueoDeFamilia}>
                Costo de lista con IVA
              </Etiqueta>
              <CampoImporte
                id="art-costo-lista-con-iva"
                className="form-control"
                valor={costoDeLista.conIva}
                disabled={bloqueoDeFamilia}
                onChange={(n) => cargarCostoDeLista(n, true, costoDeLista.conIva)}
              />
            </div>

            <div className="col-md-2">
              <Etiqueta htmlFor="art-costo-lista-por" deFamilia={bloqueoDeFamilia}>
                Costo por
              </Etiqueta>
              <select
                id="art-costo-lista-por"
                className="form-select"
                value={valor.costoListaPor}
                disabled={bloqueoDeFamilia}
                onChange={(e) => onCambio({ ...valor, costoListaPor: e.target.value as CostoListaPor })}
              >
                {COSTOS_LISTA_POR.map((c) => (
                  <option key={c.valor} value={c.valor}>
                    {c.etiqueta}
                  </option>
                ))}
              </select>
            </div>

            <div className="col-md-2">
              <Etiqueta htmlFor="art-descuento-proveedor" deFamilia={bloqueoDeFamilia}>
                Descuento (%)
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

            <div className="col-md-2">
              <label className="form-label" htmlFor="art-costo-nominal">
                Costo nominal
              </label>
              <input
                id="art-costo-nominal"
                className="form-control text-end"
                readOnly
                value={formatearImporte(costoNominal)}
              />
            </div>

            <div className="col-12">
              <div className="form-text mt-0">
                El costo nominal es el costo por unidad con IVA y descuento aplicados. Se calcula al guardar; una compra
                confirmada lo actualiza.
              </div>
              {bultoSinUnidadesPorBulto && (
                <div id="art-costo-bulto-error" className="text-danger small mt-1" role="alert">
                  Un costo por bulto necesita las unidades por bulto: completá "Unidades por bulto" con un valor mayor que
                  cero.
                </div>
              )}
            </div>
            </>
          )}

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
              <IconoDeAyuda
                id="art-acumula-en-venta-ayuda"
                texto="Desmarcado, cada vez que se agrega en el POS suma una línea nueva al ticket."
                etiqueta="Ayuda sobre acumular en una sola línea"
              />
            </div>
          </div>

          <div className="col-12 d-flex gap-2">
            <button
              type="submit"
              className="btn btn-success"
              disabled={bloqueado || bloqueadoPorCatalogos || !altaLista || bultoSinUnidadesPorBulto}
              aria-describedby={bultoSinUnidadesPorBulto ? 'art-costo-bulto-error' : undefined}
            >
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
            alEscribirPrecio={accionesDeFamilia.alEscribirPrecio}
            alCambiarLaFamilia={accionesDeFamilia.alCambiarLaFamilia}
          />
          <hr />
          <SeccionDeDisponibilidad
            idArticulo={valor.id}
            inicial={{
              disponibleVenta: valor.disponibleVenta,
              disponibleOnline: valor.disponibleOnline,
              tieneImagen: valor.tieneImagen,
            }}
            bloqueadoPorPadre={bloqueado}
            alDeEscribir={alDeEscribir}
            alCambiar={alCambiarTienda}
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
