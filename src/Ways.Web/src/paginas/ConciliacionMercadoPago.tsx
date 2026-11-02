import { useCallback, useEffect, useRef, useState } from 'react'
import {
  clienteDeConciliacionMercadoPago,
  mensajeDeErrorDeConciliacion,
  validarArchivo,
  type AccionDePropuesta,
  type CandidatoDeConfirmacion,
  type ConciliacionImportada,
  type FilaDeConciliacion,
  type PropuestaDeTesoreria,
} from '../api/conciliacionMercadoPago'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import { puedeGestionarCatalogos } from '../api/tipos'
import type { PuntoVentaListado } from '../api/tipos'
import { useAuth } from '../auth/useAuth'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { toast } from '../toasts/toasts'
import { DialogoDeConfirmacionDeFila } from './tesoreria/DialogoDeConfirmacionDeFila'
import { DialogoDeResolucionDePropuesta } from './tesoreria/DialogoDeResolucionDePropuesta'
import { SeccionConciliaciones } from './tesoreria/SeccionConciliaciones'
import type { DestinoElegido } from './tesoreria/SeccionDePropuestas'
import { SeccionImportar } from './tesoreria/SeccionImportar'
import { SeccionParaRevisar } from './tesoreria/SeccionParaRevisar'
import { useLectura } from './tesoreria/useLectura'

type Pestana = 'importar' | 'conciliaciones' | 'revisar'

const TITULO_DE_PESTANA: Record<Pestana, string> = {
  importar: 'Importar',
  conciliaciones: 'Conciliaciones',
  revisar: 'Para revisar',
}

type Confirmacion = { idConciliacion: number; fila: FilaDeConciliacion; candidato: CandidatoDeConfirmacion }

type Resolucion = { idConciliacion: number; propuesta: PropuestaDeTesoreria; accion: AccionDePropuesta; destino: DestinoElegido | null; origen: DestinoElegido | null }

type EmpresaElegible = { id: number; nombre: string }

/**
 * Las empresas que ve el usuario salen de sus puntos de venta (`GET /api/puntos-venta`, legible por Supervisor): `GET /api/empresas`
 * es solo de Root/Admin y daría 403 al Supervisor, que sí lee la conciliación.
 */
function empresasDeLosPuntosDeVenta(puntosVenta: readonly PuntoVentaListado[]): EmpresaElegible[] {
  const porId = new Map<number, EmpresaElegible>()
  for (const pv of puntosVenta) {
    // Sin razón social la empresa está dada de baja: no se ofrece.
    if (pv.razonSocialEmpresa != null && !porId.has(pv.idEmpresa)) porId.set(pv.idEmpresa, { id: pv.idEmpresa, nombre: pv.razonSocialEmpresa })
  }
  return [...porId.values()].sort((a, b) => a.nombre.localeCompare(b.nombre, 'es'))
}

const mensajeDeLectura = (e: unknown) => mensajeDeErrorDeConciliacion(e, 'lectura')

/**
 * Conciliación de Mercado Pago de una empresa: importar el reporte (Admin), ver los reportes importados con sus filas y revisar lo
 * que quedó pendiente. Lectura de Supervisor y Admin; subir y confirmar son del Admin.
 *
 * `react-async-state`: las lecturas pasan por `useLectura` (una generación por corrida, así una respuesta de otra empresa o de otra
 * página nunca se pinta). Las dos escrituras (subir, confirmar) comparten `ocupadoRef` como guarda síncrona de re-entrancia y dejan
 * inerte toda la pantalla mientras están en vuelo (`bloqueado`: empresa, pestañas, filas y diálogo), de modo que nada puede reemplazar
 * lo que su respuesta escribe. Después de escribir, `version` vuelve a pedir las lecturas; si ese refresco falla se avisa en la
 * propia lectura y la escritura no se reporta como fallida.
 */
