import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import { clienteDeReportes, rangoDeLosUltimosDoceMeses, type FiltrosDeValoracion } from '../api/reportes'
import type { EmpresaListado, Granularidad, ValoracionDeInventario } from '../api/tipos'
import { Box } from '../componentes/Box'
import { CampoFecha } from '../componentes/CampoFecha'
import { Cargando } from '../componentes/Cargando'
import { GraficoDeBarrasApiladas } from '../componentes/graficos/GraficoDeBarrasApiladas'
import { GraficoDeLineasMultiples } from '../componentes/graficos/GraficoDeLineasMultiples'
import { formatearImporte, formatearImporteConSigno } from '../formato/importes'
import {
  armarTarjetas,
  aPuntosDeGrafico,
  avisosDeCobertura,
  fraseDeValoracion,
  mensajeDeErrorDeValoracion,
  sentidoDe,
  SERIES_DE_COMPOSICION,
  SERIES_DE_EVOLUCION,
  type TarjetaDeValoracion,
} from './valoracion/calculos'

const GRANULARIDADES: { valor: Granularidad; etiqueta: string }[] = [
  { valor: 'Dia', etiqueta: 'Día' },
  { valor: 'Semana', etiqueta: 'Semana' },
  { valor: 'Mes', etiqueta: 'Mes' },
]

function moneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

function monedaEntera(valor: number): string {
  return formatearImporte(valor, { simbolo: true, decimales: 0 })
}

function textoDePorcentaje(porcentaje: number | null): string {
  if (porcentaje === null) return 'sin base'
  const redondeado = Math.round(porcentaje * 10) / 10
  const texto = formatearImporte(Math.abs(redondeado), { decimales: 1 })
  return `${redondeado > 0 ? '+' : redondeado < 0 ? '-' : ''}${texto} %`
}

function Tarjeta({ tarjeta }: { tarjeta: TarjetaDeValoracion }) {
  const { variacion } = tarjeta
  const sentido = sentidoDe(variacion.monto)
  return (
    <div className="col-6 col-lg">
      <div className="border p-3 bg-body h-100" data-testid={`valoracion-tarjeta-${tarjeta.clave}`}>
        <div className="text-muted small">{tarjeta.titulo}</div>
        <div className="fs-5 fw-semibold">{moneda(tarjeta.valor)}</div>
        <div className="small text-muted">
          {sentido === 'igual'
            ? 'Sin variación desde el inicio del rango'
            : `${formatearImporteConSigno(variacion.monto, { simbolo: true })} (${textoDePorcentaje(variacion.porcentaje)}) desde el inicio del rango`}
        </div>
      </div>
    </div>
  )
}

