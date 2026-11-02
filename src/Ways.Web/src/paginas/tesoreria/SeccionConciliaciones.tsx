import { useCallback, useState } from 'react'
import {
  RESULTADOS_EN_ORDEN,
  TAMANO_DE_PAGINA,
  clienteDeConciliacionMercadoPago,
  etiquetaDeResultado,
  mensajeDeErrorDeConciliacion,
  rangoDeFilas,
  textoDePeriodo,
  totalDePaginas,
  textoDeFecha,
  textoDeResumen,
  type CandidatoDeConfirmacion,
  type ConciliacionListado,
  type FilaDeConciliacion,
} from '../../api/conciliacionMercadoPago'
import { Cargando } from '../../componentes/Cargando'
import { TablaDeFilasDeConciliacion } from './TablaDeFilasDeConciliacion'
import { useLectura } from './useLectura'

type Props = {
  conciliaciones: readonly ConciliacionListado[] | null
  cargando: boolean
  error: string
  onReintentar: () => void
  /** Cambia cuando una escritura ya aplicada pide volver a leer el detalle abierto. */
  version: number
  puedeConfirmar: boolean
  bloqueado: boolean
  onConfirmar: (idConciliacion: number, fila: FilaDeConciliacion, candidato: CandidatoDeConfirmacion) => void
}

const mensajeDeLectura = (e: unknown) => mensajeDeErrorDeConciliacion(e, 'lectura')

function Detalle({
  idConciliacion,
  version,
  puedeConfirmar,
  bloqueado,
  onConfirmar,
  onVolver,
}: Pick<Props, 'version' | 'puedeConfirmar' | 'bloqueado' | 'onConfirmar'> & { idConciliacion: number; onVolver: () => void }) {
  const [resultado, setResultado] = useState<string | null>(null)
  const [pagina, setPagina] = useState(1)

  // `version` no se usa en el cuerpo: cambia la identidad de la lectura para que una confirmación aplicada la vuelva a pedir.
  const leer = useCallback(
    () => {
      void version
      return clienteDeConciliacionMercadoPago.detalle(idConciliacion, { resultado, pagina, tamano: TAMANO_DE_PAGINA })
    },
    [idConciliacion, resultado, pagina, version],
  )
  const lectura = useLectura(leer, mensajeDeLectura)
  const detalle = lectura.datos
  const paginas = detalle ? totalDePaginas(detalle.total, detalle.tamano) : 1
  const rango = detalle ? rangoDeFilas(detalle.total, detalle.pagina, detalle.tamano) : { desde: 0, hasta: 0 }

  return (
    <div>
      <button type="button" className="btn btn-sm btn-outline-secondary mb-3" onClick={onVolver} disabled={bloqueado}>
        Volver al listado
      </button>

      {detalle && (
        <h6>
          {detalle.conciliacion.nombreArchivo ?? 'Reporte'} · {textoDePeriodo(detalle.conciliacion.fechaDesde, detalle.conciliacion.fechaHasta)}
        </h6>
      )}

      <div className="row g-2 align-items-end mb-3">
        <div className="col-md-4">
          <label className="form-label" htmlFor="conciliacion-filtro-resultado">
            Resultado
          </label>
          <select
            id="conciliacion-filtro-resultado"
            className="form-select"
            value={resultado ?? ''}
            disabled={bloqueado}
            onChange={(e) => {
              setResultado(e.target.value === '' ? null : e.target.value)
              setPagina(1)
            }}
          >
            <option value="">Todos</option>
            {RESULTADOS_EN_ORDEN.map((r) => (
              <option key={r} value={r}>
                {etiquetaDeResultado(r)}
              </option>
            ))}
          </select>
        </div>
      </div>

      {lectura.error && (
        <div className="alert alert-danger d-flex justify-content-between align-items-center gap-2" role="alert">
          <span>{lectura.error}</span>
          <button type="button" className="btn btn-sm btn-outline-danger" onClick={lectura.recargar}>
            Reintentar
          </button>
        </div>
      )}
      {lectura.cargando && !detalle && <Cargando />}

      {detalle &&
        (detalle.filas.length === 0 ? (
          <p className="text-muted text-center py-3">No hay filas para este filtro.</p>
        ) : (
          <>
            <TablaDeFilasDeConciliacion
              filas={detalle.filas}
              puedeConfirmar={puedeConfirmar}
              bloqueado={bloqueado}
              onConfirmar={(fila, candidato) => onConfirmar(idConciliacion, fila, candidato)}
            />
            <div className="d-flex align-items-center gap-2">
              <button
                type="button"
                className="btn btn-sm btn-outline-secondary"
                disabled={bloqueado || detalle.pagina <= 1}
                onClick={() => setPagina(detalle.pagina - 1)}
              >
                Anterior
              </button>
              <span className="small" role="status">
                Filas {rango.desde}-{rango.hasta} de {detalle.total} · Página {detalle.pagina} de {paginas}
              </span>
              <button
                type="button"
                className="btn btn-sm btn-outline-secondary"
                disabled={bloqueado || detalle.pagina >= paginas}
                onClick={() => setPagina(detalle.pagina + 1)}
              >
                Siguiente
              </button>
            </div>
          </>
        ))}
    </div>
  )
}

