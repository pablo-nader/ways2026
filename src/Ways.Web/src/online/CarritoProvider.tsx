import { useCallback, useEffect, useMemo, useReducer, useRef, useState, type ReactNode } from 'react'

import {
  claveDelCarrito,
  guardarCarrito,
  leerCarrito,
  reducirCarrito,
  totalDelCarrito,
  type ArticuloParaElCarrito,
  type ComboParaElCarrito,
} from './carrito'
import { AccionesDelCarritoContext, CarritoContext } from './CarritoContext'

type AlmacenDelCarrito = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>

function almacenDelNavegador(): AlmacenDelCarrito | null {
  try {
    return window.localStorage
  } catch {
    return null
  }
}

type Anuncio = { texto: string; numero: number }

/**
 * Dueño del carrito: lo lee del navegador al montar y lo guarda en cada cambio, bajo una clave que
 * incluye el host (dos comercios en el mismo navegador no se mezclan). Si otra pestaña del mismo
 * comercio lo cambia (evento `storage`), adopta ese carrito entero, ya validado. No hay fusión: se
 * guarda el carrito completo, así que si dos pestañas cambian algo a la vez gana la última que
 * escribe, y lo que la otra tenía sin guardar se pierde. También es dueño de la región
 * `aria-live` que anuncia lo que se agregó o se quitó, porque ningún botón sobrevive a ese cambio
 * de estado (el de "Agregar" se convierte en el selector de cantidad). El cambio que viene de otra
 * pestaña no se anuncia.
 */
