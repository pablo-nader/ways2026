import { useCallback, useEffect, useRef, useState } from 'react'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import { clienteDeReportes, rangoUltimosSieteDias } from '../api/reportes'
import { puedeGestionarCatalogos } from '../api/tipos'
import type { EmpresaListado, LibroDeTesoreria } from '../api/tipos'
import { useAuth } from '../auth/useAuth'
import { Box } from '../componentes/Box'
import { CampoFecha } from '../componentes/CampoFecha'
import { Cargando } from '../componentes/Cargando'
import { ModalDeAjusteDeCuenta } from '../componentes/ModalDeAjusteDeCuenta'
import { ModalDeAjusteDeEfectivo } from '../componentes/ModalDeAjusteDeEfectivo'
import { ModalDeConciliacion } from '../componentes/ModalDeConciliacion'
import { fechaHoraATexto } from '../formato/fechas'
import { ResumenDeLiquidez } from './tesoreria/ResumenDeLiquidez'
import { TablaDelLibro } from './tesoreria/TablaDelLibro'
import { toast } from '../toasts/toasts'

type Filtros = { idEmpresa: number | null; desde: string; hasta: string }

type CuentaAAjustar = { id: number; nombre: string; saldoActual: number }

function filtrosIniciales(): Filtros {
  const rango = rangoUltimosSieteDias()
  return { idEmpresa: null, desde: rango.desde, hasta: rango.hasta }
}

/**
 * Libro de tesorería de una empresa: liquidez de hoy (efectivo y una tarjeta por cuenta) y una tabla con una fila por cierre de
 * caja, pago y ajuste manual, con el efectivo y el importe en cada cuenta. Lectura de Supervisor y Admin; los ajustes son del Admin.
 *
 * `react-async-state`: la lectura del libro lleva generación (`generacionRef`) y cada cambio de empresa o de fechas la invalida
 * en el mismo evento y limpia el libro, de modo que una respuesta tardía de otro rango nunca se pinta. Los ajustes los escriben
 * los modales, que dejan inerte su ventana mientras escriben; acá solo se recarga después. Mientras un modal está abierto el resto
 * de la pantalla no ofrece otra escritura.
 */