/** Listado de los reportes importados de la empresa y, al abrir uno, sus filas con filtro por resultado y paginado. */
export function SeccionConciliaciones({ conciliaciones, cargando, error, onReintentar, ...resto }: Props) {
  const [abierta, setAbierta] = useState<number | null>(null)

  if (abierta !== null) {
    return (
      <Detalle
        key={abierta}
        idConciliacion={abierta}
        version={resto.version}
        puedeConfirmar={resto.puedeConfirmar}
        bloqueado={resto.bloqueado}
        onConfirmar={resto.onConfirmar}
        onVolver={() => setAbierta(null)}
      />
    )
  }

  return (
    <section aria-label="Conciliaciones">
      {error && (
        <div className="alert alert-danger d-flex justify-content-between align-items-center gap-2" role="alert">
          <span>{error}</span>
          <button type="button" className="btn btn-sm btn-outline-danger" onClick={onReintentar}>
            Reintentar
          </button>
        </div>
      )}
      {cargando && conciliaciones === null && <Cargando />}
      {conciliaciones &&
        (conciliaciones.length === 0 ? (
          <p className="text-muted text-center py-3">Todavía no se importó ningún reporte para esta empresa.</p>
        ) : (
          <div className="table-responsive">
            <table className="table table-sm table-hover align-middle">
              <thead>
                <tr>
                  <th>Subido</th>
                  <th>Archivo</th>
                  <th>Período</th>
                  <th>Usuario</th>
                  <th className="text-end">Filas</th>
                  <th className="text-end">Inválidas</th>
                  <th>Resultados</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {conciliaciones.map((c) => (
                  <tr key={c.id}>
                    <td className="text-nowrap">{textoDeFecha(c.subidaEl)}</td>
                    <td>{c.nombreArchivo ?? '—'}</td>
                    <td className="text-nowrap">{textoDePeriodo(c.fechaDesde, c.fechaHasta)}</td>
                    <td>{c.usuario ?? '—'}</td>
                    <td className="text-end">{c.cantidadFilas}</td>
                    <td className="text-end">{c.cantidadInvalidas}</td>
                    <td className="small">{textoDeResumen(c.resumen)}</td>
                    <td className="text-end">
                      <button
                        type="button"
                        className="btn btn-sm btn-outline-primary"
                        disabled={resto.bloqueado}
                        aria-label={`Ver ${c.nombreArchivo ?? `la conciliación ${c.id}`}`}
                        onClick={() => setAbierta(c.id)}
                      >
                        Ver filas
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ))}
    </section>
  )
}
