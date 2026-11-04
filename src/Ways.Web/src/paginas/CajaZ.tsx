import { useEffect, useRef, useState } from 'react'
import { useParams } from 'react-router'
import { clienteDeCaja, rutasDeExportacionDeCaja } from '../api/caja'
import { ErrorApi } from '../api/cliente'
import { clienteDeCatalogo } from '../api/catalogos'
import { clienteDeOrganizacion } from '../api/organizacion'
import {
  puedeGestionarCatalogos,
  type ComprobanteEmitido,
  type DetalleDeTurno,
  type FilaDeRetiro,
  type MedioPagoAlta,
  type MedioPagoListado,
  type ResumenDeCierrePorRetiro,
  type TicketDeTurno,
  type TurnoResumen,
} from '../api/tipos'
import { useAuth } from '../auth/useAuth'
import { BotonDeDescarga } from '../componentes/BotonDeDescarga'
import { toast } from '../toasts/toasts'
import { Box } from '../componentes/Box'
import { Cargando } from '../componentes/Cargando'
import { DetalleDeTurnoListas } from '../componentes/DetalleDeTurnoListas'
import { EdicionDePagoDelTurno } from '../componentes/EdicionDePagoDelTurno'
import { EncabezadoOrdenable, useOrdenamiento, type ColumnasOrdenables } from '../componentes/grilla'
import { InsigniaDeRecalculo } from '../componentes/InsigniaDeRecalculo'
import { ModalDeCambioDeMedios } from '../componentes/ModalDeCambioDeMedios'
import { RetirosDelTurno } from '../componentes/RetirosDelTurno'
import { VistaDeTicket } from '../componentes/VistaDeTicket'
import type { LineaDeTicket } from '../impresion/escpos'
import { enEscritorio, imprimir } from '../impresion/impresora'
import { cierreDeTurno, lineasDeCierreDeTurno, lineasDeTicketDeVenta } from '../impresion/plantillas'
import type { ContextoDeImpresion } from '../impresion/plantillas'
import { formatearImporte } from '../formato/importes'
import { fechaHoraATexto } from '../formato/fechas'
import { ModalDeEdicionDeRetiro } from './ModalDeEdicionDeRetiro'

const clienteMediosPago = clienteDeCatalogo<MedioPagoListado, MedioPagoAlta>('medios-pago')

/** Lo que la administración está corrigiendo; `clave` distingue una edición de otra aunque sea sobre la misma fila. */
type EdicionSinClave =
  | { tipo: 'ticket'; ticket: TicketDeTurno }
  | { tipo: 'pago'; idPago: number }
  | { tipo: 'retiro'; retiro: FilaDeRetiro }
type Edicion = EdicionSinClave & { clave: number }

function formatearMoneda(valor: number): string {
  return formatearImporte(valor, { simbolo: true })
}

/**
 * Caja Z (stage-11-exportacion-reportes, Slice 6b, design: Web Composition) — detalle del turno
 * cerrado: el mismo `ResumenDeTurno` que `/caja/cierre` usó para derivar el arqueo, más los
 * tickets y pagos del turno, con descarga XLSX. Misma política que `/caja`
 * (`Politicas.OperacionDePos`): el cajero puede leer su propio cierre — mismo gate que
 * `GET .../resumen`.
 *
 * `GET .../detalle` no discrimina por turno-ownership (spec historico-de-cajas: A Vendedor
 * Downloads Their Own Turno's Z-Report; verificado en la Slice 5b — `OperacionDePos` es un gate
 * de rol solo, sin claim de PV ni de turno): esta pantalla tampoco intenta un bloqueo cross-turno
 * que la API no tiene — cualquier Vendedor/Supervisor/Admin autenticado que conozca el id ve el
 * detalle, igual que del lado del servidor.
 *
 * stage-desktop-pos: `contextoDeImpresion` es el mismo seam opcional que `CierreDeCaja.tsx` —
 * `undefined` en la app web normal (sin cambio de comportamiento), habilita un botón
 * "Reimprimir" ESC/POS acá solo dentro de Tauri (`enEscritorio()`).
 *
 * "Reimprimir ticket de cierre" reimprime el MISMO ticket que el POS imprime al cerrar el turno
 * (`cierreDeTurno` sobre `GET .../resumen-de-cierre`): en Tauri sale por la térmica; en la web, por
 * el diálogo de impresión del navegador con solo el ticket en el papel. Solo se ofrece con el
 * turno cerrado.
 *
 * Corrección administrativa: el Admin, y solo en la app web (no con `contextoDeImpresion`, que es el POS de escritorio),
 * ve acciones para cambiar el medio de un ticket, editar un pago y editar un retiro del turno. Un diálogo a la vez deja
 * inerte el resto; al guardar se avisa y se vuelve a pedir el detalle, el turno y los retiros sin vaciar lo mostrado
 * (un fallo de esa recarga tiene su propio aviso y no desmiente el guardado).
 */
