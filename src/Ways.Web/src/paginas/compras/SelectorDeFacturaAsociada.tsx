import { useEffect, useMemo, useState } from 'react'
import { clienteDeCompras, esFacturaAsociable, filtrosDeComprasVacios } from '../../api/compras'
import type { CompraListada, TipoComprobanteListado } from '../../api/tipos'
import { formatearImporte } from '../../formato/importes'

/** Tamaño de la página que se pide: es el tope del servidor, así que una respuesta llena puede dejar facturas afuera. */
export const TOPE_DE_FACTURAS_ASOCIABLES = 200

type Props = {
  idProveedor: number | ''
  idEmpresa: number | null
  tipos: TipoComprobanteListado[]
  valor: number | null
  disabled: boolean
  onChange: (id: number | null) => void
}

type Respuesta = { clave: string; filas: CompraListada[]; hayMas: boolean } | { clave: string; falla: true }

/**
 * Facturas confirmadas del proveedor entre las que una nota de crédito puede elegir la que corrige. Es opcional.
 * Cada respuesta se guarda con la clave del pedido que la originó y solo se usa mientras esa clave sea la vigente,
 * así una respuesta tardía de otro proveedor nunca se muestra.
 */
export function SelectorDeFacturaAsociada({ idProveedor, idEmpresa, tipos, valor, disabled, onChange }: Props) {
  const clave = idProveedor === '' ? null : `${idProveedor}|${idEmpresa ?? ''}`
  const [respuesta, setRespuesta] = useState<Respuesta | null>(null)

  useEffect(() => {
    if (clave === null || idProveedor === '') return
    let vigente = true
    clienteDeCompras
      .listar({
        ...filtrosDeComprasVacios(),
        idProveedor,
        idEmpresa,
        estado: 'Confirmada',
        tamanio: TOPE_DE_FACTURAS_ASOCIABLES,
      })
      .then(
        (pagina) => vigente && setRespuesta({ clave, filas: pagina.items, hayMas: pagina.total > pagina.items.length }),
        () => vigente && setRespuesta({ clave, falla: true }),
      )
    return () => {
      vigente = false
    }
  }, [clave, idProveedor, idEmpresa])

  const vigenteRespuesta = respuesta !== null && respuesta.clave === clave ? respuesta : null
  const filasCrudas = vigenteRespuesta !== null && 'filas' in vigenteRespuesta ? vigenteRespuesta.filas : null
  const tipoPorId = useMemo(() => new Map(tipos.map((t) => [t.id, t])), [tipos])
  const facturas = useMemo(
    () => (filasCrudas ?? []).filter((c) => esFacturaAsociable(tipoPorId.get(c.idTipoComprobante) ?? null)),
    [filasCrudas, tipoPorId],
  )
  const hayMas = vigenteRespuesta !== null && 'filas' in vigenteRespuesta && vigenteRespuesta.hayMas
  const cargando = clave !== null && vigenteRespuesta === null
  const fallo = vigenteRespuesta !== null && 'falla' in vigenteRespuesta

  return (
    <div>
      <label className="form-label" htmlFor="compra-factura-asociada">
        Factura asociada (opcional)
      </label>
      <select
        id="compra-factura-asociada"
        className="form-select"
        value={valor ?? ''}
        disabled={disabled || clave === null || cargando}
        onChange={(e) => onChange(e.target.value === '' ? null : Number(e.target.value))}
      >
        <option value="">{clave === null ? 'Elegí un proveedor' : cargando ? 'Cargando…' : 'Sin factura asociada'}</option>
        {valor !== null && !facturas.some((f) => f.id === valor) && <option value={valor}>Factura #{valor}</option>}
        {facturas.map((f) => (
          <option key={f.id} value={f.id}>
            {tipoPorId.get(f.idTipoComprobante)?.codigo ?? ''} {f.numeroExterno ?? `#${f.id}`} — {formatearImporte(f.total, { simbolo: true })}
          </option>
        ))}
      </select>
      {fallo && <div className="small text-danger mt-1">No se pudieron cargar las facturas del proveedor.</div>}
      {hayMas && (
        <div className="small text-muted mt-1">
          Se listan solo las últimas {TOPE_DE_FACTURAS_ASOCIABLES} compras confirmadas del proveedor: si la factura no
          aparece, puede estar entre las más antiguas.
        </div>
      )}
    </div>
  )
}
