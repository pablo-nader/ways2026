import { useState, type KeyboardEvent, type Ref } from 'react'

import type { UnidadVenta } from '../api/tipos'
import { CANTIDAD_MAXIMA, formatearCantidad, normalizarCantidad, parsearCantidad, pasoDelBoton } from './carrito'
import { useAccionesDelCarrito } from './CarritoContext'

type PropsDeControl = {
  idArticulo: number
  nombre: string
  unidadVenta: UnidadVenta
  cantidad: number
  /** Quien lo usa decide qué pasa con el foco cuando "−" quita la línea, porque el botón se va con ella. */
  alQuitar: () => void
  /** Referencia al botón "+", para que quien lo usa pueda llevarle el foco. */
  refMas?: Ref<HTMLButtonElement>
}

function etiquetas(unidadVenta: UnidadVenta, nombre: string) {
  return unidadVenta === 'Peso'
    ? { menos: `Quitar medio kilo de ${nombre}`, mas: `Agregar medio kilo más de ${nombre}` }
    : { menos: `Quitar uno de ${nombre}`, mas: `Agregar uno más de ${nombre}` }
}

function avisoDeCantidadRechazada(unidadVenta: UnidadVenta): string {
  return unidadVenta === 'Peso'
    ? `No se cambió la cantidad. Ingresá una cantidad mayor que 0, hasta ${CANTIDAD_MAXIMA} y con hasta 3 decimales.`
    : `No se cambió la cantidad. Ingresá una cantidad entera, hasta ${CANTIDAD_MAXIMA}.`
}

/**
 * Selector de cantidad de una línea del carrito: − / campo / +. Los botones suman el paso de la
 * unidad (uno, o medio kilo); el campo admite lo que la unidad admite. Lo tipeado se aplica al
 * salir del campo o con Enter, no tecla por tecla: mientras se escribe no se avisa nada, porque
 * "0" o "1," son pasos de una cantidad válida. Si lo tipeado no vale, el campo vuelve a la última
 * cantidad buena, el carrito no cambia y un aviso cortés (`aria-live="polite"`) lo dice; el aviso
 * se va con la próxima acción sobre el campo o los botones. Bajar del mínimo con − quita la línea
 * (`alQuitar`).
 */
export function ControlDeCantidad({ idArticulo, nombre, unidadVenta, cantidad, alQuitar, refMas }: PropsDeControl) {
  const { cambiarCantidad } = useAccionesDelCarrito()
  const [borrador, setBorrador] = useState<string | null>(null)
  const [rechazada, setRechazada] = useState(false)
  const paso = pasoDelBoton(unidadVenta)
  const { menos, mas } = etiquetas(unidadVenta, nombre)

  const restar = () => {
    setRechazada(false)
    const nueva = normalizarCantidad(cantidad - paso)
    if (nueva <= 0) alQuitar()
    else cambiarCantidad(idArticulo, nueva)
  }

  const sumar = () => {
    setRechazada(false)
    cambiarCantidad(idArticulo, normalizarCantidad(cantidad + paso))
  }

  const escribir = (texto: string) => {
    setRechazada(false)
    setBorrador(texto)
  }

  const confirmar = () => {
    if (borrador === null) return
    const escrita = parsearCantidad(unidadVenta, borrador)
    setBorrador(null)
    setRechazada(escrita === null)
    if (escrita !== null) cambiarCantidad(idArticulo, escrita)
  }

  const alPresionarTecla = (evento: KeyboardEvent<HTMLInputElement>) => {
    if (evento.key !== 'Enter') return
    evento.preventDefault()
    confirmar()
  }

  return (
    <div className="publico-cantidad">
      <button type="button" className="publico-cantidad-boton" aria-label={menos} onClick={restar}>
        <span aria-hidden="true">−</span>
      </button>
      <input
        className="publico-cantidad-campo"
        type="text"
        inputMode={unidadVenta === 'Peso' ? 'decimal' : 'numeric'}
        autoComplete="off"
        aria-label={`Cantidad de ${nombre}`}
        value={borrador ?? formatearCantidad(cantidad)}
        onChange={(evento) => escribir(evento.target.value)}
        onBlur={confirmar}
        onKeyDown={alPresionarTecla}
      />
      <button
        ref={refMas}
        type="button"
        className="publico-cantidad-boton"
        aria-label={mas}
        disabled={cantidad >= CANTIDAD_MAXIMA}
        onClick={sumar}
      >
        <span aria-hidden="true">+</span>
      </button>
      <p className="publico-cantidad-ayuda" aria-live="polite">
        {rechazada ? avisoDeCantidadRechazada(unidadVenta) : ''}
      </p>
    </div>
  )
}
