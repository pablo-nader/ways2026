import { useEffect, useMemo, useState } from 'react'
import { clienteDeCompras, type ParametrosDeDuplicados } from '../../api/compras'
import type { CompraDuplicada } from '../../api/tipos'
import { valorMedioLleno } from './numeroDeComprobante'

/** Tiempo sin tipear antes de consultar: el número se emite en cada tecla y no vale una consulta por dígito. */
export const ESPERA_DE_DUPLICADOS_MS = 400

type Entrada = {
  idProveedor: number | ''
  idTipoComprobante: number | ''
  numeroExterno: string
  excluirId: number | null
  /** Solo se consulta donde el número todavía se puede cambiar. */
  activo: boolean
}

/** Qué se consulta, o `null` si todavía no hay con qué: falta proveedor, tipo, o el número está vacío o a medio escribir. */
export function pedidoDeDuplicados(entrada: Entrada): ParametrosDeDuplicados | null {
  const numeroExterno = entrada.numeroExterno.trim()
  if (!entrada.activo || entrada.idProveedor === '' || entrada.idTipoComprobante === '') return null
  if (numeroExterno === '' || valorMedioLleno(numeroExterno)) return null
  return {
    idProveedor: entrada.idProveedor,
    idTipoComprobante: entrada.idTipoComprobante,
    numeroExterno,
    excluirId: entrada.excluirId,
  }
}

/**
 * Compras que ya tienen el número del comprobante en edición (mismo proveedor y tipo, sin contar la propia).
 * Es un aviso: una consulta que falla no muestra nada ni bloquea el guardado, y el servidor sigue rechazando un
 * duplicado exacto al guardar.
 *
 * Cada respuesta se guarda con la clave del pedido que la originó y solo se devuelve mientras esa clave sea la
 * vigente: la clave hace de token (una respuesta tardía de un número anterior queda guardada pero nunca se
 * muestra, y el aviso del número viejo desaparece en cuanto el número cambia, sin esperar la consulta nueva).
 */
export function useDuplicadosDeCompra(entrada: Entrada): CompraDuplicada[] {
  const { idProveedor, idTipoComprobante, numeroExterno, excluirId, activo } = entrada
  const pedido = useMemo(
    () => pedidoDeDuplicados({ idProveedor, idTipoComprobante, numeroExterno, excluirId, activo }),
    [idProveedor, idTipoComprobante, numeroExterno, excluirId, activo],
  )
  const clave = pedido === null ? null : JSON.stringify(pedido)

  const [respuesta, setRespuesta] = useState<{ clave: string; duplicados: CompraDuplicada[] } | null>(null)

  useEffect(() => {
    if (pedido === null || clave === null) return
    const temporizador = setTimeout(() => {
      clienteDeCompras.buscarDuplicados(pedido).then(
        (duplicados) => setRespuesta({ clave, duplicados: Array.isArray(duplicados) ? duplicados : [] }),
        () => {},
      )
    }, ESPERA_DE_DUPLICADOS_MS)

    return () => clearTimeout(temporizador)
  }, [pedido, clave])

  return respuesta !== null && respuesta.clave === clave ? respuesta.duplicados : []
}
