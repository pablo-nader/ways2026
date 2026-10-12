import { useCallback, useEffect, useMemo, useReducer, useRef, useState, type ReactNode } from 'react'

import { guardarCarrito, leerCarrito, reducirCarrito, totalDelCarrito, type ArticuloParaElCarrito } from './carrito'
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
 * incluye el host (dos comercios en el mismo navegador no se mezclan). También es dueño de la
 * región `aria-live` que anuncia lo que se agregó o se quitó, porque ningún botón sobrevive a ese
 * cambio de estado (el de "Agregar" se convierte en el selector de cantidad).
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

  useEffect(() => {
    guardarCarrito(almacenElegido, host, items)
  }, [almacenElegido, host, items])

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
      const item = itemsRef.current.find((i) => i.idArticulo === idArticulo)
      despachar({ tipo: 'quitar', idArticulo })
      if (item !== undefined) anunciar(`${item.nombre} quitado del carrito`)
    },
    [anunciar],
  )

  const cambiarCantidad = useCallback((idArticulo: number, cantidad: number) => {
    despachar({ tipo: 'cambiarCantidad', idArticulo, cantidad })
  }, [])

  const sincronizar = useCallback((articulos: readonly ArticuloParaElCarrito[]) => {
    despachar({ tipo: 'sincronizar', articulos })
  }, [])

  const vaciar = useCallback(() => {
    despachar({ tipo: 'vaciar' })
    anunciar('Carrito vaciado')
  }, [anunciar])

  const acciones = useMemo(
    () => ({ agregar, quitar, cambiarCantidad, sincronizar, vaciar }),
    [agregar, quitar, cambiarCantidad, sincronizar, vaciar],
  )

  const valor = useMemo(
    () => ({
      ...acciones,
      items,
      cantidad: items.length,
      total: totalDelCarrito(items),
      cantidades: new Map(items.map((i) => [i.idArticulo, i.cantidad])),
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
