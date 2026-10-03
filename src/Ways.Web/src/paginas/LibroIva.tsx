import { Fragment, useCallback, useEffect, useRef, useState } from 'react'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import { clienteDeReportes, rangoDelMesActual, rutasDeExportacion, type FiltrosDeLibroIva } from '../api/reportes'
import type { EmpresaListado, LibroIva as LibroIvaRespuesta } from '../api/tipos'
import { BotonDeDescarga } from '../componentes/BotonDeDescarga'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { formatearImporte } from '../formato/importes'
import {
  alicuotaDeFila,
  CONFIGURACION_DE_PESTANA,
  etiquetaDeAdvertencia,
  etiquetaDePorcentaje,
  filasConAdvertencias,
  filasConDiferencia,
  formatearFechaDeLibro,
  periodoValido,
  porcentajesDelLibro,
  type PestanaDeLibroIva,
} from './calculosDeLibroIva'

function formatearMoneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

const PESTANAS: PestanaDeLibroIva[] = ['compras', 'ventas']

function filtrosIniciales(): FiltrosDeLibroIva {
  const rango = rangoDelMesActual()
  return { idEmpresa: null, desde: rango.desde, hasta: rango.hasta }
}

/**
 * Libro IVA (compras y ventas): una pantalla, dos pestañas, un mismo período y una misma empresa
 * ("Todas" por defecto) para ambas. Mismo gate que el resto de los reportes
 * (`Politicas.LecturaDeReportes`: Supervisor + Admin). Cada cambio de pestaña o de filtro invalida
 * la respuesta anterior y descarta la que quede en vuelo (`generacionRef`): la tabla solo muestra
 * datos de la pestaña y los filtros vigentes.
 */
