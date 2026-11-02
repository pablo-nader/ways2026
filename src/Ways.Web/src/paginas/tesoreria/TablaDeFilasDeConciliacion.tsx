import {
  claseDeResultado,
  esFilaConfirmable,
  etiquetaDeResultado,
  textoDeFecha,
  textoDeImporte,
  type CandidatoDeConfirmacion,
  type FilaDeConciliacion,
} from '../../api/conciliacionMercadoPago'

type Props = {
  filas: readonly FilaDeConciliacion[]
  /** Admin: ofrece "Confirmar" en los candidatos. */
  puedeConfirmar: boolean
  /** Hay una escritura en vuelo o un diálogo abierto: nada se puede disparar. */
  bloqueado: boolean
  onConfirmar: (fila: FilaDeConciliacion, candidato: CandidatoDeConfirmacion) => void
}

function Vinculo({ fila }: { fila: FilaDeConciliacion }) {
  const partes: string[] = []
  if (fila.idCobroMercadoPago !== null) partes.push(`Cobro MP #${fila.idCobroMercadoPago}`)
  if (fila.idCobro !== null) partes.push(`Pago #${fila.idCobro}`)
  return (
    <>
      {partes.length > 0 ? partes.join(' · ') : '—'}
      {fila.resultado === 'ya_conciliado' && (
        <div className="small text-muted">
          Guardado: comisión {textoDeImporte(fila.comisionGuardada)} · neto {textoDeImporte(fila.netoGuardado)}
        </div>
      )}
      {fila.resultado === 'conciliado' && !fila.aplicadaAlCobro && <div className="small text-muted">No se sumó al cobro</div>}
    </>
  )
}

function Candidatos({ fila, puedeConfirmar, bloqueado, onConfirmar }: Props & { fila: FilaDeConciliacion }) {
  if (!esFilaConfirmable(fila)) return <>—</>

  return (
    <ul className="list-unstyled mb-0 small">
      {fila.candidatos.map((candidato) => (
        <li key={candidato.idCobro} className="d-flex align-items-center gap-2 mb-1">
          <span>
            Venta {candidato.numeroVisible} · {textoDeImporte(candidato.importe)} · {textoDeFecha(candidato.fecha)}
          </span>
          {puedeConfirmar && (
            <button
              type="button"
              className="btn btn-sm btn-outline-primary text-nowrap"
              disabled={bloqueado}
              aria-label={`Confirmar la fila ${fila.numeroLinea} con la venta ${candidato.numeroVisible}`}
              onClick={() => onConfirmar(fila, candidato)}
            >
              Confirmar
            </button>
          )}
        </li>
      ))}
    </ul>
  )
}

/** Filas de una conciliación: la lectura del reporte, su resultado y, si la fila no tiene cobro en Ways, los pagos que podría ser. */
export function TablaDeFilasDeConciliacion(props: Props) {
  const { filas } = props

  return (
    <div className="table-responsive">
      <table className="table table-sm table-hover align-middle">
        <thead>
          <tr>
            <th>Línea</th>
            <th>Fecha</th>
            <th>SOURCE_ID</th>
            <th>Referencia externa</th>
            <th>Tipo</th>
            <th className="text-end">Importe</th>
            <th className="text-end">Comisión</th>
            <th className="text-end">Neto</th>
            <th>Resultado</th>
            <th>Vinculado a</th>
            <th>Pagos candidatos</th>
          </tr>
        </thead>
        <tbody>
          {filas.map((fila) => (
            <tr key={fila.id}>
              <td>{fila.numeroLinea}</td>
              <td className="text-nowrap">{textoDeFecha(fila.fechaTransaccion)}</td>
              <td>{fila.sourceId ?? '—'}</td>
              <td>{fila.externalReference ?? '—'}</td>
              <td>{fila.tipoTransaccion ?? '—'}</td>
              <td className="text-end text-nowrap">{fila.importe === null ? '—' : textoDeImporte(fila.importe)}</td>
              <td className="text-end text-nowrap">{fila.comision === null ? '—' : textoDeImporte(fila.comision)}</td>
              <td className="text-end text-nowrap">{fila.neto === null ? '—' : textoDeImporte(fila.neto)}</td>
              <td>
                <span className={`badge ${claseDeResultado(fila.resultado)}`}>{etiquetaDeResultado(fila.resultado)}</span>
                {fila.motivo && <div className="small text-muted">{fila.motivo}</div>}
              </td>
              <td>
                <Vinculo fila={fila} />
              </td>
              <td>
                <Candidatos {...props} fila={fila} />
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
