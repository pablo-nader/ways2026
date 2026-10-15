import { useEffect, useMemo, useRef, useState } from 'react'
import { Link, Navigate, Route, Routes } from 'react-router'
import type { DispositivoActual } from '../api/dispositivos'
import { api, ErrorApi } from '../api/cliente'
import { clienteParaElTicketDeVenta } from '../api/cuentaCorriente'
import { establecerTokenDeSesionBearer } from '../api/entornoTauri'
import { terminarSesionLocalDelPos } from './finDeSesionLocal'
import type {
  ClienteListado,
  ComprobanteEmitido,
  DatosDeFacturaFiscal,
  MedioPagoListado,
  PuntoVentaListado,
  ResumenDeCierrePorRetiro,
  UsuarioAutenticado,
} from '../api/tipos'
import { AuthContext } from '../auth/AuthContext'
import { CajaZ } from '../paginas/CajaZ'
import { CierresDeCaja } from '../paginas/CierresDeCaja'
import { GastosDelTurno } from '../paginas/GastosDelTurno'
import { Pos } from '../paginas/Pos'
import type { CajaDeEscritorio, SaldoParaTicket } from '../paginas/Pos'
import { VentasDelTurno } from '../paginas/VentasDelTurno'
import { ProveedorDePuntoVentaFijo } from '../puntoVenta/ProveedorDePuntoVentaFijo'
import { abrirConfiguracion, enEscritorio, imprimir } from '../impresion/impresora'
import { cierreDeTurno, facturaDeVenta, notaDeCreditoDeVenta, ticketDeVenta } from '../impresion/plantillas'
import type { ContextoDeImpresion } from '../impresion/plantillas'
import { MenuDeAcciones } from '../componentes/MenuDeAcciones'
import type { ItemDeMenuDeAcciones } from '../componentes/MenuDeAcciones'
import { ProveedorDeAccionesDePos, useAccionesDePos } from './AccionesDePosContext'
import { AvisoDeActualizacion } from './AvisoDeActualizacion'
import { ProveedorDeBorradoresDeTicket } from './BorradorDeTicketContext'
import { RanuraHeaderPosContext } from './RanuraHeaderPosContext'
import { BotonDeTema } from '../tema/BotonDeTema'

type Props = {
  dispositivo: DispositivoActual
  usuario: UsuarioAutenticado
  puntoVenta: PuntoVentaListado
  /** Cierre de sesión del cajero (nunca desvincula el dispositivo) — vuelve a `LoginDeDispositivo`
   * con el mismo `dispositivo` ya conocido. */
  alCerrarSesion: () => void
}

type PropsCabeceraPos = {
  dispositivo: DispositivoActual
  usuario: UsuarioAutenticado
  cerrandoSesion: boolean
  /** Mientras se instala una actualización, el header no responde (ni el atajo F1). */
  inerte: boolean
  alCerrarSesion: () => void
  refRanura: (nodo: HTMLDivElement | null) => void
}

/** Un modal abierto (`Modal` o el diálogo propio de la pantalla de venta) se anuncia con `role="dialog"`. */
function hayUnDialogoAbierto(): boolean {
  return document.querySelector('[role="dialog"]') !== null
}

/**
 * Header del POS en una sola línea: título, navegación (Vender, Caja, Sistema), la ranura donde
 * `Pos.tsx` monta los chips de estado y, a la derecha, tema y cierre de sesión. Las acciones de
 * caja y sincronización viven en `Pos.tsx`; acá se piden por `useAccionesDePos`.
 */