type PropsCajaZ = {
  contextoDeImpresion?: ContextoDeImpresion
  /** Reimpresión de un ticket por la cola FIFO de la térmica del POS de escritorio (la misma de
   * "Ventas del turno"). Sin ella —app web— el ticket sale por el diálogo de impresión del navegador. */
  alReimprimirVenta?: (comprobante: ComprobanteEmitido, medios: MedioPagoListado[], contexto: ContextoDeImpresion) => void
}

type MedioDelResumen = DetalleDeTurno['resumen']['medios'][number]

const COLUMNAS_DE_MEDIOS: ColumnasOrdenables<MedioDelResumen> = {
  medio: { valor: (m) => m.nombre },
  esperado: { valor: (m) => m.importeEsperado },
}

function TablaDeMediosEsperados({ medios }: { medios: MedioDelResumen[] }) {
  const { filas, orden, alternar } = useOrdenamiento(medios, COLUMNAS_DE_MEDIOS)

  return (
    <div className="table-responsive mb-3">
      <table className="table table-sm table-striped table-bordered align-middle">
        <thead>
          <tr>
            <EncabezadoOrdenable columna="medio" orden={orden} onAlternar={alternar}>
              Medio
            </EncabezadoOrdenable>
            <EncabezadoOrdenable columna="esperado" orden={orden} onAlternar={alternar} className="text-end">
              Esperado
            </EncabezadoOrdenable>
          </tr>
        </thead>
        <tbody>
          {filas.map((m) => (
            <tr key={m.idMedioPago}>
              <td>{m.nombre}</td>
              <td className="text-end">{formatearMoneda(m.importeEsperado)}</td>
            </tr>
          ))}
          {medios.length === 0 && (
            <tr>
              <td colSpan={2} className="text-center text-muted">
                Este turno no tuvo actividad: no hay ningún medio arqueado.
              </td>
            </tr>
          )}
        </tbody>
      </table>
    </div>
  )
}

