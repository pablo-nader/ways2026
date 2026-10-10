import { useCallback, useEffect, useRef, useState } from 'react'
import { clienteDeArticulos } from '../../api/articulos'
import {
  cambiosAlDesresolver,
  cambiosAlResolverPorCodigo,
  type LineaDeCompraFormulario,
  type ModoDePreciosDeCompra,
} from '../../api/lineaDeCompra'
import type { AlicuotaIvaListado, ArticuloListado } from '../../api/tipos'

/**
 * Busca `codigo` entre los códigos del proveedor: el listado devuelve primero los artículos cuyo
 * código de ESE proveedor coincide exactamente (y completa `codigoProveedor`), así que alcanza con
 * quedarse con el resultado que lo trae igual al buscado, sin distinguir mayúsculas.
 */
export async function buscarArticuloPorCodigoDeProveedor(idProveedor: number, codigo: string): Promise<ArticuloListado | null> {
  const pagina = await clienteDeArticulos.listar(codigo, false, idProveedor)
  const buscado = codigo.toLowerCase()
  return pagina.items.find((a) => a.codigoProveedor?.trim().toLowerCase() === buscado) ?? null
}

type Opciones = {
  lineas: LineaDeCompraFormulario[]
  idProveedor: number | null
  alicuotas: AlicuotaIvaListado[]
  modoDePrecios: ModoDePreciosDeCompra
  /** La pantalla está inerte (guardando, con un diálogo abierto): una respuesta que llega entonces no escribe. */
  disabled: boolean
  /** Sube cada vez que el OPERADOR cambia el proveedor: las filas se re-consultan contra el nuevo. */
  cambiosDeProveedor: number
  /** Aplica cambios a una fila SIN invalidar sus consultas en vuelo: es la propia consulta la que escribe. */
  aplicar: (clave: number, cambios: Partial<LineaDeCompraFormulario>) => void
  /** Se llama cuando una consulta resolvió la fila a un artículo (para mover el foco). */
  alResolver: (clave: number) => void
  /** Avisa si hay alguna consulta en vuelo (guardar espera a que terminen). */
  alCambiarConsultasEnVuelo?: (hayEnVuelo: boolean) => void
}

type EnVuelo = { codigo: string; generacion: number }

function sinClave(prev: Record<number, 'buscando' | 'error'>, clave: number) {
  if (!(clave in prev)) return prev
  const { [clave]: _descartada, ...resto } = prev
  return resto
}

/**
 * Consulta del código del proveedor al salir de la celda "Código". Cada fila tiene su propia
 * generación: editar su Código o su Detalle/artículo, quitarla, lanzar otra consulta o cambiar de
 * proveedor la invalida, y una respuesta tardía no escribe nada. Editar las demás celdas NO la
 * invalida: al llegar, el artículo solo precarga las celdas que el operador no tocó entretanto. Un
 * código se da por consultado recién cuando su resultado se aplicó (encontrado o no): una
 * respuesta descartada o fallida deja volver a consultar al salir de la celda.
 */
