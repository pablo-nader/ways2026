import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { clienteDeArticulos } from '../api/articulos'
import { api, ErrorApi } from '../api/cliente'
import { clienteDeFamilias } from '../api/familias'
import { clienteDeOrganizacion } from '../api/organizacion'
import {
  aAltaOferta,
  aValoresOferta,
  clienteDeOfertas,
  copiaDeFalloAlGuardarOferta,
  formularioOfertaVacio,
  opcionesDeFamiliaDeOferta,
  opcionesDeLista,
  resumenDeBeneficio,
  type AlcanceOferta,
  type BeneficioOferta,
  type FormularioOferta,
} from '../api/ofertas'
import { clienteDePrecios } from '../api/precios'
import { DIAS_SEMANA } from '../api/tipos'
import type {
  ArticuloListado,
  CategoriaListado,
  EmpresaListado,
  FamiliaListado,
  GrupoListado,
  ListaPrecioListado,
  OfertaListado,
} from '../api/tipos'
import { Box } from '../componentes/Box'
import { CampoImporte } from '../componentes/CampoImporte'
import { Cargando } from '../componentes/Cargando'
import { ConfirmacionDeBaja } from '../componentes/ConfirmacionDeBaja'
import { BotonIcono } from '../componentes/BotonIcono'
import { CampoFecha } from '../componentes/CampoFecha'
import { ErrorDeFormulario } from '../componentes/ErrorDeFormulario'
import {
  EncabezadoOrdenable,
  FilaDeFiltros,
  useFiltrosDeColumna,
  useOrdenamiento,
  type ColumnasFiltrables,
  type ColumnasOrdenables,
} from '../componentes/grilla'
import { Modal } from '../componentes/Modal'
import { atributosDeCampoDecimal, atributosDeCampoEntero } from '../formato/numeros'

const ID_FORMULARIO_OFERTA = 'formulario-oferta'

const clienteGrupos = { listar: () => api.get<GrupoListado[]>('/catalogos/grupos') }

function validarFormulario(f: FormularioOferta): string | null {
  if (!f.nombre.trim()) return 'Ingresá un nombre para la oferta.'
  if (f.alcance === 'Articulo' && f.idArticulo === '') return 'Elegí un artículo para el alcance.'
  if (f.alcance === 'Grupo' && f.idGrupo === '') return 'Elegí un grupo para el alcance.'
  if (f.alcance === 'Categoria' && f.idCategoria === '') return 'Elegí una categoría para el alcance.'
  if (f.alcance === 'Familia' && f.idFamilia === '') return 'Elegí una familia para el alcance.'
  if (f.beneficio === 'PrecioUnitario' && f.precioUnitario.trim() === '') return 'Ingresá el precio unitario del beneficio.'
  if (f.beneficio === 'Porcentaje' && f.porcentaje.trim() === '') return 'Ingresá el porcentaje del beneficio.'
  if (f.beneficio === 'ImporteFijo' && f.importeFijo.trim() === '') return 'Ingresá el importe fijo del beneficio.'
  return null
}

function textoAlcance(o: OfertaListado, grupos: GrupoListado[], categorias: CategoriaListado[], familias: FamiliaListado[]): string {
  if (o.idArticulo !== null) return o.nombreArticulo ? `Artículo: ${o.nombreArticulo}` : `Artículo #${o.idArticulo}`
  if (o.idGrupo !== null) return `Grupo: ${grupos.find((g) => g.id === o.idGrupo)?.nombre ?? o.idGrupo}`
  if (o.idFamilia != null) return `Familia: ${familias.find((f) => f.id === o.idFamilia)?.nombre ?? o.idFamilia}`
  return `Categoría: ${categorias.find((c) => c.id === o.idCategoria)?.nombre ?? o.idCategoria}`
}

const SIN_OFERTAS: readonly OfertaListado[] = []

const OPCIONES_DE_ESTADO = [
  { valor: 'Activa', etiqueta: 'Activa' },
  { valor: 'Inactiva', etiqueta: 'Inactiva' },
]
const OPCIONES_SI_NO = [
  { valor: 'Sí', etiqueta: 'Sí' },
  { valor: 'No', etiqueta: 'No' },
]

const textoDeAcumulable = (o: OfertaListado) => (o.acumulable ? 'Sí' : 'No')
const textoDeEstado = (o: OfertaListado) => (o.activo ? 'Activa' : 'Inactiva')
const textoDeOnline = (o: OfertaListado) => (o.disponibleOnline && o.activo ? 'Sí' : 'No')

/** Lo que cada tipo de beneficio muestra, en el orden en que se agrupan al ordenar: el monto va primero por tipo y después por valor. */
function montoDeBeneficio(o: OfertaListado): { tipo: number; monto: number } | null {
  if (o.porcentaje !== null) return { tipo: 0, monto: o.porcentaje }
  if (o.importeFijo !== null) return { tipo: 1, monto: o.importeFijo }
  if (o.precioUnitario !== null) return { tipo: 2, monto: o.precioUnitario }
  return null
}