export function CajaZ({ contextoDeImpresion, alReimprimirVenta }: PropsCajaZ = {}) {
  const { id } = useParams<{ id: string }>()
  const idTurno = id !== undefined ? Number(id) : Number.NaN
  const idTurnoValido = Number.isFinite(idTurno)

  const { usuario } = useAuth()

  const [detalle, setDetalle] = useState<DetalleDeTurno | null>(null)
  const [cargando, setCargando] = useState(true)
  const [error, setError] = useState('')
  const generacionRef = useRef(0)
  const [generacionDelDetalle, setGeneracionDelDetalle] = useState(0)

  const puedeEditar = usuario !== null && puedeGestionarCatalogos(usuario.rolId) && contextoDeImpresion === undefined
  const [recarga, setRecarga] = useState(0)
  const [avisoDeRecarga, setAvisoDeRecarga] = useState('')
  const [edicion, setEdicion] = useState<Edicion | null>(null)
  const edicionRef = useRef<Edicion | null>(null)
  const claveDeEdicionRef = useRef(0)
  const disparadorRef = useRef<HTMLElement | null>(null)
  const focoPendienteRef = useRef(false)
  const [medios, setMedios] = useState<MedioPagoListado[] | null>(null)
  const [errorMedios, setErrorMedios] = useState('')
  const turnoCargadoRef = useRef<number | null>(null)

  const [errorImpresion, setErrorImpresion] = useState('')

  const [turno, setTurno] = useState<TurnoResumen | null>(null)
  const [errorTurno, setErrorTurno] = useState('')
  const [imprimiendoCierre, setImprimiendoCierre] = useState(false)
  const imprimiendoCierreRef = useRef(false)
  const [errorCierre, setErrorCierre] = useState('')
  const [lineasAImprimir, setLineasAImprimir] = useState<LineaDeTicket[] | null>(null)

  const enTermica = contextoDeImpresion !== undefined && enEscritorio()

  async function contextoDeLaWeb(resumen: ResumenDeCierrePorRetiro): Promise<ContextoDeImpresion> {
    // El nombre de la empresa es un dato de cabecera: sin él el ticket sale igual, con la línea vacía.
    const puntosVenta = await clienteDeOrganizacion.listarPuntosVenta().catch(() => [])
    const puntoVenta = puntosVenta.find((p) => p.id === resumen.puntoVenta.id)
    return {
      empresa: puntoVenta?.razonSocialEmpresa ?? puntoVenta?.nombreTenant ?? '',
      puntoVenta: `PV ${resumen.puntoVenta.numero} — ${resumen.puntoVenta.nombre}`,
      cajero: usuario?.usuario ?? '—',
    }
  }

  async function reimprimirCierre() {
    if (imprimiendoCierreRef.current) return
    imprimiendoCierreRef.current = true
    setImprimiendoCierre(true)
    setErrorCierre('')
    setErrorImpresion('')
    const miGeneracion = generacionRef.current
    try {
      const resumen = await clienteDeCaja.obtenerResumenDeCierre(idTurno)
      const contexto = contextoDeImpresion ?? (await contextoDeLaWeb(resumen))
      if (generacionRef.current !== miGeneracion) return
      if (enTermica) {
        const resultado = await imprimir(cierreDeTurno(resumen, contexto))
        if (generacionRef.current !== miGeneracion) return
        setErrorImpresion(resultado.ok ? '' : resultado.mensaje)
      } else {
        setLineasAImprimir(lineasDeCierreDeTurno(resumen, contexto))
      }
    } catch (e) {
      if (generacionRef.current !== miGeneracion) return
      setErrorCierre(e instanceof ErrorApi ? e.message : 'No se pudo obtener el ticket de cierre.')
    } finally {
      imprimiendoCierreRef.current = false
      setImprimiendoCierre(false)
    }
  }

  /** Reimprime un ticket de venta del turno: por la térmica en el POS de escritorio; en la web, con
   * las líneas del mismo ticket y el diálogo del navegador. El cajero del ticket es el del turno, no
   * quien reimprime. Si mientras tanto se cargó otro turno, no imprime nada. Los errores suben a la
   * fila del ticket. */
  async function reimprimirTicket(comprobante: ComprobanteEmitido, medios: MedioPagoListado[]) {
    const deEsteDetalle = generacionDelDetalle
    const resumen = await clienteDeCaja.obtenerResumenDeCierre(idTurno)
    const base = contextoDeImpresion ?? (await contextoDeLaWeb(resumen))
    if (generacionRef.current !== deEsteDetalle) return
    const contexto = { ...base, cajero: resumen.vendedor }
    if (enTermica && alReimprimirVenta) {
      alReimprimirVenta(comprobante, medios, contexto)
      return
    }
    setLineasAImprimir(lineasDeTicketDeVenta(comprobante, contexto, medios, { reimpresion: true }))
  }

  useEffect(() => {
    if (!lineasAImprimir) return
    const terminar = () => setLineasAImprimir(null)
    document.body.classList.add('imprimiendo-ticket')
    window.addEventListener('afterprint', terminar, { once: true })
    window.print()
    return () => {
      window.removeEventListener('afterprint', terminar)
      document.body.classList.remove('imprimiendo-ticket')
    }
  }, [lineasAImprimir])

  useEffect(() => {
    if (!idTurnoValido) return
    const miGeneracion = (generacionRef.current += 1)
    const esRecarga = turnoCargadoRef.current === idTurno
    turnoCargadoRef.current = idTurno
    setCargando(true)
    setError('')
    setAvisoDeRecarga('')
    if (!esRecarga) {
      setTurno(null)
      setErrorTurno('')
      setErrorCierre('')
      setLineasAImprimir(null)
    }

    clienteDeCaja
      .obtenerTurno(idTurno)
      .then((datos) => {
        if (generacionRef.current !== miGeneracion) return
        setTurno(datos)
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        setErrorTurno(e instanceof ErrorApi ? e.message : 'No se pudo determinar el estado del turno.')
      })

    clienteDeCaja
      .obtenerDetalle(idTurno)
      .then((datos) => {
        if (generacionRef.current !== miGeneracion) return
        setDetalle(datos)
        setGeneracionDelDetalle(miGeneracion)
      })
      .catch((e) => {
        if (generacionRef.current !== miGeneracion) return
        if (esRecarga) {
          setAvisoDeRecarga('El cambio se guardó, pero no se pudo actualizar el detalle del turno: recargá la página para verlo.')
          return
        }
        setDetalle(null)
        setError(e instanceof ErrorApi ? e.message : 'No se pudo cargar el detalle del turno.')
      })
      .finally(() => {
        if (generacionRef.current !== miGeneracion) return
        setCargando(false)
      })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [idTurno, idTurnoValido, recarga])

  useEffect(() => {
    if (!puedeEditar) return
    let vigente = true
    setErrorMedios('')
    clienteMediosPago
      .listar(true)
      .then((lista) => {
        if (vigente) setMedios(lista)
      })
      .catch(() => {
        if (vigente) setErrorMedios('No se pudo cargar el catálogo de medios de pago: no se puede cambiar el medio de un ticket.')
      })
    return () => {
      vigente = false
    }
  }, [puedeEditar])

  useEffect(() => {
    edicionRef.current = null
    focoPendienteRef.current = false
    setEdicion(null)
  }, [idTurno])

  useEffect(() => {
    if (edicion !== null || !focoPendienteRef.current) return
    focoPendienteRef.current = false
    const disparador = disparadorRef.current
    if (disparador?.isConnected && !disparador.matches(':disabled')) disparador.focus()
  }, [edicion])

  function abrirEdicion(siguiente: EdicionSinClave, disparador: HTMLElement) {
    if (edicionRef.current !== null) return
    const abierta: Edicion = { ...siguiente, clave: ++claveDeEdicionRef.current }
    disparadorRef.current = disparador
    edicionRef.current = abierta
    setEdicion(abierta)
  }

  function cerrarEdicion(clave: number) {
    if (edicionRef.current?.clave !== clave) return
    focoPendienteRef.current = true
    edicionRef.current = null
    setEdicion(null)
  }

  /** El servidor ya corrigió: se avisa y se pide de nuevo lo que la pantalla muestra. El diálogo se cierra solo si sigue
   * siendo la edición vigente (si cambió el turno, ya se descartó). */
  function alGuardar(clave: number, mensaje: string) {
    toast.exito(mensaje)
    cerrarEdicion(clave)
    setRecarga((n) => n + 1)
  }

  if (!idTurnoValido) {
    return (
      <div className="container-fluid py-4">
        <Box titulo="Caja Z" variante="warning">
          <p className="text-muted mb-0">No se especificó el turno a mostrar.</p>
        </Box>
      </div>
    )
  }

  return (
    <div className="container-fluid py-4">
      <Box
        titulo={`Caja Z — turno #${idTurno}`}
        variante="inverse"
        herramientas={
          <div className="d-flex gap-2">
            <button type="button" className="btn btn-sm btn-outline-secondary d-print-none" onClick={() => window.print()}>
              Imprimir
            </button>
            <BotonDeDescarga
              ruta={rutasDeExportacionDeCaja.detalleDeTurno(idTurno)}
              etiqueta="Descargar"
              onError={toast.error}
              className="btn btn-sm btn-outline-secondary d-print-none"
            />
            {turno?.estado === 'Cerrado' && (
              <button
                type="button"
                className="btn btn-sm btn-outline-secondary d-print-none"
                disabled={imprimiendoCierre}
                onClick={() => void reimprimirCierre()}
              >
                {imprimiendoCierre ? 'Imprimiendo…' : 'Reimprimir ticket de cierre'}
              </button>
            )}
          </div>
        }
      >
        {/* Vista de impresión (design decisión 13: mismo componente, `@media print`, sin ruta ni
            fetch dedicados): equivalente del encabezado de los exports XLSX — generado por/cuándo.
            El turno ya está en el título de `Box`, que se imprime igual. */}
        <div className="d-none d-print-block mb-3">
          <div className="small">
            Generado: {fechaHoraATexto(new Date())} — {usuario?.usuario ?? '—'}
          </div>
        </div>

        {errorTurno && (
          <div className="alert alert-warning py-1 px-2 small mb-2 d-print-none">
            {errorTurno} No se ofrece la reimpresión del ticket de cierre.
          </div>
        )}
        {errorCierre && <div className="alert alert-danger py-1 px-2 small mb-2 d-print-none">{errorCierre}</div>}
        {errorImpresion && (
          <div className="alert alert-warning py-1 px-2 small mb-2">No se pudo imprimir: {errorImpresion}</div>
        )}
        {error && (
          <div className="alert alert-danger d-flex justify-content-between align-items-center gap-2">
            <span>{error}</span>
          </div>
        )}

        {avisoDeRecarga && <div className="alert alert-warning py-1 px-2 small mb-2 d-print-none">{avisoDeRecarga}</div>}
        {puedeEditar && errorMedios && <div className="alert alert-warning py-1 px-2 small mb-2 d-print-none">{errorMedios}</div>}

        {cargando && !detalle && <Cargando />}

        {detalle && (
          <>
            {detalle.fechaRecalculo !== null && (
              <div className="mb-2">
                <InsigniaDeRecalculo fechaRecalculo={detalle.fechaRecalculo} />
              </div>
            )}
            <div className="row g-3 mb-3">
              <div className="col-md-3">
                <div className="text-muted small">Tickets</div>
                <div className="fs-5">{detalle.resumen.cantidadTickets}</div>
              </div>
              <div className="col-md-3">
                <div className="text-muted small">Primer ticket</div>
                <div>
                  {detalle.resumen.primerTicket
                    ? `${detalle.resumen.primerTicket.codigo} #${detalle.resumen.primerTicket.numero}`
                    : '—'}
                </div>
              </div>
              <div className="col-md-3">
                <div className="text-muted small">Último ticket</div>
                <div>
                  {detalle.resumen.ultimoTicket
                    ? `${detalle.resumen.ultimoTicket.codigo} #${detalle.resumen.ultimoTicket.numero}`
                    : '—'}
                </div>
              </div>
              <div className="col-md-3">
                <div className="text-muted small">Retiros</div>
                <div>{formatearMoneda(detalle.resumen.egresos.retiros)}</div>
              </div>
            </div>

            <h6>Medios de pago (esperado)</h6>
            <TablaDeMediosEsperados medios={detalle.resumen.medios} />

            <DetalleDeTurnoListas
              tickets={detalle.tickets}
              pagos={detalle.gastos}
              edicion={
                puedeEditar
                  ? {
                      alCambiarMedio: (ticket, disparador) => abrirEdicion({ tipo: 'ticket', ticket }, disparador),
                      alEditarPago: (pago, disparador) => abrirEdicion({ tipo: 'pago', idPago: pago.id }, disparador),
                      inerte: edicion !== null,
                      puedeCambiarMedio: medios !== null,
                    }
                  : undefined
              }
              alReimprimir={reimprimirTicket}
            />
            {puedeEditar && (
              <RetirosDelTurno
                idTurno={idTurno}
                recarga={recarga}
                inerte={edicion !== null}
                onEditar={(retiro, disparador) => abrirEdicion({ tipo: 'retiro', retiro }, disparador)}
              />
            )}
          </>
        )}
      </Box>
      {lineasAImprimir && <VistaDeTicket lineas={lineasAImprimir} />}
      {puedeEditar && edicion?.tipo === 'ticket' && medios && (
        <ModalDeCambioDeMedios
          key={edicion.clave}
          venta={{ id: edicion.ticket.id, numeroVisible: edicion.ticket.numeroVisible }}
          medios={medios}
          modo="administracion"
          onGuardado={(corregida) => alGuardar(edicion.clave, `Se cambió el medio de pago de la venta ${corregida.numeroVisible}.`)}
          onCerrar={() => cerrarEdicion(edicion.clave)}
        />
      )}
      {puedeEditar && edicion?.tipo === 'pago' && (
        <EdicionDePagoDelTurno
          key={edicion.clave}
          idPago={edicion.idPago}
          onGuardado={() => alGuardar(edicion.clave, 'Pago actualizado.')}
          onCerrar={() => cerrarEdicion(edicion.clave)}
        />
      )}
      {puedeEditar && edicion?.tipo === 'retiro' && (
        <ModalDeEdicionDeRetiro
          key={edicion.clave}
          retiro={edicion.retiro}
          onGuardado={() => alGuardar(edicion.clave, 'Retiro actualizado.')}
          onObsoleto={() => setRecarga((n) => n + 1)}
          onCerrar={() => cerrarEdicion(edicion.clave)}
        />
      )}
    </div>
  )
}
