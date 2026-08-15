import { Fragment, useCallback, useEffect, useRef, useState } from 'react'
import { clienteDeArticulos } from '../api/articulos'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import { clienteDeReportes, rutasDeExportacion } from '../api/reportes'
import { aSolicitudDeMinimos, clienteDeStock, reposicionMenorQueMinimo, umbralTextoValido } from '../api/stock'
import { ROL } from '../api/tipos'
import type { ArticuloListado, EstadoDeReposicion, Existencias as ExistenciasRespuesta, FilaExistencia, PuntoVentaListado } from '../api/tipos'
import { useAuth } from '../auth/useAuth'
import { BotonDeDescarga } from '../componentes/BotonDeDescarga'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'

const CANTIDAD_DE_COLUMNAS = 8

const ETIQUETA_ESTADO: Record<EstadoDeReposicion, string> = {
  SinMinimo: 'Sin mínimo',
  Bajo: 'Bajo',
  Ok: 'Ok',
}

function formatearCantidad(valor: number): string {
  return valor.toLocaleString('es-AR', { minimumFractionDigits: 0, maximumFractionDigits: 3 })
}

function formatearUmbral(valor: number | null): string {
  return valor === null ? '—' : formatearCantidad(valor)
}

// ---- Selector de artículo para agregar una fila (stage-13-stock-inteligente, Slice 3) ---------
// Duplicado de `SelectorDeArticulo` en `Transferencias.tsx`: no hay un módulo compartido de
// selectores todavía (mismo criterio ya documentado ahí). A diferencia de ese selector, este no
// mantiene una "descripción elegida" en pantalla — el resultado se consume de inmediato
// (`onElegir`) y la búsqueda se limpia sola.

type PropsSelectorDeArticuloParaAlta = {
  disabled: boolean
  onElegir: (articulo: ArticuloListado) => void
}

function SelectorDeArticuloParaAlta({ disabled, onElegir }: PropsSelectorDeArticuloParaAlta) {
  const [termino, setTermino] = useState('')
  const [resultados, setResultados] = useState<ArticuloListado[]>([])
  const [buscando, setBuscando] = useState(false)
  const generacionRef = useRef(0)

  useEffect(() => {
    if (termino.trim().length < 2) {
      setResultados([])
      setBuscando(false)
      return
    }

    let vigente = true
    const miGeneracion = (generacionRef.current += 1)
    setBuscando(true)

    const temporizador = setTimeout(() => {
      clienteDeArticulos
        .listar(termino, false)
        .then((pagina) => {
          if (!vigente || generacionRef.current !== miGeneracion) return
          setResultados(pagina.items)
        })
        .catch(() => {
          if (!vigente || generacionRef.current !== miGeneracion) return
          setResultados([])
        })
        .finally(() => {
          if (!vigente || generacionRef.current !== miGeneracion) return
          setBuscando(false)
        })
    }, 300)

    return () => {
      vigente = false
      clearTimeout(temporizador)
    }
  }, [termino])

  return (
    <div className="position-relative" style={{ maxWidth: 320 }}>
      <input
        type="text"
        className="form-control form-control-sm rounded-0"
        placeholder="Buscar artículo para agregar…"
        aria-label="Buscar artículo para agregar"
        value={termino}
        disabled={disabled}
        onChange={(e) => setTermino(e.target.value)}
      />
      {buscando && <div className="small text-muted">Buscando…</div>}
      {!buscando && resultados.length > 0 && (
        <div className="list-group position-absolute w-100" style={{ zIndex: 10 }}>
          {resultados.map((a) => (
            <button
              key={a.id}
              type="button"
              className="list-group-item list-group-item-action py-1 px-2 small"
              disabled={disabled}
              onClick={() => {
                onElegir(a)
                setTermino('')
                setResultados([])
              }}
            >
              {a.codigoInterno} — {a.nombre}
            </button>
          ))}
        </div>
      )}
    </div>
  )
}