function Resultado({ datos, granularidad }: { datos: ValoracionDeInventario; granularidad: Granularidad }) {
  const tarjetas = useMemo(() => armarTarjetas(datos.puntos), [datos])
  const frase = useMemo(() => fraseDeValoracion(datos.puntos), [datos])
  const avisos = useMemo(() => avisosDeCobertura(datos.puntos), [datos])
  const serie = useMemo(() => aPuntosDeGrafico(datos.puntos, granularidad), [datos, granularidad])

  if (datos.puntos.length === 0) {
    return <p className="text-muted text-center py-4">No hay puntos de valoración en el rango elegido.</p>
  }

  const totalCuentas = datos.actual.porCuenta.reduce((suma, c) => suma + c.saldo, 0)
  const totalCosto = datos.actual.porArea.reduce((suma, a) => suma + a.mercaderiaCosto, 0)
  const totalVenta = datos.actual.porArea.reduce((suma, a) => suma + a.mercaderiaVenta, 0)

  return (
    <>
      {frase && (
        <p className="fs-5 mb-3" data-testid="valoracion-frase">
          {frase}
        </p>
      )}

      <div className="row g-2 mb-3">
        {tarjetas.map((t) => (
          <Tarjeta key={t.clave} tarjeta={t} />
        ))}
      </div>

      {avisos.length > 0 && (
        <div className="alert alert-warning small py-2 px-3" role="status">
          <ul className="mb-0">
            {avisos.map((a) => (
              <li key={a.clave}>
                {a.texto}
                {a.importe !== undefined && ` ${moneda(a.importe)}.`}
              </li>
            ))}
          </ul>
        </div>
      )}

      <div className="row g-3 mb-3">
        <div className="col-lg-7">
          <div className="border p-3 bg-body h-100">
            <h6>Evolución</h6>
            <GraficoDeLineasMultiples
              data={serie}
              series={SERIES_DE_EVOLUCION}
              alto={320}
              titulo="Evolución de la mercadería, el dinero y el total"
              formatearValor={monedaEntera}
            />
          </div>
        </div>
        <div className="col-lg-5">
          <div className="border p-3 bg-body h-100">
            <h6>Composición a costo</h6>
            <GraficoDeBarrasApiladas
              data={serie}
              series={SERIES_DE_COMPOSICION}
              alto={320}
              titulo="Composición del total a costo: mercadería, efectivo y cuentas"
              formatearValor={monedaEntera}
            />
          </div>
        </div>
      </div>

      <div className="row g-3 mb-3">
        <div className="col-lg-5">
          <div className="border p-3 bg-body h-100">
            <h6>Saldo por cuenta</h6>
            <table className="table table-sm mt-2 mb-0" aria-label="Saldo por cuenta">
              <thead>
                <tr>
                  <th>Cuenta</th>
                  <th className="text-end">Saldo</th>
                </tr>
              </thead>
              <tbody>
                {datos.actual.porCuenta.length === 0 && (
                  <tr>
                    <td colSpan={2} className="text-muted">
                      No hay cuentas de tesorería.
                    </td>
                  </tr>
                )}
                {datos.actual.porCuenta.map((c) => (
                  <tr key={c.idCuenta}>
                    <td>{c.nombre}</td>
                    <td className="text-end">{moneda(c.saldo)}</td>
                  </tr>
                ))}
              </tbody>
              <tfoot>
                <tr className="fw-semibold">
                  <td>Total</td>
                  <td className="text-end">{moneda(totalCuentas)}</td>
                </tr>
              </tfoot>
            </table>
          </div>
        </div>
        <div className="col-lg-7">
          <div className="border p-3 bg-body h-100">
            <h6>Mercadería por área</h6>
            <table className="table table-sm mt-2 mb-0" aria-label="Mercadería por área">
              <thead>
                <tr>
                  <th>Área</th>
                  <th className="text-end">A costo</th>
                  <th className="text-end">A precio de venta</th>
                </tr>
              </thead>
              <tbody>
                {datos.actual.porArea.length === 0 && (
                  <tr>
                    <td colSpan={3} className="text-muted">
                      No hay mercadería valorada.
                    </td>
                  </tr>
                )}
                {datos.actual.porArea.map((a) => (
                  <tr key={a.idArea ?? 'sin-area'}>
                    <td>{a.nombre}</td>
                    <td className="text-end">{moneda(a.mercaderiaCosto)}</td>
                    <td className="text-end">{moneda(a.mercaderiaVenta)}</td>
                  </tr>
                ))}
              </tbody>
              <tfoot>
                <tr className="fw-semibold">
                  <td>Total</td>
                  <td className="text-end">{moneda(totalCosto)}</td>
                  <td className="text-end">{moneda(totalVenta)}</td>
                </tr>
              </tfoot>
            </table>
          </div>
        </div>
      </div>

      <ul className="small text-muted mb-0">
        <li>
          {datos.actual.nombreListaPrecio === null
            ? 'No hay una lista de precios predeterminada: no se pudo valorar la mercadería a precio de venta.'
            : `La mercadería a precio de venta se valora con la lista «${datos.actual.nombreListaPrecio}».`}
        </li>
        <li>Solo suma stock positivo: un artículo con stock negativo en una sucursal no resta valor.</li>
        <li>El efectivo del cajón de un turno abierto no está incluido, igual que en la tesorería.</li>
        <li>
          El costo de los puntos anteriores a hoy se reconstruye con las compras: es el de la última compra confirmada
          hasta esa fecha, o el costo actual (informado como estimado) si no hay ninguna.
        </li>
        <li>Zona horaria: {datos.zonaHoraria}.</li>
      </ul>
    </>
  )
}

/**
 * Cuánto vale lo que tengo: mercadería a costo y a precio de venta más el dinero (caja general y cuentas), hoy y en
 * evolución (`Politicas.LecturaDeReportes`: Supervisor + Admin). Cada cambio de filtro lanza una consulta nueva gateada
 * por un único `useRef` de generación (`react-async-state`): una respuesta vieja nunca pisa un filtro ya cambiado.
 * Por defecto, los últimos 12 meses por mes.
 */
