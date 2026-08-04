import { useCallback, useEffect, useRef, useState } from 'react'
import { clienteDeArticulos } from '../api/articulos'
import { api, ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import {
  aAltaOferta,
  aValoresOferta,
  clienteDeOfertas,
  formularioOfertaVacio,
  opcionesDeLista,
  resumenDeBeneficio,
  type AlcanceOferta,
  type BeneficioOferta,
  type FormularioOferta,
} from '../api/ofertas'
import { clienteDePrecios } from '../api/precios'
import { DIAS_SEMANA } from '../api/tipos'
import type { ArticuloListado, CategoriaListado, EmpresaListado, GrupoListado, ListaPrecioListado, OfertaListado } from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'

const clienteGrupos = { listar: () => api.get<GrupoListado[]>('/catalogos/grupos') }

function validarFormulario(f: FormularioOferta): string | null {
  if (!f.nombre.trim()) return 'Ingresá un nombre para la oferta.'
  if (f.alcance === 'Articulo' && f.idArticulo === '') return 'Elegí un artículo para el alcance.'
  if (f.alcance === 'Grupo' && f.idGrupo === '') return 'Elegí un grupo para el alcance.'
  if (f.alcance === 'Categoria' && f.idCategoria === '') return 'Elegí una categoría para el alcance.'
  if (f.beneficio === 'PrecioUnitario' && f.precioUnitario.trim() === '') return 'Ingresá el precio unitario del beneficio.'
  if (f.beneficio === 'Porcentaje' && f.porcentaje.trim() === '') return 'Ingresá el porcentaje del beneficio.'
  if (f.beneficio === 'ImporteFijo' && f.importeFijo.trim() === '') return 'Ingresá el importe fijo del beneficio.'
  return null
}

function textoAlcance(o: OfertaListado, grupos: GrupoListado[], categorias: CategoriaListado[]): string {
  if (o.idArticulo !== null) return `Artículo #${o.idArticulo}`
  if (o.idGrupo !== null) return `Grupo: ${grupos.find((g) => g.id === o.idGrupo)?.nombre ?? o.idGrupo}`
  return `Categoría: ${categorias.find((c) => c.id === o.idCategoria)?.nombre ?? o.idCategoria}`
}

/**
 * ABM dedicado de ofertas (design decision 9: no la máquina genérica de catálogos, no la
 * pantalla genérica de descriptores) — Slice 4, dependiente solo del CRUD de la Slice 2. El
 * listado del servidor no completa `idsListas` por fila (evita el N+1, ver el doc-comment de
 * `OfertaListado`), así que la tabla nunca muestra el targeting de listas: eso solo se ve/edita
 * en el detalle, después de `obtener`.
 */
export function Ofertas() {
  const [ofertas, setOfertas] = useState<OfertaListado[] | null>(null)
  const [grupos, setGrupos] = useState<GrupoListado[]>([])
  const [categorias, setCategorias] = useState<CategoriaListado[]>([])
  const [empresas, setEmpresas] = useState<EmpresaListado[]>([])
  const [listasPrecio, setListasPrecio] = useState<ListaPrecioListado[]>([])
  const [avisosCatalogos, setAvisosCatalogos] = useState<string[]>([])
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const [aviso, setAviso] = useState('')
  const [formulario, setFormulario] = useState<FormularioOferta | null>(null)
  const [guardando, setGuardando] = useState(false)
  const [eliminando, setEliminando] = useState(false)
  const tokenEdicionRef = useRef(0)
  const generacionCargaRef = useRef(0)
  const cargaInicialHechaRef = useRef(false)
  const ocupado = guardando || eliminando

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
      setError(mensajeInvalido)
      return
    }

    const token = invalidarEdicionEnCurso()
    setGuardando(true)
    setError('')
    setAviso('')

    try {
      if (formulario.id === null) {
        const creada = await clienteDeOfertas.crear(aAltaOferta(formulario))
        if (tokenEdicionRef.current === token) {
          setAviso(`Oferta "${formulario.nombre}" creada.`)
          setFormulario({ ...aValoresOferta(creada), nombreArticulo: formulario.nombreArticulo })
        }
      } else {
        const actualizada = await clienteDeOfertas.actualizar(formulario.id, aAltaOferta(formulario))
        if (tokenEdicionRef.current === token) {
          setAviso(`Oferta "${formulario.nombre}" actualizada.`)
          setFormulario({ ...aValoresOferta(actualizada), nombreArticulo: formulario.nombreArticulo })
        }
      }

      // El refresco de la tabla no pertenece al token de edición: un fallo acá es solo de
      // vista, el guardado ya tuvo éxito (mismo criterio que Articulos.tsx).
      try {
        await cargar({ relanzarError: true })
      } catch {
        if (tokenEdicionRef.current === token) {
          setError('La oferta se guardó, pero no se pudo actualizar el listado. Volvé a entrar para verla.')
        }
      }
    } catch (e) {
      if (tokenEdicionRef.current === token) {
        setError(e instanceof ErrorApi ? e.message : 'No se pudo guardar la oferta.')
      }
    } finally {
      if (tokenEdicionRef.current === token) setGuardando(false)
    }
  }

  async function eliminar(o: OfertaListado) {
    if (ocupado) return
    if (!confirm(`¿Dar de baja la oferta "${o.nombre}"?`)) return

    invalidarEdicionEnCurso()
    setError('')
    setAviso('')
    setEliminando(true)
    try {
      await clienteDeOfertas.eliminar(o.id)
      setAviso(`Oferta "${o.nombre}" dada de baja.`)
      if (formulario?.id === o.id) setFormulario(null)
      try {
        await cargar({ relanzarError: true })
      } catch {
        setError('La oferta se dio de baja, pero no se pudo actualizar el listado. Volvé a entrar para verla.')
      }
    } catch (e) {
      setError(e instanceof ErrorApi ? e.message : 'No se pudo dar de baja la oferta.')
    } finally {
      setEliminando(false)
    }
  }

  const herramientas = (
    <nav className="p-2 d-flex gap-2">
      <button type="button" className="btn btn-sm btn-success rounded-0 text-nowrap" disabled={ocupado} onClick={abrirNuevo}>
        Nuevo
      </button>
    </nav>
  )

  return (
    <div className="container-fluid py-4">
      <Box titulo="Ofertas" variante="inverse" herramientas={herramientas}>
        {error && <div className="alert alert-danger rounded-0">{error}</div>}
        {aviso && <div className="alert alert-success rounded-0">{aviso}</div>}
        {avisosCatalogos.length > 0 && (
          <div className="alert alert-warning rounded-0">{avisosCatalogos.join(' ')}</div>
        )}

        {formulario && (
          <FormularioOfertaCampos
            key={formulario.id ?? 'nuevo'}
            valor={formulario}
            grupos={grupos}
            categorias={categorias}
            empresas={empresas}
            listasPrecio={listasPrecio}
            guardando={guardando}
            ocupado={ocupado}
            onCambio={setFormulario}
            onGuardar={guardar}
            onCancelar={cancelarEdicion}
          />
        )}

        {cargando && !cargaInicialHechaRef.current ? (
          <Cargando />
        ) : (
          <div className="table-responsive">
            <table className="table table-striped table-hover table-bordered align-middle">
              <thead>
                <tr>
                  <th>Nombre</th>
                  <th>Alcance</th>
                  <th>Beneficio</th>
                  <th>Prioridad</th>
                  <th>Acumulable</th>
                  <th>Estado</th>
                  <th className="text-end">Acciones</th>
                </tr>
              </thead>
              <tbody>
                {ofertas?.map((o) => (
                  <tr key={o.id}>
                    <td>{o.nombre}</td>
                    <td>{textoAlcance(o, grupos, categorias)}</td>
                    <td>{resumenDeBeneficio(o)}</td>
                    <td>{o.prioridad}</td>
                    <td>{o.acumulable ? 'Sí' : 'No'}</td>
                    <td>
                      <span className={`badge rounded-0 ${o.activo ? 'text-bg-success' : 'text-bg-secondary'}`}>
                        {o.activo ? 'Activa' : 'Inactiva'}
                      </span>
                    </td>
                    <td className="text-end text-nowrap">
                      <button
                        type="button"
                        className="btn btn-sm btn-outline-primary rounded-0 me-1"
                        disabled={ocupado}
                        onClick={() => abrirEdicion(o)}
                      >
                        Editar
                      </button>
                      <button
                        type="button"
                        className="btn btn-sm btn-outline-danger rounded-0"
                        disabled={ocupado}
                        onClick={() => eliminar(o)}
                      >
                        Baja
                      </button>
                    </td>
                  </tr>
                ))}
                {ofertas !== null && ofertas.length === 0 && (
                  <tr>
                    <td colSpan={7} className="text-center text-muted py-4">
                      No hay ofertas cargadas.
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        )}
      </Box>
    </div>
  )
}

function FormularioOfertaCampos({
  valor,
  grupos,
  categorias,
  empresas,
  listasPrecio,
  guardando,
  ocupado,
  onCambio,
  onGuardar,
  onCancelar,
}: {
  valor: FormularioOferta
  grupos: GrupoListado[]
  categorias: CategoriaListado[]
  empresas: EmpresaListado[]
  listasPrecio: ListaPrecioListado[]
  guardando: boolean
  ocupado: boolean
  onCambio: (f: FormularioOferta) => void
  onGuardar: () => void
  onCancelar: () => void
}) {
  const esNuevo = valor.id === null
  const opcionesLista = opcionesDeLista(listasPrecio)

  function alternarDia(dia: number) {
    const yaEsta = valor.diasSemana.includes(dia)
    onCambio({ ...valor, diasSemana: yaEsta ? valor.diasSemana.filter((d) => d !== dia) : [...valor.diasSemana, dia] })
  }

  function alternarLista(id: number) {
    const yaEsta = valor.idsListas.includes(id)
    onCambio({ ...valor, idsListas: yaEsta ? valor.idsListas.filter((x) => x !== id) : [...valor.idsListas, id] })
  }

  return (
    <div className="border p-3 mb-4 bg-white">
      <form
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
            <strong>{esNuevo ? 'Nueva oferta' : `Editando oferta "${valor.nombre}"`}</strong>
          </div>

          <div className="col-12">
            <strong className="text-muted small text-uppercase">Identificación</strong>
          </div>

          <div className="col-md-5">
            <label className="form-label" htmlFor="of-nombre">
              Nombre
            </label>
            <input
              id="of-nombre"
              className="form-control rounded-0"
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
              type="number"
              step="1"
              className="form-control rounded-0"
              value={valor.prioridad}
              onChange={(e) => onCambio({ ...valor, prioridad: e.target.value })}
            />
          </div>

          <div className="col-md-2 d-flex align-items-end">
            <div className="form-check">
              <input
                id="of-acumulable"
                type="checkbox"
                className="form-check-input rounded-0"
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
              className="form-select rounded-0"
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
            {(['Articulo', 'Grupo', 'Categoria'] as AlcanceOferta[]).map((opcion) => (
              <div className="form-check" key={opcion}>
                <input
                  id={`of-alcance-${opcion}`}
                  type="radio"
                  name="of-alcance"
                  className="form-check-input rounded-0"
                  checked={valor.alcance === opcion}
                  onChange={() => onCambio({ ...valor, alcance: opcion })}
                />
                <label className="form-check-label" htmlFor={`of-alcance-${opcion}`}>
                  {opcion === 'Articulo' ? 'Artículo' : opcion === 'Grupo' ? 'Grupo' : 'Categoría'}
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
                className="form-select rounded-0"
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
                className="form-select rounded-0"
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

          <div className="col-12">
            <strong className="text-muted small text-uppercase">Vigencia</strong>
          </div>

          <div className="col-md-3">
            <label className="form-label" htmlFor="of-fecha-desde">
              Fecha desde
            </label>
            <input
              id="of-fecha-desde"
              type="date"
              className="form-control rounded-0"
              value={valor.fechaDesde}
              onChange={(e) => onCambio({ ...valor, fechaDesde: e.target.value })}
            />
          </div>
          <div className="col-md-3">
            <label className="form-label" htmlFor="of-fecha-hasta">
              Fecha hasta
            </label>
            <input
              id="of-fecha-hasta"
              type="date"
              className="form-control rounded-0"
              value={valor.fechaHasta}
              onChange={(e) => onCambio({ ...valor, fechaHasta: e.target.value })}
            />
          </div>
          <div className="col-md-3">
            <label className="form-label" htmlFor="of-hora-desde">
              Hora desde
            </label>
            <input
              id="of-hora-desde"
              type="time"
              className="form-control rounded-0"
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
              className="form-control rounded-0"
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
                    className="form-check-input rounded-0"
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
              type="number"
              step="0.001"
              min="0"
              className="form-control rounded-0"
              placeholder="Sin mínimo (oferta directa)"
              value={valor.cantidadMinima}
              onChange={(e) => onCambio({ ...valor, cantidadMinima: e.target.value })}
            />
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
                  className="form-check-input rounded-0"
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
                type="number"
                step="0.01"
                min="0"
                max="100"
                className="form-control rounded-0"
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
              <input
                id="of-importe-fijo"
                type="number"
                step="0.01"
                min="0"
                className="form-control rounded-0"
                value={valor.importeFijo}
                onChange={(e) => onCambio({ ...valor, importeFijo: e.target.value })}
              />
            </div>
          )}

          {valor.beneficio === 'PrecioUnitario' && (
            <div className="col-md-3">
              <label className="form-label" htmlFor="of-precio-unitario">
                Precio unitario ($)
              </label>
              <input
                id="of-precio-unitario"
                type="number"
                step="0.01"
                min="0"
                className="form-control rounded-0"
                value={valor.precioUnitario}
                onChange={(e) => onCambio({ ...valor, precioUnitario: e.target.value })}
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
                    className="form-check-input rounded-0"
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
                className="form-check-input rounded-0"
                checked={valor.activo}
                onChange={(e) => onCambio({ ...valor, activo: e.target.checked })}
              />
              <label className="form-check-label" htmlFor="of-activo">
                Activa
              </label>
            </div>
          </div>

          <div className="col-12 d-flex gap-2">
            <button type="submit" className="btn btn-success rounded-0" disabled={ocupado}>
              {guardando ? 'Guardando…' : 'Guardar'}
            </button>
            <button type="button" className="btn btn-outline-secondary rounded-0" onClick={onCancelar} disabled={ocupado}>
              Cancelar
            </button>
          </div>
        </fieldset>
      </form>
    </div>
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
          className="form-control rounded-0"
          placeholder="Buscar por nombre o código interno…"
          value={termino}
          disabled={disabled}
          onChange={(e) => setTermino(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' && (e.preventDefault(), buscar())}
        />
        <button type="button" className="btn btn-outline-primary rounded-0" disabled={disabled || buscando} onClick={buscar}>
          {buscando ? 'Buscando…' : 'Buscar'}
        </button>
      </div>
      {error && <div className="alert alert-danger rounded-0 py-1 px-2 small">{error}</div>}
      {resultados.length > 0 && (
        <select
          className="form-select rounded-0"
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
