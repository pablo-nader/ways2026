import { useEffect, useRef, type Dispatch, type SetStateAction } from 'react'
import { clienteDeArticulos } from '../api/articulos'
import type { UnidadVenta } from '../api/tipos'

type LineaConArticulo = { idArticulo: number | ''; unidadVenta?: UnidadVenta }

/**
 * Las líneas de un documento reabierto (remito, presupuesto, orden de compra, compra) traen solo
 * el `idArticulo`: el renglón guardado no incluye la unidad de venta. Con `habilitado`, este hook
 * la pide una vez por artículo, guarda la respuesta y la completa en las líneas que no la tienen.
 * Cuando la pantalla reemplaza `lineas` (al guardar o al volver a leer el documento), la vuelve a
 * aplicar desde lo ya guardado, sin otra consulta. Una línea cuyo artículo se eligió en pantalla ya
 * trae la suya y no genera ninguna consulta.
 *
 * Sin `habilitado` (un documento de solo lectura) no consulta ni completa nada. Si una consulta
 * falla, esa línea queda sin unidad, que se trata como peso, y ese artículo no se vuelve a pedir:
 * nunca bloquea. Cada respuesta solo completa líneas de SU artículo que siguen sin unidad, así que
 * una respuesta tardía no pisa lo que el operador eligió mientras tanto.
 */
export function useUnidadesDeVentaDeLineas<L extends LineaConArticulo>(
  lineas: L[],
  setLineas: Dispatch<SetStateAction<L[]>>,
  habilitado: boolean,
): void {
  const unidadesRef = useRef(new Map<number, UnidadVenta>())
  const consultadosRef = useRef(new Set<number>())
  const montadoRef = useRef(true)

  useEffect(() => {
    montadoRef.current = true
    return () => {
      montadoRef.current = false
    }
  }, [])

  useEffect(() => {
    if (!habilitado) return

    const completarDesdeLoYaConocido = (prev: L[]) =>
      prev.map((l) => {
        const unidadVenta = l.idArticulo === '' || l.unidadVenta !== undefined ? undefined : unidadesRef.current.get(l.idArticulo)
        return unidadVenta === undefined ? l : { ...l, unidadVenta }
      })

    if (lineas.some((l) => l.idArticulo !== '' && l.unidadVenta === undefined && unidadesRef.current.has(l.idArticulo))) {
      setLineas(completarDesdeLoYaConocido)
    }

    const pendientes = new Set<number>()
    for (const linea of lineas) {
      if (linea.idArticulo !== '' && linea.unidadVenta === undefined && !consultadosRef.current.has(linea.idArticulo)) {
        pendientes.add(linea.idArticulo)
      }
    }

    for (const idArticulo of pendientes) {
      consultadosRef.current.add(idArticulo)
      Promise.resolve()
        .then(() => clienteDeArticulos.obtener(idArticulo))
        .then((articulo) => {
          const unidadVenta = articulo?.unidadVenta
          if (!montadoRef.current || unidadVenta === undefined) return
          unidadesRef.current.set(idArticulo, unidadVenta)
          setLineas((prev) =>
            prev.map((l) => (l.idArticulo === idArticulo && l.unidadVenta === undefined ? { ...l, unidadVenta } : l)),
          )
        })
        .catch(() => {})
    }
  }, [lineas, setLineas, habilitado])
}
