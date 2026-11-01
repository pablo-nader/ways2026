import { Fragment, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import { clienteDeReportes, rangoDelMesActual, rutasDeExportacion, type FiltrosDeLibroIva } from '../api/reportes'
import type { EmpresaListado, FilaDeLibroIva, LibroIva as LibroIvaRespuesta } from '../api/tipos'
import { BotonDeDescarga } from '../componentes/BotonDeDescarga'
import { toast } from '../toasts/toasts'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { EncabezadoOrdenable, useOrdenamiento, type ColumnasOrdenables } from '../componentes/grilla'
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
import { CampoFecha } from '../componentes/CampoFecha'

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
    setFiltros((prev) => ({ ...prev, ...cambios }))
  }

  function cambiarPestana(nueva: PestanaDeLibroIva) {
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
            <CampoFecha
              id="libro-iva-desde"
              className="form-control"
              value={filtros.desde}
              onChange={(valor) => cambiarFiltro({ desde: valor })}
            />
          </div>
          <div className="col-md-2">
            <label className="form-label" htmlFor="libro-iva-hasta">
              Hasta
            </label>
            <CampoFecha
              id="libro-iva-hasta"
              className="form-control"
              value={filtros.hasta}
              onChange={(valor) => cambiarFiltro({ hasta: valor })}
            />
          </div>
          <div className="col-auto">
            <BotonDeDescarga
              ruta={rutaDeExportacion}
              etiqueta="Descargar"
              disabled={!valido}
              onError={toast.error}
            />
          </div>
        </div>

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
  const porcentajes = useMemo(() => porcentajesDelLibro(libro), [libro])
  const columnas = useMemo<ColumnasOrdenables<FilaDeLibroIva>>(() => {
    const porAlicuota: ColumnasOrdenables<FilaDeLibroIva> = {}
    for (const p of porcentajes) {
      porAlicuota[`neto-${p}`] = { valor: (f) => alicuotaDeFila(f, p)?.neto ?? 0 }
      porAlicuota[`iva-${p}`] = { valor: (f) => alicuotaDeFila(f, p)?.iva ?? 0 }
    }

    return {
      fecha: { valor: (f) => new Date(f.fecha) },
      tipo: { valor: (f) => f.tipoComprobante },
      numero: { valor: (f) => f.numero },
      contraparte: { valor: (f) => f.contraparte },
      documento: { valor: (f) => f.documento },
      ...porAlicuota,
      noGravado: { valor: (f) => f.noGravado },
      exento: { valor: (f) => f.exento },
      percepcionIva: { valor: (f) => f.percepcionIva },
      percepcionIibb: { valor: (f) => f.percepcionIibb },
      impuestosInternos: { valor: (f) => f.impuestosInternos },
      total: { valor: (f) => f.total },
      diferencia: { valor: (f) => f.diferencia },
      observaciones: { valor: (f) => f.advertencias.map(etiquetaDeAdvertencia).join(', ') },
    }
  }, [porcentajes])
  const { filas, orden, alternar } = useOrdenamiento(libro.filas, columnas)
  const conDiferencia = filasConDiferencia(libro)
  const mostrarDiferencia = conDiferencia.length > 0
  const mostrarAdvertencias = filasConAdvertencias(libro).length > 0
  const columnasFijas = 5
  const cantidadDeColumnas =
    columnasFijas + porcentajes.length * 2 + 3 + (configuracion.conPercepciones ? 2 : 0) + (configuracion.conImpuestosInternos ? 1 : 0) + (mostrarDiferencia ? 1 : 0) +
    (mostrarAdvertencias ? 1 : 0)

  return (
    <>
      {mostrarDiferencia && (
        <div className="alert alert-warning py-1 px-2 small mb-2" role="alert">
          {conDiferencia.length === 1
            ? '1 comprobante no cierra contra sus importes: revisá la columna Dif.'
            : `${conDiferencia.length} comprobantes no cierran contra sus importes: revisá la columna Dif.`}
        </div>
      )}
      <div className="table-responsive">
        <table className="table table-sm table-striped table-bordered align-middle">
          <thead>
            <tr>
              <EncabezadoOrdenable columna="fecha" orden={orden} onAlternar={alternar}>
                Fecha
              </EncabezadoOrdenable>
              <EncabezadoOrdenable columna="tipo" orden={orden} onAlternar={alternar}>
                Tipo
              </EncabezadoOrdenable>
              <EncabezadoOrdenable columna="numero" orden={orden} onAlternar={alternar}>
                Número
              </EncabezadoOrdenable>
              <EncabezadoOrdenable columna="contraparte" orden={orden} onAlternar={alternar}>
                {configuracion.contraparte}
              </EncabezadoOrdenable>
              <EncabezadoOrdenable columna="documento" orden={orden} onAlternar={alternar}>
                {configuracion.documento}
              </EncabezadoOrdenable>
              {porcentajes.map((p) => (
                <Fragment key={p}>
                  <EncabezadoOrdenable columna={`neto-${p}`} orden={orden} onAlternar={alternar} className="text-end">
                    Neto {etiquetaDePorcentaje(p)}
                  </EncabezadoOrdenable>
                  <EncabezadoOrdenable columna={`iva-${p}`} orden={orden} onAlternar={alternar} className="text-end">
                    IVA {etiquetaDePorcentaje(p)}
                  </EncabezadoOrdenable>
                </Fragment>
              ))}
              <EncabezadoOrdenable columna="noGravado" orden={orden} onAlternar={alternar} className="text-end">
                No gravado
              </EncabezadoOrdenable>
              <EncabezadoOrdenable columna="exento" orden={orden} onAlternar={alternar} className="text-end">
                Exento
              </EncabezadoOrdenable>
              {configuracion.conPercepciones && (
                <EncabezadoOrdenable columna="percepcionIva" orden={orden} onAlternar={alternar} className="text-end">
                  Perc. IVA
                </EncabezadoOrdenable>
              )}
              {configuracion.conPercepciones && (
                <EncabezadoOrdenable columna="percepcionIibb" orden={orden} onAlternar={alternar} className="text-end">
                  Perc. IIBB
                </EncabezadoOrdenable>
              )}
              {configuracion.conImpuestosInternos && (
                <EncabezadoOrdenable columna="impuestosInternos" orden={orden} onAlternar={alternar} className="text-end">
                  Imp. internos
                </EncabezadoOrdenable>
              )}
              <EncabezadoOrdenable columna="total" orden={orden} onAlternar={alternar} className="text-end">
                Total
              </EncabezadoOrdenable>
              {mostrarDiferencia && (
                <EncabezadoOrdenable columna="diferencia" orden={orden} onAlternar={alternar} className="text-end">
                  Dif.
                </EncabezadoOrdenable>
              )}
              {mostrarAdvertencias && (
                <EncabezadoOrdenable columna="observaciones" orden={orden} onAlternar={alternar}>
                  Observaciones
                </EncabezadoOrdenable>
              )}
            </tr>
          </thead>
          <tbody>
            {filas.map((fila, indice) => (
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
                {configuracion.conImpuestosInternos && <td className="text-end">{formatearMoneda(fila.impuestosInternos)}</td>}
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
                {configuracion.conImpuestosInternos && (
                  <td className="text-end">{formatearMoneda(libro.totales.impuestosInternos)}</td>
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
