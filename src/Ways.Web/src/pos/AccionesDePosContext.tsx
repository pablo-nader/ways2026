import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { useLocation, useNavigate } from 'react-router'

export const RUTA_DE_VENTA = '/vender'

export type AccionDePos = 'abrirTurno' | 'cerrarTurno' | 'retirar' | 'sincronizar'
export type PedidoDePos = AccionDePos | 'enfocarEscaneo'

/** Lo que la pantalla de venta le cuenta al header: disponibilidad de cada acción de caja y de
 * sincronización, sin sus funciones (esas viajan aparte, en `EjecutoresDePos`). */
export type EstadoDeAccionesDePos = {
  /** `true` cuando el turno ya se consultó y no hay una consulta ni un error pendientes: recién
   * ahí se puede ejecutar un pedido sin que la pantalla lo pise. */
  listo: boolean
  turnoAbierto: boolean | null
  /** El retiro de efectivo solo existe en el POS de escritorio. */
  retiroOfrecido: boolean
  /** Hay una instantánea local de este punto de venta: sincronizar tiene algo que hacer. */
  puedeSincronizar: boolean
  sincronizando: boolean
  habilitadas: Record<AccionDePos, boolean>
}

export type EjecutoresDePos = Record<PedidoDePos, () => void>

type RegistroDeAcciones = {
  registrar: (estado: EstadoDeAccionesDePos, ejecutores: EjecutoresDePos) => void
  desregistrar: () => void
}

type AccionesDePos = {
  /** Último estado informado por la pantalla de venta; se conserva (en reposo) al desmontarla para
   * que el header siga ofreciendo las acciones mientras el cajero está en otra pantalla. */
  estado: EstadoDeAccionesDePos | null
  /** Ejecuta el pedido ya, si la pantalla de venta está montada y lista; si no, navega a la venta
   * y lo ejecuta una sola vez cuando esa pantalla esté lista. */
  solicitar: (pedido: PedidoDePos) => void
}

const RegistroContext = createContext<RegistroDeAcciones | null>(null)
const AccionesContext = createContext<AccionesDePos>({ estado: null, solicitar: () => {} })

type Registro = { estado: EstadoDeAccionesDePos | null; montado: boolean }

/** Qué ofrece el header cuando la pantalla de venta no está montada: lo que depende del turno
 * conocido, sin las inercias pasajeras de un flujo que ya no existe. */
function estadoEnReposo(estado: EstadoDeAccionesDePos): EstadoDeAccionesDePos {
  const abierto = estado.turnoAbierto === true
  return {
    ...estado,
    listo: false,
    sincronizando: false,
    habilitadas: {
      abrirTurno: estado.turnoAbierto === false,
      cerrarTurno: abierto,
      retirar: abierto && estado.retiroOfrecido,
      sincronizar: estado.puedeSincronizar,
    },
  }
}

function sonIguales(a: EstadoDeAccionesDePos | null, b: EstadoDeAccionesDePos): boolean {
  return a !== null && JSON.stringify(a) === JSON.stringify(b)
}

/**
 * Puente entre el header de `ShellPos` y la pantalla de venta, que es la dueña de los flujos de
 * turno y de sincronización (su lógica no se mueve). La pantalla registra su estado y sus
 * ejecutores; el header pide acciones con `solicitar`. Los ejecutores viajan en un ref para no
 * volver a renderizar el árbol en cada render de la pantalla de venta.
 */
export function ProveedorDeAccionesDePos({ children }: { children: ReactNode }) {
  const { pathname } = useLocation()
  const navegar = useNavigate()
  const [registro, setRegistro] = useState<Registro>({ estado: null, montado: false })
  const ejecutoresRef = useRef<EjecutoresDePos | null>(null)
  const pedidoPendienteRef = useRef<PedidoDePos | null>(null)

  const registrar = useCallback((estado: EstadoDeAccionesDePos, ejecutores: EjecutoresDePos) => {
    ejecutoresRef.current = ejecutores
    setRegistro((previo) => (previo.montado && sonIguales(previo.estado, estado) ? previo : { estado, montado: true }))
  }, [])

  const desregistrar = useCallback(() => {
    ejecutoresRef.current = null
    setRegistro((previo) => ({ estado: previo.estado && estadoEnReposo(previo.estado), montado: false }))
  }, [])

  const valorDeRegistro = useMemo(() => ({ registrar, desregistrar }), [registrar, desregistrar])

  function puedeEjecutar(pedido: PedidoDePos, actual: Registro): actual is Registro & { estado: EstadoDeAccionesDePos } {
    if (!actual.montado || actual.estado === null) return false
    return pedido === 'enfocarEscaneo' || actual.estado.listo
  }

  function ejecutar(pedido: PedidoDePos, estado: EstadoDeAccionesDePos) {
    if (pedido !== 'enfocarEscaneo' && !estado.habilitadas[pedido]) return
    ejecutoresRef.current?.[pedido]()
  }

  // Un pedido que quedó esperando a la pantalla de venta no sobrevive a que el cajero se vaya a otra.
  useEffect(() => {
    if (pathname !== RUTA_DE_VENTA) pedidoPendienteRef.current = null
  }, [pathname])

  useEffect(() => {
    const pedido = pedidoPendienteRef.current
    if (pedido === null || !puedeEjecutar(pedido, registro)) return
    pedidoPendienteRef.current = null
    ejecutar(pedido, registro.estado)
    // `ejecutar` solo lee refs y el estado recibido.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [registro])

  const solicitar = useCallback(
    (pedido: PedidoDePos) => {
      if (puedeEjecutar(pedido, registro)) {
        ejecutar(pedido, registro.estado)
        return
      }
      pedidoPendienteRef.current = pedido
      if (pathname !== RUTA_DE_VENTA) void navegar(RUTA_DE_VENTA)
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [registro, pathname, navegar],
  )

  const valorDeAcciones = useMemo(() => ({ estado: registro.estado, solicitar }), [registro.estado, solicitar])

  return (
    <RegistroContext.Provider value={valorDeRegistro}>
      <AccionesContext.Provider value={valorDeAcciones}>{children}</AccionesContext.Provider>
    </RegistroContext.Provider>
  )
}

export function useAccionesDePos(): AccionesDePos {
  return useContext(AccionesContext)
}

/** Lo usa la pantalla de venta para exponerle al header su estado y sus ejecutores. Fuera de
 * `ShellPos` (app web, tests aislados) no hay proveedor y no hace nada. */
export function useRegistroDeAccionesDePos(estado: EstadoDeAccionesDePos, ejecutores: EjecutoresDePos) {
  const registro = useContext(RegistroContext)
  useEffect(() => {
    registro?.registrar(estado, ejecutores)
  })
  useEffect(() => {
    if (!registro) return
    return registro.desregistrar
  }, [registro])
}
