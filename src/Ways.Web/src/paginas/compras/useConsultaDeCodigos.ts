import { useCallback, useEffect, useRef, useState } from 'react'
import { clienteDeArticulos } from '../../api/articulos'
import { cambiosAlElegirArticulo, type LineaDeCompraFormulario } from '../../api/lineaDeCompra'
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
  /** Aplica cambios a una fila SIN invalidar sus consultas en vuelo: es la propia consulta la que escribe. */
  aplicar: (clave: number, cambios: Partial<LineaDeCompraFormulario>) => void
  /** Se llama cuando una consulta resolvió la fila a un artículo (para mover el foco). */
  alResolver: (clave: number) => void
}

/**
 * Consulta del código del proveedor al salir de la celda "Código". Cada fila tiene su propia
 * generación: cualquier edición posterior de la fila, su baja o una nueva consulta la invalidan,
 * y una respuesta tardía no escribe nada. La consulta corre solo al salir de la celda y solo si el
 * código cambió desde la última vez (o nunca se consultó) y la fila no está ya resuelta por él.
 */
export function useConsultaDeCodigos({ lineas, idProveedor, alicuotas, aplicar, alResolver }: Opciones) {
  const generacionesRef = useRef(new Map<number, number>())
  const lineasRef = useRef(lineas)
  const montadoRef = useRef(true)
  const idProveedorRef = useRef(idProveedor)
  const [consultas, setConsultas] = useState<Record<number, 'buscando' | 'error'>>({})

  useEffect(() => {
    lineasRef.current = lineas
    idProveedorRef.current = idProveedor
  }, [lineas, idProveedor])

  useEffect(() => {
    montadoRef.current = true
    return () => {
      montadoRef.current = false
    }
  }, [])

  /** Toda edición, baja o re-resolución de la fila pasa por acá. */
  const invalidar = useCallback((clave: number) => {
    generacionesRef.current.set(clave, (generacionesRef.current.get(clave) ?? 0) + 1)
    setConsultas((prev) => {
      if (!(clave in prev)) return prev
      const { [clave]: _descartada, ...resto } = prev
      return resto
    })
  }, [])

  const olvidar = useCallback((clave: number) => {
    generacionesRef.current.delete(clave)
    setConsultas((prev) => {
      if (!(clave in prev)) return prev
      const { [clave]: _descartada, ...resto } = prev
      return resto
    })
  }, [])

  const alSalirDelCodigo = useCallback(
    async (clave: number) => {
      const fila = lineasRef.current.find((l) => l.clave === clave)
      if (!fila || idProveedor === null) return
      const codigo = fila.codigoProveedor.trim()
      if (codigo === '' || fila.resueltaPorCodigo || codigo === fila.codigoConsultado) return

      const generacion = (generacionesRef.current.get(clave) ?? 0) + 1
      generacionesRef.current.set(clave, generacion)
      const vigente = () =>
        montadoRef.current && generacionesRef.current.get(clave) === generacion && idProveedorRef.current === idProveedor
      setConsultas((prev) => ({ ...prev, [clave]: 'buscando' }))
      aplicar(clave, { codigoConsultado: codigo })

      const terminar = (estado: 'error' | null) =>
        setConsultas((prev) => {
          if (estado === null) {
            const { [clave]: _descartada, ...resto } = prev
            return resto
          }
          return { ...prev, [clave]: estado }
        })

      try {
        const articulo = await buscarArticuloPorCodigoDeProveedor(idProveedor, codigo)
        if (!vigente()) return
        const actual = lineasRef.current.find((l) => l.clave === clave)
        if (!actual) return
        terminar(null)
        if (articulo !== null) {
          aplicar(clave, cambiosAlElegirArticulo(actual, articulo, alicuotas, 'codigo'))
          alResolver(clave)
        }
      } catch {
        if (!vigente()) return
        // Sin respuesta no se sabe si el código existe: se deja volver a intentar al salir de la celda.
        aplicar(clave, { codigoConsultado: '' })
        terminar('error')
      }
    },
    [idProveedor, alicuotas, aplicar, alResolver],
  )

  return { consultas, alSalirDelCodigo, invalidar, olvidar }
}
