/**
 * Reducer puro del carrito del POS (stage-5-pos-ventas, Slice 6, design decisión 12): sin
 * `useState` mutando líneas a mano dentro de `Pos.tsx` — toda mutación pasa por
 * `reducirCarrito`, invocado desde un actualizador funcional (`react-async-state` regla 1). No
 * conoce HTTP ni `ArticuloEscaneado`: `ventas.ts` es quien traduce la respuesta del escaneo a la
 * forma que esta acción espera (`escanear`), manteniendo este módulo testeable sin red ni DOM.
 */

export type LineaCarrito = {
  idArticulo: number
  codigoInterno: string
  nombre: string
  codigoBarra: string | null
  /** Con signo: positivo en una venta normal (TX); negativo cuando la línea es de una
   * devolución (NCX, design decisión 4) — el reducer no impone el signo, solo lo preserva; la
   * pantalla que arma el carrito de una NCX (Slice 7) es quien decide sumar con signo negativo. */
  cantidad: number
}

export type AccionCarrito =
  | { tipo: 'escanear'; linea: Omit<LineaCarrito, 'cantidad'>; cantidad: number }
  | { tipo: 'editarCantidad'; idArticulo: number; cantidad: number }
  | { tipo: 'quitarLinea'; idArticulo: number }
  | { tipo: 'vaciar' }

/**
 * Único punto de mutación del carrito. `escanear` sobre un `idArticulo` ya presente SUMA la
 * cantidad a la línea existente en vez de duplicarla (spec: codigos-barra / "Re-scanning sums
 * quantity instead of duplicating the line") — el resto de las acciones no tiene ese caso
 * especial, cada `idArticulo` es a lo sumo una línea.
 */
export function reducirCarrito(lineas: LineaCarrito[], accion: AccionCarrito): LineaCarrito[] {
  switch (accion.tipo) {
    case 'escanear': {
      const existente = lineas.find((l) => l.idArticulo === accion.linea.idArticulo)
      if (existente) {
        return lineas.map((l) =>
          l.idArticulo === accion.linea.idArticulo ? { ...l, cantidad: l.cantidad + accion.cantidad } : l,
        )
      }
      return [...lineas, { ...accion.linea, cantidad: accion.cantidad }]
    }

    case 'editarCantidad':
      return lineas.map((l) => (l.idArticulo === accion.idArticulo ? { ...l, cantidad: accion.cantidad } : l))

    case 'quitarLinea':
      return lineas.filter((l) => l.idArticulo !== accion.idArticulo)

    case 'vaciar':
      return []
  }
}
