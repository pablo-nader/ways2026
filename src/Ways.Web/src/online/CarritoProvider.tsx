import { useCallback, useEffect, useMemo, useReducer, useState, type ReactNode } from 'react'

import { guardarCarrito, leerCarrito, reducirCarrito, totalDelCarrito, type ArticuloParaElCarrito } from './carrito'
import { CarritoContext } from './CarritoContext'

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

  const quitar = useCallback(
    (idArticulo: number) => {
      const item = items.find((i) => i.idArticulo === idArticulo)
      despachar({ tipo: 'quitar', idArticulo })
      if (item !== undefined) anunciar(`${item.nombre} quitado del carrito`)
    },
    [items, anunciar],
  )

  const cambiarCantidad = useCallback((idArticulo: number, cantidad: number) => {
    despachar({ tipo: 'cambiarCantidad', idArticulo, cantidad })
  }, [])

  const vaciar = useCallback(() => {
    despachar({ tipo: 'vaciar' })
    anunciar('Carrito vaciado')
  }, [anunciar])

  const valor = useMemo(
    () => ({
      items,
      cantidad: items.length,
      total: totalDelCarrito(items),
      cantidadDe: (idArticulo: number) => items.find((i) => i.idArticulo === idArticulo)?.cantidad ?? 0,
      agregar,
      quitar,
      cambiarCantidad,
      vaciar,
    }),
    [items, agregar, quitar, cambiarCantidad, vaciar],
  )

  return (
    <CarritoContext.Provider value={valor}>
      {children}
      <div className="publico-solo-lectura" aria-live="polite" aria-atomic="true">
        <span key={anuncio.numero}>{anuncio.texto}</span>
      </div>
    </CarritoContext.Provider>
  )
}