function CabeceraPos({ dispositivo, usuario, cerrandoSesion, inerte, alCerrarSesion, refRanura }: PropsCabeceraPos) {
  const { estado, aviso, solicitar } = useAccionesDePos()

  useEffect(() => {
    function alTeclado(evento: KeyboardEvent) {
      if (evento.key !== 'F1') return
      evento.preventDefault()
      if (inerte || hayUnDialogoAbierto()) return
      solicitar('enfocarEscaneo')
    }
    document.addEventListener('keydown', alTeclado)
    return () => document.removeEventListener('keydown', alTeclado)
  }, [inerte, solicitar])

  const habilitadas = estado?.habilitadas
  const itemsDeCaja: ItemDeMenuDeAcciones[] = [
    { clave: 'ventas', etiqueta: 'Ventas del turno', a: '/ventas-del-turno' },
    { clave: 'historial', etiqueta: 'Historial de turnos', a: '/cierres-de-caja' },
    { clave: 'gastos', etiqueta: 'Gastos', a: '/gastos-del-turno' },
  ]
  if (estado === null || estado.retiroOfrecido) {
    itemsDeCaja.push({
      clave: 'retirar',
      etiqueta: 'Retirar efectivo',
      separadorAntes: true,
      deshabilitado: !habilitadas?.retirar,
      alSeleccionar: () => solicitar('retirar'),
    })
  }
  if (estado?.turnoAbierto) {
    itemsDeCaja.push({
      clave: 'cerrar-turno',
      etiqueta: estado.verificandoCierre ? 'Verificando…' : 'Cerrar turno',
      deshabilitado: !habilitadas?.cerrarTurno,
      alSeleccionar: () => solicitar('cerrarTurno'),
    })
  } else if (estado?.turnoAbierto === false) {
    itemsDeCaja.push({
      clave: 'abrir-turno',
      etiqueta: 'Abrir turno',
      deshabilitado: !habilitadas?.abrirTurno,
      alSeleccionar: () => solicitar('abrirTurno'),
    })
  } else {
    itemsDeCaja.push({
      clave: 'turno-desconocido',
      etiqueta: estado?.imposible ? 'Turno no disponible' : 'Consultando turno…',
      deshabilitado: true,
      alSeleccionar: () => {},
    })
  }

  const itemsDeSistema: ItemDeMenuDeAcciones[] = []
  if (enEscritorio()) {
    itemsDeSistema.push({ clave: 'configuracion', etiqueta: 'Configuración', alSeleccionar: () => void abrirConfiguracion() })
  }
  itemsDeSistema.push({
    clave: 'sincronizar',
    etiqueta: estado?.sincronizando ? 'Sincronizando…' : 'Sincronizar',
    deshabilitado: !habilitadas?.sincronizar,
    alSeleccionar: () => solicitar('sincronizar'),
  })

  return (
    <header className="navbar bg-body px-3 py-2 ways-navbar d-print-none flex-nowrap gap-3" inert={inerte}>
      <div className="d-flex align-items-baseline gap-2 flex-shrink-1" style={{ minWidth: 0 }}>
        <strong className="text-body text-truncate">{dispositivo.empresa.nombre}</strong>
        <span className="text-body-tertiary" aria-hidden="true">
          |
        </span>
        <small className="text-body-secondary text-truncate">
          PV {dispositivo.puntoVenta.numero} — {dispositivo.puntoVenta.nombre} · {usuario.usuario}
        </small>
      </div>
      <nav className="d-flex align-items-center gap-2 flex-shrink-0" aria-label="Principal">
        <Link className="btn btn-success" to="/vender" aria-keyshortcuts="F1" title="Vender (F1)">
          Vender <sup aria-hidden="true">(F1)</sup>
        </Link>
        <MenuDeAcciones etiqueta="Caja" items={itemsDeCaja} />
        <MenuDeAcciones etiqueta="Sistema" items={itemsDeSistema} />
      </nav>
      {aviso && (
        <span role="status" className="small text-warning text-truncate flex-shrink-1" style={{ minWidth: 0, maxWidth: '22rem' }} title={aviso}>
          {aviso}
        </span>
      )}
      {/* Contenedor vacío: `Pos.tsx` portalea acá los chips de estado (ver `RanuraHeaderPosContext`).
          Nunca se renderiza nada directamente en este `div`. */}
      <div className="d-flex align-items-center gap-2 flex-wrap ms-auto" style={{ minWidth: 0 }} ref={refRanura} />
      <div className="d-flex align-items-center gap-2 flex-shrink-0">
        <BotonDeTema className="btn btn-outline-secondary" />
        <button type="button" className="btn btn-outline-secondary" disabled={cerrandoSesion} onClick={alCerrarSesion}>
          {cerrandoSesion ? 'Saliendo…' : 'Cerrar sesión'}
        </button>
      </div>
    </header>
  )
}