export function LibroIva() {
  const [empresas, setEmpresas] = useState<EmpresaListado[] | null>(null)
  const [errorEmpresas, setErrorEmpresas] = useState('')

  const [pestana, setPestana] = useState<PestanaDeLibroIva>('compras')
  const [filtros, setFiltros] = useState<FiltrosDeLibroIva>(filtrosIniciales)
  const [libro, setLibro] = useState<LibroIvaRespuesta | null>(null)
  const [cargando, setCargando] = useState(false)
  const [error, setError] = useState('')
  const [errorDescarga, setErrorDescarga] = useState('')
  const generacionRef = useRef(0)

  useEffect(() => {
    let vigente = true
    clienteDeOrganizacion
      .listarEmpresas()
      .then((datos) => {
        if (vigente) setEmpresas(datos)
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

  const cargar = useCallback(() => {
    const miGeneracion = (generacionRef.current += 1)
    setLibro(null)
    setError('')

    if (!periodoValido(filtros.desde, filtros.hasta)) {
      setCargando(false)
      return
    }

    setCargando(true)
    const pedir = pestana === 'compras' ? clienteDeReportes.libroIvaCompras : clienteDeReportes.libroIvaVentas

    pedir(filtros)
      .then((datos) => {
        if (generacionRef.current !== miGeneracion) return
        setLibro(datos)
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        setError(e instanceof ErrorApi ? e.message : 'No se pudo cargar el libro IVA.')
      })
      .finally(() => {
        if (generacionRef.current !== miGeneracion) return
        setCargando(false)
      })
  }, [pestana, filtros])

  useEffect(() => {
    cargar()
  }, [cargar])

  function cambiarFiltro(cambios: Partial<FiltrosDeLibroIva>) {
    setErrorDescarga('')
    setFiltros((prev) => ({ ...prev, ...cambios }))
  }

  function cambiarPestana(nueva: PestanaDeLibroIva) {
    setErrorDescarga('')
    setPestana(nueva)
  }

  const valido = periodoValido(filtros.desde, filtros.hasta)
  const rutaDeExportacion =
    pestana === 'compras' ? rutasDeExportacion.libroIvaCompras(filtros) : rutasDeExportacion.libroIvaVentas(filtros)

  return (
    <div className="container-fluid py-4">
      <Box titulo="Libro IVA" variante="inverse">
        <ul className="nav nav-tabs mb-3" role="tablist">
          {PESTANAS.map((clave) => (
            <li key={clave} className="nav-item" role="presentation">
              <button
                type="button"
                role="tab"
                aria-selected={pestana === clave}
                className={`nav-link${pestana === clave ? ' active' : ''}`}
                onClick={() => cambiarPestana(clave)}
              >
                {CONFIGURACION_DE_PESTANA[clave].titulo}
              </button>
            </li>
          ))}
        </ul>

        {errorEmpresas && <div className="alert alert-warning py-1 px-2 small">{errorEmpresas}</div>}

        <div className="row g-2 align-items-end mb-3">
          <div className="col-md-3">
            <label className="form-label" htmlFor="libro-iva-empresa">
              Empresa
            </label>
            <select
              id="libro-iva-empresa"
              className="form-select"
              value={filtros.idEmpresa ?? ''}
              onChange={(e) => cambiarFiltro({ idEmpresa: e.target.value === '' ? null : Number(e.target.value) })}
            >
              <option value="">Todas</option>
              {(empresas ?? []).map((empresa) => (
                <option key={empresa.id} value={empresa.id}>
                  {empresa.razonSocial}
                </option>
              ))}
            </select>
          </div>
          <div className="col-md-2">
            <label className="form-label" htmlFor="libro-iva-desde">
              Desde
            </label>
            <input
              id="libro-iva-desde"
              type="date"
              className="form-control"
              value={filtros.desde}
              onChange={(e) => cambiarFiltro({ desde: e.target.value })}
            />
          </div>
          <div className="col-md-2">
            <label className="form-label" htmlFor="libro-iva-hasta">
              Hasta
            </label>
            <input
              id="libro-iva-hasta"
              type="date"
              className="form-control"
              value={filtros.hasta}
              onChange={(e) => cambiarFiltro({ hasta: e.target.value })}
            />
          </div>
          <div className="col-auto">
            <BotonDeDescarga
              ruta={rutaDeExportacion}
              etiqueta="Descargar"
              disabled={!valido}
              onError={setErrorDescarga}
              onInicio={() => setErrorDescarga('')}
            />
          </div>
        </div>

        {errorDescarga && <div className="alert alert-danger py-1 px-2 small mb-2">{errorDescarga}</div>}

        {!valido && (
          <p className="text-muted text-center py-4">Elegí un período válido: “desde” no puede ser posterior a “hasta”.</p>
        )}

        {valido && error && (
          <div className="alert alert-danger d-flex justify-content-between align-items-center gap-2">
            <span>{error}</span>
            <button type="button" className="btn btn-sm btn-outline-danger" onClick={cargar}>
              Reintentar
            </button>
          </div>
        )}

        {valido && cargando && <Cargando />}

        {valido && libro && <TablaDeLibro libro={libro} pestana={pestana} />}
        {valido && libro && (
          <p className="small text-muted mb-0">
            {pestana === 'compras'
              ? 'Compras por fecha del comprobante. Una factura B o C no discrimina IVA: va entera a No gravado, sin crédito fiscal.'
              : 'Ventas fiscales con CAE aprobado, por el día local de la empresa. Una nota de crédito resta.'}
            {pestana === 'ventas' && libro.zonaHoraria ? ` Zona: ${libro.zonaHoraria}.` : ''}
          </p>
        )}
      </Box>
    </div>
  )
}

type PropsTablaDeLibro = { libro: LibroIvaRespuesta; pestana: PestanaDeLibroIva }

function TablaDeLibro({ libro, pestana }: PropsTablaDeLibro) {
  const configuracion = CONFIGURACION_DE_PESTANA[pestana]
  const porcentajes = porcentajesDelLibro(libro)
  const conDiferencia = filasConDiferencia(libro)
  const mostrarDiferencia = conDiferencia.length > 0
  const mostrarAdvertencias = filasConAdvertencias(libro).length > 0
  const columnasFijas = 5
  const cantidadDeColumnas =
    columnasFijas + porcentajes.length * 2 + 3 + (configuracion.conPercepciones ? 2 : 0) + (mostrarDiferencia ? 1 : 0) +
    (mostrarAdvertencias ? 1 : 0)

  return (
    <>
      {mostrarDiferencia && (
        <div className="alert alert-warning py-1 px-2 small mb-2" role="alert">
          {conDiferencia.length} comprobante(s) no cierran contra sus importes: revisá la columna Dif.
        </div>
      )}
      <div className="table-responsive">
        <table className="table table-sm table-striped table-bordered align-middle">
          <thead>
            <tr>
              <th>Fecha</th>
              <th>Tipo</th>
              <th>Número</th>
              <th>{configuracion.contraparte}</th>
              <th>{configuracion.documento}</th>
              {porcentajes.map((p) => (
                <Fragment key={p}>
                  <th className="text-end">Neto {etiquetaDePorcentaje(p)}</th>
                  <th className="text-end">IVA {etiquetaDePorcentaje(p)}</th>
                </Fragment>
              ))}
              <th className="text-end">No gravado</th>
              <th className="text-end">Exento</th>
              {configuracion.conPercepciones && <th className="text-end">Perc. IVA</th>}
              {configuracion.conPercepciones && <th className="text-end">Perc. IIBB</th>}
              <th className="text-end">Total</th>
              {mostrarDiferencia && <th className="text-end">Dif.</th>}
              {mostrarAdvertencias && <th>Observaciones</th>}
            </tr>
          </thead>
          <tbody>
            {libro.filas.map((fila, indice) => (
              <tr key={`${indice}-${fila.tipoComprobante}-${fila.numero}`}>
                <td>{formatearFechaDeLibro(fila.fecha)}</td>
                <td>{fila.tipoComprobante}</td>
                <td>{fila.numero}</td>
                <td>{fila.contraparte}</td>
                <td>{fila.documento ?? '—'}</td>
                {porcentajes.map((p) => (
                  <Fragment key={p}>
                    <td className="text-end">{formatearMoneda(alicuotaDeFila(fila, p)?.neto ?? 0)}</td>
                    <td className="text-end">{formatearMoneda(alicuotaDeFila(fila, p)?.iva ?? 0)}</td>
                  </Fragment>
                ))}
                <td className="text-end">{formatearMoneda(fila.noGravado)}</td>
                <td className="text-end">{formatearMoneda(fila.exento)}</td>
                {configuracion.conPercepciones && <td className="text-end">{formatearMoneda(fila.percepcionIva)}</td>}
                {configuracion.conPercepciones && <td className="text-end">{formatearMoneda(fila.percepcionIibb)}</td>}
                <td className="text-end">{formatearMoneda(fila.total)}</td>
                {mostrarDiferencia && (
                  <td className={`text-end${fila.diferencia !== 0 ? ' text-danger fw-bold' : ''}`}>
                    {fila.diferencia !== 0 ? formatearMoneda(fila.diferencia) : '—'}
                  </td>
                )}
                {mostrarAdvertencias && (
                  <td>
                    {fila.advertencias.map((codigo) => (
                      <span key={codigo} className="badge text-bg-warning me-1">
                        {etiquetaDeAdvertencia(codigo)}
                      </span>
                    ))}
                  </td>
                )}
              </tr>
            ))}
            {libro.filas.length === 0 && (
              <tr>
                <td colSpan={cantidadDeColumnas} className="text-center text-muted py-4">
                  No hay comprobantes en el período.
                </td>
              </tr>
            )}
          </tbody>
          {libro.filas.length > 0 && (
            <tfoot>
              <tr className="fw-bold">
                <td colSpan={columnasFijas}>Totales</td>
                {porcentajes.map((p) => (
                  <Fragment key={p}>
                    <td className="text-end">
                      {formatearMoneda(libro.totales.porAlicuota.find((a) => a.porcentaje === p)?.neto ?? 0)}
                    </td>
                    <td className="text-end">
                      {formatearMoneda(libro.totales.porAlicuota.find((a) => a.porcentaje === p)?.iva ?? 0)}
                    </td>
                  </Fragment>
                ))}
                <td className="text-end">{formatearMoneda(libro.totales.noGravado)}</td>
                <td className="text-end">{formatearMoneda(libro.totales.exento)}</td>
                {configuracion.conPercepciones && (
                  <td className="text-end">{formatearMoneda(libro.totales.percepcionIva)}</td>
                )}
                {configuracion.conPercepciones && (
                  <td className="text-end">{formatearMoneda(libro.totales.percepcionIibb)}</td>
                )}
                <td className="text-end">{formatearMoneda(libro.totales.total)}</td>
                {mostrarDiferencia && <td className="text-end">{formatearMoneda(libro.totales.diferencia)}</td>}
                {mostrarAdvertencias && <td />}
              </tr>
            </tfoot>
          )}
        </table>
      </div>
    </>
  )
}
