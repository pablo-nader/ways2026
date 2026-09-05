import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { useNavigate } from 'react-router'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import { puedeOperarPos } from '../api/tipos'
import type { PuntoVentaListado, UsuarioAutenticado } from '../api/tipos'
import { useAuth } from '../auth/useAuth'
import { Cargando } from '../componentes/Cargando'
import {
  guardarPuntoVentaDeSesion,
  leerPuntoVentaDeSesion,
  olvidarPuntoVentaDeSesion,
} from './almacenDePuntoVenta'
import { ElegirPuntoDeVenta } from './ElegirPuntoDeVenta'
import { PuntoVentaContext } from './PuntoVentaContext'
import type { EstadoDePuntoVenta } from './PuntoVentaContext'

type Estado =
  | { fase: 'cargando' }
  | { fase: 'error'; mensaje: string }
  | { fase: 'listo'; puntosVenta: PuntoVentaListado[]; puntoVenta: PuntoVentaListado | null }

const MENSAJE_GENERICO = 'No se pudieron cargar los puntos de venta.'

/** Root no opera ningún punto de venta: el contexto existe igual para que los consumidores no
 * tengan que preguntar por el rol, pero está vacío y `recargar` no pide nada. */
const SIN_PUNTOS_VENTA: EstadoDePuntoVenta = {
  puntosVenta: [],
  puntoVenta: null,
  elegir: () => undefined,
  recargar: () => Promise.resolve(),
}

/**
 * Selección a partir de una lista recién recibida: con un solo punto de venta se elige solo;
 * con varios, el preferido (el guardado o el que estaba activo) solo si sigue en la lista.
 */
function listoCon(puntosVenta: PuntoVentaListado[], idPreferido: number | null): Estado {
  const puntoVenta =
    puntosVenta.length === 1 ? puntosVenta[0] : (puntosVenta.find((p) => p.id === idPreferido) ?? null)

  return { fase: 'listo', puntosVenta, puntoVenta }
}

function mensajeDe(error: unknown): string {
  return error instanceof ErrorApi ? error.message : MENSAJE_GENERICO
}

type Props = { children: ReactNode }

/**
 * Resuelve el punto de venta de la sesión antes de dejar entrar a la aplicación: con uno solo lo
 * elige, con varios ofrece la pantalla de elección hasta que haya uno (o recupera el guardado), y
 * sin ninguno deja pasar con la selección vacía. Se monta dentro de `RutaProtegida`.
 */
export function PuertaDePuntoVenta({ children }: Props) {
  const { usuario } = useAuth()

  if (!usuario) {
    throw new Error('PuertaDePuntoVenta tiene que montarse dentro de una RutaProtegida.')
  }

  if (!puedeOperarPos(usuario.rolId)) {
    return <PuntoVentaContext.Provider value={SIN_PUNTOS_VENTA}>{children}</PuntoVentaContext.Provider>
  }

  // La clave por usuario remonta el proveedor al cambiar la cuenta: el estado y las lecturas en
  // vuelo de la anterior se descartan por construcción en vez de reconciliarse a mano.
  return (
    <ProveedorDePuntoVenta key={usuario.id} usuario={usuario}>
      {children}
    </ProveedorDePuntoVenta>
  )
}

