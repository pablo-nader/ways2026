/**
 * Reducer puro del carrito del POS (stage-5-pos-ventas, Slice 6, design decisión 12): sin
 * `useState` mutando líneas a mano dentro de `Pos.tsx` — toda mutación pasa por
 * `reducirCarrito`, invocado desde un actualizador funcional (`react-async-state` regla 1). No
 * conoce HTTP ni `ArticuloEscaneado`: `ventas.ts` es quien traduce la respuesta del escaneo a la
 * forma que esta acción espera (`escanear`), manteniendo este módulo testeable sin red ni DOM.
 *
 * La identidad de una línea es `idLinea`, no `idArticulo`: un artículo que no acumula en venta
 * (`acumulaEnVenta: false`) puede ocupar varias líneas del mismo ticket, así que toda operación
 * sobre una línea (cantidad, ajuste, quitar) y todo estado por línea en la pantalla (precio
 * resuelto, edición en curso, `key` de React) se indexa por `idLinea`.
 */

export type LineaCarrito = {
  /** Identidad estable de la línea dentro del ticket. La genera quien despacha el escaneo
   * (`nuevoIdLinea`), nunca el reducer, para que este siga siendo puro. */
  idLinea: string
  idArticulo: number
  codigoInterno: string
  nombre: string
  codigoBarra: string | null
  /** `true`: volver a agregar el artículo suma cantidad a su línea; `false`: abre otra línea. */
  acumulaEnVenta: boolean
  /** Con signo: positivo en una venta normal (TX); negativo cuando la línea es de una
   * devolución (NCX, design decisión 4) — el reducer no impone el signo, solo lo preserva; la
   * pantalla que arma el carrito de una NCX (Slice 7) es quien decide sumar con signo negativo. */
  cantidad: number
  /** Ajuste manual de precio en porcentaje con signo (negativo = descuento, positivo = recargo).
   * Ausente o `null` = línea sin ajuste. Un borrador guardado antes de que existiera el campo
   * restaura sin ajuste, sin necesitar migración. */
  ajusteManualPorcentaje?: number | null
}

/** Datos de un artículo listo para agregarse al carrito: todo menos la identidad de la línea y
 * la cantidad, que viajan aparte en la acción `escanear`. */
export type ArticuloParaCarrito = Omit<LineaCarrito, 'idLinea' | 'cantidad' | 'ajusteManualPorcentaje'>

export type AccionCarrito =
  /** `idLinea` es la identidad que toma la línea SI el escaneo abre una nueva; se ignora cuando
   * suma sobre una existente. */
  | { tipo: 'escanear'; linea: ArticuloParaCarrito; cantidad: number; idLinea: string }
  | { tipo: 'editarCantidad'; idLinea: string; cantidad: number }
  | { tipo: 'quitarLinea'; idLinea: string }
  | { tipo: 'fijarAjusteManual'; idLinea: string; porcentaje: number }
  | { tipo: 'quitarAjusteManual'; idLinea: string }
  | { tipo: 'vaciar' }

/**
 * Línea sobre la que sumaría un escaneo de `articulo`, o `null` si el escaneo abre una línea
 * nueva. Solo acumula un artículo con `acumulaEnVenta`, y siempre sobre la ÚLTIMA línea de ese
 * artículo: es la que el cajero acaba de ver crecer, y con el flag activo normalmente es la única.
 * Decide por el flag del escaneo entrante (dato fresco del catálogo), no por el de la línea ya
 * cargada.
 */
export function idLineaDestinoDeEscaneo(lineas: readonly LineaCarrito[], articulo: Pick<ArticuloParaCarrito, 'idArticulo' | 'acumulaEnVenta'>): string | null {
  if (!articulo.acumulaEnVenta) return null
  for (let i = lineas.length - 1; i >= 0; i--) {
    if (lineas[i].idArticulo === articulo.idArticulo) return lineas[i].idLinea
  }
  return null
}

