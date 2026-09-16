import { useMemo, useRef, useState } from 'react'
import { Link, Navigate, Route, Routes, useNavigate } from 'react-router'
import { clienteDeCaja } from '../api/caja'
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
import { ProveedorDePuntoVentaFijo } from '../puntoVenta/ProveedorDePuntoVentaFijo'
import { abrirConfiguracion, enEscritorio, imprimir } from '../impresion/impresora'
import { ticketDeVenta } from '../impresion/plantillas'
import type { ContextoDeImpresion } from '../impresion/plantillas'

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
 * cajero, navegación) + las tres pantallas que reusa sin fork (`Pos`, `CierreDeCaja`, `CajaZ`).
 *
 * El punto de venta lo fija el dispositivo — `ProveedorDePuntoVentaFijo`, nunca el
 * `PuertaDePuntoVenta` de elección manual de la app completa. La sesión se expone por el mismo
 * `AuthContext` que ya consumen `CajaZ`/`Layout` sin fork: acá se provee un valor propio (login del
 * dispositivo en vez de mail/password) en lugar de montar el `AuthProvider` de la app completa, que
 * no conoce `POST /auth/login-dispositivo`.
 */
export function ShellPos({ dispositivo, usuario, puntoVenta, alCerrarSesion }: Props) {
  const navegar = useNavigate()

  const [cerrandoSesion, setCerrandoSesion] = useState(false)
  const cerrandoSesionRef = useRef(false)

  // "Cerrar caja" no navega a ciegas: resuelve el turno ABIERTO del PV fijo primero (regla 9 de
  // react-async-state — guarda de reentrancia de primera línea) y solo entonces navega con el
  // `idTurno` real. Sin esto `CierreDeCaja` se monta sin `?idTurno=` y muestra un aviso genérico
  // en vez de dejar cerrar la caja — el defecto que este seam existe para evitar.
  const [buscandoTurno, setBuscandoTurno] = useState(false)
  const buscandoTurnoRef = useRef(false)
  const [errorTurno, setErrorTurno] = useState('')

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

  async function irACerrarCaja() {
    if (buscandoTurnoRef.current) return
    buscandoTurnoRef.current = true
    setBuscandoTurno(true)
    setErrorTurno('')

    try {
      const turno = await clienteDeCaja.obtenerAbierto(puntoVenta.id)
      if (!turno) {
        setErrorTurno('No hay un turno abierto en este punto de venta.')
        return
      }
      navegar(`/cerrar-caja?idTurno=${turno.id}`)
    } catch (e) {
      setErrorTurno(e instanceof ErrorApi ? e.message : 'No se pudo consultar el turno abierto.')
    } finally {
      buscandoTurnoRef.current = false
      setBuscandoTurno(false)
    }
  }

  function alEmitirVenta(comprobante: ComprobanteEmitido, _cliente: ClienteListado, medios: MedioPagoListado[]) {
    // No bloqueante y sin cambiar el resultado de la venta: si falla, `Pos.tsx` no se entera —
    // esta pantalla no tiene hoy un lugar para un botón "Reimprimir" del ticket recién vendido,
    // el ticket sigue disponible mientras `ventaEmitida` esté en pantalla (nueva venta lo limpia).
    void imprimir(ticketDeVenta(comprobante, contextoDeImpresion, medios))
  }

  return (
    <AuthContext.Provider value={valorAuth}>
      <ProveedorDePuntoVentaFijo puntoVenta={puntoVenta}>
        <div className="d-flex flex-column min-vh-100">
          <header className="navbar navbar-dark bg-dark px-3 py-2 d-print-none">
            <div className="d-flex flex-column">
              <strong className="text-light">{dispositivo.empresa.nombre}</strong>
              <small className="text-light-emphasis">
                PV {dispositivo.puntoVenta.numero} — {dispositivo.puntoVenta.nombre} · {usuario.usuario}
              </small>
            </div>
            <div className="d-flex gap-2">
              <Link className="btn btn-success rounded-0" to="/vender">
                Vender
              </Link>
              <button
                type="button"
                className="btn btn-outline-light rounded-0"
                disabled={buscandoTurno}
                onClick={() => void irACerrarCaja()}
              >
                {buscandoTurno ? 'Verificando…' : 'Cerrar caja'}
              </button>
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

          {errorTurno && (
            <div role="alert" className="alert alert-warning rounded-0 py-1 px-3 mb-0 d-print-none">
              {errorTurno}
            </div>
          )}

          <main className="flex-grow-1">
            <Routes>
              <Route path="/vender" element={<Pos alEmitir={alEmitirVenta} />} />
              <Route
                path="/cerrar-caja"
                element={
                  <CierreDeCaja
                    rutaVolver="/vender"
                    contextoDeImpresion={contextoDeImpresion}
                    alCerrarExitosamente={(turno: TurnoConArqueos) => navegar(`/caja/turnos/${turno.id}/z`, { replace: true })}
                  />
                }
              />
              <Route path="/caja/turnos/:id/z" element={<CajaZ contextoDeImpresion={contextoDeImpresion} />} />
              <Route path="*" element={<Navigate to="/vender" replace />} />
            </Routes>
          </main>
        </div>
      </ProveedorDePuntoVentaFijo>
    </AuthContext.Provider>
  )
}