function ProveedorDePuntoVenta({ usuario, children }: Props & { usuario: UsuarioAutenticado }) {
  const { cerrarSesion } = useAuth()
  const navegar = useNavigate()
  const idUsuario = usuario.id

  const [estado, setEstado] = useState<Estado>({ fase: 'cargando' })
  const [ocupado, setOcupado] = useState(false)

  /**
   * Generación compartida por la carga inicial, "Reintentar" y `recargar`: cada lectura acuña la
   * suya y solo la última que arrancó aplica su resultado. Se invalida al desmontar, que es
   * también lo que pasa al cambiar de usuario porque el proveedor va con `key`.
   */
  const generacionRef = useRef(0)
  /** Guarda de reentrancia de la pantalla de error, compartida por "Reintentar" y "Salir": un
   * segundo clic en el mismo tick no dispara nada, aunque el `disabled` todavía no se haya pintado. */
  const ocupadoRef = useRef(false)

  /** Acuña la generación siguiente; toda lectura en vuelo con una anterior queda superada. */
  const avanzarGeneracion = useCallback(() => ++generacionRef.current, [])

  const cargar = useCallback(
    async (generacion: number) => {
      let siguiente: Estado
      try {
        const puntosVenta = await clienteDeOrganizacion.listarPuntosVenta()
        siguiente = listoCon(puntosVenta, leerPuntoVentaDeSesion(idUsuario))
      } catch (error) {
        siguiente = { fase: 'error', mensaje: mensajeDe(error) }
      }

      if (generacionRef.current === generacion) setEstado(siguiente)
    },
    [idUsuario],
  )

  useEffect(() => {
    void cargar(avanzarGeneracion())

    return () => {
      avanzarGeneracion()
    }
  }, [cargar, avanzarGeneracion])

  // El almacén es una proyección de la selección vigente: cada estado listo la escribe (o la borra
  // cuando no hay ninguna), así el refresco de página la recupera y un id que dejó de existir no
  // sobrevive.
  useEffect(() => {
    if (estado.fase !== 'listo') return

    if (estado.puntoVenta) guardarPuntoVentaDeSesion(idUsuario, estado.puntoVenta.id)
    else olvidarPuntoVentaDeSesion()
  }, [estado, idUsuario])

  const elegir = useCallback((id: number) => {
    setEstado((previo) => {
      if (previo.fase !== 'listo') return previo

      const elegido = previo.puntosVenta.find((p) => p.id === id)

      return elegido ? { ...previo, puntoVenta: elegido } : previo
    })
  }, [])

  const recargar = useCallback(async () => {
    const generacion = avanzarGeneracion()
    try {
      const puntosVenta = await clienteDeOrganizacion.listarPuntosVenta()
      if (generacionRef.current !== generacion) return

      setEstado((previo) =>
        previo.fase === 'listo' ? listoCon(puntosVenta, previo.puntoVenta?.id ?? null) : previo,
      )
    } catch (error) {
      // Una lectura superada por otra más nueva no tiene resultado que informar: ni estado ni rechazo.
      if (generacionRef.current === generacion) throw error
    }
  }, [avanzarGeneracion])

  async function reintentar() {
    if (ocupadoRef.current) return
    ocupadoRef.current = true
    setOcupado(true)

    const generacion = avanzarGeneracion()
    try {
      await cargar(generacion)
    } finally {
      ocupadoRef.current = false
      if (generacionRef.current === generacion) setOcupado(false)
    }
  }

  async function salir() {
    if (ocupadoRef.current) return
    ocupadoRef.current = true
    setOcupado(true)

    const generacion = generacionRef.current
    try {
      await cerrarSesion()
      navegar('/login', { replace: true })
    } finally {
      ocupadoRef.current = false
      if (generacionRef.current === generacion) setOcupado(false)
    }
  }

  const valor = useMemo<EstadoDePuntoVenta>(
    () => ({
      puntosVenta: estado.fase === 'listo' ? estado.puntosVenta : [],
      puntoVenta: estado.fase === 'listo' ? estado.puntoVenta : null,
      elegir,
      recargar,
    }),
    [estado, elegir, recargar],
  )

  if (estado.fase === 'cargando') {
    return <Cargando texto="Cargando puntos de venta…" />
  }

  if (estado.fase === 'error') {
    return <PantallaDeError mensaje={estado.mensaje} ocupado={ocupado} alReintentar={reintentar} alSalir={salir} />
  }

  if (!estado.puntoVenta && estado.puntosVenta.length > 1) {
    return <ElegirPuntoDeVenta puntosVenta={estado.puntosVenta} alElegir={elegir} />
  }

  return <PuntoVentaContext.Provider value={valor}>{children}</PuntoVentaContext.Provider>
}

type PropsDeError = {
  mensaje: string
  ocupado: boolean
  alReintentar: () => void
  alSalir: () => void
}

function PantallaDeError({ mensaje, ocupado, alReintentar, alSalir }: PropsDeError) {
  return (
    <div className="d-flex align-items-center justify-content-center min-vh-100 p-3">
      <div role="alert" className="alert alert-danger rounded-0 text-center w-100" style={{ maxWidth: 480 }}>
        <p>{mensaje}</p>
        <button type="button" className="btn btn-outline-dark rounded-0 me-2" disabled={ocupado} onClick={alReintentar}>
          Reintentar
        </button>
        <button type="button" className="btn btn-outline-secondary rounded-0" disabled={ocupado} onClick={alSalir}>
          Salir
        </button>
      </div>
    </div>
  )
}
