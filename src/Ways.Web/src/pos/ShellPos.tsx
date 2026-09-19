import { useEffect, useMemo, useRef, useState } from 'react'
import { Link, Navigate, Route, Routes, useNavigate } from 'react-router'
import type { DispositivoActual } from '../api/dispositivos'
import { api, ErrorApi } from '../api/cliente'
import type {
  ClienteListado,
  ComprobanteEmitido,
  MedioPagoListado,
  PuntoVentaListado,
  TurnoConArqueos,
  UsuarioAutenticado,
} from '../api/tipos'
import { AuthContext } from '../auth/AuthContext'
import { CajaZ } from '../paginas/CajaZ'
import { CierreDeCaja } from '../paginas/CierreDeCaja'
import { Pos } from '../paginas/Pos'
import { VentasDelTurno } from '../paginas/VentasDelTurno'
import { ProveedorDePuntoVentaFijo } from '../puntoVenta/ProveedorDePuntoVentaFijo'
import { abrirConfiguracion, enEscritorio, imprimir } from '../impresion/impresora'
import { reporteZ, ticketDeVenta } from '../impresion/plantillas'
import type { ContextoDeImpresion } from '../impresion/plantillas'
import { RanuraHeaderPosContext } from './RanuraHeaderPosContext'

type Props = {
  dispositivo: DispositivoActual
  usuario: UsuarioAutenticado
  puntoVenta: PuntoVentaListado
  /** Cierre de sesión del cajero (nunca desvincula el dispositivo) — vuelve a `LoginDeDispositivo`
   * con el mismo `dispositivo` ya conocido. */
  alCerrarSesion: () => void
}

/**
 * Shell del POS de escritorio con sesión activa (stage-desktop-pos): header compacto (empresa, PV,
 * cajero, navegación) + las pantallas que reusa sin fork (`Pos`, `CierreDeCaja`, `CajaZ`,
 * `VentasDelTurno`).
 *
 * El punto de venta lo fija el dispositivo — `ProveedorDePuntoVentaFijo`, nunca el
 * `PuertaDePuntoVenta` de elección manual de la app completa. La sesión se expone por el mismo
 * `AuthContext` que ya consumen `CajaZ`/`Layout` sin fork: acá se provee un valor propio (login del
 * dispositivo en vez de mail/password) en lugar de montar el `AuthProvider` de la app completa, que
 * no conoce `POST /auth/login-dispositivo`.
 *
 * stage-pos-turno-y-foco: el header YA NO tiene su propio botón "Cerrar caja" — vivía acá porque
 * `Pos.tsx` no sabía nada del turno; ahora que la propia pantalla de venta (`/vender`, la ruta `*`
 * de fallback) muestra el estado del turno y ofrece "Cerrar caja" con el `idTurno` ya resuelto,
 * mantener el botón del header habría duplicado la misma consulta `GET …/abierto` en dos lugares —
 * exactamente el tipo de gemelo que la regla 10 de `react-async-state` pide mantener en
 * sincronía, evitado acá eliminando uno de los dos en vez de replicarlo. `alIrACerrarCaja` (seam
 * de `Pos.tsx`) navega a la ruta propia del shell (`/cerrar-caja`, distinta de `/caja/cierre` de
 * la app web) con el turno que la pantalla ya resolvió — nunca vuelve a golpear el endpoint.
 */