/**
 * Shell del POS de escritorio con sesión activa (stage-desktop-pos): header compacto (empresa, PV,
 * cajero, navegación) + las pantallas que reusa sin fork (`Pos`, `CajaZ`, `VentasDelTurno`).
 *
 * El punto de venta lo fija el dispositivo — `ProveedorDePuntoVentaFijo`, nunca el
 * `PuertaDePuntoVenta` de elección manual de la app completa. La sesión se expone por el mismo
 * `AuthContext` que ya consumen `CajaZ`/`Layout` sin fork: acá se provee un valor propio (login del
 * dispositivo en vez de mail/password) en lugar de montar el `AuthProvider` de la app completa, que
 * no conoce `POST /auth/login-dispositivo`.
 *
 * Las acciones de caja (abrir/cerrar turno, retirar efectivo) y la sincronización manual viven en
 * `Pos.tsx`, que es la dueña de esos flujos: el header las ofrece en los menús "Caja" y "Sistema"
 * y las pide por `ProveedorDeAccionesDePos`, que las ejecuta desde cualquier ruta del shell. El
 * cierre de turno del escritorio se resuelve íntegro dentro de `Pos.tsx` vía el seam
 * `cajaDeEscritorio` (contexto de impresión + `encolarImpresion`, la MISMA cola FIFO que usa el
 * ticket de venta): confirmar apertura de cajón, contar el efectivo y `POST cierre-por-retiro`,
 * sin abandonar `/vender`. `CierreDeCaja` sigue viva para la app web (ruta `/caja/cierre`); este
 * shell no la monta. `CajaZ` (`/caja/turnos/:id/z`) se mantiene montada por si hace falta más
 * adelante, aunque ningún control de este shell navega ahí hoy.
 */
