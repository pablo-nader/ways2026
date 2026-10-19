import { Fragment } from 'react'
import { Link } from 'react-router'
import { etiquetaDeEstadoCompra } from '../../api/compras'
import type { CompraDuplicada } from '../../api/tipos'

type Props = {
  duplicados: CompraDuplicada[]
}

/** Aviso, no bloqueo: nombra las compras que ya tienen el número, con un vínculo a cada una. */
export function AvisoDeNumeroDuplicado({ duplicados }: Props) {
  if (duplicados.length === 0) return null

  return (
    <div className="alert alert-warning py-1 px-2 small mb-0" role="status">
      {duplicados.length === 1 ? 'Ya hay una compra con ese número: ' : 'Ya hay compras con ese número: '}
      {duplicados.map((d, i) => (
        <Fragment key={d.id}>
          {i > 0 && ', '}
          <Link to={`/compras/${d.id}`}>#{d.id}</Link> ({etiquetaDeEstadoCompra(d.estado)})
        </Fragment>
      ))}
      .
    </div>
  )
}