/**
 * Existencias (stage-11-exportacion-reportes, Slice 9 — web; ampliada en stage-13-stock-inteligente,
 * Slice 3, design: Web Composition; decisiones 15/16) — pantalla del punto de venta: `stock` ⋈
 * `articulos`, con edición inline de `minimo`/`reposicion` fila-por-fila y alta de artículos nuevos
 * al grupo gestionado. Misma política que `/tablero` (`Politicas.LecturaDeReportes`: Supervisor +
 * Admin en lectura; `PUT /api/stock/minimos` es Admin-only del lado del servidor) —
 * `puedeEscribir` oculta las acciones de escritura, `GestionDeCatalogo` es la autoridad real del
 * lado del servidor (mismo patrón que `CompraEditor.tsx`).
 */
export function Existencias() {
  const { usuario } = useAuth()
  const puedeEscribir = usuario !== null && usuario.rolId === ROL.Admin
  const [puntosVenta, setPuntosVenta] = useState<PuntoVentaListado[] | null>(null)
  const [errorPuntosVenta, setErrorPuntosVenta] = useState('')

  const [idPuntoVenta, setIdPuntoVenta] = useState<number | null>(null)
  const [existencias, setExistencias] = useState<ExistenciasRespuesta | null>(null)
  // stage-13-stock-inteligente, Slice 7 (tarea 7.6): mapa idArticulo → minimoSugerido de
  // `GET /rotacion`, fetcheado JUNTO con el reporte (Promise.all, misma generación) — un artículo
  // AUSENTE del mapa renderiza `—`, nunca `0` (design decisión 14: la ausencia es la respuesta
  // honesta). Si `/rotacion` falla, degrada a mapa vacío (`—` en toda la columna) SIN romper la
  // pantalla — el reporte de existencias sigue siendo la fuente de verdad de esta grilla
  // (`react-async-state` regla 6: un fallo de un feed secundario nunca reporta el load como fallido).
  const [mapaSugeridos, setMapaSugeridos] = useState<Map<number, number>>(new Map())
  const [cargando, setCargando] = useState(false)
  const [error, setError] = useState('')
  const [errorDescarga, setErrorDescarga] = useState('')
  const generacionRef = useRef(0) // staleness de LECTURAS (regla 2), sin tocar por esta slice

  // ---- Editor inline de una sola fila (decisión 15: bloquear supersede-during-write, nunca
  // reconciliar por token) --------------------------------------------------------------------
  const [filaEnEdicion, setFilaEnEdicion] = useState<number | null>(null) // idArticulo, UNA sola
  // Tarea 3.4 / fila fantasma: `agregarFila` appenda la fila local ANTES del `PUT` que la
  // persiste — este ref recuerda el `idArticulo` de esa fila local mientras sigue sin guardarse,
  // para que `cancelarEdicion` pueda sacarla de la grilla en vez de dejarla huérfana.
  const filaLocalSinGuardarRef = useRef<number | null>(null)
  // Judgment-day ronda A-3 (residual del FINDING 1 CRITICAL): espejo de estado del ref de arriba,
  // mismo patrón que `guardando`/`guardandoRef` — el ref sirve para guards síncronos, pero mutarlo
  // NO re-renderiza, así que el picker no puede gatear su visibilidad por el ref solo. Este estado
  // es la única fuente de verdad para el render: "existe un fantasma sin guardar" (a diferencia de
  // `filaEnEdicion`, que solo dice cuál fila está abierta, no si quedó un fantasma benched).
  const [filaFantasma, setFilaFantasma] = useState<number | null>(null)
  const [minimoTexto, setMinimoTexto] = useState('')
  const [reposicionTexto, setReposicionTexto] = useState('')
  const [guardando, setGuardando] = useState<number | null>(null) // idArticulo en vuelo — atributo `disabled`
  // Espejo síncrono de `guardando`, mismo criterio que `transfiriendoRef` en Transferencias.tsx /
  // CompraEditor.tsx: el `useState` de arriba solo maneja el render (`disabled`), pero React 18
  // batchea su commit — dos clicks despachados en el MISMO tick (sin render entre medio) todavía
  // leerían el `guardando` VIEJO del cierre. El guard de reentrancia de primera línea (regla 9)
  // necesita una escritura/lectura SÍNCRONA, así que vive en este ref, no en el estado.
  const guardandoRef = useRef<number | null>(null)
  const [errorGuardado, setErrorGuardado] = useState('')
  const tokenDeEscrituraRef = useRef(0) // gate de la respuesta/`finally` del propio guardado (regla 2/4)

  useEffect(() => {
    let vigente = true
    clienteDeOrganizacion
      .listarPuntosVenta()
      .then((lista) => {
        if (!vigente) return
        setPuntosVenta(lista)
        if (lista.length > 0) setIdPuntoVenta(lista[0].id)
      })
      .catch((e) => {
        if (!vigente) return
        setPuntosVenta([])
        setErrorPuntosVenta(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los puntos de venta.')
      })

    return () => {
      vigente = false
    }
  }, [])

  const cargar = useCallback(() => {
    if (idPuntoVenta === null) return
    // Judgment-day round 2 (FINDING 1, MAJOR fix-caused): `cargar` siempre reemplaza la grilla
    // completa (mount inicial, cambio de punto de venta, botón Reintentar) — el `ref` de la fila
    // local sin guardar de la carga ANTERIOR queda huérfano si no se limpia acá. Sin este clear,
    // `cancelarEdicion` puede matchear por `idArticulo` una fila PERSISTIDA de la nueva grilla que
    // coincide por casualidad con el `idArticulo` fantasma, y borrarla.
    filaLocalSinGuardarRef.current = null
    setFilaFantasma(null) // espejo de estado (patrón guardandoRef): sin esto el picker no reaparecería
    const miGeneracion = (generacionRef.current += 1)
    setCargando(true)
    setError('')

    // tarea 7.6: `/rotacion` se pide JUNTO con `/existencias` (mismo disparo, misma generación),
    // pero con su propio `.catch` — un fallo del feed de sugerencia nunca convierte el load de
    // existencias en un error (degrada a mapa vacío, `—` en toda la columna).
    const cargaSugeridos = clienteDeReportes
      .rotacion(idPuntoVenta, null)
      .then((rotacion) => new Map(rotacion.filas.map((f) => [f.idArticulo, f.minimoSugerido])))
      .catch(() => new Map<number, number>())

    Promise.all([clienteDeReportes.existencias(idPuntoVenta), cargaSugeridos])
      .then(([datos, mapa]) => {
        if (generacionRef.current !== miGeneracion) return
        setExistencias(datos)
        setMapaSugeridos(mapa)
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        setExistencias(null)
        setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar las existencias.')
      })
      .finally(() => {
        if (generacionRef.current !== miGeneracion) return
        setCargando(false)
      })
  }, [idPuntoVenta])

  useEffect(() => {
    cargar()
  }, [cargar])

  function cambiarPuntoVenta(nuevoId: number) {
    if (guardandoRef.current !== null) return
    setIdPuntoVenta(nuevoId)
    setFilaEnEdicion(null)
    setErrorGuardado('')
  }

  /** Abre una fila para edición — mutation target nombrado (design decisión 15, tarea 3.6):
   * sin este guard de primera línea, abrir la fila B mientras la fila A se guarda deja de estar
   * bloqueado. Lee `guardandoRef` (no el estado `guardando`): un doble click en el MISMO tick
   * vence tanto al re-render del atributo `disabled` COMO a un guard basado en estado — solo un
   * ref, escrito/leído sincrónicamente, sobrevive esa carrera (`react-async-state` regla 9). */
  function abrirFila(fila: FilaExistencia) {
    if (guardandoRef.current !== null) return
    setFilaEnEdicion(fila.idArticulo)
    setMinimoTexto(fila.minimo === null ? '' : String(fila.minimo))
    setReposicionTexto(fila.reposicion === null ? '' : String(fila.reposicion))
    setErrorGuardado('')
  }

  function cancelarEdicion() {
    if (guardandoRef.current !== null) return
    if (filaLocalSinGuardarRef.current !== null && filaLocalSinGuardarRef.current === filaEnEdicion) {
      const idALimpiar = filaLocalSinGuardarRef.current
      setExistencias((prev) => (prev === null ? prev : { ...prev, filas: prev.filas.filter((f) => f.idArticulo !== idALimpiar) }))
      filaLocalSinGuardarRef.current = null
      setFilaFantasma(null) // espejo de estado (patrón guardandoRef): sin esto el picker no reaparecería
    }
    setFilaEnEdicion(null)
    setErrorGuardado('')
  }

  async function guardarFila(fila: FilaExistencia) {
    if (guardandoRef.current !== null) return // regla 9: guard de reentrancia de primera línea (doble click)
    if (idPuntoVenta === null) return
    if (!umbralTextoValido(minimoTexto) || !umbralTextoValido(reposicionTexto)) return
    if (reposicionMenorQueMinimo(minimoTexto, reposicionTexto)) return

    const miToken = (tokenDeEscrituraRef.current += 1)
    guardandoRef.current = fila.idArticulo
    setGuardando(fila.idArticulo)
    setErrorGuardado('')

    try {
      const solicitud = aSolicitudDeMinimos(idPuntoVenta, fila.idArticulo, minimoTexto, reposicionTexto)
      const resultado = await clienteDeStock.escribirMinimos(solicitud)
      if (tokenDeEscrituraRef.current !== miToken) return

      // decisión 16: la respuesta AUTORITATIVA se aplica con un updater funcional desde `prev` —
      // nunca un refetch del reporte completo (regla 1: nunca se lee `existencias` del cierre).
      setExistencias((prev) => {
        if (prev === null) return prev
        return {
          ...prev,
          filas: prev.filas.map((f) =>
            f.idArticulo === resultado.idArticulo
              ? { ...f, cantidad: resultado.cantidad, minimo: resultado.minimo, reposicion: resultado.reposicion, estado: resultado.estado }
              : f,
          ),
        }
      })
      // Judgment-day round A (FINDING 1, CRITICAL): este clear gatea por IDENTIDAD — si el
      // usuario abrió OTRA fila (Y) mientras la fantasma (X) seguía sin guardar, guardar Y no
      // puede pisar el ref de X: eso la huerfanizaría (`cancelarEdicion` matchea por identidad
      // contra `filaEnEdicion`, así que ya no la encontraría para sacarla de la grilla).
      if (fila.idArticulo === filaLocalSinGuardarRef.current) {
        filaLocalSinGuardarRef.current = null
        setFilaFantasma(null) // espejo de estado (patrón guardandoRef): sin esto el picker no reaparecería
      }
      setFilaEnEdicion(null)
    } catch (e) {
      if (tokenDeEscrituraRef.current === miToken) {
        setErrorGuardado(e instanceof ErrorApi ? e.message : 'No se pudo guardar el mínimo.')
      }
    } finally {
      if (tokenDeEscrituraRef.current === miToken) {
        guardandoRef.current = null
        setGuardando(null)
      }
    }
  }

  /** Alta de artículo (tarea 3.4): agrega una fila local en `cantidad = 0` (el mismo residuo que
   * deja el upsert del servidor cuando no había fila de `stock` previa) y la abre para edición —
   * la fila existe recién cuando el `PUT` la persiste. Si el artículo ya está en la grilla, abre
   * la fila existente en vez de duplicarla. */
  function agregarFila(articulo: ArticuloListado) {
    if (guardandoRef.current !== null) return
    const existente = existencias?.filas.find((f) => f.idArticulo === articulo.id)
    if (existente) {
      abrirFila(existente)
      return
    }
    const nuevaFila: FilaExistencia = {
      idArticulo: articulo.id,
      nombre: articulo.nombre,
      cantidad: 0,
      minimo: null,
      reposicion: null,
      estado: 'SinMinimo',
    }
    setExistencias((prev) => (prev === null ? prev : { ...prev, filas: [...prev.filas, nuevaFila] }))
    filaLocalSinGuardarRef.current = nuevaFila.idArticulo
    setFilaFantasma(nuevaFila.idArticulo) // espejo de estado (patrón guardandoRef): gobierna el render del picker
    abrirFila(nuevaFila)
  }

  return (
    <div className="container-fluid py-4">
      <Box titulo="Existencias" variante="inverse">
        {error && (
          <div className="alert alert-danger rounded-0 d-flex justify-content-between align-items-center gap-2">
            <span>{error}</span>
            <button type="button" className="btn btn-sm btn-outline-danger rounded-0" onClick={cargar}>
              Reintentar
            </button>
          </div>
        )}
        {errorPuntosVenta && <div className="alert alert-warning rounded-0 py-1 px-2 small">{errorPuntosVenta}</div>}

        {puntosVenta === null ? (
          <Cargando />
        ) : puntosVenta.length === 0 ? (
          <p className="text-muted text-center py-4">No hay puntos de venta visibles para las existencias.</p>
        ) : idPuntoVenta === null ? (
          <Cargando />
        ) : (
          <>
            <div className="row g-2 align-items-end mb-3">
              <div className="col-md-3">
                <label className="form-label" htmlFor="existencias-punto-venta">
                  Punto de venta
                </label>
                <select
                  id="existencias-punto-venta"
                  className="form-select rounded-0"
                  value={idPuntoVenta}
                  disabled={guardando !== null}
                  onChange={(e) => cambiarPuntoVenta(Number(e.target.value))}
                >
                  {puntosVenta.map((pv) => (
                    <option key={pv.id} value={pv.id}>
                      {pv.nombre}
                    </option>
                  ))}
                </select>
              </div>
              <div className="col-auto">
                <BotonDeDescarga
                  ruta={rutasDeExportacion.existencias(idPuntoVenta)}
                  etiqueta="Descargar"
                  onError={setErrorDescarga}
                  onInicio={() => setErrorDescarga('')}
                  disabled={guardando !== null}
                />
              </div>
            </div>

            {errorDescarga && <div className="alert alert-danger rounded-0 py-1 px-2 small mb-2">{errorDescarga}</div>}

            {cargando && !existencias && <Cargando />}

            {existencias && (
              <div className="table-responsive">
                <table className="table table-sm table-striped table-bordered align-middle">
                  <thead>
                    <tr>
                      <th>Artículo</th>
                      <th>Nombre</th>
                      <th className="text-end">Cantidad</th>
                      <th className="text-end">Mínimo</th>
                      <th className="text-end">Reposición</th>
                      <th>Estado</th>
                      <th className="text-end">Sugerido</th>
                      <th></th>
                    </tr>
                  </thead>
                  <tbody>
                    {existencias.filas.map((fila) => {
                      const enEdicion = filaEnEdicion === fila.idArticulo
                      const guardandoEstaFila = guardando === fila.idArticulo
                      const minimoInvalido = enEdicion && !umbralTextoValido(minimoTexto)
                      const reposicionInvalida = enEdicion && !umbralTextoValido(reposicionTexto)
                      const violaReposicion = enEdicion && reposicionMenorQueMinimo(minimoTexto, reposicionTexto)
                      const puedeGuardar = enEdicion && !minimoInvalido && !reposicionInvalida && !violaReposicion

                      return (
                        <Fragment key={fila.idArticulo}>
                          <tr>
                            <td>{fila.idArticulo}</td>
                            <td>{fila.nombre}</td>
                            <td className="text-end">{formatearCantidad(fila.cantidad)}</td>
                            <td className="text-end" style={{ minWidth: 110 }}>
                              {enEdicion ? (
                                <input
                                  type="text"
                                  inputMode="decimal"
                                  className="form-control form-control-sm rounded-0 text-end"
                                  aria-label={`Mínimo de ${fila.nombre}`}
                                  value={minimoTexto}
                                  disabled={guardando !== null}
                                  onChange={(e) => setMinimoTexto(e.target.value)}
                                />
                              ) : (
                                formatearUmbral(fila.minimo)
                              )}
                            </td>
                            <td className="text-end" style={{ minWidth: 110 }}>
                              {enEdicion ? (
                                <input
                                  type="text"
                                  inputMode="decimal"
                                  className="form-control form-control-sm rounded-0 text-end"
                                  aria-label={`Reposición de ${fila.nombre}`}
                                  value={reposicionTexto}
                                  disabled={guardando !== null}
                                  onChange={(e) => setReposicionTexto(e.target.value)}
                                />
                              ) : (
                                formatearUmbral(fila.reposicion)
                              )}
                              {violaReposicion && <div className="small text-danger">La reposición no puede ser menor que el mínimo.</div>}
                              {(minimoInvalido || reposicionInvalida) && !violaReposicion && (
                                <div className="small text-danger">Formato numérico inválido.</div>
                              )}
                            </td>
                            <td>{ETIQUETA_ESTADO[fila.estado]}</td>
                            <td className="text-end">{formatearUmbral(mapaSugeridos.get(fila.idArticulo) ?? null)}</td>
                            <td className="text-end" style={{ minWidth: 170 }}>
                              {puedeEscribir &&
                                (enEdicion ? (
                                  <>
                                    <button
                                      type="button"
                                      className="btn btn-primary btn-sm rounded-0 me-1"
                                      disabled={!puedeGuardar || guardandoEstaFila}
                                      onClick={() => guardarFila(fila)}
                                    >
                                      {guardandoEstaFila ? 'Guardando…' : 'Guardar'}
                                    </button>
                                    <button
                                      type="button"
                                      className="btn btn-outline-secondary btn-sm rounded-0"
                                      disabled={guardando !== null}
                                      onClick={cancelarEdicion}
                                    >
                                      Cancelar
                                    </button>
                                  </>
                                ) : (
                                  <button
                                    type="button"
                                    className="btn btn-outline-secondary btn-sm rounded-0"
                                    disabled={guardando !== null}
                                    onClick={() => abrirFila(fila)}
                                  >
                                    Editar
                                  </button>
                                ))}
                            </td>
                          </tr>
                          {enEdicion && errorGuardado && (
                            <tr>
                              <td colSpan={CANTIDAD_DE_COLUMNAS} className="text-danger small py-1">
                                {errorGuardado}
                              </td>
                            </tr>
                          )}
                        </Fragment>
                      )
                    })}
                    {existencias.filas.length === 0 && (
                      <tr>
                        <td colSpan={CANTIDAD_DE_COLUMNAS} className="text-center text-muted py-4">
                          No hay stock cargado para este punto de venta.
                        </td>
                      </tr>
                    )}
                  </tbody>
                </table>

                {/* Judgment-day ronda A-3 (residual del FINDING 1 CRITICAL, tercera variante): gatear
                    solo por `filaEnEdicion === null` no alcanza — un fantasma puede quedar "benched"
                    (agregado pero sin guardar) mientras se abre y cancela OTRA fila persistida, y ese
                    camino deja `filaEnEdicion` en `null` con el fantasma todavía vivo, reapareciendo
                    el picker y permitiendo que un segundo alta pise el slot único del ref. La condición
                    correcta es "no existe NINGÚN fantasma sin guardar" — no "no hay fila en edición" —,
                    así que el render usa `filaFantasma` (estado), nunca el ref (mutar un ref no
                    re-renderiza). */}
                {puedeEscribir && filaEnEdicion === null && filaFantasma === null && (
                  <div className="mt-2">
                    <label className="form-label small text-muted d-block mb-1">Agregar artículo</label>
                    <SelectorDeArticuloParaAlta disabled={guardando !== null} onElegir={agregarFila} />
                  </div>
                )}
              </div>
            )}
          </>
        )}
      </Box>
    </div>
  )
}