export function ReporteDeValoracion() {
  const [empresas, setEmpresas] = useState<EmpresaListado[] | null>(null)
  const [errorEmpresas, setErrorEmpresas] = useState('')

  const [idEmpresa, setIdEmpresa] = useState<number | null>(null)
  const [desde, setDesde] = useState(() => rangoDeLosUltimosDoceMeses().desde)
  const [hasta, setHasta] = useState(() => rangoDeLosUltimosDoceMeses().hasta)
  const [granularidad, setGranularidad] = useState<Granularidad>('Mes')

  const [datos, setDatos] = useState<ValoracionDeInventario | null>(null)
  const [granularidadDeLosDatos, setGranularidadDeLosDatos] = useState<Granularidad>('Mes')
  const [cargando, setCargando] = useState(false)
  const [error, setError] = useState('')
  const generacionRef = useRef(0)

  useEffect(() => {
    let vigente = true
    clienteDeOrganizacion
      .listarEmpresas()
      .then((lista) => {
        if (!vigente) return
        setEmpresas(lista)
        if (lista.length > 0) setIdEmpresa(lista[0].id)
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
  const filtros: FiltrosDeValoracion | null =
    idEmpresa !== null && rangoValido ? { idEmpresa, desde, hasta, granularidad } : null

  // La consulta depende de los valores y no de la identidad del objeto: se recrea solo si cambia un filtro.
  const clave = filtros === null ? null : JSON.stringify(filtros)

  const cargar = useCallback(() => {
    const miGeneracion = (generacionRef.current += 1)
    if (clave === null) {
      setDatos(null)
      setCargando(false)
      setError('')
      return
    }
    const pedidos = JSON.parse(clave) as FiltrosDeValoracion
    setDatos(null)
    setCargando(true)
    setError('')

    clienteDeReportes
      .valoracion(pedidos)
      .then((respuesta) => {
        if (generacionRef.current !== miGeneracion) return
        setGranularidadDeLosDatos(pedidos.granularidad)
        setDatos(respuesta)
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        setDatos(null)
        setError(mensajeDeErrorDeValoracion(e))
      })
      .finally(() => {
        if (generacionRef.current !== miGeneracion) return
        setCargando(false)
      })
  }, [clave])

  useEffect(() => {
    cargar()
  }, [cargar])

  return (
    <div className="container-fluid py-4">
      <Box titulo="Valoración" variante="inverse">
        {errorEmpresas && <div className="alert alert-warning py-1 px-2 small">{errorEmpresas}</div>}

        {empresas === null ? (
          <Cargando />
        ) : empresas.length === 0 ? (
          <p className="text-muted text-center py-4">No hay empresas visibles para mostrar el reporte.</p>
        ) : (
          <>
            <div className="row g-2 align-items-end mb-3">
              <div className="col-md-3">
                <label className="form-label" htmlFor="valoracion-empresa">
                  Empresa
                </label>
                <select
                  id="valoracion-empresa"
                  className="form-select"
                  value={idEmpresa ?? ''}
                  onChange={(e) => setIdEmpresa(Number(e.target.value))}
                >
                  {empresas.map((e) => (
                    <option key={e.id} value={e.id}>
                      {e.razonSocial}
                    </option>
                  ))}
                </select>
              </div>
              <div className="col-md-2">
                <label className="form-label" htmlFor="valoracion-desde">
                  Desde
                </label>
                <CampoFecha id="valoracion-desde" className="form-control" value={desde} onChange={setDesde} />
              </div>
              <div className="col-md-2">
                <label className="form-label" htmlFor="valoracion-hasta">
                  Hasta
                </label>
                <CampoFecha id="valoracion-hasta" className="form-control" value={hasta} onChange={setHasta} />
              </div>
              <div className="col-md-2">
                <label className="form-label" htmlFor="valoracion-granularidad">
                  Un punto por
                </label>
                <select
                  id="valoracion-granularidad"
                  className="form-select"
                  value={granularidad}
                  onChange={(e) => setGranularidad(e.target.value as Granularidad)}
                >
                  {GRANULARIDADES.map((g) => (
                    <option key={g.valor} value={g.valor}>
                      {g.etiqueta}
                    </option>
                  ))}
                </select>
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

            {datos && <Resultado datos={datos} granularidad={granularidadDeLosDatos} />}
          </>
        )}
      </Box>
    </div>
  )
}