export function Tesoreria() {
  const { usuario } = useAuth()
  const esAdmin = usuario !== null && puedeGestionarCatalogos(usuario.rolId)

  const [empresas, setEmpresas] = useState<EmpresaListado[] | null>(null)
  const [errorCatalogos, setErrorCatalogos] = useState('')
  const [filtros, setFiltros] = useState<Filtros>(filtrosIniciales)
  const [libro, setLibro] = useState<LibroDeTesoreria | null>(null)
  const [cargando, setCargando] = useState(false)
  const [error, setError] = useState('')
  const generacionRef = useRef(0)

  const [ajustandoEfectivo, setAjustandoEfectivo] = useState(false)
  const [cuentaAAjustar, setCuentaAAjustar] = useState<CuentaAAjustar | null>(null)
  const [conciliando, setConciliando] = useState(false)
  const [avisoIncierto, setAvisoIncierto] = useState('')

  const { idEmpresa, desde, hasta } = filtros
  const rangoInvertido = desde !== '' && hasta !== '' && desde > hasta

  useEffect(() => {
    let vigente = true
    clienteDeOrganizacion
      .listarEmpresas()
      .then((cargadas) => {
        if (!vigente) return
        setEmpresas(cargadas)
        // Empresa única: se preselecciona, mismo criterio que Pagos.
        if (cargadas.length === 1) setFiltros((previos) => ({ ...previos, idEmpresa: cargadas[0].id }))
      })
      .catch((e) => {
        if (!vigente) return
        setEmpresas([])
        setErrorCatalogos(e instanceof ErrorApi ? e.message : 'No se pudieron cargar las empresas.')
      })

    return () => {
      vigente = false
    }
  }, [])

  const cargar = useCallback(() => {
    const miGeneracion = (generacionRef.current += 1)
    if (idEmpresa === null || desde === '' || hasta === '' || desde > hasta) {
      setLibro(null)
      setCargando(false)
      setError('')
      return
    }

    setCargando(true)
    setError('')

    clienteDeReportes
      .libroDeTesoreria({ idEmpresa, desde, hasta })
      .then((datos) => {
        if (generacionRef.current !== miGeneracion) return
        setLibro(datos)
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        setLibro(null)
        setError(e instanceof ErrorApi ? e.message : 'No se pudo cargar el libro de tesorería.')
      })
      .finally(() => {
        if (generacionRef.current !== miGeneracion) return
        setCargando(false)
      })
  }, [idEmpresa, desde, hasta])

  useEffect(() => {
    cargar()
  }, [cargar])

  function cambiarFiltros(cambios: Partial<Filtros>) {
    generacionRef.current += 1
    setLibro(null)
    setFiltros((previos) => ({ ...previos, ...cambios }))
  }

  function alAjustar(mensaje: string) {
    setAjustandoEfectivo(false)
    setCuentaAAjustar(null)
    setAvisoIncierto('')
    toast.exito(mensaje)
    cargar()
  }

  function alSerIncierto(que: string) {
    setAjustandoEfectivo(false)
    setCuentaAAjustar(null)
    setAvisoIncierto(`No se pudo confirmar el ajuste de ${que}. Ya se volvió a cargar el libro: verificá si figura antes de reintentarlo.`)
    cargar()
  }

  function alConciliar() {
    setConciliando(false)
    setAvisoIncierto('')
    toast.exito('Conciliación registrada.')
    cargar()
  }

  function alSerInciertaLaConciliacion() {
    setConciliando(false)
    setAvisoIncierto(
      'No se pudo confirmar la conciliación. Ya se volvió a cargar el libro: verificá si figura como conciliado antes de reintentarla.',
    )
    cargar()
  }

  const modalAbierto = ajustandoEfectivo || cuentaAAjustar !== null || conciliando

  return (
    <div className="container-fluid py-4">
      <Box
        titulo="Libro de tesorería"
        variante="inverse"
        herramientas={
          esAdmin && idEmpresa !== null ? (
            <nav className="p-2 d-flex gap-2">
              <button
                type="button"
                className="btn btn-sm btn-outline-light text-nowrap"
                disabled={modalAbierto}
                onClick={() => setAjustandoEfectivo(true)}
              >
                Ajustar efectivo
              </button>
              <button
                type="button"
                className="btn btn-sm btn-outline-light text-nowrap"
                disabled={modalAbierto}
                onClick={() => setConciliando(true)}
              >
                Conciliar
              </button>
            </nav>
          ) : undefined
        }
      >
        {errorCatalogos && <div className="alert alert-warning py-1 px-2 small">{errorCatalogos}</div>}

        {empresas === null ? (
          <Cargando />
        ) : empresas.length === 0 ? (
          <p className="text-muted text-center py-4">No hay empresas visibles para el libro de tesorería.</p>
        ) : (
          <>
            <div className="row g-2 align-items-end mb-3">
              <div className="col-md-4">
                <label className="form-label" htmlFor="libro-empresa">
                  Empresa
                </label>
                <select
                  id="libro-empresa"
                  className="form-select"
                  value={idEmpresa ?? ''}
                  onChange={(e) =>
                    cambiarFiltros({
                      idEmpresa: e.target.value === '' ? null : Number(e.target.value),
                    })
                  }
                >
                  <option value="">Elegir…</option>
                  {empresas.map((emp) => (
                    <option key={emp.id} value={emp.id}>
                      {emp.razonSocial}
                    </option>
                  ))}
                </select>
              </div>
              <div className="col-md-2">
                <label className="form-label" htmlFor="libro-desde">
                  Desde
                </label>
                <CampoFecha
                  id="libro-desde"
                  className="form-control"
                  value={desde}
                  onChange={(valor) => cambiarFiltros({ desde: valor })}
                />
              </div>
              <div className="col-md-2">
                <label className="form-label" htmlFor="libro-hasta">
                  Hasta
                </label>
                <CampoFecha
                  id="libro-hasta"
                  className="form-control"
                  value={hasta}
                  onChange={(valor) => cambiarFiltros({ hasta: valor })}
                />
              </div>
            </div>

            {error && (
              <div className="alert alert-danger d-flex justify-content-between align-items-center gap-2">
                <span>{error}</span>
                <button type="button" className="btn btn-sm btn-outline-danger" onClick={cargar}>
                  Reintentar
                </button>
              </div>
            )}


            {avisoIncierto && (
              <div className="alert alert-warning py-2 px-3 small d-flex justify-content-between align-items-center">
                <span>{avisoIncierto}</span>
                <button type="button" className="btn btn-sm btn-outline-secondary" onClick={() => setAvisoIncierto('')}>
                  Ya lo revisé
                </button>
              </div>
            )}

            {idEmpresa === null ? (
              <p className="text-muted text-center py-4">Elegí una empresa para ver su libro de tesorería.</p>
            ) : rangoInvertido ? (
              <div className="alert alert-warning py-1 px-2 small">
                La fecha &quot;Desde&quot; no puede ser posterior a &quot;Hasta&quot;.
              </div>
            ) : desde === '' || hasta === '' ? (
              <p className="text-muted text-center py-4">Elegí las fechas &quot;Desde&quot; y &quot;Hasta&quot;.</p>
            ) : (
              <>
                {cargando && !libro && <Cargando />}
                {libro && (
                  <>
                    <p className="libro-conciliacion small mb-2" role="status" data-conciliado={libro.conciliadoHasta !== null}>
                      {libro.conciliadoHasta !== null ? `Conciliado hasta ${fechaHoraATexto(libro.conciliadoHasta)}` : 'Sin conciliar'}
                    </p>
                    <ResumenDeLiquidez libro={libro} esAdmin={esAdmin} deshabilitado={modalAbierto} onAjustarCuenta={setCuentaAAjustar} />
                    <TablaDelLibro libro={libro} puedeAbrirPagos={esAdmin} />
                  </>
                )}
              </>
            )}
          </>
        )}
      </Box>

      {ajustandoEfectivo && idEmpresa !== null && (
        <ModalDeAjusteDeEfectivo
          idEmpresa={idEmpresa}
          onCerrar={() => setAjustandoEfectivo(false)}
          onAjustado={() => alAjustar('Ajuste de efectivo registrado.')}
          onIncierto={() => alSerIncierto('efectivo')}
        />
      )}

      {conciliando && idEmpresa !== null && (
        <ModalDeConciliacion
          idEmpresa={idEmpresa}
          conciliadoHasta={libro?.conciliadoHasta ?? null}
          onCerrar={() => setConciliando(false)}
          onConciliado={alConciliar}
          onIncierto={alSerInciertaLaConciliacion}
        />
      )}

      {cuentaAAjustar && (
        <ModalDeAjusteDeCuenta
          cuenta={cuentaAAjustar}
          onCerrar={() => setCuentaAAjustar(null)}
          onAjustado={() => alAjustar(`Ajuste de ${cuentaAAjustar.nombre} registrado.`)}
          onIncierto={() => alSerIncierto(cuentaAAjustar.nombre)}
        />
      )}
    </div>
  )
}
