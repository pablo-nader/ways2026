import { useEffect, useRef, useState } from 'react'
import {
  aSolicitudDeTransferencia,
  articulosRepetidosEnTransferencia,
  clienteDeStock,
  lineaDeTransferenciaVacia,
  lineaTransferenciaCompleta,
  type LineaDeTransferenciaFormulario,
} from '../api/stock'
import { clienteDeArticulos } from '../api/articulos'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import type { ArticuloListado, PuntoVentaListado, ResultadoTransferencia } from '../api/tipos'
import { Box } from '../componentes/Box'

// ---- Selector de artículo por búsqueda (search-as-you-type) — duplicado de CompraEditor.tsx:
// no hay un módulo compartido de selectores todavía (mismo criterio documentado en compras.ts
// para el helper de offset horario). --------------------------------------------------------

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

// ---- Fila editable del grid de líneas ----------------------------------------------------------

type PropsFilaDeLinea = {
  linea: LineaDeTransferenciaFormulario
  disabled: boolean
  repetida: boolean
  incompleta: boolean
  onCambio: (clave: number, cambios: Partial<LineaDeTransferenciaFormulario>) => void
  onQuitar: (clave: number) => void
}

function FilaDeLinea({ linea, disabled, repetida, incompleta, onCambio, onQuitar }: PropsFilaDeLinea) {
  return (
    <tr className={repetida ? 'table-danger' : incompleta ? 'table-warning text-muted' : undefined}>
      <td style={{ minWidth: 220 }}>
        <SelectorDeArticulo
          descripcion={linea.descripcion}
          disabled={disabled}
          onElegir={(a) => onCambio(linea.clave, { idArticulo: a.id, descripcion: a.nombre })}
        />
        {repetida && <div className="small text-danger">Artículo repetido en la transferencia.</div>}
        {incompleta && !repetida && <div className="small text-warning-emphasis">Línea incompleta — no se va a transferir.</div>}
      </td>
      <td style={{ width: 120 }}>
        <input
          type="number"
          step="0.001"
          min="0"
          className="form-control form-control-sm rounded-0"
          aria-label="Cantidad"
          value={linea.cantidad}
          disabled={disabled}
          onChange={(e) => onCambio(linea.clave, { cantidad: e.target.value })}
        />
      </td>
      <td>
        <button type="button" className="btn btn-outline-danger btn-sm rounded-0" disabled={disabled} onClick={() => onQuitar(linea.clave)}>
          Quitar
        </button>
      </td>
    </tr>
  )
}

/**
 * Transferencia de stock entre puntos de venta (stage-8-compras-transferencias-inventario,
 * Slice 6, design: Web Composition; POST /api/stock/transferencias): origen/destino + una grilla
 * multi-línea, siempre con cantidades positivas — el signo por punto de venta lo decide el
 * servidor (design decisión 9). Ruta Admin-only end-to-end (`GestionDeCatalogo`, sin contraparte
 * de lectura: a diferencia de `/compras`, esta pantalla es puro formulario de escritura).
 */
