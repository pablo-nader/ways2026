import { useEffect, useRef, useState } from 'react'
import { aSolicitudDeConteo, clienteDeStock, contadaValida } from '../api/stock'
import { clienteDeArticulos } from '../api/articulos'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import type { ArticuloListado, PuntoVentaListado } from '../api/tipos'
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

type ResultadoConteoMostrado = { anterior: number; final: number; delta: number }

/**
 * Conteo de inventario por artículo (stage-8-compras-transferencias-inventario, Slice 6, design:
 * Web Composition; POST /api/stock/conteos): el operador manda el TOTAL físicamente contado —
 * nunca un delta (spec: conteo-de-inventario / Conteo Input Is The Counted Total, Never A Delta)
 * — el servidor deriva el ajuste bajo el lock de la fila de stock. El "antes" que se muestra en
 * pantalla se trae de `GET /api/stock` apenas se elige artículo+punto de venta, y es lo que se usa
 * para mostrar el delta con signo — o el no-op honesto — después de un submit exitoso.
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
  const [contada, setContada] = useState('')
  const [observaciones, setObservaciones] = useState('')

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

  const [contando, setContando] = useState(false)
  const contandoRef = useRef(false)
  const [error, setError] = useState('')
  const [resultado, setResultado] = useState<ResultadoConteoMostrado | null>(null)

  const puedeContar =
    referenciaOk &&
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
    // Capturado ANTES del await: es el "antes" honesto que se muestra junto al delta, no lo que
    // `actual` termine valiendo después de la escritura.
    const anterior = actual ?? 0
    generacionActualRef.current += 1

    try {
      const solicitud = aSolicitudDeConteo(idPuntoVenta, idArticulo, contada, observaciones)
      const respuesta = await clienteDeStock.contar(solicitud)
      contandoRef.current = false
      setContando(false)
      // regla 6: un 2xx nunca se reporta como fallo — el no-op de diferencia cero también es un
      // 2xx honesto (spec: Zero-Difference Conteo Writes No Ledger Row), nunca un error.
      setResultado({ anterior, final: respuesta.cantidad, delta: respuesta.cantidad - anterior })
      setActual(respuesta.cantidad)
      setContada('')
      setObservaciones('')
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
          <div className={`alert rounded-0 ${resultado.delta === 0 ? 'alert-secondary' : 'alert-success'}`}>
            {resultado.delta === 0
              ? 'Sin diferencia — no se registró ningún movimiento.'
              : `Diferencia registrada: ${resultado.delta > 0 ? '+' : ''}${resultado.delta} (motivo = inventario). Stock resultante: ${resultado.final}.`}
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
              }}
            />
          </div>
          <div className="col-md-2">
            <div className="small text-muted">Stock actual</div>
            <div>{idPuntoVenta === '' || idArticulo === '' ? '—' : cargandoActual ? 'Cargando…' : (actual ?? '—')}</div>
          </div>
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

        <button type="button" className="btn btn-primary rounded-0" disabled={!puedeContar} onClick={contar}>
          {contando ? 'Contando…' : 'Contar'}
        </button>
      </Box>
    </div>
  )
}
