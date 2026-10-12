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
  /** La pantalla no va a quedar lista por sí sola: la consulta del turno falló, no hay punto de
   * venta o está en modo presupuesto. Un pedido pendiente no tiene nada que esperar. */
  imposible: boolean
  /** Está verificando el turno antes de cerrarlo (progreso de "Cerrar turno"). */
  verificandoCierre: boolean
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
  /** Por qué un pedido no se ejecutó, para que el cajero no se quede sin respuesta; se borra solo. */
  aviso: string | null
  /** Ejecuta el pedido ya, si la pantalla de venta está montada y lista; si no, navega a la venta
   * y lo ejecuta una sola vez cuando esa pantalla esté lista. Un pedido que no se puede cumplir
   * pronto (la pantalla no puede quedar lista, o tarda más del plazo) se descarta con un aviso:
   * una acción de caja nunca corre mucho después del click. */
  solicitar: (pedido: PedidoDePos) => void
}

export const PLAZO_DEL_PEDIDO_PENDIENTE_MS = 10_000
const DURACION_DEL_AVISO_MS = 6_000

const ETIQUETA_DEL_PEDIDO: Record<AccionDePos, string> = {
  abrirTurno: 'abrir el turno',
  cerrarTurno: 'cerrar el turno',
  retirar: 'retirar efectivo',
  sincronizar: 'sincronizar',
}

const RegistroContext = createContext<RegistroDeAcciones | null>(null)
const AccionesContext = createContext<AccionesDePos>({ estado: null, aviso: null, solicitar: () => {} })

type Registro = { estado: EstadoDeAccionesDePos | null; montado: boolean }

/** Qué ofrece el header cuando la pantalla de venta no está montada: lo que depende del turno
 * conocido, sin las inercias pasajeras de un flujo que ya no existe. */
function estadoEnReposo(estado: EstadoDeAccionesDePos): EstadoDeAccionesDePos {
  const abierto = estado.turnoAbierto === true
  return {
    ...estado,
    listo: false,
    imposible: false,
    verificandoCierre: false,
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
  const [aviso, setAviso] = useState<string | null>(null)
  const ejecutoresRef = useRef<EjecutoresDePos | null>(null)
  const pedidoPendienteRef = useRef<PedidoDePos | null>(null)
  const plazoDelPedidoRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const plazoDelAvisoRef = useRef<ReturnType<typeof setTimeout> | null>(null)

  useEffect(
    () => () => {
      if (plazoDelPedidoRef.current) clearTimeout(plazoDelPedidoRef.current)
      if (plazoDelAvisoRef.current) clearTimeout(plazoDelAvisoRef.current)
    },
    [],
  )

  const registrar = useCallback((estado: EstadoDeAccionesDePos, ejecutores: EjecutoresDePos) => {
    ejecutoresRef.current = ejecutores
    setRegistro((previo) => (previo.montado && sonIguales(previo.estado, estado) ? previo : { estado, montado: true }))
  }, [])

  const desregistrar = useCallback(() => {
    ejecutoresRef.current = null
    setRegistro((previo) => ({ estado: previo.estado && estadoEnReposo(previo.estado), montado: false }))
  }, [])

  const valorDeRegistro = useMemo(() => ({ registrar, desregistrar }), [registrar, desregistrar])

  function avisar(texto: string | null) {
    if (plazoDelAvisoRef.current) clearTimeout(plazoDelAvisoRef.current)
    setAviso(texto)
    if (texto === null) return
    plazoDelAvisoRef.current = setTimeout(() => setAviso(null), DURACION_DEL_AVISO_MS)
  }

  function soltarPedido() {
    pedidoPendienteRef.current = null
    if (plazoDelPedidoRef.current) clearTimeout(plazoDelPedidoRef.current)
    plazoDelPedidoRef.current = null
  }

  function descartarPedido(pedido: AccionDePos, motivo: string) {
    soltarPedido()
    avisar(`No se pudo ${ETIQUETA_DEL_PEDIDO[pedido]}: ${motivo}`)
  }

  function puedeEjecutar(pedido: PedidoDePos, actual: Registro): actual is Registro & { estado: EstadoDeAccionesDePos } {
    if (!actual.montado || actual.estado === null) return false
    return pedido === 'enfocarEscaneo' || actual.estado.listo
  }

  function ejecutar(pedido: PedidoDePos, estado: EstadoDeAccionesDePos) {
    if (pedido === 'enfocarEscaneo') {
      ejecutoresRef.current?.enfocarEscaneo()
      return
    }
    // Una sincronización que ya está corriendo (por ejemplo la de arranque) cumple el pedido.
    if (pedido === 'sincronizar' && estado.sincronizando) return
    if (!estado.habilitadas[pedido]) {
      avisar(`No se pudo ${ETIQUETA_DEL_PEDIDO[pedido]}: no está disponible ahora.`)
      return
    }
    ejecutoresRef.current?.[pedido]()
  }

  // Un pedido que quedó esperando a la pantalla de venta no sobrevive a que el cajero se vaya a otra.
  useEffect(() => {
    if (pathname !== RUTA_DE_VENTA) soltarPedido()
    // `soltarPedido` solo toca refs.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pathname])

  useEffect(() => {
    const pedido = pedidoPendienteRef.current
    if (pedido === null) return
    if (pedido !== 'enfocarEscaneo' && registro.montado && registro.estado?.imposible) {
      descartarPedido(pedido, 'no se pudo consultar el turno.')
      return
    }
    if (!puedeEjecutar(pedido, registro)) return
    soltarPedido()
    ejecutar(pedido, registro.estado)
    // Estas funciones solo leen refs y el estado recibido.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [registro])

  const solicitar = useCallback(
    (pedido: PedidoDePos) => {
      avisar(null)
      if (puedeEjecutar(pedido, registro)) {
        ejecutar(pedido, registro.estado)
        return
      }
      if (pedido !== 'enfocarEscaneo' && registro.montado && registro.estado?.imposible) {
        descartarPedido(pedido, 'no se pudo consultar el turno.')
        return
      }
      soltarPedido()
      pedidoPendienteRef.current = pedido
      if (pedido !== 'enfocarEscaneo') {
        plazoDelPedidoRef.current = setTimeout(() => {
          if (pedidoPendienteRef.current === pedido) descartarPedido(pedido, 'la pantalla de venta tardó demasiado en estar lista.')
        }, PLAZO_DEL_PEDIDO_PENDIENTE_MS)
      }
      if (pathname !== RUTA_DE_VENTA) void navegar(RUTA_DE_VENTA)
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [registro, pathname, navegar],
  )

  const valorDeAcciones = useMemo(() => ({ estado: registro.estado, aviso, solicitar }), [registro.estado, aviso, solicitar])

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