/**
 * Único punto de mutación del carrito. `escanear` de un artículo que acumula y ya está en el
 * carrito SUMA la cantidad a su línea (spec: codigos-barra / "Re-scanning sums quantity instead
 * of duplicating the line"); un artículo que no acumula siempre agrega una línea nueva. El resto
 * de las acciones apunta a una sola línea por `idLinea`.
 *
 * El ajuste manual vive en la línea: re-escanear y editar la cantidad lo conservan (los dos
 * construyen sobre `...l`) y quitar la línea se lo lleva. `fijarAjusteManual` no valida el
 * porcentaje (como `editarCantidad` no valida la cantidad): la entrada se valida antes, en
 * `validarPorcentajeDeAjuste`.
 */
export function reducirCarrito(lineas: LineaCarrito[], accion: AccionCarrito): LineaCarrito[] {
  switch (accion.tipo) {
    case 'escanear': {
      const destino = idLineaDestinoDeEscaneo(lineas, accion.linea)
      if (destino !== null) {
        return lineas.map((l) => (l.idLinea === destino ? { ...l, cantidad: l.cantidad + accion.cantidad } : l))
      }
      return [...lineas, { ...accion.linea, idLinea: accion.idLinea, cantidad: accion.cantidad }]
    }

    case 'editarCantidad':
      return lineas.map((l) => (l.idLinea === accion.idLinea ? { ...l, cantidad: accion.cantidad } : l))

    case 'quitarLinea':
      return lineas.filter((l) => l.idLinea !== accion.idLinea)

    case 'fijarAjusteManual':
      return lineas.map((l) => (l.idLinea === accion.idLinea ? { ...l, ajusteManualPorcentaje: accion.porcentaje } : l))

    case 'quitarAjusteManual':
      return lineas.map((l) => {
        if (l.idLinea !== accion.idLinea) return l
        const { ajusteManualPorcentaje: _omitido, ...sinAjuste } = l
        return sinAjuste
      })

    case 'vaciar':
      return []
  }
}

let contadorDeLineas = 0

/** Id nuevo para una línea del carrito. Único dentro de la sesión y también frente a los ids de un
 * borrador restaurado de otra sesión (por eso no es solo un contador). */
export function nuevoIdLinea(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') return crypto.randomUUID()
  contadorDeLineas += 1
  return `l-${Date.now().toString(36)}-${contadorDeLineas}-${Math.random().toString(36).slice(2, 10)}`
}

/**
 * Lleva las líneas y las ediciones de cantidad en curso de un borrador persistido a la forma
 * actual. Un borrador guardado antes de `idLinea`/`acumulaEnVenta` trae líneas sin id (se les
 * asigna uno con `generarId`), sin flag (se asume `true`, el comportamiento de siempre) y las
 * ediciones indexadas por `idArticulo`: en ese formato cada artículo ocupaba a lo sumo una línea,
 * así que la clave se traslada sin ambigüedad al id nuevo de esa línea. Una edición cuya línea ya
 * no existe se descarta.
 */
export function migrarLineasDeBorrador(
  lineas: readonly unknown[],
  cantidadesEnEdicion: Readonly<Record<string, string>>,
  generarId: () => string = nuevoIdLinea,
): { lineas: LineaCarrito[]; cantidadesEnEdicion: Record<string, string> } {
  const idPorArticuloMigrado = new Map<string, string>()
  const migradas = lineas.map((cruda) => {
    const l = cruda as Omit<LineaCarrito, 'idLinea' | 'acumulaEnVenta'> & { idLinea?: unknown; acumulaEnVenta?: unknown }
    const idLinea = typeof l.idLinea === 'string' && l.idLinea !== '' ? l.idLinea : null
    const linea: LineaCarrito = {
      ...l,
      idLinea: idLinea ?? generarId(),
      acumulaEnVenta: l.acumulaEnVenta !== false,
    }
    if (idLinea === null) idPorArticuloMigrado.set(String(l.idArticulo), linea.idLinea)
    return linea
  })

  const idsVigentes = new Set(migradas.map((l) => l.idLinea))
  const ediciones: Record<string, string> = {}
  for (const [clave, texto] of Object.entries(cantidadesEnEdicion)) {
    const destino = idsVigentes.has(clave) ? clave : idPorArticuloMigrado.get(clave)
    if (destino !== undefined) ediciones[destino] = texto
  }
  return { lineas: migradas, cantidadesEnEdicion: ediciones }
}