function compararBeneficios(a: OfertaListado, b: OfertaListado): number {
  const x = montoDeBeneficio(a)
  const y = montoDeBeneficio(b)
  if (x === null || y === null) return 0

  return x.tipo - y.tipo || x.monto - y.monto
}

/** Una entrada por columna del encabezado, en su orden; `null` es Acciones. */
const CELDAS_DE_FILTROS = ['nombre', 'alcance', 'beneficio', 'prioridad', 'acumulable', 'estado', 'online', null]

/**
 * ABM dedicado de ofertas (design decision 9: no la máquina genérica de catálogos, no la
 * pantalla genérica de descriptores) — Slice 4, dependiente solo del CRUD de la Slice 2. El
 * listado del servidor no completa `idsListas` por fila (evita el N+1, ver el doc-comment de
 * `OfertaListado`), así que la tabla nunca muestra el targeting de listas: eso solo se ve/edita
 * en el detalle, después de `obtener`.
 *
 * El alta y la edición viven en un `Modal` (mismo criterio que `Pagos.tsx`): un guardado exitoso
 * lo cierra y el aviso queda en la pantalla; los errores que ocurren con el modal abierto se
 * muestran adentro (`errorFormulario`), porque un alert de la pantalla quedaría detrás del fondo.
 */
export function Ofertas() {
  const [ofertas, setOfertas] = useState<OfertaListado[] | null>(null)
  const [grupos, setGrupos] = useState<GrupoListado[]>([])
  const [categorias, setCategorias] = useState<CategoriaListado[]>([])
  const [familias, setFamilias] = useState<FamiliaListado[]>([])
  const [empresas, setEmpresas] = useState<EmpresaListado[]>([])
  const [listasPrecio, setListasPrecio] = useState<ListaPrecioListado[]>([])
  const [avisosCatalogos, setAvisosCatalogos] = useState<string[]>([])
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')
  const [errorFormulario, setErrorFormulario] = useState('')
  /** Cuenta los rechazos de validación: el mensaje puede repetirse sin cambiar el estado. */
  const [rechazosDeValidacion, setRechazosDeValidacion] = useState(0)
  const [formulario, setFormulario] = useState<FormularioOferta | null>(null)
  const [guardando, setGuardando] = useState(false)
  const [eliminando, setEliminando] = useState(false)
  /** Baja pendiente de confirmación, con el botón que la pidió (capturado en su `onClick`). */
  const [baja, setBaja] = useState<{ oferta: OfertaListado; disparador: HTMLElement } | null>(null)
  const [errorDeBaja, setErrorDeBaja] = useState('')
  /** Espejo síncrono de "hay una baja en vuelo": dos clics en el mismo tick no emiten dos DELETE. */
  const eliminandoRef = useRef(false)
  const tokenEdicionRef = useRef(0)
  /** Destino de foco cuando el modal se cierra con la pantalla inerte (guardado en vuelo): ningún
   * botón es alcanzable en ese momento. */
  const contenedorRef = useRef<HTMLDivElement>(null)
  const generacionCargaRef = useRef(0)
  const cargaInicialHechaRef = useRef(false)
  const ocupado = guardando || eliminando

  // El texto del alcance depende de los catálogos cargados: las columnas se rehacen solo cuando ellos cambian.
  const filtrables = useMemo<ColumnasFiltrables<OfertaListado>>(
    () => ({
      nombre: { etiqueta: 'Nombre', valor: (o) => o.nombre },
      alcance: { etiqueta: 'Alcance', valor: (o) => textoAlcance(o, grupos, categorias, familias) },
      beneficio: { etiqueta: 'Beneficio', valor: resumenDeBeneficio },
      prioridad: { etiqueta: 'Prioridad', valor: (o) => o.prioridad },
      acumulable: { etiqueta: 'Acumulable', valor: textoDeAcumulable, opciones: OPCIONES_SI_NO },
      estado: { etiqueta: 'Estado', valor: textoDeEstado, opciones: OPCIONES_DE_ESTADO },
      online: { etiqueta: 'Online', valor: textoDeOnline, opciones: OPCIONES_SI_NO },
    }),
    [grupos, categorias, familias],
  )
  const ordenables = useMemo<ColumnasOrdenables<OfertaListado>>(
    () => ({
      nombre: { valor: (o) => o.nombre },
      alcance: { valor: (o) => textoAlcance(o, grupos, categorias, familias) },
      beneficio: { valor: (o) => (montoDeBeneficio(o) === null ? null : resumenDeBeneficio(o)), comparar: compararBeneficios },
      prioridad: { valor: (o) => o.prioridad },
      acumulable: { valor: textoDeAcumulable },
      estado: { valor: textoDeEstado },
      online: { valor: textoDeOnline },
    }),
    [grupos, categorias, familias],
  )
  const { filas: filtradas, filtros, cambiar: cambiarFiltro, limpiar: limpiarFiltros, hayFiltros } = useFiltrosDeColumna(
    ofertas ?? SIN_OFERTAS,
    filtrables,
  )
  const { filas: visibles, orden, alternar } = useOrdenamiento(filtradas, ordenables)

  function invalidarEdicionEnCurso(): number {
    return (tokenEdicionRef.current += 1)
  }

  function agregarAvisoCatalogo(mensaje: string) {
    setAvisosCatalogos((prev) => (prev.includes(mensaje) ? prev : [...prev, mensaje]))
  }

  const cargar = useCallback(async (opciones?: { relanzarError?: boolean }) => {
    const generacion = (generacionCargaRef.current += 1)
    setCargando(true)
    setError('')
    try {
      const lista = await clienteDeOfertas.listar(false)
      if (generacionCargaRef.current !== generacion) return
      setOfertas(lista)
    } catch (e) {
      if (generacionCargaRef.current === generacion) {
        setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar las ofertas.')
      }
      if (opciones?.relanzarError && generacionCargaRef.current === generacion) throw e
    } finally {
      if (generacionCargaRef.current === generacion) {
        setCargando(false)
        cargaInicialHechaRef.current = true
      }
    }
  }, [])

  useEffect(() => {
    void cargar()
    clienteGrupos
      .listar()
      .then(setGrupos)
      .catch(() => {
        setGrupos([])
        agregarAvisoCatalogo('No se pudieron cargar los grupos: el alcance por grupo no va a estar disponible hasta recargar la página.')
      })
    api
      .get<CategoriaListado[]>('/catalogos/categorias')
      .then(setCategorias)
      .catch(() => {
        setCategorias([])
        agregarAvisoCatalogo(
          'No se pudieron cargar las categorías: el alcance por categoría no va a estar disponible hasta recargar la página.',
        )
      })
    clienteDeFamilias
      .listar()
      .then(setFamilias)
      .catch(() => {
        setFamilias([])
        agregarAvisoCatalogo(
          'No se pudieron cargar las familias: no hay familias para elegir hasta recargar la página. Una oferta que ya apunta a una familia se muestra por su número y se puede guardar sin cambiarla.',
        )
      })
    clienteDeOrganizacion
      .listarEmpresas()
      .then(setEmpresas)
      .catch(() => {
        setEmpresas([])
        agregarAvisoCatalogo(
          'No se pudieron cargar las empresas: el selector de empresa no va a estar disponible (la oferta se crea para todo el tenant).',
        )
      })
    clienteDePrecios
      .listasDePrecio()
      .then(setListasPrecio)
      .catch(() => {
        setListasPrecio([])
        agregarAvisoCatalogo(
          'No se pudieron cargar las listas de precio: el targeting manual de listas no va a estar disponible (la oferta va a aplicar a todas las listas).',
        )
      })
  }, [cargar])

  async function abrirNuevo() {
    if (ocupado) return
    invalidarEdicionEnCurso()
    setGuardando(false)
    setFormulario(formularioOfertaVacio())
    setAviso('')
    setError('')
    setErrorFormulario('')
  }

  function cancelarEdicion() {
    if (ocupado) return
    invalidarEdicionEnCurso()
    setGuardando(false)
    setFormulario(null)
  }

  async function abrirEdicion(o: OfertaListado) {
    if (ocupado) return
    setError('')
    const token = invalidarEdicionEnCurso()
    try {
      const detalle = await clienteDeOfertas.obtener(o.id)
      if (tokenEdicionRef.current !== token) return

      let nombreArticulo = ''
      if (detalle.idArticulo !== null) {
        try {
          const articulo = await clienteDeArticulos.obtener(detalle.idArticulo)
          if (tokenEdicionRef.current !== token) return
          nombreArticulo = articulo.nombre
        } catch {
          if (tokenEdicionRef.current !== token) return
          nombreArticulo = ''
        }
      }

      if (tokenEdicionRef.current !== token) return
      setFormulario({ ...aValoresOferta(detalle), nombreArticulo })
      setGuardando(false)
      setAviso('')
      setErrorFormulario('')
    } catch (e) {
      if (tokenEdicionRef.current !== token) return
      setGuardando(false)
      setError(e instanceof ErrorApi ? e.message : 'No se pudo abrir la oferta.')
    }
  }

  async function guardar() {
    if (ocupado) return
    if (!formulario) return

    const mensajeInvalido = validarFormulario(formulario)
    if (mensajeInvalido) {
      setErrorFormulario(mensajeInvalido)
      setRechazosDeValidacion((n) => n + 1)
      return
    }

    const token = invalidarEdicionEnCurso()
    setGuardando(true)
    setError('')
    setErrorFormulario('')
    setAviso('')

    try {
      if (formulario.id === null) {
        await clienteDeOfertas.crear(aAltaOferta(formulario))
        // La oferta nueva puede caer fuera de los filtros vigentes: se limpian para que la fila creada se vea.
        limpiarFiltros()
        if (tokenEdicionRef.current === token) {
          setAviso(`Oferta "${formulario.nombre}" creada.`)
          setFormulario(null)
        }
      } else {
        await clienteDeOfertas.actualizar(formulario.id, aAltaOferta(formulario))
        if (tokenEdicionRef.current === token) {
          setAviso(`Oferta "${formulario.nombre}" actualizada.`)
          setFormulario(null)
        }
      }

      // El refresco de la tabla no pertenece al token de edición: un fallo acá es solo de
      // vista, el guardado ya tuvo éxito (mismo criterio que Articulos.tsx). El modal ya se
      // cerró, así que el aviso va a la pantalla.
      try {
        await cargar({ relanzarError: true })
      } catch {
        if (tokenEdicionRef.current === token) {
          setError('La oferta se guardó, pero no se pudo actualizar el listado. Volvé a entrar para verla.')
        }
      }
    } catch (e) {
      if (tokenEdicionRef.current === token) {
        setErrorFormulario(copiaDeFalloAlGuardarOferta(e))
      }
    } finally {
      if (tokenEdicionRef.current === token) setGuardando(false)
    }
  }

  function pedirBaja(o: OfertaListado, disparador: HTMLElement) {
    // Una apertura de edición todavía en vuelo no puede montar su modal encima de la confirmación.
    invalidarEdicionEnCurso()
    setErrorDeBaja('')
    setBaja({ oferta: o, disparador })
  }

  function cancelarBaja() {
    setErrorDeBaja('')
    setBaja(null)
  }

  async function confirmarBaja() {
    if (baja === null || eliminandoRef.current) return
    eliminandoRef.current = true
    const o = baja.oferta

    invalidarEdicionEnCurso()
    setError('')
    setAviso('')
    setErrorDeBaja('')
    setEliminando(true)
    try {
      try {
        await clienteDeOfertas.eliminar(o.id)
      } catch (e) {
        setErrorDeBaja(e instanceof ErrorApi ? e.message : 'No se pudo dar de baja la oferta.')
        return
      }

      setAviso(`Oferta "${o.nombre}" dada de baja.`)
      setBaja(null)
      if (formulario?.id === o.id) setFormulario(null)
      try {
        await cargar({ relanzarError: true })
      } catch {
        setError('La oferta se dio de baja, pero no se pudo actualizar el listado. Volvé a entrar para verla.')
      }
    } finally {
      eliminandoRef.current = false
      setEliminando(false)
    }
  }

  const herramientas = (
    <nav className="p-2 d-flex gap-2">
      <BotonIcono icono="agregar" className="text-nowrap" disabled={ocupado} onClick={abrirNuevo} />
    </nav>
  )

  return (
    <div ref={contenedorRef} tabIndex={-1} style={{ outline: 'none' }} className="container-fluid py-4">
      <Box titulo="Ofertas" variante="inverse" herramientas={herramientas}>
        {error && <div className="alert alert-danger">{error}</div>}
        {aviso && <div className="alert alert-success">{aviso}</div>}
        {avisosCatalogos.length > 0 && (
          <div className="alert alert-warning">{avisosCatalogos.join(' ')}</div>
        )}

        {cargando && !cargaInicialHechaRef.current ? (
          <Cargando />
        ) : (
          <div className="table-responsive">
            <table className="table table-striped table-hover table-bordered align-middle">
              <thead>
                <tr>
                  <EncabezadoOrdenable columna="nombre" orden={orden} onAlternar={alternar}>
                    Nombre
                  </EncabezadoOrdenable>
                  <EncabezadoOrdenable columna="alcance" orden={orden} onAlternar={alternar}>
                    Alcance
                  </EncabezadoOrdenable>
                  <EncabezadoOrdenable columna="beneficio" orden={orden} onAlternar={alternar}>
                    Beneficio
                  </EncabezadoOrdenable>
                  <EncabezadoOrdenable columna="prioridad" orden={orden} onAlternar={alternar}>
                    Prioridad
                  </EncabezadoOrdenable>
                  <EncabezadoOrdenable columna="acumulable" orden={orden} onAlternar={alternar}>
                    Acumulable
                  </EncabezadoOrdenable>
                  <EncabezadoOrdenable columna="estado" orden={orden} onAlternar={alternar}>
                    Estado
                  </EncabezadoOrdenable>
                  <EncabezadoOrdenable columna="online" orden={orden} onAlternar={alternar}>
                    Online
                  </EncabezadoOrdenable>
                  <th scope="col" className="text-end">
                    Acciones
                  </th>
                </tr>
                <FilaDeFiltros
                  celdas={CELDAS_DE_FILTROS}
                  columnas={filtrables}
                  filtros={filtros}
                  onCambio={cambiarFiltro}
                  onLimpiar={limpiarFiltros}
                  hayFiltros={hayFiltros}
                  deshabilitado={ocupado || baja !== null}
                />
              </thead>
              <tbody>
                {visibles.map((o) => (
                  <tr key={o.id}>
                    <td>{o.nombre}</td>
                    <td>{textoAlcance(o, grupos, categorias, familias)}</td>
                    <td>{resumenDeBeneficio(o)}</td>
                    <td>{o.prioridad}</td>
                    <td>{textoDeAcumulable(o)}</td>
                    <td>
                      <span className={`badge ${o.activo ? 'text-bg-success' : 'text-bg-secondary'}`}>
                        {textoDeEstado(o)}
                      </span>
                    </td>
                    <td>{textoDeOnline(o)}</td>
                    <td className="text-end text-nowrap">
                      <BotonIcono
                        icono="editar"
                        className="me-1"
                        disabled={ocupado}
                        onClick={() => abrirEdicion(o)}
                      />
                      <BotonIcono
                        icono="eliminar"
                        disabled={ocupado}
                        onClick={(evento) => pedirBaja(o, evento.currentTarget)}
                      />
                    </td>
                  </tr>
                ))}
                {ofertas !== null && visibles.length === 0 && (
                  <tr>
                    <td colSpan={8} className="text-center text-muted py-4">
                      {ofertas.length === 0 ? 'No hay ofertas cargadas.' : 'Ningún registro coincide con los filtros.'}
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        )}
      </Box>

      {formulario && (
        <ModalDeOferta
          key={formulario.id ?? 'nuevo'}
          valor={formulario}
          grupos={grupos}
          categorias={categorias}
          familias={familias}
          empresas={empresas}
          listasPrecio={listasPrecio}
          guardando={guardando}
          ocupado={ocupado}
          error={errorFormulario}
          disparoDelError={rechazosDeValidacion}
          focoDeReserva={contenedorRef}
          onCambio={setFormulario}
          onGuardar={guardar}
          onCancelar={cancelarEdicion}
        />
      )}

      {baja && (
        <ConfirmacionDeBaja
          titulo={`la oferta "${baja.oferta.nombre}"`}
          ocupado={eliminando}
          disparador={baja.disparador}
          regreso={contenedorRef}
          error={errorDeBaja || null}
          onConfirmar={() => void confirmarBaja()}
          onCancelar={cancelarBaja}
        />
      )}
    </div>
  )
}

function ModalDeOferta({
  valor,
  grupos,
  categorias,
  familias,
  empresas,
  listasPrecio,
  guardando,
  ocupado,
  error,
  disparoDelError,
  focoDeReserva,
  onCambio,
  onGuardar,
  onCancelar,
}: {
  valor: FormularioOferta
  grupos: GrupoListado[]
  categorias: CategoriaListado[]
  familias: FamiliaListado[]
  empresas: EmpresaListado[]
  listasPrecio: ListaPrecioListado[]
  guardando: boolean
  ocupado: boolean
  error: string
  disparoDelError: number
  focoDeReserva: React.RefObject<HTMLElement | null>
  onCambio: (f: FormularioOferta) => void
  onGuardar: () => void
  onCancelar: () => void
}) {
  const esNuevo = valor.id === null
  const opcionesLista = opcionesDeLista(listasPrecio)
  const opcionesFamilia = opcionesDeFamiliaDeOferta(familias)
  // Una familia que no cargó (o no está en el listado) no puede cambiar el valor de la oferta en silencio: la
  // opción actual se conserva rotulada como no disponible.
  const familiaFaltante = valor.idFamilia !== '' && !familias.some((f) => f.id === valor.idFamilia)

  function alternarDia(dia: number) {
    const yaEsta = valor.diasSemana.includes(dia)
    onCambio({ ...valor, diasSemana: yaEsta ? valor.diasSemana.filter((d) => d !== dia) : [...valor.diasSemana, dia] })
  }

  function alternarLista(id: number) {
    const yaEsta = valor.idsListas.includes(id)
    onCambio({ ...valor, idsListas: yaEsta ? valor.idsListas.filter((x) => x !== id) : [...valor.idsListas, id] })
  }

  const pie = (
    <>
      <button type="button" className="btn btn-outline-secondary" onClick={onCancelar} disabled={ocupado}>
        Cancelar
      </button>
      <button type="submit" form={ID_FORMULARIO_OFERTA} className="btn btn-success" disabled={ocupado}>
        {guardando ? 'Guardando…' : 'Guardar'}
      </button>
    </>
  )

  return (
    <Modal
      titulo={esNuevo ? 'Nueva oferta' : 'Editar oferta'}
      tamano="xl"
      desplazable
      ocupado={ocupado}
      focoDeReserva={focoDeReserva}
      pie={pie}
      onCerrar={onCancelar}
    >
      <ErrorDeFormulario mensaje={error} disparo={disparoDelError} />
      <form
        id={ID_FORMULARIO_OFERTA}
        autoComplete="off"
        onSubmit={(e) => {
          e.preventDefault()
          if (ocupado) return
          onGuardar()
        }}
      >
        {/* fieldset disabled: cascada nativa a todos los controles anidados mientras hay un
            guardado en vuelo (react-async-state rule 5/9), mismo criterio que Articulos.tsx. */}
        <fieldset disabled={ocupado} className="row g-3 border-0 p-0 m-0">
          <div className="col-12">
            <strong className="text-muted small text-uppercase">Identificación</strong>
          </div>

          <div className="col-md-5">
            <label className="form-label" htmlFor="of-nombre">
              Nombre
            </label>
            <input
              id="of-nombre"
              autoFocus
              className="form-control"
              maxLength={150}
              value={valor.nombre}
              onChange={(e) => onCambio({ ...valor, nombre: e.target.value })}
              required
            />
          </div>

          <div className="col-md-2">
            <label className="form-label" htmlFor="of-prioridad">
              Prioridad
            </label>
            <input
              id="of-prioridad"
              {...atributosDeCampoEntero()}
              className="form-control"
              value={valor.prioridad}
              onChange={(e) => onCambio({ ...valor, prioridad: e.target.value })}
            />
          </div>

          <div className="col-md-2 d-flex align-items-end">
            <div className="form-check">
              <input
                id="of-acumulable"
                type="checkbox"
                className="form-check-input"
                checked={valor.acumulable}
                onChange={(e) => onCambio({ ...valor, acumulable: e.target.checked })}
              />
              <label className="form-check-label" htmlFor="of-acumulable">
                Acumulable
              </label>
            </div>
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="of-empresa">
              Empresa
            </label>
            <select
              id="of-empresa"
              className="form-select"
              value={valor.idEmpresa}
              onChange={(e) => onCambio({ ...valor, idEmpresa: e.target.value === '' ? '' : Number(e.target.value) })}
            >
              <option value="">Todo el tenant</option>
              {empresas.map((e) => (
                <option key={e.id} value={e.id}>
                  {e.razonSocial}
                  {e.nombreFantasia ? ` (${e.nombreFantasia})` : ''}
                </option>
              ))}
            </select>
          </div>

          <div className="col-12">
            <strong className="text-muted small text-uppercase">Alcance (exactamente uno)</strong>
          </div>

          <div className="col-12 d-flex gap-3">
            {(['Articulo', 'Grupo', 'Categoria', 'Familia'] as AlcanceOferta[]).map((opcion) => (
              <div className="form-check" key={opcion}>
                <input
                  id={`of-alcance-${opcion}`}
                  type="radio"
                  name="of-alcance"
                  className="form-check-input"
                  checked={valor.alcance === opcion}
                  onChange={() => onCambio({ ...valor, alcance: opcion })}
                />
                <label className="form-check-label" htmlFor={`of-alcance-${opcion}`}>
                  {opcion === 'Articulo' ? 'Artículo' : opcion === 'Grupo' ? 'Grupo' : opcion === 'Familia' ? 'Familia' : 'Categoría'}
                </label>
              </div>
            ))}
          </div>

          {valor.alcance === 'Articulo' && (
            <div className="col-12">
              <SelectorDeArticulo
                idArticulo={valor.idArticulo}
                nombreArticulo={valor.nombreArticulo}
                disabled={ocupado}
                onSeleccionar={(id, nombre) => onCambio({ ...valor, idArticulo: id, nombreArticulo: nombre })}
              />
            </div>
          )}

          {valor.alcance === 'Grupo' && (
            <div className="col-md-4">
              <label className="form-label" htmlFor="of-grupo">
                Grupo objetivo
              </label>
              <select
                id="of-grupo"
                className="form-select"
                value={valor.idGrupo}
                onChange={(e) => onCambio({ ...valor, idGrupo: e.target.value === '' ? '' : Number(e.target.value) })}
              >
                <option value="" disabled>
                  Elegir…
                </option>
                {grupos.map((g) => (
                  <option key={g.id} value={g.id}>
                    {g.nombre}
                  </option>
                ))}
              </select>
            </div>
          )}

          {valor.alcance === 'Categoria' && (
            <div className="col-md-4">
              <label className="form-label" htmlFor="of-categoria">
                Categoría objetivo
              </label>
              <select
                id="of-categoria"
                className="form-select"
                value={valor.idCategoria}
                onChange={(e) => onCambio({ ...valor, idCategoria: e.target.value === '' ? '' : Number(e.target.value) })}
              >
                <option value="" disabled>
                  Elegir…
                </option>
                {categorias.map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.nombre}
                  </option>
                ))}
              </select>
            </div>
          )}

          {valor.alcance === 'Familia' && (
            <div className="col-md-4">
              <label className="form-label" htmlFor="of-familia">
                Familia objetivo
              </label>
              <select
                id="of-familia"
                className="form-select"
                value={valor.idFamilia}
                onChange={(e) => onCambio({ ...valor, idFamilia: e.target.value === '' ? '' : Number(e.target.value) })}
              >
                <option value="" disabled>
                  Elegir…
                </option>
                {familiaFaltante && <option value={valor.idFamilia}>Familia #{valor.idFamilia} (no disponible)</option>}
                {opcionesFamilia.map((o) => (
                  <option key={o.valor} value={o.valor}>
                    {o.etiqueta}
                  </option>
                ))}
              </select>
            </div>
          )}

          <div className="col-12">
            <strong className="text-muted small text-uppercase">Vigencia</strong>
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="of-fecha-desde">
              Fecha desde
            </label>
            <CampoFecha
              id="of-fecha-desde"
              className="form-control"
              value={valor.fechaDesde}
              onChange={(fecha) => onCambio({ ...valor, fechaDesde: fecha })}
            />
          </div>
          <div className="col-md-3">
            <label className="form-label" htmlFor="of-fecha-hasta">
              Fecha hasta
            </label>
            <CampoFecha
              id="of-fecha-hasta"
              className="form-control"
              value={valor.fechaHasta}
              onChange={(fecha) => onCambio({ ...valor, fechaHasta: fecha })}
            />
          </div>
          <div className="col-md-3">
            <label className="form-label" htmlFor="of-hora-desde">
              Hora desde
            </label>
            <input
              id="of-hora-desde"
              type="time"
              className="form-control"
              value={valor.horaDesde}
              onChange={(e) => onCambio({ ...valor, horaDesde: e.target.value })}
            />
          </div>
          <div className="col-md-3">
            <label className="form-label" htmlFor="of-hora-hasta">
              Hora hasta
            </label>
            <input
              id="of-hora-hasta"
              type="time"
              className="form-control"
              value={valor.horaHasta}
              onChange={(e) => onCambio({ ...valor, horaHasta: e.target.value })}
            />
          </div>

          <div className="col-12">
            <div className="form-text mb-1">Días de la semana (sin marcar ninguno = todos los días).</div>
            <div className="d-flex flex-wrap gap-3">
              {DIAS_SEMANA.map((d) => (
                <div className="form-check" key={d.valor}>
                  <input
                    id={`of-dia-${d.valor}`}
                    type="checkbox"
                    className="form-check-input"
                    checked={valor.diasSemana.includes(d.valor)}
                    onChange={() => alternarDia(d.valor)}
                  />
                  <label className="form-check-label" htmlFor={`of-dia-${d.valor}`}>
                    {d.etiqueta}
                  </label>
                </div>
              ))}
            </div>
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="of-cantidad-minima">
              Cantidad mínima
            </label>
            <input
              id="of-cantidad-minima"
              {...atributosDeCampoDecimal({ decimales: 3 })}
              className="form-control"
              placeholder="Sin mínimo (oferta directa)"
              value={valor.cantidadMinima}
              onChange={(e) => {
                const cantidadMinima = e.target.value
                onCambio({ ...valor, cantidadMinima, soloMultiplos: cantidadMinima.trim() === '' ? false : valor.soloMultiplos })
              }}
            />
          </div>

          <div className="col-md-9 d-flex flex-column justify-content-end">
            <div className="form-check">
              <input
                id="of-solo-multiplos"
                type="checkbox"
                className="form-check-input"
                checked={valor.soloMultiplos}
                disabled={valor.cantidadMinima.trim() === ''}
                aria-describedby="of-solo-multiplos-ayuda"
                onChange={(e) => onCambio({ ...valor, soloMultiplos: e.target.checked })}
              />
              <label className="form-check-label" htmlFor="of-solo-multiplos">
                Solo múltiplos de la cantidad
              </label>
            </div>
            <div className="form-text" id="of-solo-multiplos-ayuda">
              Solo se rebajan los grupos completos de la cantidad mínima; el resto paga el precio normal. Ej.: 2 x $7.500, con 3 unidades =
              $7.500 + 1 a precio normal.
              {valor.cantidadMinima.trim() === '' && ' Requiere cargar una cantidad mínima.'}
            </div>
          </div>

          <div className="col-12">
            <strong className="text-muted small text-uppercase">Beneficio (exactamente uno)</strong>
          </div>

          <div className="col-12 d-flex gap-3">
            {(['Porcentaje', 'ImporteFijo', 'PrecioUnitario'] as BeneficioOferta[]).map((opcion) => (
              <div className="form-check" key={opcion}>
                <input
                  id={`of-beneficio-${opcion}`}
                  type="radio"
                  name="of-beneficio"
                  className="form-check-input"
                  checked={valor.beneficio === opcion}
                  onChange={() => onCambio({ ...valor, beneficio: opcion })}
                />
                <label className="form-check-label" htmlFor={`of-beneficio-${opcion}`}>
                  {opcion === 'Porcentaje' ? 'Porcentaje' : opcion === 'ImporteFijo' ? 'Importe fijo por unidad' : 'Precio unitario'}
                </label>
              </div>
            ))}
          </div>

          {valor.beneficio === 'Porcentaje' && (
            <div className="col-md-3">
              <label className="form-label" htmlFor="of-porcentaje">
                Porcentaje (%)
              </label>
              <input
                id="of-porcentaje"
                {...atributosDeCampoDecimal({ decimales: 2 })}
                className="form-control"
                value={valor.porcentaje}
                onChange={(e) => onCambio({ ...valor, porcentaje: e.target.value })}
              />
            </div>
          )}

          {valor.beneficio === 'ImporteFijo' && (
            <div className="col-md-3">
              <label className="form-label" htmlFor="of-importe-fijo">
                Importe fijo por unidad ($)
              </label>
              <CampoImporte
                id="of-importe-fijo"
                className="form-control"
                valor={valor.importeFijo === '' ? null : Number(valor.importeFijo)}
                onChange={(n) => onCambio({ ...valor, importeFijo: n === null ? '' : String(n) })}
              />
            </div>
          )}

          {valor.beneficio === 'PrecioUnitario' && (
            <div className="col-md-3">
              <label className="form-label" htmlFor="of-precio-unitario">
                Precio unitario ($)
              </label>
              <CampoImporte
                id="of-precio-unitario"
                className="form-control"
                valor={valor.precioUnitario === '' ? null : Number(valor.precioUnitario)}
                onChange={(n) => onCambio({ ...valor, precioUnitario: n === null ? '' : String(n) })}
              />
            </div>
          )}

          <div className="col-12">
            <strong className="text-muted small text-uppercase">Listas de precio</strong>
          </div>

          <div className="col-12">
            <div className="form-text mb-1">
              Sin marcar ninguna lista, la oferta aplica a <strong>todas</strong> las listas del tenant.
            </div>
            <div className="d-flex flex-wrap gap-3">
              {opcionesLista.map((o) => (
                <div className="form-check" key={o.valor}>
                  <input
                    id={`of-lista-${o.valor}`}
                    type="checkbox"
                    className="form-check-input"
                    checked={valor.idsListas.includes(Number(o.valor))}
                    onChange={() => alternarLista(Number(o.valor))}
                  />
                  <label className="form-check-label" htmlFor={`of-lista-${o.valor}`}>
                    {o.etiqueta}
                  </label>
                </div>
              ))}
              {opcionesLista.length === 0 && <span className="text-muted small">No hay listas de precio activas.</span>}
            </div>
          </div>

          <div className="col-md-3 d-flex align-items-end">
            <div className="form-check">
              <input
                id="of-activo"
                type="checkbox"
                className="form-check-input"
                checked={valor.activo}
                onChange={(e) => onCambio({ ...valor, activo: e.target.checked })}
              />
              <label className="form-check-label" htmlFor="of-activo">
                Activa
              </label>
            </div>
          </div>

          <div className="col-md-3 d-flex align-items-end">
            <div className="form-check">
              <input
                id="of-disponible-online"
                type="checkbox"
                className="form-check-input"
                checked={valor.disponibleOnline}
                onChange={(e) => onCambio({ ...valor, disponibleOnline: e.target.checked })}
                aria-describedby="of-disponible-online-ayuda"
              />
              <label className="form-check-label" htmlFor="of-disponible-online">
                Disponible online
              </label>
              <div id="of-disponible-online-ayuda" className="form-text">
                Desmarcalo para que la tienda online no la aplique a los precios.
              </div>
            </div>
          </div>
        </fieldset>
      </form>
    </Modal>
  )
}