export function ShellPos({ dispositivo, usuario, puntoVenta, alCerrarSesion }: Props) {
  const navegar = useNavigate()

  const [cerrandoSesion, setCerrandoSesion] = useState(false)
  const cerrandoSesionRef = useRef(false)

  // stage-pos-caja-en-cabecera: nodo del contenedor que reserva en el header para los controles
  // de caja de `Pos.tsx` (ver `RanuraHeaderPosContext`) — `useState` (no un `useRef` solo) porque
  // los consumidores del contexto necesitan volver a renderizar apenas el nodo existe, recién
  // después del primer commit de este componente.
  const [nodoRanuraHeader, setNodoRanuraHeader] = useState<HTMLDivElement | null>(null)

  // stage-desktop-pos (Fix judgment-day W1/W2, corregido en la ronda 2 — R2-1/R2-2): el shell es
  // el ÚNICO dueño de la impresión de escritorio — tanto el ticket de venta como el reporte Z
  // auto-impreso al cerrar caja pasan por acá. Una impresora física es un recurso serial: dos
  // trabajos nunca pueden mandarse en simultáneo, así que se encolan (FIFO) y se procesan de a
  // uno — un `imprimiendoRef` COMPARTIDO entre trabajos (la versión de la ronda 1) descartaba en
  // silencio cualquier trabajo que llegara mientras otro estaba en vuelo, en vez de encolarlo.
  // Cada trabajo tiene su propio id; una falla es un aviso propio de ESE id (react-async-state
  // regla 14: "un slot de estado tiene un solo dueño") — un trabajo posterior exitoso o fallido
  // nunca pisa el aviso de uno anterior. Los avisos viven fuera de `<Routes>` (sobreviven a la
  // navegación de `alCerrarExitosamente` hacia la Caja Z).
  type TrabajoDeImpresion = { id: number; descripcion: string; bytes: Uint8Array }
  type AvisoDeImpresion = TrabajoDeImpresion & { mensaje: string; reintentando: boolean }

  const [avisosDeImpresion, setAvisosDeImpresion] = useState<AvisoDeImpresion[]>([])
  const proximoIdTrabajoRef = useRef(1)
  const colaDeImpresionRef = useRef<TrabajoDeImpresion[]>([])
  const procesandoColaRef = useRef(false)
  // Guarda de desmontaje: el shell vive mientras dura la sesión, pero un trabajo pudiera resolver
  // después de que el shell se desmonte (ej. cierre de sesión en vuelo) — ningún `setState` corre
  // después de eso.
  const montadoRef = useRef(true)
  useEffect(() => {
    montadoRef.current = true
    return () => {
      montadoRef.current = false
    }
  }, [])

  async function procesarColaDeImpresion() {
    if (procesandoColaRef.current) return
    procesandoColaRef.current = true
    try {
      let trabajo: TrabajoDeImpresion | undefined
      while ((trabajo = colaDeImpresionRef.current.shift())) {
        const resultado = await imprimir(trabajo.bytes)
        if (!montadoRef.current) continue
        const trabajoActual = trabajo
        setAvisosDeImpresion((prev) => {
          const sinEsteId = prev.filter((a) => a.id !== trabajoActual.id)
          if (resultado.ok) return sinEsteId
          return [...sinEsteId, { ...trabajoActual, mensaje: resultado.mensaje, reintentando: false }]
        })
      }
    } finally {
      procesandoColaRef.current = false
    }
  }

  /** Encola un trabajo de impresión (nunca lo descarta, aunque otro esté en vuelo) y dispara el
   * procesamiento de la cola si no está corriendo ya. */
  function encolarImpresion(descripcion: string, bytes: Uint8Array) {
    colaDeImpresionRef.current.push({ id: proximoIdTrabajoRef.current++, descripcion, bytes })
    void procesarColaDeImpresion()
  }

  /** "Reimprimir" de un aviso puntual — guarda de reentrancia POR aviso (react-async-state regla
   * 11): un doble click en el mismo tick sobre el mismo aviso no encola dos reintentos, pero
   * "Reimprimir" de OTRO aviso en simultáneo sigue andando. */
  function reimprimir(aviso: AvisoDeImpresion) {
    if (aviso.reintentando) return
    setAvisosDeImpresion((prev) => prev.map((a) => (a.id === aviso.id ? { ...a, reintentando: true } : a)))
    colaDeImpresionRef.current.push({ id: aviso.id, descripcion: aviso.descripcion, bytes: aviso.bytes })
    void procesarColaDeImpresion()
  }

  function cerrarAviso(id: number) {
    setAvisosDeImpresion((prev) => prev.filter((a) => a.id !== id))
  }

  const contextoDeImpresion = useMemo<ContextoDeImpresion>(
    () => ({
      empresa: dispositivo.empresa.nombre,
      puntoVenta: `PV ${dispositivo.puntoVenta.numero} — ${dispositivo.puntoVenta.nombre}`,
      cajero: usuario.usuario,
    }),
    [dispositivo, usuario],
  )

  const valorAuth = useMemo(
    () => ({
      usuario,
      cargando: false,
      // Este shell nunca inicia sesión con mail/password — el único camino es login-dispositivo,
      // que ya corrió en `LoginDeDispositivo` antes de montar este componente.
      iniciarSesion: () => Promise.reject(new Error('El POS de escritorio inicia sesión con login-dispositivo.')),
      cerrarSesion,
    }),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [usuario],
  )

  async function cerrarSesion() {
    if (cerrandoSesionRef.current) return
    cerrandoSesionRef.current = true
    setCerrandoSesion(true)

    try {
      await api.post('/auth/logout')
    } catch (e) {
      // Mismo criterio que `AuthContext.cerrarSesion`: una sesión ya vencida no es un problema,
      // igual se cierra del lado del cliente.
      if (!(e instanceof ErrorApi && e.esNoAutenticado)) throw e
    } finally {
      cerrandoSesionRef.current = false
      setCerrandoSesion(false)
      alCerrarSesion()
    }
  }

  function alEmitirVenta(comprobante: ComprobanteEmitido, _cliente: ClienteListado, medios: MedioPagoListado[]) {
    // No bloqueante y sin cambiar el resultado de la venta, que ya se confirmó en el servidor —
    // una falla queda como su propio aviso persistente del shell + "Reimprimir", nunca silenciosa
    // ni descartada por otro trabajo de impresión que esté (o quede) en vuelo.
    encolarImpresion('el ticket de venta', ticketDeVenta(comprobante, contextoDeImpresion, medios))
  }

  /** "Reimprimir" de "Ventas del turno" (stage-desktop-pos): mismo dueño único de la impresión de
   * escritorio que `alEmitirVenta` — pasa por la MISMA cola FIFO (`encolarImpresion`), nunca un
   * segundo camino de impresión. `{ reimpresion: true }` marca el ticket para que nunca se
   * confunda con el original. */
  function alReimprimirVenta(comprobante: ComprobanteEmitido, medios: MedioPagoListado[]) {
    encolarImpresion(
      `la reimpresión del ticket ${comprobante.numeroVisible}`,
      ticketDeVenta(comprobante, contextoDeImpresion, medios, { reimpresion: true }),
    )
  }

  return (
    <AuthContext.Provider value={valorAuth}>
      <ProveedorDePuntoVentaFijo puntoVenta={puntoVenta}>
        {/* stage-pos-caja-en-cabecera: envuelve el árbol existente sin reindentarlo, a propósito
            (mantiene chico el diff de un archivo que otro trabajo en paralelo también toca, en las
            rutas de más abajo) — `RanuraHeaderPosContext` solo agrega el `Provider` alrededor. */}
        <RanuraHeaderPosContext.Provider value={nodoRanuraHeader}>
        <div className="d-flex flex-column min-vh-100">
          <header className="navbar navbar-dark bg-dark px-3 py-2 d-print-none">
            <div className="d-flex flex-column">
              <strong className="text-light">{dispositivo.empresa.nombre}</strong>
              <small className="text-light-emphasis">
                PV {dispositivo.puntoVenta.numero} — {dispositivo.puntoVenta.nombre} · {usuario.usuario}
              </small>
            </div>
            {/* stage-pos-caja-en-cabecera: contenedor vacío — `Pos.tsx` portalea acá el badge +
                "Abrir caja"/"Cerrar caja" (ver `RanuraHeaderPosContext`). Nunca se renderiza nada
                directamente en este `div`, así que el propio `ref` alcanza para saber si está
                vacío o no en pantallas sin turno (ej. sin punto de venta). */}
            <div className="d-flex align-items-center gap-2 flex-wrap" ref={setNodoRanuraHeader} />
            <div className="d-flex gap-2">
              <Link className="btn btn-success rounded-0" to="/vender">
                Vender
              </Link>
              <Link className="btn btn-outline-light rounded-0" to="/ventas-del-turno">
                Ventas del turno
              </Link>
              {enEscritorio() && (
                <button type="button" className="btn btn-outline-light rounded-0" onClick={() => void abrirConfiguracion()}>
                  Configuración
                </button>
              )}
              <button type="button" className="btn btn-outline-light rounded-0" disabled={cerrandoSesion} onClick={() => void cerrarSesion()}>
                {cerrandoSesion ? 'Saliendo…' : 'Cerrar sesión'}
              </button>
            </div>
          </header>

          {/* stage-desktop-pos (Fix judgment-day W1/W2, ronda 2 — R2-2): fuera de `<Routes>` a
              propósito — sobreviven a la navegación de `alCerrarExitosamente` hacia la Caja Z (y
              a cualquier otra navegación del shell). Un aviso por trabajo fallido (regla 14): el
              de un trabajo nunca lo pisa ni lo borra el de otro. */}
          {avisosDeImpresion.map((aviso) => (
            <div
              key={aviso.id}
              role="alert"
              className="alert alert-warning rounded-0 py-1 px-3 mb-0 d-flex justify-content-between align-items-center gap-2 d-print-none"
            >
              <span>
                No se pudo imprimir {aviso.descripcion}: {aviso.mensaje}
              </span>
              <div className="d-flex gap-2">
                <button
                  type="button"
                  className="btn btn-sm btn-outline-dark rounded-0"
                  disabled={aviso.reintentando}
                  onClick={() => reimprimir(aviso)}
                >
                  {aviso.reintentando ? 'Imprimiendo…' : 'Reimprimir'}
                </button>
                <button type="button" className="btn btn-sm btn-outline-dark rounded-0" onClick={() => cerrarAviso(aviso.id)}>
                  Cerrar
                </button>
              </div>
            </div>
          ))}

          <main className="flex-grow-1">
            <Routes>
              <Route
                path="/vender"
                element={
                  <Pos
                    alEmitir={alEmitirVenta}
                    alIrACerrarCaja={(idTurno) => navegar(`/cerrar-caja?idTurno=${idTurno}`)}
                  />
                }
              />
              <Route path="/ventas-del-turno" element={<VentasDelTurno alReimprimir={alReimprimirVenta} />} />
              <Route
                path="/cerrar-caja"
                element={
                  <CierreDeCaja
                    rutaVolver="/vender"
                    alCerrarExitosamente={(turno: TurnoConArqueos) => {
                      // `CierreDeCaja` no recibe `contextoDeImpresion` acá a propósito: sin él no
                      // auto-imprime ni muestra su propio "Reimprimir" (evita la doble impresión),
                      // el shell imprime el reporte Z él mismo — mismo helper/cola que el ticket
                      // de venta, con su propio aviso persistente si falla.
                      encolarImpresion('el reporte Z', reporteZ(turno, contextoDeImpresion))
                      navegar(`/caja/turnos/${turno.id}/z`, { replace: true })
                    }}
                  />
                }
              />
              <Route path="/caja/turnos/:id/z" element={<CajaZ contextoDeImpresion={contextoDeImpresion} />} />
              <Route path="*" element={<Navigate to="/vender" replace />} />
            </Routes>
          </main>
        </div>
        </RanuraHeaderPosContext.Provider>
      </ProveedorDePuntoVentaFijo>
    </AuthContext.Provider>
  )
}
