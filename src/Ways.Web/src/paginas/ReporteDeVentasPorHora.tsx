import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import { clienteDeReportes, rangoDeLasUltimasCuatroSemanas, type FiltrosDeVentasPorHora } from '../api/reportes'
import type { EmpresaListado, PuntoVentaListado, VentasPorHora } from '../api/tipos'
import { Box } from '../componentes/Box'
import { CampoFecha } from '../componentes/CampoFecha'
import { Cargando } from '../componentes/Cargando'
import { MapaDeCalor } from '../componentes/graficos/MapaDeCalor'
import { armarMatriz, DIAS_DE_LA_SEMANA } from '../componentes/graficos/mapaDeCalorEscala'
import { formatearImporte } from '../formato/importes'

type Metrica = 'importe' | 'tickets'

const METRICAS: { valor: Metrica; etiqueta: string }[] = [
  { valor: 'importe', etiqueta: 'Importe promedio' },
  { valor: 'tickets', etiqueta: 'Tickets promedio' },
]

function formatearMoneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

/** Promedios de tickets con hasta 2 decimales: "2", "1,5", "0,25". */
function formatearTickets(valor: number): string {
  return formatearImporte(valor, { decimales: 2 }).replace(/,00$/, '').replace(/(,\d)0$/, '$1')
}

function idODefecto(valor: string): number | null {
  return valor === '' ? null : Number(valor)
}

/**
 * Cuándo se vende más: promedio por día de la semana y hora del importe o de los tickets en un rango
 * (`Politicas.LecturaDeReportes`: Supervisor + Admin). Cada cambio de filtro lanza una consulta nueva gateada por
 * un único `useRef` de generación (`react-async-state`): una respuesta vieja nunca pisa un filtro ya cambiado.
 * El rango por defecto son las 4 semanas completas que terminan ayer.
 */
