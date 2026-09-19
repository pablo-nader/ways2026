import { Fragment, useCallback, useEffect, useRef, useState } from 'react'
import { clienteDeArticulos } from '../../api/articulos'
import { ErrorApi } from '../../api/cliente'
import { clienteDePrecios } from '../../api/precios'
import type { HistorialDePrecio, ListaPrecioListado, PrecioVigente } from '../../api/tipos'
import { CampoImporte } from '../../componentes/CampoImporte'
import { Cargando } from '../../componentes/Cargando'
import { formatearImporte } from '../../formato/importes'

type EstadoDeLista = {
  monto: string
  programado: boolean
  vigenteDesde: string
  guardando: boolean
  refrescando: boolean
  error: string
  confirmarPendiente: boolean
}

function estadoDeListaVacio(): EstadoDeLista {
  return {
    monto: '',
    programado: false,
    vigenteDesde: '',
    guardando: false,
    refrescando: false,
    error: '',
    confirmarPendiente: false,
  }
}

/**
 * Precio por lista (design: ABM Composition) — precio vigente + badge de pendiente (dentro del
 * panel expandido, no en la fila colapsada, para no multiplicar el N+1 de historial por cada
 * lista al cargar la pantalla) + sugerencia de margen (propone, nunca aplica sola) + alta/
 * programación con el flujo de `confirmarReemplazo` en el 409 `precio_pendiente_existe` +
 * historial. Las listas `derivada` no admiten alta propia (se resuelven en lectura): solo
 * muestran el precio vigente resuelto.
 */
