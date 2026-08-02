import { useEffect, useState } from 'react'
import { api, ErrorApi } from '../api/cliente'
import type { AlicuotaIvaListado, CondicionFiscalListado, TipoComprobanteListado } from '../api/tipos'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'

/**
 * Los 3 catálogos fiscales son de solo lectura en esta etapa (ADR-11, gate #4): los define
 * la plataforma, no el tenant — sin POST/PUT/DELETE mapeados en la API a propósito.
 */
export function CatalogosFiscales() {
  const [condiciones, setCondiciones] = useState<CondicionFiscalListado[] | null>(null)
  const [alicuotas, setAlicuotas] = useState<AlicuotaIvaListado[] | null>(null)
  const [tipos, setTipos] = useState<TipoComprobanteListado[] | null>(null)
  const [error, setError] = useState('')

  useEffect(() => {
    let vigente = true

    Promise.all([
      api.get<CondicionFiscalListado[]>('/catalogos-fiscales/condiciones-fiscales'),
      api.get<AlicuotaIvaListado[]>('/catalogos-fiscales/alicuotas-iva'),
      api.get<TipoComprobanteListado[]>('/catalogos-fiscales/tipos-comprobante'),
    ])
      .then(([c, a, t]) => {
        if (!vigente) return
        setCondiciones(c)
        setAlicuotas(a)
        setTipos(t)
      })
      .catch((e) => {
        if (!vigente) return
        setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar los catálogos fiscales.')
      })

    return () => {
      vigente = false
    }
  }, [])

  const cargando = condiciones === null || alicuotas === null || tipos === null

  return (
    <div className="container-fluid py-4 d-flex flex-column gap-4">
      {error && <div className="alert alert-danger rounded-0">{error}</div>}

      {cargando && !error ? (
        <Cargando />
      ) : (
        <>
          <Box titulo="Condiciones fiscales" variante="inverse">
            <p className="text-muted">Las define la plataforma. RI, Monotributo, Exento, Consumidor Final…</p>
            <div className="table-responsive">
              <table className="table table-striped table-bordered align-middle">
                <thead>
                  <tr>
                    <th>Código</th>
                    <th>Nombre</th>
                    <th>Código AFIP/ARCA</th>
                    <th>Estado</th>
                  </tr>
                </thead>
                <tbody>
                  {condiciones?.map((c) => (
                    <tr key={c.id}>
                      <td>{c.codigo}</td>
                      <td>{c.nombre}</td>
                      <td>{c.codigoAfip ?? '—'}</td>
                      <td>
                        <EtiquetaActivo activo={c.activo} />
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </Box>

          <Box titulo="Alícuotas de IVA" variante="inverse">
            <div className="table-responsive">
              <table className="table table-striped table-bordered align-middle">
                <thead>
                  <tr>
                    <th>Nombre</th>
                    <th>Porcentaje</th>
                    <th>Código AFIP/ARCA</th>
                    <th>Estado</th>
                  </tr>
                </thead>
                <tbody>
                  {alicuotas?.map((a) => (
                    <tr key={a.id}>
                      <td>{a.nombre}</td>
                      <td>{a.porcentaje}%</td>
                      <td>{a.codigoAfip ?? '—'}</td>
                      <td>
                        <EtiquetaActivo activo={a.activo} />
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </Box>

          <Box titulo="Tipos de comprobante" variante="inverse">
            <div className="table-responsive">
              <table className="table table-striped table-bordered align-middle">
                <thead>
                  <tr>
                    <th>Clase</th>
                    <th>Código</th>
                    <th>Nombre</th>
                    <th>Letra</th>
                    <th>Signo</th>
                    <th>Discrimina IVA</th>
                    <th>Fiscal</th>
                    <th>Afecta stock</th>
                    <th>Estado</th>
                  </tr>
                </thead>
                <tbody>
                  {tipos?.map((t) => (
                    <tr key={t.id}>
                      <td>{t.clase === 'Venta' ? 'Venta' : 'Compra'}</td>
                      <td>{t.codigo}</td>
                      <td>{t.nombre}</td>
                      <td>{t.letra ?? '—'}</td>
                      <td>{t.signo > 0 ? '+1' : '−1'}</td>
                      <td>{t.discriminaIva ? 'Sí' : 'No'}</td>
                      <td>{t.esFiscal ? 'Sí' : 'No'}</td>
                      <td>{t.afectaStock ? 'Sí' : 'No'}</td>
                      <td>
                        <EtiquetaActivo activo={t.activo} />
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </Box>
        </>
      )}
    </div>
  )
}

function EtiquetaActivo({ activo }: { activo: boolean }) {
  return (
    <span className={`badge rounded-0 ${activo ? 'text-bg-success' : 'text-bg-secondary'}`}>
      {activo ? 'Activo' : 'Inactivo'}
    </span>
  )
}
