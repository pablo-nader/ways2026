import { useCallback, useEffect, useRef, useState } from 'react'
import {
  TAMANIO_DE_PAGINA_POR_DEFECTO,
  hayFiltrosEnPadron,
  type ConsultaDePadron,
  type FiltrosDePadron,
} from '../../api/padron'
import { siguienteOrden } from '../grilla/ordenamiento'

export const DEMORA_DE_FILTRO_DE_PADRON_MS = 300

type Opciones<C extends string> = {
  /** Una clave de `orden` que el servidor acepta: una columna que no es de datos (Acciones) no ordena. */
  esColumnaOrdenable: (columna: string) => columna is C
  /** `true` mientras hay una escritura en vuelo. Un filtro tipeado antes de que empezara espera a que termine: aplicarlo
   * pediría un listado que invalida la generación de la escritura. */
  ocupado: () => boolean
}

/**
 * Estado de la consulta de un padrón que filtra, ordena y pagina en el servidor: qué está aplicado (`consulta`), qué
 * muestran los controles de filtro (`borrador`) y cómo se pasa de uno a otro. El hook no pide nada: la pantalla vuelve a
 * pedir el listado cuando cambia `consulta`, con su propia generación. Cualquier cambio de filtro, de orden o de tamaño
 * vuelve a la página 1.
 *
 * Los textos se aplican con demora (así tipear no pide una vez por tecla) y los selectores en el acto, y al aplicar se
 * aplica todo el borrador junto. `consultaRef` es la última consulta aplicada, que es la que recarga una escritura.
 */
export function usePadronEnServidor<F extends FiltrosDePadron, C extends string>(
  filtrosVacios: F,
  opciones: Opciones<C>,
) {
  const inicial: ConsultaDePadron<F, C> = {
    filtros: filtrosVacios,
    orden: null,
    pagina: 1,
    tamanio: TAMANIO_DE_PAGINA_POR_DEFECTO,
  }
  const [consulta, setConsulta] = useState(inicial)
  const [borrador, setBorrador] = useState(filtrosVacios)
  const consultaRef = useRef(consulta)
  const borradorRef = useRef(borrador)
  const temporizador = useRef<ReturnType<typeof setTimeout> | null>(null)
  const opcionesRef = useRef(opciones)

  useEffect(() => {
    opcionesRef.current = opciones
  })

  useEffect(
    () => () => {
      if (temporizador.current) clearTimeout(temporizador.current)
    },
    [],
  )

  const aplicar = useCallback((nueva: ConsultaDePadron<F, C>) => {
    consultaRef.current = nueva
    setConsulta(nueva)
  }, [])

  const cancelarTemporizador = useCallback(() => {
    if (temporizador.current) clearTimeout(temporizador.current)
    temporizador.current = null
  }, [])

  const aplicarBorrador = useCallback(() => {
    cancelarTemporizador()
    aplicar({ ...consultaRef.current, filtros: borradorRef.current, pagina: 1 })
  }, [aplicar, cancelarTemporizador])

  const programar = useCallback(() => {
    cancelarTemporizador()
    temporizador.current = setTimeout(function alVencer() {
      temporizador.current = null
      if (opcionesRef.current.ocupado()) {
        temporizador.current = setTimeout(alVencer, DEMORA_DE_FILTRO_DE_PADRON_MS)

        return
      }

      aplicarBorrador()
    }, DEMORA_DE_FILTRO_DE_PADRON_MS)
  }, [aplicarBorrador, cancelarTemporizador])

  const cambiarFiltros = useCallback(
    (cambios: Partial<F>, enElActo = false) => {
      borradorRef.current = { ...borradorRef.current, ...cambios }
      setBorrador(borradorRef.current)

      if (enElActo) aplicarBorrador()
      else programar()
    },
    [aplicarBorrador, programar],
  )

  /** Ascendente, luego descendente, luego el orden de siempre. */
  const alternarOrden = useCallback(
    (columna: string) => {
      if (!opcionesRef.current.esColumnaOrdenable(columna)) return

      const actual = consultaRef.current
      const siguiente = siguienteOrden(actual.orden, columna)
      aplicar({
        ...actual,
        orden: siguiente === null ? null : { columna, direccion: siguiente.direccion },
        pagina: 1,
      })
    },
    [aplicar],
  )

  const irAPagina = useCallback(
    (pagina: number) => aplicar({ ...consultaRef.current, pagina: Math.max(1, pagina) }),
    [aplicar],
  )

  const cambiarPagina = useCallback(
    (delta: number) => irAPagina(consultaRef.current.pagina + delta),
    [irAPagina],
  )

  const cambiarTamanio = useCallback(
    (tamanio: number) => aplicar({ ...consultaRef.current, tamanio, pagina: 1 }),
    [aplicar],
  )

  /** El tamaño de página y el orden son preferencias de vista: limpiar solo vacía los filtros. */
  const limpiar = useCallback(() => {
    borradorRef.current = filtrosVacios
    setBorrador(filtrosVacios)
    aplicarBorrador()
  }, [aplicarBorrador, filtrosVacios])

  return {
    consulta,
    consultaRef,
    borrador,
    hayFiltros: hayFiltrosEnPadron(borrador),
    cambiarFiltros,
    alternarOrden,
    irAPagina,
    cambiarPagina,
    cambiarTamanio,
    limpiar,
  }
}