export function Transferencias() {
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

  const [idPuntoVentaOrigen, setIdPuntoVentaOrigen] = useState<number | ''>('')
  const [idPuntoVentaDestino, setIdPuntoVentaDestino] = useState<number | ''>('')
  const [observaciones, setObservaciones] = useState('')
  const proximaClaveRef = useRef(1)
  const [lineas, setLineas] = useState<LineaDeTransferenciaFormulario[]>([lineaDeTransferenciaVacia(1)])

  const [transfiriendo, setTransfiriendo] = useState(false)
  const transfiriendoRef = useRef(false)
  const [confirmado, setConfirmado] = useState(false)
  const [error, setError] = useState('')
  const [resultado, setResultado] = useState<ResultadoTransferencia | null>(null)
  // Nombres de la última transferencia exitosa, tomados del formulario ANTES de resetearlo (regla
  // 1: nunca se lee el estado ya limpiado) — la tabla de resultado cruza `#idArticulo` con el
  // nombre elegido, nunca muestra un id crudo solo.
  const [descripcionesTransferidas, setDescripcionesTransferidas] = useState<Record<number, string>>({})

  const ocupado = transfiriendo

  function cambiarLinea(clave: number, cambios: Partial<LineaDeTransferenciaFormulario>) {
    if (ocupado) return
    // regla 1: updater funcional, nunca lee `lineas` del cierre.
    setLineas((prev) => prev.map((l) => (l.clave === clave ? { ...l, ...cambios } : l)))
  }

  function agregarLinea() {
    if (ocupado) return
    setLineas((prev) => [...prev, lineaDeTransferenciaVacia(proximaClaveRef.current++)])
  }

  function quitarLinea(clave: number) {
    if (ocupado) return
    setLineas((prev) => prev.filter((l) => l.clave !== clave))
  }

  const repetidos = articulosRepetidosEnTransferencia(lineas)
  const lineasCompletas = lineas.filter(lineaTransferenciaCompleta)
  const lineasIncompletas = lineas.length - lineasCompletas.length
  // Espejo de `transferencia_origen_igual_destino` (400) — feedback instantáneo, nunca
  // autoritativo: el servidor lo vuelve a validar.
  const origenIgualDestino = idPuntoVentaOrigen !== '' && idPuntoVentaDestino !== '' && idPuntoVentaOrigen === idPuntoVentaDestino

  const puedeTransferir =
    referenciaOk &&
    !ocupado &&
    confirmado &&
    idPuntoVentaOrigen !== '' &&
    idPuntoVentaDestino !== '' &&
    !origenIgualDestino &&
    observaciones.trim() !== '' &&
    lineasCompletas.length > 0 &&
    repetidos.size === 0

  async function transferir() {
    // regla 9: guard de reentrancia de primera línea.
    if (transfiriendoRef.current) return
    if (!puedeTransferir) return

    transfiriendoRef.current = true
    setTransfiriendo(true)
    setError('')

    try {
      const solicitud = aSolicitudDeTransferencia(idPuntoVentaOrigen, idPuntoVentaDestino, observaciones, lineas)
      const res = await clienteDeStock.transferir(solicitud)
      transfiriendoRef.current = false
      setTransfiriendo(false)
      // regla 6: un 2xx nunca se reporta como fallo — la respuesta ES el resultado. Los nombres se
      // capturan del formulario ANTES de resetearlo — el formulario queda bloqueado durante todo
      // el `await` (regla 9), así que esta lectura sigue siendo la que se mandó en `solicitud`.
      const descripciones: Record<number, string> = {}
      for (const l of lineasCompletas) descripciones[Number(l.idArticulo)] = l.descripcion
      setDescripcionesTransferidas(descripciones)
      setResultado(res)
      setConfirmado(false)
      setObservaciones('')
      setLineas([lineaDeTransferenciaVacia(proximaClaveRef.current++)])
    } catch (e) {
      transfiriendoRef.current = false
      setTransfiriendo(false)
      // El refusal por stock insuficiente (409 stock_insuficiente_para_transferencia) nombra el
      // artículo ofensivo en `e.message` — se muestra tal cual, mismo criterio que
      // `compra_anulacion_stock_negativo` en `CompraEditor.tsx` (react-async-state regla 10:
      // misma copia de recuperación replicada en ambas superficies hermanas de esta etapa).
      setError(e instanceof ErrorApi ? e.message : 'No se pudo registrar la transferencia.')
    }
  }

  const nombrePuntoVenta = (id: number) => (puntosVenta ?? []).find((pv) => pv.id === id)?.nombre ?? `#${id}`

  return (
    <div className="container-fluid py-4">
      <Box titulo="Transferencias de stock" variante="inverse">
        {error && <div className="alert alert-danger rounded-0">{error}</div>}
        {errorPuntosVenta && (
          <div className="alert alert-warning rounded-0 py-1 px-2 small">
            {errorPuntosVenta} No se pueden registrar transferencias hasta que esto se resuelva.
          </div>
        )}

        {resultado && (
          <div className="alert alert-success rounded-0">
            <strong>
              Transferencia registrada: {nombrePuntoVenta(resultado.idPuntoVentaOrigen)} → {nombrePuntoVenta(resultado.idPuntoVentaDestino)}
            </strong>
            <table className="table table-sm table-bordered mt-2 mb-0">
              <thead>
                <tr>
                  <th>Artículo</th>
                  <th className="text-end">Stock en origen</th>
                  <th className="text-end">Stock en destino</th>
                </tr>
              </thead>
              <tbody>
                {resultado.lineas.map((l) => (
                  <tr key={l.idArticulo}>
                    <td>{descripcionesTransferidas[l.idArticulo] ?? `Artículo #${l.idArticulo}`} (#{l.idArticulo})</td>
                    <td className="text-end">{l.cantidadOrigen}</td>
                    <td className="text-end">{l.cantidadDestino}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

        <div className="row g-2 mb-3">
          <div className="col-md-4">
            <label className="form-label" htmlFor="transferencia-origen">
              Origen
            </label>
            <select
              id="transferencia-origen"
              className="form-select rounded-0"
              value={idPuntoVentaOrigen}
              disabled={ocupado || !referenciaOk}
              onChange={(e) => setIdPuntoVentaOrigen(e.target.value === '' ? '' : Number(e.target.value))}
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
            <label className="form-label" htmlFor="transferencia-destino">
              Destino
            </label>
            <select
              id="transferencia-destino"
              className="form-select rounded-0"
              value={idPuntoVentaDestino}
              disabled={ocupado || !referenciaOk}
              onChange={(e) => setIdPuntoVentaDestino(e.target.value === '' ? '' : Number(e.target.value))}
            >
              <option value="">Elegir…</option>
              {(puntosVenta ?? []).map((pv) => (
                <option key={pv.id} value={pv.id}>
                  {pv.nombre}
                </option>
              ))}
            </select>
            {origenIgualDestino && (
              <div className="small text-danger">El origen y el destino tienen que ser puntos de venta distintos.</div>
            )}
          </div>
          <div className="col-md-4">
            <label className="form-label" htmlFor="transferencia-observaciones">
              Observaciones
            </label>
            <input
              id="transferencia-observaciones"
              type="text"
              className="form-control rounded-0"
              value={observaciones}
              disabled={ocupado || !referenciaOk}
              onChange={(e) => setObservaciones(e.target.value)}
            />
          </div>
        </div>

        <div className="table-responsive">
          <table className="table table-sm table-bordered align-middle">
            <thead>
              <tr>
                <th>Artículo</th>
                <th>Cantidad</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {lineas.map((l) => (
                <FilaDeLinea
                  key={l.clave}
                  linea={l}
                  disabled={ocupado || !referenciaOk}
                  repetida={l.idArticulo !== '' && repetidos.has(Number(l.idArticulo))}
                  incompleta={!lineaTransferenciaCompleta(l)}
                  onCambio={cambiarLinea}
                  onQuitar={quitarLinea}
                />
              ))}
            </tbody>
          </table>
        </div>

        <button type="button" className="btn btn-outline-secondary btn-sm rounded-0 mb-3" disabled={ocupado || !referenciaOk} onClick={agregarLinea}>
          + Agregar línea
        </button>

        {lineasIncompletas > 0 && (
          <div className="alert alert-warning rounded-0 py-1 px-2 small mb-3">
            {lineasIncompletas} línea(s) incompleta(s) — no se van a transferir.
          </div>
        )}

        <div className="border p-3 mb-3">
          <div className="form-check my-2">
            <input
              id="transferencia-confirmacion"
              type="checkbox"
              className="form-check-input rounded-0"
              checked={confirmado}
              disabled={ocupado}
              onChange={(e) => setConfirmado(e.target.checked)}
            />
            <label className="form-check-label" htmlFor="transferencia-confirmacion">
              Confirmo que quiero mover este stock físicamente entre puntos de venta. Es irreversible: una vez
              transferido, corregirlo requiere una transferencia inversa.
            </label>
          </div>
          <button type="button" className="btn btn-primary rounded-0" disabled={!puedeTransferir} onClick={transferir}>
            {transfiriendo ? 'Transfiriendo…' : 'Transferir'}
          </button>
        </div>
      </Box>
    </div>
  )
}