export function useConsultaDeCodigos({
  lineas,
  idProveedor,
  alicuotas,
  modoDePrecios,
  disabled,
  cambiosDeProveedor,
  aplicar,
  alResolver,
  alCambiarConsultasEnVuelo,
}: Opciones) {
  const generacionesRef = useRef(new Map<number, number>())
  const enVueloRef = useRef(new Map<number, EnVuelo>())
  const ultimoAvisoRef = useRef(false)
  const montadoRef = useRef(true)
  const lineasRef = useRef(lineas)
  const idProveedorRef = useRef(idProveedor)
  const alicuotasRef = useRef(alicuotas)
  const modoRef = useRef(modoDePrecios)
  const disabledRef = useRef(disabled)
  const aplicarRef = useRef(aplicar)
  const alResolverRef = useRef(alResolver)
  const alCambiarRef = useRef(alCambiarConsultasEnVuelo)
  const [consultas, setConsultas] = useState<Record<number, 'buscando' | 'error'>>({})

  useEffect(() => {
    lineasRef.current = lineas
    idProveedorRef.current = idProveedor
    alicuotasRef.current = alicuotas
    modoRef.current = modoDePrecios
    disabledRef.current = disabled
    aplicarRef.current = aplicar
    alResolverRef.current = alResolver
    alCambiarRef.current = alCambiarConsultasEnVuelo
  }, [lineas, idProveedor, alicuotas, modoDePrecios, disabled, aplicar, alResolver, alCambiarConsultasEnVuelo])

  const avisar = useCallback(() => {
    const hay = enVueloRef.current.size > 0
    if (hay === ultimoAvisoRef.current) return
    ultimoAvisoRef.current = hay
    alCambiarRef.current?.(hay)
  }, [])

  useEffect(() => {
    montadoRef.current = true
    const enVuelo = enVueloRef.current
    return () => {
      montadoRef.current = false
      enVuelo.clear()
      if (ultimoAvisoRef.current) {
        ultimoAvisoRef.current = false
        alCambiarRef.current?.(false)
      }
    }
  }, [])

  const soltar = useCallback(
    (clave: number) => {
      enVueloRef.current.delete(clave)
      setConsultas((prev) => sinClave(prev, clave))
      avisar()
    },
    [avisar],
  )

  /** Una edición del Código o del Detalle/artículo de la fila, o su re-resolución, pasa por acá. */
  const invalidar = useCallback(
    (clave: number) => {
      generacionesRef.current.set(clave, (generacionesRef.current.get(clave) ?? 0) + 1)
      soltar(clave)
    },
    [soltar],
  )

  const olvidar = useCallback(
    (clave: number) => {
      generacionesRef.current.delete(clave)
      soltar(clave)
    },
    [soltar],
  )

  /** `fila` es la fila tal como está al lanzar la consulta; ya no depende del último código consultado. */
  const consultar = useCallback(
    async (fila: LineaDeCompraFormulario, idProveedorConsultado: number) => {
      const clave = fila.clave
      const codigo = fila.codigoProveedor.trim()
      if (enVueloRef.current.get(clave)?.codigo === codigo) return

      const generacion = (generacionesRef.current.get(clave) ?? 0) + 1
      generacionesRef.current.set(clave, generacion)
      enVueloRef.current.set(clave, { codigo, generacion })
      setConsultas((prev) => ({ ...prev, [clave]: 'buscando' }))
      avisar()

      // Nadie la reemplazó ni la invalidó: le toca a ella dejar limpio el estado de la fila.
      const esPropia = () => montadoRef.current && generacionesRef.current.get(clave) === generacion
      const terminar = (estado: 'error' | null) => {
        enVueloRef.current.delete(clave)
        setConsultas((prev) => (estado === null ? sinClave(prev, clave) : { ...prev, [clave]: estado }))
        avisar()
      }

      let articulo: ArticuloListado | null
      try {
        articulo = await buscarArticuloPorCodigoDeProveedor(idProveedorConsultado, codigo)
      } catch {
        if (!esPropia()) return
        // Sin respuesta no se sabe si el código existe: no se marca como consultado y se puede reintentar.
        terminar(idProveedorRef.current === idProveedorConsultado ? 'error' : null)
        return
      }
      if (!esPropia()) return

      const actual = lineasRef.current.find((l) => l.clave === clave)
      terminar(null)
      if (
        !actual ||
        disabledRef.current ||
        idProveedorRef.current !== idProveedorConsultado ||
        actual.codigoProveedor.trim() !== codigo
      ) {
        return
      }

      if (articulo === null) {
        aplicarRef.current(clave, { codigoConsultado: codigo })
        return
      }
      aplicarRef.current(clave, {
        ...cambiosAlResolverPorCodigo(fila, actual, articulo, alicuotasRef.current, modoRef.current),
        codigoConsultado: codigo,
      })
      alResolverRef.current(clave)
    },
    [avisar],
  )

  const alSalirDelCodigo = useCallback(
    (clave: number) => {
      const fila = lineasRef.current.find((l) => l.clave === clave)
      const idProv = idProveedorRef.current
      if (!fila || idProv === null) return
      const codigo = fila.codigoProveedor.trim()
      if (codigo === '' || fila.resueltaPorCodigo || codigo === fila.codigoConsultado) return
      void consultar(fila, idProv)
    },
    [consultar],
  )

  // El proveedor lo cambió el operador: ninguna fila queda atada al anterior. Las resueltas por
  // código pierden el artículo, los marcadores de "ya consultado" se reinician y toda fila con
  // código y sin artículo se consulta de nuevo. Una fila con artículo elegido buscando o creado
  // conserva su artículo y su código.
  const cambiosPrevios = useRef(cambiosDeProveedor)
  useEffect(() => {
    if (cambiosPrevios.current === cambiosDeProveedor) return
    cambiosPrevios.current = cambiosDeProveedor

    for (const [clave, generacion] of generacionesRef.current) generacionesRef.current.set(clave, generacion + 1)
    enVueloRef.current.clear()
    setConsultas({})
    avisar()

    const idProv = idProveedorRef.current
    for (const fila of lineasRef.current) {
      const cambios = fila.resueltaPorCodigo ? cambiosAlDesresolver() : fila.codigoConsultado !== '' ? { codigoConsultado: '' } : null
      if (cambios) aplicarRef.current(fila.clave, cambios)
      const despues = cambios ? { ...fila, ...cambios } : fila
      if (idProv !== null && despues.idArticulo === '' && despues.codigoProveedor.trim() !== '') void consultar(despues, idProv)
    }
  }, [cambiosDeProveedor, avisar, consultar])

  return { consultas, alSalirDelCodigo, invalidar, olvidar }
}
