import { useEffect, useRef, useState } from 'react'
import {
  aSolicitudDeConteo,
  aSolicitudDeConteoPorLote,
  clienteDeStock,
  contadaValida,
  lineaDeConteoDeLoteVacia,
  lineasDeConteoDeLoteCompletas,
  type LineaDeConteoDeLoteFormulario,
} from '../api/stock'
import { clienteDeArticulos } from '../api/articulos'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import { clienteDeParametros } from '../api/parametros'
import type { ArticuloListado, PuntoVentaListado, ResultadoConteo } from '../api/tipos'
import { Box } from '../componentes/Box'

// ---- Selector de artículo por búsqueda — duplicado de Transferencias.tsx/CompraEditor.tsx: no
// hay un módulo compartido de selectores todavía. ------------------------------------------------

type PropsSelectorDeArticulo = {
  descripcion: string
  disabled: boolean
  onElegir: (articulo: ArticuloListado) => void
}

function SelectorDeArticulo({ descripcion, disabled, onElegir }: PropsSelectorDeArticulo) {
  const [termino, setTermino] = useState('')
  const [resultados, setResultados] = useState<ArticuloListado[]>([])
  const [buscando, setBuscando] = useState(false)
  const generacionRef = useRef(0)

  useEffect(() => {
    if (termino.trim().length < 2) {
      setResultados([])
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
    <div className="position-relative">
      <input
        type="text"
        className="form-control form-control-sm rounded-0"
        placeholder="Buscar artículo…"
        value={termino}
        disabled={disabled}
        onChange={(e) => setTermino(e.target.value)}
      />
      {descripcion && <div className="small text-muted">Elegido: {descripcion}</div>}
      {buscando && <div className="small text-muted">Buscando…</div>}
      {!buscando && resultados.length > 0 && (
        <div className="list-group position-absolute w-100" style={{ zIndex: 10 }}>
          {resultados.map((a) => (
            <button
              key={a.id}
              type="button"
              className="list-group-item list-group-item-action py-1 px-2 small"
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
 * Conteo de inventario por artículo (stage-8-compras-transferencias-inventario, Slice 6, design:
 * Web Composition; POST /api/stock/conteos): el operador manda el TOTAL físicamente contado —
 * nunca un delta (spec: conteo-de-inventario / Conteo Input Is The Counted Total, Never A Delta)
 * — el servidor deriva el ajuste bajo el lock de la fila de stock. El "antes" que se trae de
 * `GET /api/stock` apenas se elige artículo+punto de venta es puramente un dato de referencia en
 * pantalla: el resultado que se renderiza después de un submit sale ÍNTEGRO de la respuesta de
 * `POST /api/stock/conteos` (`ResultadoConteo`), nunca de ese pre-fetch — que puede haber quedado
 * desactualizado por una venta concurrente (judgment-day stage-8 Slice 6).
 *
 * stage-12-lotes-vencimientos (Slice 15, design decisión 12/18 — exactly-one-of espejado
 * client-side): un artículo lote-efectivo (`ArticuloListado.controlaLote`) cambia la pantalla a
 * un grid de "contada por lote" — nunca muestra el campo agregado a la vez. Un artículo sin
 * control de lote sigue el flujo agregado de siempre. La pantalla arma UNA de las dos formas del
 * contrato, nunca ambas ni ninguna, mismo criterio que el backend (`400 conteo_contada_y_lotes`).
 *
 * judgment-day (Slice 15, ronda juez A): el control EFECTIVO no es solo `controlaLote` — es el AND
 * con `lotes_habilitado` de la empresa, espejo de `ReglaDeLotes.ControlEfectivo` (Domain). Con el
 * flag propio en `true` pero el módulo apagado (`lotes_habilitado = false`), `GET /api/stock/lotes`
 * jamás tuvo reconciliación y devuelve cero lotes — mostrar igual el grid por lote dejaba al
 * operador sin ninguna forma de contar ese artículo (dead-end permanente, `puedeContar` exigía
 * ≥1 línea que nunca podía existir). Acá se resuelve `lotes_habilitado` vía
 * `GET /api/parametros/lotes_habilitado` (mismo endpoint que prueba `Parametros.tsx`, con
 * `Politicas.OperacionDePos` — no hace falta ser admin) antes de decidir la forma del formulario;
 * mientras no resuelve, o si el fetch falla, el default es agregado (mismo default del parámetro
 * en el servidor, `"false"`) — nunca un dead-end: si el módulo está realmente ON, el servidor
 * rechaza el envío agregado con `400 conteo_requiere_lotes`, error que el funnel ya muestra tal
 * cual — el server manda, honesto y sin bloquear al operador silenciosamente.
 */
export function ConteoDeInventario() {
  const [puntosVenta, setPuntosVenta] = useState<PuntoVentaListado[] | null>(null)
  const [errorPuntosVenta, setErrorPuntosVenta] = useState('')

  useEffect(() => {
    let vigente = true
    clienteDeOrganizacion
      .listarPuntosVenta()
      .then((lista) => {
        if (vigente) setPuntosVenta(lista)
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

  const referenciaOk = puntosVenta !== null && errorPuntosVenta === ''

  const [idPuntoVenta, setIdPuntoVenta] = useState<number | ''>('')
  const [idArticulo, setIdArticulo] = useState<number | ''>('')
  const [descripcionArticulo, setDescripcionArticulo] = useState('')
  const [articuloSeleccionado, setArticuloSeleccionado] = useState<ArticuloListado | null>(null)
  const [contada, setContada] = useState('')
  const [observaciones, setObservaciones] = useState('')

  const articuloControlaLote = articuloSeleccionado?.controlaLote === true
  const idEmpresaSeleccionada = (puntosVenta ?? []).find((pv) => pv.id === idPuntoVenta)?.idEmpresa ?? null

  // ---- `lotes_habilitado` de la empresa (judgment-day, Slice 15) — solo se resuelve cuando hace
  // falta (el artículo elegido tiene `controlaLote`): mismo patrón token-gated que `actual`/
  // `lineasDeLote` de abajo. `null` = sin resolver todavía (o fetch fallido) — el default es
  // agregado, nunca el grid por lote, hasta tener una respuesta positiva explícita del servidor.
  const [lotesHabilitado, setLotesHabilitado] = useState<boolean | null>(null)
  const generacionLotesHabilitadoRef = useRef(0)

  useEffect(() => {
    setLotesHabilitado(null)
    if (idEmpresaSeleccionada === null || idPuntoVenta === '' || !articuloControlaLote) return

    let vigente = true
    const miGeneracion = (generacionLotesHabilitadoRef.current += 1)

    clienteDeParametros
      .resolver('lotes_habilitado', idEmpresaSeleccionada, idPuntoVenta)
      .then((resuelto) => {
        if (!vigente || generacionLotesHabilitadoRef.current !== miGeneracion) return
        setLotesHabilitado(resuelto.valor === 'true')
      })
      .catch(() => {
        if (!vigente || generacionLotesHabilitadoRef.current !== miGeneracion) return
        // Fetch fallido: se queda en `null` → `esLoteEfectivo` cae a agregado (default honesto,
        // mismo criterio que el default del parámetro en el servidor). Nunca un dead-end: si el
        // módulo está realmente ON, el servidor rechaza el envío agregado y ese error se ve.
        setLotesHabilitado(null)
      })

    return () => {
      vigente = false
    }
  }, [idEmpresaSeleccionada, idPuntoVenta, articuloControlaLote])

  // Control EFECTIVO — espejo de `ReglaDeLotes.ControlEfectivo` (Domain): el AND de ambos flags,
  // nunca `controlaLote` a secas (ese era el dead-end del judgment-day: con el módulo apagado, la
  // grilla por lote se mostraba igual y nunca tenía líneas para completar).
  const esLoteEfectivo = articuloControlaLote && lotesHabilitado === true

  // ---- stock actual conocido (el "antes" honesto) — token-gated, se reconsulta cada vez que
  // cambia el par (punto de venta, artículo). ----------------------------------------------------
  const [actual, setActual] = useState<number | null>(null)
  const [cargandoActual, setCargandoActual] = useState(false)
  const generacionActualRef = useRef(0)

  useEffect(() => {
    setActual(null)
    if (idPuntoVenta === '' || idArticulo === '') return

    let vigente = true
    const miGeneracion = (generacionActualRef.current += 1)
    setCargandoActual(true)

    clienteDeStock
      .obtenerActual(idPuntoVenta, idArticulo)
      .then((stock) => {
        if (!vigente || generacionActualRef.current !== miGeneracion) return
        setActual(stock.cantidad)
      })
      .catch(() => {
        if (!vigente || generacionActualRef.current !== miGeneracion) return
        setActual(null)
      })
      .finally(() => {
        if (!vigente || generacionActualRef.current !== miGeneracion) return
        setCargandoActual(false)
      })

    return () => {
      vigente = false
    }
  }, [idPuntoVenta, idArticulo])

  // ---- lotes del artículo (solo para uno lote-efectivo) — token-gated, mismo patrón que
  // `actual` arriba. Cada lote listado arranca sin contada tipeada (regla 8: subárbol reseteado
  // por artículo — este efecto corre de nuevo ante cualquier cambio de (PV, artículo)). ----------
  const [lineasDeLote, setLineasDeLote] = useState<LineaDeConteoDeLoteFormulario[]>([])
  const [cargandoLotes, setCargandoLotes] = useState(false)
  const [errorLotes, setErrorLotes] = useState('')
  const generacionLotesRef = useRef(0)

  useEffect(() => {
    setLineasDeLote([])
    setErrorLotes('')
    if (idPuntoVenta === '' || idArticulo === '' || !esLoteEfectivo) return

    let vigente = true
    const miGeneracion = (generacionLotesRef.current += 1)
    setCargandoLotes(true)

    clienteDeStock
      .lotes(idPuntoVenta, idArticulo)
      .then((lista) => {
        if (!vigente || generacionLotesRef.current !== miGeneracion) return
        setLineasDeLote(lista.map(lineaDeConteoDeLoteVacia))
      })
      .catch((e) => {
        if (!vigente || generacionLotesRef.current !== miGeneracion) return
        setLineasDeLote([])
        setErrorLotes(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los lotes.')
      })
      .finally(() => {
        if (!vigente || generacionLotesRef.current !== miGeneracion) return
        setCargandoLotes(false)
      })

    return () => {
      vigente = false
    }
  }, [idPuntoVenta, idArticulo, esLoteEfectivo])

  function cambiarLineaDeLote(idLote: number, contadaLote: string) {
    // regla 1: updater funcional, nunca lee `lineasDeLote` del cierre.
    setLineasDeLote((prev) => prev.map((l) => (l.idLote === idLote ? { ...l, contada: contadaLote } : l)))
  }

  const lineasDeLoteCompletas = lineasDeConteoDeLoteCompletas(lineasDeLote)
  const lineasDeLoteIncompletas = lineasDeLote.length - lineasDeLoteCompletas.length

  const [contando, setContando] = useState(false)
  const contandoRef = useRef(false)
  const [error, setError] = useState('')
  const [resultado, setResultado] = useState<ResultadoConteo | null>(null)

  const puedeContar = esLoteEfectivo
    ? referenciaOk &&
      !contando &&
      idPuntoVenta !== '' &&
      idArticulo !== '' &&
      lineasDeLoteCompletas.length > 0 &&
      observaciones.trim() !== ''
    : referenciaOk &&
      !contando &&
      idPuntoVenta !== '' &&
      idArticulo !== '' &&
      contadaValida(contada) &&
      observaciones.trim() !== ''

  async function contar() {
    // regla 9: guard de reentrancia de primera línea.
    if (contandoRef.current) return
    if (!puedeContar) return

    contandoRef.current = true
    setContando(true)
    setError('')
    setResultado(null)
    generacionActualRef.current += 1

    try {
      const solicitud = esLoteEfectivo
        ? aSolicitudDeConteoPorLote(idPuntoVenta, idArticulo, lineasDeLoteCompletas, observaciones)
        : aSolicitudDeConteo(idPuntoVenta, idArticulo, contada, observaciones)
      const respuesta = await clienteDeStock.contar(solicitud)
      contandoRef.current = false
      setContando(false)
      // regla 6: un 2xx nunca se reporta como fallo — el no-op de diferencia cero también es un
      // 2xx honesto (spec: Zero-Difference Conteo Writes No Ledger Row), nunca un error. La
      // respuesta ES el resultado: nunca se deriva del `actual` pre-fetch, que puede haber
      // quedado desactualizado por una venta concurrente.
      setResultado(respuesta)
      setActual(respuesta.cantidad)
      setContada('')
      setObservaciones('')
      setLineasDeLote((prev) => prev.map((l) => ({ ...l, contada: '' })))
    } catch (e) {
      contandoRef.current = false
      setContando(false)
      setError(e instanceof ErrorApi ? e.message : 'No se pudo registrar el conteo.')
    }
  }

  return (
    <div className="container-fluid py-4">
      <Box titulo="Conteo de inventario" variante="inverse">
        {error && <div className="alert alert-danger rounded-0">{error}</div>}
        {errorPuntosVenta && (
          <div className="alert alert-warning rounded-0 py-1 px-2 small">
            {errorPuntosVenta} No se pueden registrar conteos hasta que esto se resuelva.
          </div>
        )}

        {resultado && (
          <div className={`alert rounded-0 ${resultado.movimientoRegistrado ? 'alert-success' : 'alert-secondary'}`}>
            {!resultado.movimientoRegistrado
              ? 'Sin diferencia — no se registró ningún movimiento.'
              : `Diferencia registrada: ${resultado.delta > 0 ? '+' : ''}${resultado.delta} (antes ${resultado.cantidadAnterior} → ahora ${resultado.cantidad}).`}
            {resultado.lotes && resultado.lotes.length > 0 && (
              <table className="table table-sm table-bordered mt-2 mb-0 bg-white">
                <thead>
                  <tr>
                    <th>Lote</th>
                    <th className="text-end">Anterior</th>
                    <th className="text-end">Nueva</th>
                    <th className="text-end">Delta</th>
                  </tr>
                </thead>
                <tbody>
                  {resultado.lotes.map((l) => (
                    <tr key={l.idLote}>
                      <td>{l.idLote}</td>
                      <td className="text-end">{l.cantidadAnterior}</td>
                      <td className="text-end">{l.cantidad}</td>
                      <td className="text-end">
                        {l.movimientoRegistrado ? `${l.delta > 0 ? '+' : ''}${l.delta}` : 'sin diferencia'}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>
        )}

        <div className="row g-2 mb-3">
          <div className="col-md-3">
            <label className="form-label" htmlFor="conteo-punto-venta">
              Punto de venta
            </label>
            <select
              id="conteo-punto-venta"
              className="form-select rounded-0"
              value={idPuntoVenta}
              disabled={contando || !referenciaOk}
              onChange={(e) => setIdPuntoVenta(e.target.value === '' ? '' : Number(e.target.value))}
            >
              <option value="">Elegir…</option>
              {(puntosVenta ?? []).map((pv) => (
                <option key={pv.id} value={pv.id}>
                  {pv.nombre}
                </option>
              ))}
            </select>
          </div>
          <div className="col-md-4">
            <label className="form-label" htmlFor="conteo-articulo">
              Artículo
            </label>
            <SelectorDeArticulo
              descripcion={descripcionArticulo}
              disabled={contando || !referenciaOk}
              onElegir={(a) => {
                setIdArticulo(a.id)
                setDescripcionArticulo(a.nombre)
                setArticuloSeleccionado(a)
                setContada('')
              }}
            />
          </div>
          <div className="col-md-2">
            <div className="small text-muted">Stock actual</div>
            <div>{idPuntoVenta === '' || idArticulo === '' ? '—' : cargandoActual ? 'Cargando…' : (actual ?? '—')}</div>
          </div>
          {!esLoteEfectivo && (
            <div className="col-md-3">
              <label className="form-label" htmlFor="conteo-contada">
                Cantidad contada
              </label>
              <input
                id="conteo-contada"
                type="number"
                step="0.001"
                min="0"
                className="form-control rounded-0"
                value={contada}
                disabled={contando || !referenciaOk}
                onChange={(e) => setContada(e.target.value)}
              />
            </div>
          )}
          <div className="col-12">
            <label className="form-label" htmlFor="conteo-observaciones">
              Observaciones
            </label>
            <input
              id="conteo-observaciones"
              type="text"
              className="form-control rounded-0"
              value={observaciones}
              disabled={contando || !referenciaOk}
              onChange={(e) => setObservaciones(e.target.value)}
            />
          </div>
        </div>

        {esLoteEfectivo && (
          <div className="mb-3">
            <strong className="text-muted small text-uppercase">Conteo por lote</strong>
            {errorLotes && <div className="alert alert-danger rounded-0 py-1 px-2 small mt-2">{errorLotes}</div>}
            {cargandoLotes && lineasDeLote.length === 0 ? (
              <p className="text-muted small mt-2">Cargando lotes…</p>
            ) : lineasDeLote.length === 0 && !errorLotes ? (
              <p className="text-muted small mt-2">Este artículo no tiene lotes cargados en este punto de venta.</p>
            ) : (
              <div className="table-responsive mt-2">
                <table className="table table-sm table-bordered align-middle">
                  <thead>
                    <tr>
                      <th>Lote</th>
                      <th style={{ width: 160 }}>Contada</th>
                    </tr>
                  </thead>
                  <tbody>
                    {lineasDeLote.map((l) => (
                      <tr key={l.idLote} className={contadaValida(l.contada) ? undefined : 'table-warning text-muted'}>
                        <td>{l.codigo}</td>
                        <td>
                          <input
                            type="number"
                            step="0.001"
                            min="0"
                            className="form-control form-control-sm rounded-0"
                            aria-label={`Contada del lote ${l.codigo}`}
                            value={l.contada}
                            disabled={contando || !referenciaOk}
                            onChange={(e) => cambiarLineaDeLote(l.idLote, e.target.value)}
                          />
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            {lineasDeLote.length > 0 && lineasDeLoteIncompletas > 0 && (
              <div className="alert alert-warning rounded-0 py-1 px-2 small mb-0">
                {lineasDeLoteIncompletas} lote(s) sin contar — no se van a incluir en el conteo.
              </div>
            )}
          </div>
        )}

        <button type="button" className="btn btn-primary rounded-0" disabled={!puedeContar} onClick={contar}>
          {contando ? 'Contando…' : 'Contar'}
        </button>
      </Box>
    </div>
  )
}
