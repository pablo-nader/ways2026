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
  resumenDeBeneficio,
  type FormularioOferta,
} from '../api/ofertas'
import { clienteDePrecios } from '../api/precios'
import type {
  CategoriaListado,
  EmpresaListado,
  FamiliaListado,
  GrupoListado,
  ListaPrecioListado,
  OfertaListado,
} from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { ConfirmacionDeBaja } from '../componentes/ConfirmacionDeBaja'
import { BotonIcono } from '../componentes/BotonIcono'
import {
  EncabezadoOrdenable,
  FilaDeFiltros,
  useFiltrosDeColumna,
  useOrdenamiento,
  type ColumnasFiltrables,
  type ColumnasOrdenables,
} from '../componentes/grilla'
import { ModalDeOferta } from './ofertas/ModalDeOferta'
import { validarFormulario } from './ofertas/validarFormulario'

const clienteGrupos = { listar: () => api.get<GrupoListado[]>('/catalogos/grupos') }

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
          'No se pudieron cargar las empresas: el selector de empresa no va a estar disponible (la oferta se crea para todas las empresas).',
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
