import { useCallback, useEffect, useRef, useState } from 'react'
import { ErrorApi } from '../../api/cliente'
import type { OrdenDeServidor } from '../../api/reportes'
import { siguienteOrden } from './ordenamiento'

const DEMORA_DEBOUNCE_MS = 300

type FiltrosPaginados = { pagina: number; tamanio: number }
type PaginaPaginada = { pagina: number; tamanio: number; total: number }

type Configuracion<F extends FiltrosPaginados, Columna extends string, P extends PaginaPaginada> = {
  filtrosVacios: () => F
  /** Filtros de texto o de rango que se aplican con demora: el borrador se actualiza al tipear y el filtro 300 ms después. */
  clavesDeBorrador: readonly (keyof F)[]
  consultar: (filtros: F, orden: OrdenDeServidor<Columna>) => Promise<P>
  esColumna: (columna: string) => columna is Columna
  mensajeDeError: string
}

/**
 * Estado de una grilla que filtra, ordena y pagina en el servidor: filtros con borrador (demora de 300 ms en los de texto),
 * orden, página y una generación que descarta la respuesta de toda consulta que ya no es la última pedida. `recargar` vuelve a
 * pedir con los filtros, el orden y la página en curso, y resuelve cuando la respuesta ya se aplicó (o se descartó): una
 * escritura puede esperarlo para mantener inerte la pantalla hasta que el refresco aterrice. Nunca rechaza.
 */
export function useGrillaDeServidor<F extends FiltrosPaginados, Columna extends string, P extends PaginaPaginada>(
  configuracion: Configuracion<F, Columna, P>,
) {
  const configuracionRef = useRef(configuracion)
  const [filtros, setFiltros] = useState<F>(() => configuracion.filtrosVacios())
  const [borrador, setBorrador] = useState<F>(() => configuracion.filtrosVacios())
  const [orden, setOrden] = useState<OrdenDeServidor<Columna>>(null)
  const [pagina, setPagina] = useState<P | null>(null)
  const [cargando, setCargando] = useState(true)
  const [cargaInicialHecha, setCargaInicialHecha] = useState(false)
  const [error, setError] = useState('')

  const filtrosRef = useRef(filtros)
  const ordenRef = useRef(orden)
  const borradorRef = useRef(borrador)
  const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  // Cada cambio de filtro, orden o página y cada recarga abren una generación nueva: una respuesta tardía de una consulta anterior
  // no pisa lo que el usuario ya cambió.
  const generacionRef = useRef(0)

  // Declarado antes que el efecto de carga: `recargar` es estable y lee de estas refs lo último que muestra la pantalla, de modo que
  // un refresco pedido por una escritura nunca usa filtros de un render anterior.
  useEffect(() => {
    configuracionRef.current = configuracion
    filtrosRef.current = filtros
    ordenRef.current = orden
  })

  useEffect(() => {
    return () => {
      if (debounceRef.current) clearTimeout(debounceRef.current)
    }
  }, [])

  const recargar = useCallback(async () => {
    const generacion = (generacionRef.current += 1)
    setCargando(true)
    setError('')
    try {
      const respuesta = await configuracionRef.current.consultar(filtrosRef.current, ordenRef.current)
      if (generacionRef.current !== generacion) return
      // Una baja puede dejar la página pedida fuera de rango: se pide la última válida (la página 1 nunca la supera).
      const ultimaPaginaValida = Math.max(1, Math.ceil(respuesta.total / respuesta.tamanio))
      if (respuesta.pagina > ultimaPaginaValida) {
        setFiltros((prev) => (prev.pagina === ultimaPaginaValida ? prev : { ...prev, pagina: ultimaPaginaValida }))
        return
      }
      setPagina(respuesta)
      setCargando(false)
      setCargaInicialHecha(true)
    } catch (e) {
      if (generacionRef.current !== generacion) return
      setPagina(null)
      setError(e instanceof ErrorApi ? e.message : configuracionRef.current.mensajeDeError)
      setCargando(false)
      setCargaInicialHecha(true)
    }
  }, [])

  useEffect(() => {
    void recargar()
  }, [recargar, filtros, orden])

  function cambiarBorrador(cambios: Partial<F>) {
    const nuevo = { ...borradorRef.current, ...cambios }
    borradorRef.current = nuevo
    setBorrador(nuevo)
    if (debounceRef.current) clearTimeout(debounceRef.current)
    debounceRef.current = setTimeout(() => {
      const aplicar: Partial<F> = {}
      for (const clave of configuracionRef.current.clavesDeBorrador) aplicar[clave] = borradorRef.current[clave]
      setFiltros((prev) => ({ ...prev, ...aplicar, pagina: 1 }))
    }, DEMORA_DEBOUNCE_MS)
  }

  function cambiarFiltro(cambios: Partial<Omit<F, 'pagina' | 'tamanio'>>) {
    setFiltros((prev) => ({ ...prev, ...cambios, pagina: 1 }))
  }

  /** Ascendente → descendente → sin orden; vuelve a la página 1 y conserva los filtros. */
  function alternarOrden(columna: string) {
    if (!configuracionRef.current.esColumna(columna)) return
    setOrden((actual) => {
      const siguiente = siguienteOrden(actual, columna)
      return siguiente === null ? null : { columna, direccion: siguiente.direccion }
    })
    setFiltros((prev) => (prev.pagina === 1 ? prev : { ...prev, pagina: 1 }))
  }

  function cambiarPagina(delta: number) {
    setFiltros((prev) => ({ ...prev, pagina: Math.max(1, prev.pagina + delta) }))
  }

  function cambiarTamanio(tamanio: number) {
    setFiltros((prev) => ({ ...prev, tamanio, pagina: 1 }))
  }

  /** El tamaño de página y el orden son preferencias de vista, no filtros: limpiar no los toca. */
  function limpiar() {
    if (debounceRef.current) clearTimeout(debounceRef.current)
    const vacios = configuracionRef.current.filtrosVacios()
    borradorRef.current = vacios
    setBorrador(vacios)
    setFiltros((prev) => ({ ...vacios, tamanio: prev.tamanio }))
  }

  return {
    filtros,
    borrador,
    orden,
    pagina,
    cargando,
    cargaInicialHecha,
    error,
    recargar,
    cambiarBorrador,
    cambiarFiltro,
    alternarOrden,
    cambiarPagina,
    cambiarTamanio,
    limpiar,
  }
}