export function ShellPos({ dispositivo, usuario, puntoVenta, alCerrarSesion }: Props) {
  const [cerrandoSesion, setCerrandoSesion] = useState(false)
  // Si el cajero sale de "Vender" con ítems en el carrito, el último valor informado se mantiene:
  // el borrador sigue vivo y la actualización sigue bloqueada.
  const [ventaEnCurso, setVentaEnCurso] = useState(false)
  const [instalandoActualizacion, setInstalandoActualizacion] = useState(false)
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
  // Espejo renderizable de `procesandoColaRef`: la cola vive solo en memoria, así que reiniciar
  // la app por una actualización con trabajos pendientes los perdería.
  const [imprimiendo, setImprimiendo] = useState(false)
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
    setImprimiendo(true)
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
      if (montadoRef.current) setImprimiendo(false)
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
   * "Reimprimir" de OTRO aviso en simultáneo sigue andando. Reenvía los mismos bytes, pulso del
   * cajón incluido: el trabajo falló entero, así que el cajón nunca se abrió y el reintento debe abrirlo. */
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

  /** stage-pos-retiros-y-cierre-por-retiro (etapa 5): seam de `Pos.tsx` para "Retirar" y "Cerrar
   * caja" por retiro — el mismo `contexto` y la MISMA `encolarImpresion` (cola FIFO) que ya usa el
   * ticket de venta; `Pos.tsx` arma los bytes de cada ticket propio con sus propias plantillas. */
  const cajaDeEscritorio: CajaDeEscritorio = { contexto: contextoDeImpresion, encolarImpresion }

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
      // stage-pos-sesion-offline: logout explícito es uno de los tres disparadores de limpieza
      // de la sesión persistida (ver el doc-comment de `limpiarSesionDeCajeroPersistida`) — corre
      // en el `finally` para limpiarse tanto si el POST tuvo éxito como si la sesión ya estaba
      // vencida del lado del servidor (el catch de arriba). No hace nada fuera de Tauri, nunca
      // lanza.
      //
      // judgment-day ronda 1 (FIX 2a, BLOCKER): `limpiarSesionDeCajeroPersistida` sola solo borra
      // el archivo — el bearer EN MEMORIA (`entornoTauri.ts`) seguía vivo y `cliente.ts` lo sigue
      // adjuntando a cada request siguiente, así que "Cerrar sesión" no cerraba nada de verdad.
      // Tiene que limpiarse ACÁ (antes de `alCerrarSesion()`, que desmonta este shell): ningún
      // otro punto del código vuelve a tocar el bearer una vez que se vuelve a `LoginDeDispositivo`.
      establecerTokenDeSesionBearer(null)
      await terminarSesionLocalDelPos()
      cerrandoSesionRef.current = false
      setCerrandoSesion(false)
      alCerrarSesion()
    }
  }

  function alEmitirVenta(comprobante: ComprobanteEmitido, cliente: ClienteListado, medios: MedioPagoListado[], saldo?: SaldoParaTicket) {
    // Sin lectura de la cola o sin saldo confirmado, el saldo se rotula estimado.
    const clienteConSaldo = clienteParaElTicketDeVenta({
      cliente,
      pagos: comprobante.pagos,
      medios,
      consumoPendiente: saldo?.consumoPendiente ?? null,
      saldoConfirmado: saldo?.saldoConfirmado ?? false,
    })
    const opciones = clienteConSaldo ? { cliente: clienteConSaldo } : {}
    // No bloqueante y sin cambiar el resultado de la venta, que ya se confirmó en el servidor —
    // una falla queda como su propio aviso persistente del shell + "Reimprimir", nunca silenciosa
    // ni descartada por otro trabajo de impresión que esté (o quede) en vuelo.
    // `Pos.tsx` deja `fiscal` en el comprobante solo cuando la venta seguro se facturaba y la factura se aprobó a
    // tiempo (`conFiscalSoloSiSeguroSeFactura`); sin eso se imprime el ticket de siempre.
    const factura = comprobante.fiscal?.comprobante ?? null
    if (factura) {
      encolarImpresion('la factura de venta', facturaDeVenta(comprobante, factura, contextoDeImpresion, medios, opciones))
      return
    }
    encolarImpresion('el ticket de venta', ticketDeVenta(comprobante, contextoDeImpresion, medios, opciones))
  }

  /** "Imprimir" de "Ventas del turno" (stage-desktop-pos): mismo dueño único de la impresión de
   * escritorio que `alEmitirVenta` — pasa por la MISMA cola FIFO (`encolarImpresion`), nunca un
   * segundo camino de impresión. `{ reimpresion: true }` marca el ticket para que nunca se
   * confunda con el original. */
  function alReimprimirVenta(comprobante: ComprobanteEmitido, medios: MedioPagoListado[]) {
    encolarImpresion(
      `la reimpresión del ticket ${comprobante.numeroVisible}`,
      ticketDeVenta(comprobante, contextoDeImpresion, medios, { reimpresion: true }),
    )
  }

  /** "Reimprimir factura" de "Ventas del turno": la misma cola FIFO y la misma marca de reimpresión que el ticket. */
  function alReimprimirFacturaDeVenta(comprobante: ComprobanteEmitido, factura: DatosDeFacturaFiscal, medios: MedioPagoListado[]) {
    encolarImpresion(
      `la reimpresión de la factura ${comprobante.numeroVisible}`,
      facturaDeVenta(comprobante, factura, contextoDeImpresion, medios, { reimpresion: true }),
    )
  }

  /** Nota de crédito aprobada de una venta anulada (al anular o desde "Reimprimir NC"): la misma cola FIFO. */
  function alImprimirNotaDeCredito(
    comprobante: ComprobanteEmitido,
    notaDeCredito: DatosDeFacturaFiscal,
    facturaAsociada: DatosDeFacturaFiscal | null,
    medios: MedioPagoListado[],
    opciones: { reimpresion?: boolean } = {},
  ) {
    const reimpresion = opciones.reimpresion ?? false
    encolarImpresion(
      reimpresion ? `la reimpresión de la nota de crédito de ${comprobante.numeroVisible}` : `la nota de crédito de ${comprobante.numeroVisible}`,
      notaDeCreditoDeVenta(comprobante, notaDeCredito, facturaAsociada, contextoDeImpresion, medios, { reimpresion }),
    )
  }

  /** "Imprimir" de "Cierres de caja": el mismo `cierreDeTurno` y la misma cola
   * FIFO que el cierre del turno. */
  function alReimprimirCierre(resumen: ResumenDeCierrePorRetiro) {
    encolarImpresion(`el ticket de cierre del turno #${resumen.idTurnoCaja}`, cierreDeTurno(resumen, contextoDeImpresion))
  }

  return (
    <AuthContext.Provider value={valorAuth}>
      <ProveedorDePuntoVentaFijo puntoVenta={puntoVenta}>
        {/* stage-pos-caja-en-cabecera: `RanuraHeaderPosContext` expone el nodo del header donde
            `Pos.tsx` monta sus chips de estado. */}
        <RanuraHeaderPosContext.Provider value={nodoRanuraHeader}>
          {/* stage-pos-adjustments: por encima de `<Routes>`, para que el borrador de un ticket en
              curso sobreviva a navegar entre "Vender"/"Ventas del turno"/"Gastos" — el `Provider` es
              una sola instancia por sesión de este shell, nunca se remonta con la navegación. */}
          <ProveedorDeBorradoresDeTicket>
            <ProveedorDeAccionesDePos>
              <div className="d-flex flex-column min-vh-100">
                <CabeceraPos
                  dispositivo={dispositivo}
                  usuario={usuario}
                  cerrandoSesion={cerrandoSesion}
                  inerte={instalandoActualizacion}
                  alCerrarSesion={() => void cerrarSesion()}
                  refRanura={setNodoRanuraHeader}
                />

                <AvisoDeActualizacion
                  motivoDeBloqueo={
                    ventaEnCurso
                      ? 'Terminá la venta en curso para instalarla.'
                      : imprimiendo
                        ? 'Esperá a que termine la impresión para instalarla.'
                        : null
                  }
                  alCambiarInstalando={setInstalandoActualizacion}
                />

                {/* stage-desktop-pos (Fix judgment-day W1/W2, ronda 2 — R2-2): fuera de `<Routes>` a
                    propósito — sobreviven a la navegación de `alCerrarExitosamente` hacia la Caja Z (y
                    a cualquier otra navegación del shell). Un aviso por trabajo fallido (regla 14): el
                    de un trabajo nunca lo pisa ni lo borra el de otro. */}
                {avisosDeImpresion.map((aviso) => (
                  <div
                    key={aviso.id}
                    role="alert"
                    className="alert alert-warning py-1 px-3 mb-0 d-flex justify-content-between align-items-center gap-2 d-print-none"
                  >
                    <span>
                      No se pudo imprimir {aviso.descripcion}: {aviso.mensaje}
                    </span>
                    <div className="d-flex gap-2">
                      <button
                        type="button"
                        className="btn btn-sm btn-outline-secondary"
                        disabled={aviso.reintentando}
                        onClick={() => reimprimir(aviso)}
                      >
                        {aviso.reintentando ? 'Imprimiendo…' : 'Reimprimir'}
                      </button>
                      <button type="button" className="btn btn-sm btn-outline-secondary" onClick={() => cerrarAviso(aviso.id)}>
                        Cerrar
                      </button>
                    </div>
                  </div>
                ))}

                <main className="flex-grow-1" inert={instalandoActualizacion}>
                  <Routes>
                    <Route path="/vender" element={<Pos alEmitir={alEmitirVenta} cajaDeEscritorio={cajaDeEscritorio} alCambiarVentaEnCurso={setVentaEnCurso} />} />
                    <Route path="/ventas-del-turno" element={<VentasDelTurno alReimprimir={alReimprimirVenta} alReimprimirFactura={alReimprimirFacturaDeVenta} alImprimirNotaDeCredito={alImprimirNotaDeCredito} />} />
                    <Route path="/gastos-del-turno" element={<GastosDelTurno cajaDeEscritorio={cajaDeEscritorio} />} />
                    <Route path="/cierres-de-caja" element={<CierresDeCaja alReimprimir={alReimprimirCierre} />} />
                    <Route path="/caja/turnos/:id/z" element={<CajaZ contextoDeImpresion={contextoDeImpresion} />} />
                    <Route path="*" element={<Navigate to="/vender" replace />} />
                  </Routes>
                </main>
              </div>
            </ProveedorDeAccionesDePos>
          </ProveedorDeBorradoresDeTicket>
        </RanuraHeaderPosContext.Provider>
      </ProveedorDePuntoVentaFijo>
    </AuthContext.Provider>
  )
}