export function ConciliacionMercadoPago() {
  const { usuario } = useAuth()
  const esAdmin = usuario !== null && puedeGestionarCatalogos(usuario.rolId)
  const pestanas: Pestana[] = esAdmin ? ['importar', 'conciliaciones', 'revisar'] : ['conciliaciones', 'revisar']

  const [empresas, setEmpresas] = useState<EmpresaElegible[] | null>(null)
  const [errorEmpresas, setErrorEmpresas] = useState('')
  const [idEmpresa, setIdEmpresa] = useState<number | null>(null)
  const [pestana, setPestana] = useState<Pestana>(esAdmin ? 'importar' : 'conciliaciones')
  const [version, setVersion] = useState(0)

  const [archivo, setArchivo] = useState<File | null>(null)
  const [subiendo, setSubiendo] = useState(false)
  const [errorDeSubida, setErrorDeSubida] = useState('')
  const [importada, setImportada] = useState<ConciliacionImportada | null>(null)

  const [pendiente, setPendiente] = useState<Confirmacion | null>(null)
  const [confirmando, setConfirmando] = useState(false)
  const [errorDeConfirmacion, setErrorDeConfirmacion] = useState('')

  const [resolucion, setResolucion] = useState<Resolucion | null>(null)
  const [resolviendo, setResolviendo] = useState(false)
  const [errorDeResolucion, setErrorDeResolucion] = useState('')

  /** Espejo síncrono de "hay una escritura en vuelo" (subir o confirmar): la única guarda de re-entrancia válida. */
  const ocupadoRef = useRef(false)
  const bloqueado = subiendo || confirmando || resolviendo

  useEffect(() => {
    let vigente = true
    clienteDeOrganizacion
      .listarPuntosVenta()
      .then((puntosVenta) => {
        if (!vigente) return
        const cargadas = empresasDeLosPuntosDeVenta(puntosVenta)
        setEmpresas(cargadas)
        if (cargadas.length === 1) setIdEmpresa(cargadas[0].id)
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

  const leerConciliaciones = useCallback(() => {
    void version
    return idEmpresa === null ? Promise.resolve([]) : clienteDeConciliacionMercadoPago.listar(idEmpresa)
  }, [idEmpresa, version])
  const conciliaciones = useLectura(idEmpresa === null ? null : leerConciliaciones, mensajeDeLectura)

  function elegirEmpresa(valor: string) {
    if (ocupadoRef.current) return
    setIdEmpresa(valor === '' ? null : Number(valor))
    setArchivo(null)
    setImportada(null)
    setErrorDeSubida('')
  }

  function elegirPestana(nueva: Pestana) {
    if (ocupadoRef.current) return
    if (nueva !== 'importar') setArchivo(null)
    setPestana(nueva)
  }

  async function subir() {
    if (ocupadoRef.current || idEmpresa === null || !esAdmin) return

    const validacion = validarArchivo(archivo)
    if (!validacion.valido || archivo === null) {
      setErrorDeSubida(validacion.valido ? '' : validacion.mensaje)
      return
    }

    ocupadoRef.current = true
    setSubiendo(true)
    setErrorDeSubida('')
    try {
      let resultado: ConciliacionImportada
      try {
        resultado = await clienteDeConciliacionMercadoPago.subir(idEmpresa, archivo)
      } catch (e) {
        setErrorDeSubida(mensajeDeErrorDeConciliacion(e, 'subida'))
        return
      }

      setImportada(resultado)
      setArchivo(null)
      setVersion((v) => v + 1)
      toast.exito('Reporte importado.')
    } finally {
      ocupadoRef.current = false
      setSubiendo(false)
    }
  }

  function pedirConfirmacion(idConciliacion: number, fila: FilaDeConciliacion, candidato: CandidatoDeConfirmacion) {
    if (ocupadoRef.current || !esAdmin) return
    setErrorDeConfirmacion('')
    setPendiente({ idConciliacion, fila, candidato })
  }

  function cancelarConfirmacion() {
    if (ocupadoRef.current) return
    setPendiente(null)
    setErrorDeConfirmacion('')
  }

  async function confirmar() {
    if (ocupadoRef.current || pendiente === null || !esAdmin) return

    const { idConciliacion, fila, candidato } = pendiente
    ocupadoRef.current = true
    setConfirmando(true)
    setErrorDeConfirmacion('')
    try {
      try {
        await clienteDeConciliacionMercadoPago.confirmar(idConciliacion, fila.id, candidato.idCobro)
      } catch (e) {
        setErrorDeConfirmacion(mensajeDeErrorDeConciliacion(e, 'confirmacion'))
        // Un rechazo por estado (404/409) quiere decir que lo que se ve ya cambió: se vuelve a leer detrás del diálogo.
        if (e instanceof ErrorApi && (e.estado === 404 || e.estado === 409)) setVersion((v) => v + 1)
        return
      }

      setPendiente(null)
      setVersion((v) => v + 1)
      toast.exito('Fila confirmada: el pago quedó verificado manualmente.')
    } finally {
      ocupadoRef.current = false
      setConfirmando(false)
    }
  }

  function pedirResolucion(
    idConciliacion: number,
    propuesta: PropuestaDeTesoreria,
    accion: AccionDePropuesta,
    destino: DestinoElegido | null,
    origen: DestinoElegido | null,
  ) {
    if (ocupadoRef.current || !esAdmin) return
    setErrorDeResolucion('')
    setResolucion({ idConciliacion, propuesta, accion, destino, origen })
  }

  function cancelarResolucion() {
    if (ocupadoRef.current) return
    setResolucion(null)
    setErrorDeResolucion('')
  }

  async function resolver() {
    if (ocupadoRef.current || resolucion === null || !esAdmin) return

    const { idConciliacion, propuesta, accion, destino, origen } = resolucion
    ocupadoRef.current = true
    setResolviendo(true)
    setErrorDeResolucion('')
    try {
      try {
        if (accion === 'aplicar') {
          await clienteDeConciliacionMercadoPago.aplicarPropuesta(idConciliacion, propuesta.id, destino?.id ?? null, origen?.id ?? null)
        } else if (accion === 'reabrir') {
          await clienteDeConciliacionMercadoPago.reabrirPropuesta(idConciliacion, propuesta.id)
        } else {
          await clienteDeConciliacionMercadoPago.descartarPropuesta(idConciliacion, propuesta.id)
        }
      } catch (e) {
        setErrorDeResolucion(mensajeDeErrorDeConciliacion(e, 'resolucion'))
        // Un rechazo por estado (404/409) quiere decir que lo que se ve ya cambió: se vuelve a leer detrás del diálogo.
        if (e instanceof ErrorApi && (e.estado === 404 || e.estado === 409)) setVersion((v) => v + 1)
        return
      }

      setResolucion(null)
      setVersion((v) => v + 1)
      toast.exito(
        accion === 'aplicar' ? 'Movimiento aplicado en la tesorería.' : accion === 'reabrir' ? 'Propuesta reabierta.' : 'Propuesta descartada.',
      )
    } finally {
      ocupadoRef.current = false
      setResolviendo(false)
    }
  }

  return (
    <div className="container-fluid py-4">
      <Box titulo="Conciliación Mercado Pago" variante="inverse">
        {errorEmpresas && <div className="alert alert-warning py-1 px-2 small">{errorEmpresas}</div>}

        {empresas === null ? (
          <Cargando />
        ) : empresas.length === 0 ? (
          <p className="text-muted text-center py-4">No hay empresas visibles para la conciliación.</p>
        ) : (
          <>
            <div className="row g-2 align-items-end mb-3">
              <div className="col-md-4">
                <label className="form-label" htmlFor="conciliacion-empresa">
                  Empresa
                </label>
                <select
                  id="conciliacion-empresa"
                  className="form-select"
                  value={idEmpresa ?? ''}
                  disabled={bloqueado || pendiente !== null || resolucion !== null}
                  onChange={(e) => elegirEmpresa(e.target.value)}
                >
                  <option value="">Elegir…</option>
                  {empresas.map((emp) => (
                    <option key={emp.id} value={emp.id}>
                      {emp.nombre}
                    </option>
                  ))}
                </select>
              </div>
            </div>

            {idEmpresa === null ? (
              <p className="text-muted text-center py-4">Elegí una empresa para ver su conciliación de Mercado Pago.</p>
            ) : (
              <>
                <ul className="nav nav-tabs mb-3" role="tablist">
                  {pestanas.map((clave) => (
                    <li key={clave} className="nav-item" role="presentation">
                      <button
                        type="button"
                        role="tab"
                        aria-selected={pestana === clave}
                        className={`nav-link${pestana === clave ? ' active' : ''}`}
                        disabled={bloqueado}
                        onClick={() => elegirPestana(clave)}
                      >
                        {TITULO_DE_PESTANA[clave]}
                      </button>
                    </li>
                  ))}
                </ul>

                {pestana === 'importar' && esAdmin && (
                  <SeccionImportar
                    key={idEmpresa}
                    archivo={archivo}
                    subiendo={subiendo}
                    error={errorDeSubida}
                    resultado={importada}
                    onElegir={(elegido) => {
                      setArchivo(elegido)
                      setErrorDeSubida('')
                    }}
                    onSubir={() => void subir()}
                  />
                )}
                {pestana === 'conciliaciones' && (
                  <SeccionConciliaciones
                    key={idEmpresa}
                    conciliaciones={conciliaciones.datos}
                    cargando={conciliaciones.cargando}
                    error={conciliaciones.error}
                    onReintentar={conciliaciones.recargar}
                    version={version}
                    puedeConfirmar={esAdmin}
                    bloqueado={bloqueado || pendiente !== null || resolucion !== null}
                    onConfirmar={pedirConfirmacion}
                    idEmpresa={idEmpresa}
                    puedeResolver={esAdmin}
                    onResolver={pedirResolucion}
                  />
                )}
                {pestana === 'revisar' && (
                  <SeccionParaRevisar
                    key={idEmpresa}
                    idEmpresa={idEmpresa}
                    conciliaciones={conciliaciones.datos}
                    version={version}
                    puedeConfirmar={esAdmin}
                    bloqueado={bloqueado || pendiente !== null || resolucion !== null}
                    onConfirmar={pedirConfirmacion}
                  />
                )}
              </>
            )}
          </>
        )}
      </Box>

      {resolucion && (
        <DialogoDeResolucionDePropuesta
          propuesta={resolucion.propuesta}
          accion={resolucion.accion}
          nombreDeDestino={resolucion.destino?.nombre ?? null}
          nombreDeOrigen={resolucion.origen?.nombre ?? null}
          ocupado={resolviendo}
          error={errorDeResolucion}
          onConfirmar={() => void resolver()}
          onCancelar={cancelarResolucion}
        />
      )}

      {pendiente && (
        <DialogoDeConfirmacionDeFila
          fila={pendiente.fila}
          candidato={pendiente.candidato}
          ocupado={confirmando}
          error={errorDeConfirmacion}
          onConfirmar={() => void confirmar()}
          onCancelar={cancelarConfirmacion}
        />
      )}
    </div>
  )
}