/**
 * Picker de artículo (design: "articulo search/select"): sin catálogo completo cargado en
 * memoria (podrían ser miles), busca contra `/api/articulos` con el mismo cliente que la
 * pantalla de artículos. Búsqueda generacional (react-async-state rule 2): una respuesta tardía
 * de una búsqueda anterior no puede pisar los resultados de la más reciente.
 */
function SelectorDeArticulo({
  idArticulo,
  nombreArticulo,
  disabled,
  onSeleccionar,
}: {
  idArticulo: number | ''
  nombreArticulo: string
  disabled: boolean
  onSeleccionar: (id: number, nombre: string) => void
}) {
  const [termino, setTermino] = useState('')
  const [resultados, setResultados] = useState<ArticuloListado[]>([])
  const [buscando, setBuscando] = useState(false)
  const [error, setError] = useState('')
  const generacionRef = useRef(0)

  async function buscar() {
    if (buscando || disabled) return
    const generacion = (generacionRef.current += 1)
    setBuscando(true)
    setError('')
    try {
      const pagina = await clienteDeArticulos.listar(termino, false)
      if (generacionRef.current !== generacion) return
      setResultados(pagina.items)
    } catch (e) {
      if (generacionRef.current === generacion) {
        setError(e instanceof ErrorApi ? e.message : 'No se pudieron buscar artículos.')
      }
    } finally {
      if (generacionRef.current === generacion) setBuscando(false)
    }
  }

  return (
    <div>
      <label className="form-label" htmlFor="of-buscar-articulo">
        Buscar artículo
      </label>
      {idArticulo !== '' && (
        <div className="mb-2">
          Seleccionado: <strong>{nombreArticulo || `Artículo #${idArticulo}`}</strong>
        </div>
      )}
      <div className="input-group mb-2" style={{ maxWidth: 420 }}>
        <input
          id="of-buscar-articulo"
          type="search"
          className="form-control"
          placeholder="Buscar por nombre o código interno…"
          value={termino}
          disabled={disabled}
          onChange={(e) => setTermino(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' && (e.preventDefault(), buscar())}
        />
        <button type="button" className="btn btn-outline-primary" disabled={disabled || buscando} onClick={buscar}>
          {buscando ? 'Buscando…' : 'Buscar'}
        </button>
      </div>
      {error && <div className="alert alert-danger py-1 px-2 small">{error}</div>}
      {resultados.length > 0 && (
        <select
          className="form-select"
          size={Math.min(resultados.length, 6)}
          disabled={disabled}
          value=""
          onChange={(e) => {
            const encontrado = resultados.find((a) => a.id === Number(e.target.value))
            if (encontrado) onSeleccionar(encontrado.id, encontrado.nombre)
          }}
        >
          <option value="" disabled>
            Elegí un resultado…
          </option>
          {resultados.map((a) => (
            <option key={a.id} value={a.id}>
              {a.codigoInterno} — {a.nombre}
            </option>
          ))}
        </select>
      )}
    </div>
  )
}