export function EditorDePrecios({
  idArticulo,
  listasPrecio,
  bloqueadoPorPadre,
  alDeEscribir,
}: {
  idArticulo: number
  listasPrecio: ListaPrecioListado[]
  bloqueadoPorPadre: boolean
  alDeEscribir: (enCurso: boolean) => void
}) {
  const [vigentes, setVigentes] = useState<Record<number, PrecioVigente>>({})
  const [cargandoVigentes, setCargandoVigentes] = useState(true)
  const [errorVigentes, setErrorVigentes] = useState('')
  const cargaInicialHechaRef = useRef(false)
  const generacionVigentesRef = useRef(0)
  const generacionSugerenciaRef = useRef(0)
  // Generación por lista: la carga inicial de `alternarExpandida` y el refresco post-guardado
  // de `guardarPrecio` compiten por la misma lista — sin esto, la que resuelve primero pero
  // arrancó antes puede pisar el historial ya actualizado por la otra.
  const generacionHistorialRef = useRef<Record<number, number>>({})
  const [listaExpandida, setListaExpandida] = useState<number | null>(null)
  const [historiales, setHistoriales] = useState<Record<number, HistorialDePrecio[]>>({})
  const [estados, setEstados] = useState<Record<number, EstadoDeLista>>({})
  const [sugerencia, setSugerencia] = useState<number | null>(null)
  const [sinSugerencia, setSinSugerencia] = useState(false)
  const [cargandoSugerencia, setCargandoSugerencia] = useState(false)
  const [errorSugerencia, setErrorSugerencia] = useState('')

  const cargarVigentes = useCallback(
    async (opciones?: { relanzarError?: boolean }) => {
      // Generación: si mientras esta llamada está en vuelo se dispara otra (dos paneles
      // abriéndose/refrescando en simultáneo), la que llega tarde no debe pisar el estado
      // compartido con una respuesta desactualizada — solo aplica la más reciente en curso.
      const generacion = (generacionVigentesRef.current += 1)
      setCargandoVigentes(true)
      setErrorVigentes('')
      try {
        const lista = await clienteDePrecios.vigentes(idArticulo)
        if (generacionVigentesRef.current !== generacion) return
        const mapa: Record<number, PrecioVigente> = {}
        for (const p of lista) mapa[p.idListaPrecio] = p
        setVigentes(mapa)
      } catch (e) {
        if (generacionVigentesRef.current === generacion) {
          setErrorVigentes(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los precios vigentes.')
        }
        if (opciones?.relanzarError && generacionVigentesRef.current === generacion) throw e
      } finally {
        if (generacionVigentesRef.current === generacion) {
          setCargandoVigentes(false)
          cargaInicialHechaRef.current = true
        }
      }
    },
    [idArticulo],
  )

  useEffect(() => {
    void cargarVigentes()
  }, [cargarVigentes])

  function estadoDe(idLista: number): EstadoDeLista {
    return estados[idLista] ?? estadoDeListaVacio()
  }

  function actualizarEstado(idLista: number, parcial: Partial<EstadoDeLista>) {
    setEstados((prev) => ({ ...prev, [idLista]: { ...(prev[idLista] ?? estadoDeListaVacio()), ...parcial } }))
  }

  async function alternarExpandida(lista: ListaPrecioListado) {
    const abrir = listaExpandida !== lista.id
    setListaExpandida(abrir ? lista.id : null)

    if (abrir && lista.modo === 'Fija' && !historiales[lista.id]) {
      const generacion = (generacionHistorialRef.current[lista.id] = (generacionHistorialRef.current[lista.id] ?? 0) + 1)
      try {
        const historial = await clienteDePrecios.historial(idArticulo, lista.id)
        if (generacionHistorialRef.current[lista.id] !== generacion) return
        setHistoriales((prev) => ({ ...prev, [lista.id]: historial }))
      } catch (e) {
        if (generacionHistorialRef.current[lista.id] !== generacion) return
        actualizarEstado(lista.id, { error: e instanceof ErrorApi ? e.message : 'No se pudo cargar el historial.' })
      }
    }
  }

  async function pedirSugerencia() {
    if (cargandoSugerencia || bloqueadoPorPadre) return
    // Generación: protege contra una respuesta tardía de un pedido anterior pisando una
    // sugerencia más reciente que el usuario ya podría haber aplicado al borrador de precio.
    const generacion = (generacionSugerenciaRef.current += 1)
    setCargandoSugerencia(true)
    setErrorSugerencia('')
    setSinSugerencia(false)
    try {
      const { precioSugerido } = await clienteDeArticulos.sugerenciaDePrecio(idArticulo)
      if (generacionSugerenciaRef.current !== generacion) return
      setSugerencia(precioSugerido)
      setSinSugerencia(precioSugerido === null)
    } catch (e) {
      if (generacionSugerenciaRef.current === generacion) {
        setSugerencia(null)
        setErrorSugerencia(e instanceof ErrorApi ? e.message : 'No se pudo calcular la sugerencia de precio.')
      }
    } finally {
      if (generacionSugerenciaRef.current === generacion) setCargandoSugerencia(false)
    }
  }

  async function guardarPrecio(idLista: number, confirmarReemplazo: boolean) {
    const estado = estadoDe(idLista)
    if (estado.guardando || estado.refrescando || bloqueadoPorPadre) return
    const monto = Number(estado.monto)

    if (!estado.monto.trim() || Number.isNaN(monto)) {
      actualizarEstado(idLista, { error: 'Ingresá un precio válido.' })
      return
    }

    actualizarEstado(idLista, { guardando: true, error: '', confirmarPendiente: false })
    alDeEscribir(true)

    try {
      try {
        if (estado.programado) {
          if (!estado.vigenteDesde) {
            actualizarEstado(idLista, { guardando: false, error: 'Elegí la fecha de vigencia.' })
            return
          }
          await clienteDePrecios.programar(idArticulo, {
            idListaPrecio: idLista,
            precio: monto,
            vigenteDesde: new Date(estado.vigenteDesde).toISOString(),
            confirmarReemplazo,
          })
        } else {
          await clienteDePrecios.establecer(idArticulo, { idListaPrecio: idLista, precio: monto, confirmarReemplazo })
        }
      } catch (e) {
        if (e instanceof ErrorApi && e.codigo === 'precio_pendiente_existe') {
          actualizarEstado(idLista, { guardando: false, confirmarPendiente: true })
          return
        }
        actualizarEstado(idLista, {
          guardando: false,
          error: e instanceof ErrorApi ? e.message : 'No se pudo guardar el precio.',
        })
        return
      }

      // El precio ya quedó confirmado en el servidor: a partir de acá un fallo es solo de refresco
      // de vista, nunca "no se guardó" — evita que el usuario reintente un guardado que ya se aplicó.
      // `refrescando` mantiene el panel inerte hasta que el refresco termine, para no habilitar un
      // segundo guardado que corra en paralelo con este refresco.
      setEstados((prev) => ({ ...prev, [idLista]: { ...estadoDeListaVacio(), refrescando: true } }))

      try {
        await cargarVigentes({ relanzarError: true })
        const generacion = (generacionHistorialRef.current[idLista] = (generacionHistorialRef.current[idLista] ?? 0) + 1)
        const historial = await clienteDePrecios.historial(idArticulo, idLista)
        if (generacionHistorialRef.current[idLista] === generacion) {
          setHistoriales((prev) => ({ ...prev, [idLista]: historial }))
        }
        actualizarEstado(idLista, { refrescando: false })
      } catch {
        actualizarEstado(idLista, {
          refrescando: false,
          error: 'El precio se guardó, pero no se pudo actualizar la vista. Cerrá y volvé a abrir la lista para verlo.',
        })
      }
    } finally {
      alDeEscribir(false)
    }
  }

  // Solo la carga inicial gatea todo el panel: los refrescos posteriores (p.ej. tras guardar un
  // precio) mantienen el panel montado para no perder el foco ni parpadear en el camino feliz.
  if (cargandoVigentes && !cargaInicialHechaRef.current) return <Cargando texto="Cargando precios…" />

  return (
    <div>
      <div className="d-flex align-items-center justify-content-between">
        <strong className="text-muted small text-uppercase">Precios por lista</strong>
        <button
          type="button"
          className="btn btn-sm btn-outline-secondary rounded-0"
          disabled={cargandoSugerencia || bloqueadoPorPadre}
          onClick={pedirSugerencia}
        >
          {cargandoSugerencia ? 'Calculando…' : 'Calcular sugerencia de precio'}
        </button>
      </div>

      {sugerencia !== null && (
        <div className="alert alert-info rounded-0 py-2 px-2 small mt-2">
          Precio sugerido a partir de costo y margen: <strong>{formatearImporte(sugerencia, { simbolo: true })}</strong>. Usá "Usar sugerencia"
          en la lista que corresponda — nunca se aplica sola.
        </div>
      )}

      {sinSugerencia && (
        <div className="alert alert-info rounded-0 py-2 px-2 small mt-2">
          No hay costo o margen suficientes para sugerir un precio.
        </div>
      )}

      {errorSugerencia && <div className="alert alert-danger rounded-0 py-2 px-2 small mt-2">{errorSugerencia}</div>}

      {errorVigentes && <div className="alert alert-danger rounded-0 mt-2">{errorVigentes}</div>}

      <div className="table-responsive mt-2">
        <table className="table table-sm table-bordered align-middle mb-0">
          <thead>
            <tr>
              <th>Lista</th>
              <th>Precio vigente</th>
              <th className="text-end">Acciones</th>
            </tr>
          </thead>
          <tbody>
            {listasPrecio.map((lista) => {
              const vigente = vigentes[lista.id]
              const expandida = listaExpandida === lista.id
              const listaBase = lista.idListaBase !== null ? listasPrecio.find((l) => l.id === lista.idListaBase) : null

              return (
                <Fragment key={lista.id}>
                  <tr>
                    <td>
                      {lista.nombre}
                      {lista.esDefault && <span className="badge rounded-0 text-bg-secondary ms-1">Default</span>}
                      {lista.modo === 'Derivada' && (
                        <div className="text-muted small">
                          Derivada de {listaBase?.nombre ?? lista.idListaBase} (
                          {(lista.porcentaje ?? 0) >= 0 ? '+' : ''}
                          {lista.porcentaje}%)
                        </div>
                      )}
                    </td>
                    <td>{vigente?.precio !== null && vigente?.precio !== undefined ? formatearImporte(vigente.precio, { simbolo: true }) : '—'}</td>
                    <td className="text-end">
                      <button
                        type="button"
                        className="btn btn-sm btn-outline-primary rounded-0"
                        onClick={() => alternarExpandida(lista)}
                        disabled={bloqueadoPorPadre || estadoDe(lista.id).guardando || estadoDe(lista.id).refrescando}
                      >
                        {expandida ? 'Cerrar' : lista.modo === 'Fija' ? 'Gestionar' : 'Ver detalle'}
                      </button>
                    </td>
                  </tr>
                  {expandida && (
                    <tr>
                      <td colSpan={3} className="bg-light">
                        <PanelDeLista
                          lista={lista}
                          estado={estadoDe(lista.id)}
                          historial={historiales[lista.id] ?? []}
                          sugerencia={sugerencia}
                          cargandoSugerencia={cargandoSugerencia}
                          bloqueadoPorPadre={bloqueadoPorPadre}
                          onCambio={(parcial) => actualizarEstado(lista.id, parcial)}
                          onGuardar={(confirmarReemplazo) => guardarPrecio(lista.id, confirmarReemplazo)}
                        />
                      </td>
                    </tr>
                  )}
                </Fragment>
              )
            })}
            {listasPrecio.length === 0 && (
              <tr>
                <td colSpan={3} className="text-center text-muted py-3">
                  No hay listas de precio activas.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  )
}

function PanelDeLista({
  lista,
  estado,
  historial,
  sugerencia,
  cargandoSugerencia,
  bloqueadoPorPadre,
  onCambio,
  onGuardar,
}: {
  lista: ListaPrecioListado
  estado: EstadoDeLista
  historial: HistorialDePrecio[]
  sugerencia: number | null
  cargandoSugerencia: boolean
  bloqueadoPorPadre: boolean
  onCambio: (parcial: Partial<EstadoDeLista>) => void
  onGuardar: (confirmarReemplazo: boolean) => void
}) {
  const ahora = new Date()
  const filaAbierta = historial.find((h) => h.vigenteHasta === null)
  const pendiente = filaAbierta && new Date(filaAbierta.vigenteDesde) > ahora ? filaAbierta : null
  const bloqueado = estado.guardando || estado.refrescando || bloqueadoPorPadre

  if (lista.modo !== 'Fija') {
    return (
      <div className="p-2">
        <p className="mb-0 text-muted">
          Lista derivada: el precio se resuelve solo a partir de la lista base, sin historial propio.
        </p>
      </div>
    )
  }

  return (
    <div className="p-2">
      {pendiente && (
        <div className="alert alert-warning rounded-0 py-1 px-2 small">
          Precio programado: {formatearImporte(pendiente.precio, { simbolo: true })} desde {new Date(pendiente.vigenteDesde).toLocaleString()}
        </div>
      )}

      {estado.error && <div className="alert alert-danger rounded-0 py-1 px-2 small">{estado.error}</div>}

      {estado.confirmarPendiente && (
        <div className="alert alert-warning rounded-0 py-2 px-2 small d-flex align-items-center justify-content-between">
          <span>Ya existe un precio programado para esta lista. ¿Confirmás el reemplazo?</span>
          <div className="d-flex gap-2">
            <button
              type="button"
              className="btn btn-sm btn-warning rounded-0"
              disabled={bloqueado}
              onClick={() => onGuardar(true)}
            >
              Reemplazar
            </button>
            <button
              type="button"
              className="btn btn-sm btn-outline-secondary rounded-0"
              onClick={() => onCambio({ confirmarPendiente: false })}
            >
              Cancelar
            </button>
          </div>
        </div>
      )}

      <div className="row g-2 align-items-end">
        <div className="col-auto">
          <label className="form-label mb-0 small">Precio</label>
          <CampoImporte
            className="form-control form-control-sm rounded-0"
            style={{ width: 140 }}
            valor={estado.monto === '' ? null : Number(estado.monto)}
            disabled={bloqueado}
            onChange={(n) => onCambio({ monto: n === null ? '' : String(n) })}
          />
        </div>

        {sugerencia !== null && (
          <div className="col-auto">
            <button
              type="button"
              className="btn btn-sm btn-outline-info rounded-0"
              disabled={bloqueado || cargandoSugerencia}
              onClick={() => onCambio({ monto: String(sugerencia) })}
            >
              Usar sugerencia ({formatearImporte(sugerencia, { simbolo: true })})
            </button>
          </div>
        )}

        <div className="col-auto">
          <div className="form-check">
            <input
              id={`lp-programado-${lista.id}`}
              type="checkbox"
              className="form-check-input rounded-0"
              checked={estado.programado}
              disabled={bloqueado}
              onChange={(e) => onCambio({ programado: e.target.checked })}
            />
            <label className="form-check-label small" htmlFor={`lp-programado-${lista.id}`}>
              Programar a futuro
            </label>
          </div>
        </div>

        {estado.programado && (
          <div className="col-auto">
            <label className="form-label mb-0 small">Vigente desde</label>
            <input
              type="datetime-local"
              className="form-control form-control-sm rounded-0"
              value={estado.vigenteDesde}
              disabled={bloqueado}
              onChange={(e) => onCambio({ vigenteDesde: e.target.value })}
            />
          </div>
        )}

        <div className="col-auto">
          <button
            type="button"
            className="btn btn-sm btn-success rounded-0"
            disabled={bloqueado}
            onClick={() => onGuardar(false)}
          >
            {estado.guardando ? 'Guardando…' : estado.refrescando ? 'Actualizando…' : estado.programado ? 'Programar' : 'Establecer ahora'}
          </button>
        </div>
      </div>

      <div className="mt-3">
        <strong className="small text-uppercase text-muted">Historial</strong>
        <table className="table table-sm mb-0 mt-1">
          <thead>
            <tr>
              <th>Precio</th>
              <th>Vigente desde</th>
              <th>Vigente hasta</th>
            </tr>
          </thead>
          <tbody>
            {historial.map((h) => (
              <tr key={h.id}>
                <td>{formatearImporte(h.precio, { simbolo: true })}</td>
                <td>{new Date(h.vigenteDesde).toLocaleString()}</td>
                <td>{h.vigenteHasta ? new Date(h.vigenteHasta).toLocaleString() : '—'}</td>
              </tr>
            ))}
            {historial.length === 0 && (
              <tr>
                <td colSpan={3} className="text-center text-muted py-2">
                  Sin historial todavía.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  )
}