export function CarritoProvider({
  children,
  host = window.location.host,
  almacen,
}: {
  children: ReactNode
  host?: string
  almacen?: AlmacenDelCarrito | null
}) {
  const [almacenElegido] = useState(() => (almacen === undefined ? almacenDelNavegador() : almacen))
  const [items, despachar] = useReducer(reducirCarrito, undefined, () => leerCarrito(almacenElegido, host))
  const [anuncio, setAnuncio] = useState<Anuncio>({ texto: '', numero: 0 })
  // Última lista de artículos con la que se sincronizó: sirve para volver a hacerlo tras adoptar un carrito de otra pestaña.
  const ultimosArticulos = useRef<readonly ArticuloParaElCarrito[]>([])
  // Ídem para los combos; `null` mientras no hubo una lectura buena (entonces no se sabe cuáles siguen disponibles).
  const ultimosCombos = useRef<readonly ComboParaElCarrito[] | null>(null)
  // Marca que el próximo cambio del carrito viene de otra pestaña (adoptado y sincronizado en el mismo
  // render): no se escribe de vuelta. Si dos pestañas tienen listas distintas, escribir lo sincronizado
  // haría que cada una pisara a la otra sin fin.
  const adoptadoDeOtraPestana = useRef(false)

  useEffect(() => {
    if (adoptadoDeOtraPestana.current) {
      adoptadoDeOtraPestana.current = false

      return
    }
    guardarCarrito(almacenElegido, host, items)
  }, [almacenElegido, host, items])

  useEffect(() => {
    if (almacenElegido === null) return
    const clave = claveDelCarrito(host)
    const alCambiarElAlmacen = (evento: StorageEvent) => {
      // `key` nulo es `localStorage.clear()` desde otra pestaña.
      if (evento.storageArea !== almacenElegido || (evento.key !== null && evento.key !== clave)) return
      adoptadoDeOtraPestana.current = true
      despachar({ tipo: 'reemplazar', items: leerCarrito(almacenElegido, host) })
      // El carrito adoptado puede traer precios más viejos que la lista que esta pestaña ya leyó.
      despachar({ tipo: 'sincronizar', articulos: ultimosArticulos.current })
      if (ultimosCombos.current !== null) despachar({ tipo: 'sincronizarCombos', combos: ultimosCombos.current })
    }
    window.addEventListener('storage', alCambiarElAlmacen)

    return () => window.removeEventListener('storage', alCambiarElAlmacen)
  }, [almacenElegido, host])

  const anunciar = useCallback((texto: string) => {
    setAnuncio((prev) => ({ texto, numero: prev.numero + 1 }))
  }, [])

  const agregar = useCallback(
    (articulo: ArticuloParaElCarrito, cantidad = 1) => {
      despachar({ tipo: 'agregar', articulo, cantidad })
      anunciar(`${articulo.nombre} agregado al carrito`)
    },
    [anunciar],
  )

  // `quitar` anuncia el nombre de la línea que se va; lo lee de acá para conservar su identidad entre renders.
  const itemsRef = useRef(items)
  useEffect(() => {
    itemsRef.current = items
  }, [items])

  const quitar = useCallback(
    (idArticulo: number) => {
      const item = itemsRef.current.find((i) => i.tipo === 'articulo' && i.idArticulo === idArticulo)
      despachar({ tipo: 'quitar', idArticulo })
      if (item !== undefined) anunciar(`${item.nombre} quitado del carrito`)
    },
    [anunciar],
  )

  const cambiarCantidad = useCallback((idArticulo: number, cantidad: number) => {
    despachar({ tipo: 'cambiarCantidad', idArticulo, cantidad })
  }, [])

  const agregarCombo = useCallback(
    (combo: ComboParaElCarrito, cantidad = 1) => {
      despachar({ tipo: 'agregarCombo', combo, cantidad })
      anunciar(`${combo.nombre} agregado al carrito`)
    },
    [anunciar],
  )

  const quitarCombo = useCallback(
    (idCombo: number) => {
      const item = itemsRef.current.find((i) => i.tipo === 'combo' && i.idCombo === idCombo)
      despachar({ tipo: 'quitarCombo', idCombo })
      if (item !== undefined) anunciar(`${item.nombre} quitado del carrito`)
    },
    [anunciar],
  )

  const cambiarCantidadCombo = useCallback((idCombo: number, cantidad: number) => {
    despachar({ tipo: 'cambiarCantidadCombo', idCombo, cantidad })
  }, [])

  const sincronizarCombos = useCallback((combos: readonly ComboParaElCarrito[]) => {
    ultimosCombos.current = combos
    despachar({ tipo: 'sincronizarCombos', combos })
  }, [])

  const sincronizar = useCallback((articulos: readonly ArticuloParaElCarrito[]) => {
    ultimosArticulos.current = articulos
    despachar({ tipo: 'sincronizar', articulos })
  }, [])

  const vaciar = useCallback(() => {
    despachar({ tipo: 'vaciar' })
    anunciar('Carrito vaciado')
  }, [anunciar])

  const acciones = useMemo(
    () => ({
      agregar,
      quitar,
      cambiarCantidad,
      sincronizar,
      agregarCombo,
      quitarCombo,
      cambiarCantidadCombo,
      sincronizarCombos,
      vaciar,
    }),
    [agregar, quitar, cambiarCantidad, sincronizar, agregarCombo, quitarCombo, cambiarCantidadCombo, sincronizarCombos, vaciar],
  )

  const valor = useMemo(
    () => ({
      ...acciones,
      items,
      cantidad: items.length,
      total: totalDelCarrito(items),
      cantidades: new Map(items.flatMap((i) => (i.tipo === 'articulo' ? [[i.idArticulo, i.cantidad] as const] : []))),
      cantidadesDeCombos: new Map(items.flatMap((i) => (i.tipo === 'combo' ? [[i.idCombo, i.cantidad] as const] : []))),
    }),
    [items, acciones],
  )

  return (
    <AccionesDelCarritoContext.Provider value={acciones}>
      <CarritoContext.Provider value={valor}>
        {children}
        <div className="publico-solo-lectura" aria-live="polite" aria-atomic="true">
          <span key={anuncio.numero}>{anuncio.texto}</span>
        </div>
      </CarritoContext.Provider>
    </AccionesDelCarritoContext.Provider>
  )
}