export function ReporteDeVentasPorHora() {
  const [empresas, setEmpresas] = useState<EmpresaListado[] | null>(null)
  const [puntosVenta, setPuntosVenta] = useState<PuntoVentaListado[]>([])
  const [errorEmpresas, setErrorEmpresas] = useState('')

  const [idEmpresa, setIdEmpresa] = useState<number | null>(null)
  const [idPuntoVenta, setIdPuntoVenta] = useState<number | null>(null)
  const [desde, setDesde] = useState(() => rangoDeLasUltimasCuatroSemanas().desde)
  const [hasta, setHasta] = useState(() => rangoDeLasUltimasCuatroSemanas().hasta)
  const [metrica, setMetrica] = useState<Metrica>('importe')

  const [datos, setDatos] = useState<VentasPorHora | null>(null)
  const [cargando, setCargando] = useState(false)
  const [error, setError] = useState('')
  const generacionRef = useRef(0)

  useEffect(() => {
    let vigente = true
    Promise.all([clienteDeOrganizacion.listarEmpresas(), clienteDeOrganizacion.listarPuntosVenta()])
      .then(([listaEmpresas, listaPuntosVenta]) => {
        if (!vigente) return
        setEmpresas(listaEmpresas)
        setPuntosVenta(listaPuntosVenta)
        if (listaEmpresas.length > 0) setIdEmpresa(listaEmpresas[0].id)
      })
      .catch((e) => {
        if (!vigente) return
        setEmpresas([])
        setErrorEmpresas(e instanceof ErrorApi ? e.message : 'No se pudieron cargar las empresas.')
      })
    return () => {
      vigente = false
    }
  }, [])

  const rangoValido = desde !== '' && hasta !== '' && desde <= hasta
  const filtros: FiltrosDeVentasPorHora | null =
    idEmpresa !== null && rangoValido ? { idEmpresa, idPuntoVenta, desde, hasta } : null

  // La consulta depende de los valores y no de la identidad del objeto: se recrea solo si cambia un filtro.
  const clave = filtros === null ? null : JSON.stringify(filtros)

  const cargar = useCallback(() => {
    const miGeneracion = (generacionRef.current += 1)
    if (clave === null) {
      setDatos(null)
      setCargando(false)
      return
    }
    setCargando(true)
    setError('')

    clienteDeReportes
      .ventasPorHora(JSON.parse(clave) as FiltrosDeVentasPorHora)
      .then((respuesta) => {
        if (generacionRef.current !== miGeneracion) return
        setDatos(respuesta)
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        setDatos(null)
        setError(e instanceof ErrorApi ? e.message : 'No se pudo cargar el reporte de ventas por hora.')
      })
      .finally(() => {
        if (generacionRef.current !== miGeneracion) return
        setCargando(false)
      })
  }, [clave])

  useEffect(() => {
    cargar()
  }, [cargar])

  const valores = useMemo(
    () =>
      datos === null
        ? null
        : armarMatriz(datos.celdas, (c) => (metrica === 'importe' ? c.promedioTotal : c.promedioTickets)),
    [datos, metrica],
  )

  const puntosVentaDeLaEmpresa = puntosVenta.filter((pv) => pv.idEmpresa === idEmpresa)
  const esImporte = metrica === 'importe'
  const ocurrencias = datos === null ? [] : [...datos.dias].sort((a, b) => a.diaDeLaSemana - b.diaDeLaSemana)

  return (
    <div className="container-fluid py-4">
      <Box titulo="Ventas por hora" variante="inverse">
        {errorEmpresas && <div className="alert alert-warning py-1 px-2 small">{errorEmpresas}</div>}

        {empresas === null ? (
          <Cargando />
        ) : empresas.length === 0 ? (
          <p className="text-muted text-center py-4">No hay empresas visibles para mostrar el reporte.</p>
        ) : (
          <>
            <div className="row g-2 align-items-end mb-3">
              <div className="col-md-3">
                <label className="form-label" htmlFor="ventas-hora-empresa">
                  Empresa
                </label>
                <select
                  id="ventas-hora-empresa"
                  className="form-select"
                  value={idEmpresa ?? ''}
                  onChange={(e) => {
                    setIdEmpresa(Number(e.target.value))
                    setIdPuntoVenta(null)
                  }}
                >
                  {empresas.map((e) => (
                    <option key={e.id} value={e.id}>
                      {e.razonSocial}
                    </option>
                  ))}
                </select>
              </div>
              <div className="col-md-3">
                <label className="form-label" htmlFor="ventas-hora-punto-venta">
                  Punto de venta
                </label>
                <select
                  id="ventas-hora-punto-venta"
                  className="form-select"
                  value={idPuntoVenta ?? ''}
                  onChange={(e) => setIdPuntoVenta(idODefecto(e.target.value))}
                >
                  <option value="">Todos</option>
                  {puntosVentaDeLaEmpresa.map((pv) => (
                    <option key={pv.id} value={pv.id}>
                      {pv.nombre}
                    </option>
                  ))}
                </select>
              </div>
              <div className="col-md-2">
                <label className="form-label" htmlFor="ventas-hora-desde">
                  Desde
                </label>
                <CampoFecha id="ventas-hora-desde" className="form-control" value={desde} onChange={setDesde} />
              </div>
              <div className="col-md-2">
                <label className="form-label" htmlFor="ventas-hora-hasta">
                  Hasta
                </label>
                <CampoFecha id="ventas-hora-hasta" className="form-control" value={hasta} onChange={setHasta} />
              </div>
              <div className="col-auto">
                <div className="btn-group" role="group" aria-label="Métrica">
                  {METRICAS.map((m) => (
                    <button
                      key={m.valor}
                      type="button"
                      className={`btn ${metrica === m.valor ? 'btn-primary' : 'btn-outline-secondary'}`}
                      aria-pressed={metrica === m.valor}
                      onClick={() => setMetrica(m.valor)}
                    >
                      {m.etiqueta}
                    </button>
                  ))}
                </div>
              </div>
            </div>

            {!rangoValido && (
              <div className="alert alert-warning py-1 px-2 small">
                Elegí un rango de fechas válido: la fecha inicial no puede ser posterior a la final.
              </div>
            )}
            {error && (
              <div className="alert alert-danger d-flex justify-content-between align-items-center gap-2">
                <span>{error}</span>
                <button type="button" className="btn btn-sm btn-outline-danger" onClick={cargar}>
                  Reintentar
                </button>
              </div>
            )}

            {cargando && !datos && <Cargando />}

            {datos && valores && (
              <>
                <dl className="row small mb-2">
                  <dt className="col-auto">Total del rango</dt>
                  <dd className="col-auto">{formatearMoneda(datos.total)}</dd>
                  <dt className="col-auto">Tickets</dt>
                  <dd className="col-auto">{formatearTickets(datos.tickets)}</dd>
                  <dt className="col-auto">Zona horaria</dt>
                  <dd className="col-auto">{datos.zonaHoraria}</dd>
                </dl>
                <p className="small text-muted mb-1">
                  Cada celda es el promedio de ese día de la semana y hora: el total del rango dividido por las veces
                  que cae ese día. Los totales suman los promedios mostrados.
                  {esImporte ? ' Los importes de las celdas se redondean al peso; el detalle está en cada una.' : ''}
                </p>
                <ul className="list-inline small mb-3" aria-label="Días incluidos en el rango">
                  {ocurrencias.map((d) => (
                    <li key={d.diaDeLaSemana} className="list-inline-item">
                      {DIAS_DE_LA_SEMANA[d.diaDeLaSemana - 1]} ×{d.ocurrencias}
                    </li>
                  ))}
                </ul>
                <MapaDeCalor
                  titulo={`${esImporte ? 'Importe' : 'Tickets'} promedio por día de la semana y hora`}
                  valores={valores}
                  formatearCelda={
                    esImporte
                      ? (v) => (v === 0 ? '–' : formatearImporte(v, { decimales: 0 }))
                      : (v) => (v === 0 ? '–' : formatearTickets(v))
                  }
                  formatearDetalle={esImporte ? formatearMoneda : formatearTickets}
                />
              </>
            )}
          </>
        )}
      </Box>
    </div>
  )
}
