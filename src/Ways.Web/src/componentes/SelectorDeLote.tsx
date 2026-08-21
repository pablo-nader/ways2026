import { useEffect, useRef, useState } from 'react'
import { clienteDeStock } from '../api/stock'
import { ErrorApi } from '../api/cliente'
import { opcionDeLote } from '../api/ventas'
import type { LoteListado } from '../api/tipos'

export type PropsSelectorDeLote = {
  idPuntoVenta: number
  idArticulo: number
  nombreArticulo: string
  idLoteElegido: number | null
  disabled: boolean
  onElegir: (idLote: number | null) => void
}

/**
 * Picker de lote de una línea (stage-12-lotes-vencimientos, Slice 14, design decisión 19): se
 * pide bajo demanda (click en "Elegir lote") — el camino feliz de cero tecleo (omitir `idLote`,
 * el servidor resuelve FEFO solo) nunca dispara este fetch. `sugerido` llega ya resuelto del
 * servidor (`ReglaDeLotes.ElegirFefo`); acá solo se resalta, nunca se recalcula.
 *
 * stage-17-presupuestos-y-remitos (Slice 8, design.md:416-418): extraído de `Pos.tsx` (nació ahí
 * en Slice 14) a este módulo compartido — `Remito.tsx` lo reusa tal cual para el pick de lote del
 * borrador, mismo criterio literal de "lot picker reusing SelectorDeLote" del design. Ambos call
 * sites importan de acá; ninguno mantiene una copia local.
 */
export function SelectorDeLote({ idPuntoVenta, idArticulo, nombreArticulo, idLoteElegido, disabled, onElegir }: PropsSelectorDeLote) {
  const [cargando, setCargando] = useState(false)
  const [error, setError] = useState('')
  const [lotes, setLotes] = useState<LoteListado[] | null>(null)
  const tokenRef = useRef(0)

  // react-async-state regla 3: los saldos de lote son por punto de venta — un cambio de PV
  // invalida cualquier fetch en vuelo y cualquier lote ya cargado, nunca puede sobrevivir a la
  // selección anterior (mutation-proof-tests regla 7: probado resolviendo la promesa vieja
  // DESPUÉS del cambio de PV, dentro de `act`).
  useEffect(() => {
    tokenRef.current += 1
    setCargando(false)
    setError('')
    setLotes(null)
  }, [idPuntoVenta, idArticulo])

  async function cargar() {
    // El guard de reentrancia de primera línea es el `disabled` nativo del botón (más abajo):
    // JSDOM y los navegadores no despachan `click` sobre un elemento disabled, así que un
    // `cargandoRef` extra acá era inalcanzable — verificado por mutación en judgment-day
    // (slice 14, MAJOR 2b). `lotes !== null` sigue evitando un refetch tras una carga exitosa.
    if (lotes !== null) return

    const miToken = (tokenRef.current += 1)
    setCargando(true)
    setError('')

    try {
      const resultado = await clienteDeStock.listarLotes(idPuntoVenta, idArticulo)
      if (tokenRef.current !== miToken) return
      setLotes(resultado)
    } catch (e) {
      if (tokenRef.current !== miToken) return
      setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los lotes.')
    } finally {
      if (tokenRef.current === miToken) {
        setCargando(false)
      }
    }
  }

  if (lotes === null) {
    return (
      <div>
        <button type="button" className="btn btn-sm btn-outline-secondary rounded-0" disabled={disabled || cargando} onClick={cargar}>
          {cargando ? 'Cargando…' : 'Elegir lote'}
        </button>
        {error && <div className="small text-danger">{error}</div>}
      </div>
    )
  }

  if (lotes.length === 0) {
    return <span className="small text-muted">Sin lotes registrados — FEFO automático.</span>
  }

  const sugerido = lotes.find((l) => l.sugerido) ?? null
  const valorActual = idLoteElegido !== null ? String(idLoteElegido) : sugerido ? String(sugerido.idLote) : ''

  return (
    <select
      className="form-select form-select-sm rounded-0"
      aria-label={`Lote de ${nombreArticulo}`}
      value={valorActual}
      disabled={disabled}
      onChange={(e) => onElegir(e.target.value === '' ? null : Number(e.target.value))}
    >
      <option value="">FEFO automático (recomendado)</option>
      {lotes.map((l) => {
        const opcion = opcionDeLote(l)
        return (
          <option key={l.idLote} value={opcion.valor}>
            {opcion.etiqueta}
          </option>
        )
      })}
    </select>
  )
}
