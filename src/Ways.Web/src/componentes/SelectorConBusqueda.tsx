import { useEffect, useId, useLayoutEffect, useRef, useState } from 'react'
import type { CSSProperties, KeyboardEvent } from 'react'
import { contieneSinAcentos } from '../formato/texto'

export type OpcionDeSelector = {
  id: number | string
  etiqueta: string
}

const ALTO_MAXIMO_DE_LISTA_PX = 320
const MARGEN_CON_LA_VENTANA_PX = 8
const ESPACIO_MINIMO_ABAJO_PX = 180
const ESPERA_DE_TIPEO_MS = 300

/** Las opciones cuya etiqueta contiene `texto`, sin distinguir mayúsculas ni acentos. Un texto vacío no filtra. */
export function filtrarOpciones<T extends OpcionDeSelector>(opciones: readonly T[], texto: string): T[] {
  return opciones.filter((o) => contieneSinAcentos(o.etiqueta, texto))
}

/** Las remotas primero y, a continuación, las locales que no estén ya en las remotas: para un mismo id gana la remota. */
export function combinarResultados<T extends OpcionDeSelector>(remotas: readonly T[], locales: readonly T[]): T[] {
  const vistos = new Set<T['id']>()
  const resultado: T[] = []
  for (const opcion of [...remotas, ...locales]) {
    if (vistos.has(opcion.id)) continue
    vistos.add(opcion.id)
    resultado.push(opcion)
  }
  return resultado
}

type ResultadoRemoto<T> = { texto: string; opciones: readonly T[]; error: string | null }

type Props<T extends OpcionDeSelector> = {
  /** Nombre accesible de la caja. */
  nombreAccesible: string
  /** Opciones conocidas: se filtran localmente mientras se tipea. */
  opciones: readonly T[]
  /** Id de la opción elegida; `null` si no hay ninguna. */
  valor: T['id'] | null
  onChange: (opcion: T) => void
  /** Coincidencias que la etiqueta no alcanza a ver (otros campos, una copia local): se suman al instante, sin esperar a `alBuscar`. */
  buscarLocal?: (texto: string) => readonly T[]
  /** Consulta remota con el texto tipeado, con espera de tipeo y respuesta tardía descartada. Si rechaza, el mensaje del
   * error se muestra solo cuando no hay ninguna coincidencia local. */
  alBuscar?: (texto: string) => Promise<readonly T[]>
  esperaMs?: number
  /** Texto cuando no hay ninguna coincidencia y no se está buscando. */
  textoSinResultados?: string | ((texto: string) => string)
  /** El usuario canceló con Escape (con la lista abierta o cerrada). Perder el foco no lo dispara. */
  alCancelar?: () => void
  disabled?: boolean
  id?: string
  className?: string
  placeholder?: string
}

type PosicionDeLista = { estilo: CSSProperties }

function calcularPosicion(caja: DOMRect): PosicionDeLista {
  const alto = window.innerHeight
  const ancho = window.innerWidth
  const abajo = alto - caja.bottom - MARGEN_CON_LA_VENTANA_PX
  const arriba = caja.top - MARGEN_CON_LA_VENTANA_PX
  const haciaArriba = abajo < ESPACIO_MINIMO_ABAJO_PX && arriba > abajo
  const disponible = Math.max(haciaArriba ? arriba : abajo, 0)
  return {
    estilo: {
      position: 'fixed',
      left: caja.left,
      minWidth: caja.width,
      maxWidth: Math.max(ancho - caja.left - MARGEN_CON_LA_VENTANA_PX, caja.width),
      ...(haciaArriba ? { bottom: alto - caja.top } : { top: caja.bottom }),
      maxHeight: Math.min(ALTO_MAXIMO_DE_LISTA_PX, disponible),
      overflowY: 'auto',
      zIndex: 1080,
    },
  }
}

/**
 * Select con aspecto de `form-select` en el que se puede tipear para filtrar las opciones por
 * "contiene" (sin distinguir mayúsculas ni acentos). Combobox WAI-ARIA con listbox: flechas, Home y
 * End mueven la opción activa; Enter elige; Escape cierra y restaura la etiqueta elegida; perder el
 * foco cierra sin elegir. Tipear nunca cambia el valor por sí solo. La lista se ancla a la ventana y
 * se desplaza por dentro si no entra.
 */
export function SelectorConBusqueda<T extends OpcionDeSelector>({
  nombreAccesible,
  opciones,
  valor,
  onChange,
  buscarLocal,
  alBuscar,
  esperaMs = ESPERA_DE_TIPEO_MS,
  textoSinResultados = 'Sin resultados.',
  alCancelar,
  disabled = false,
  id,
  className,
  placeholder,
}: Props<T>) {
  const idLista = useId()
  const cajaRef = useRef<HTMLInputElement>(null)
  const listaRef = useRef<HTMLDivElement>(null)
  // Lo que escribió el usuario; `null` mientras no está editando (la caja muestra la etiqueta elegida).
  const [texto, setTexto] = useState<string | null>(null)
  const [abierta, setAbierta] = useState(false)
  const [idActivo, setIdActivo] = useState<T['id'] | null>(null)
  const [remoto, setRemoto] = useState<ResultadoRemoto<T> | null>(null)
  const [posicion, setPosicion] = useState<PosicionDeLista | null>(null)
  // Cada corrida del efecto de consulta toma una generación; cerrar o tipear de nuevo corre el efecto
  // otra vez e invalida la anterior: una respuesta tardía nunca pisa lo que hay en pantalla.
  const generacionRef = useRef(0)
  const alBuscarRef = useRef(alBuscar)
  alBuscarRef.current = alBuscar
  const hayBusquedaRemota = alBuscar !== undefined

  const buscado = (texto ?? '').trim()
  const visible = abierta && !disabled

  useEffect(() => {
    const miGeneracion = (generacionRef.current += 1)
    const consulta = alBuscarRef.current
    if (!visible || buscado === '' || consulta === undefined) return

    const temporizador = setTimeout(() => {
      consulta(buscado)
        .then((opcionesRemotas) => {
          if (generacionRef.current !== miGeneracion) return
          setRemoto({ texto: buscado, opciones: opcionesRemotas, error: null })
        })
        .catch((e: unknown) => {
          if (generacionRef.current !== miGeneracion) return
          setRemoto({ texto: buscado, opciones: [], error: e instanceof Error && e.message !== '' ? e.message : 'No se pudo buscar.' })
        })
    }, esperaMs)

    return () => clearTimeout(temporizador)
  }, [buscado, visible, esperaMs, hayBusquedaRemota])

  const remotoVigente = remoto !== null && remoto.texto === buscado ? remoto : null
  const buscando = visible && buscado !== '' && hayBusquedaRemota && remotoVigente === null

  const locales =
    buscado === ''
      ? opciones
      : combinarResultados(buscarLocal ? buscarLocal(buscado) : [], filtrarOpciones(opciones, buscado))
  const lista = combinarResultados(remotoVigente?.opciones ?? [], locales)

  const posicionActiva = lista.findIndex((o) => o.id === idActivo)
  const indiceActivo = posicionActiva >= 0 ? posicionActiva : buscado !== '' && lista.length > 0 ? 0 : -1
  const opcionActiva = indiceActivo >= 0 ? lista[indiceActivo] : null

  const elegida = opciones.find((o) => o.id === valor) ?? null
  const mostrado = texto ?? elegida?.etiqueta ?? ''

  // La lista se ancla a la ventana: dentro de un contenedor con scroll la de una caja baja quedaría recortada.
  useLayoutEffect(() => {
    if (!visible || cajaRef.current === null) {
      setPosicion(null)
      return
    }
    const medir = () => {
      if (cajaRef.current) setPosicion(calcularPosicion(cajaRef.current.getBoundingClientRect()))
    }
    medir()
    const cerrarPorScroll = (evento: Event) => {
      if (evento.target instanceof Node && listaRef.current?.contains(evento.target)) return
      cerrar()
    }
    window.addEventListener('scroll', cerrarPorScroll, true)
    window.addEventListener('resize', medir)
    return () => {
      window.removeEventListener('scroll', cerrarPorScroll, true)
      window.removeEventListener('resize', medir)
    }
  }, [visible])

  useEffect(() => {
    if (!visible || opcionActiva === null) return
    document.getElementById(`${idLista}-${opcionActiva.id}`)?.scrollIntoView?.({ block: 'nearest' })
  }, [visible, opcionActiva, idLista])

  function cerrar() {
    setAbierta(false)
    setTexto(null)
    setIdActivo(null)
  }

  function abrir() {
    if (disabled || abierta) return
    setAbierta(true)
    setIdActivo(valor)
  }

  function elegir(opcion: T) {
    cerrar()
    onChange(opcion)
  }

  function moverActiva(indice: number) {
    if (lista.length === 0) return
    setIdActivo(lista[(indice + lista.length) % lista.length].id)
  }

  function alTeclear(evento: KeyboardEvent<HTMLInputElement>) {
    switch (evento.key) {
      case 'ArrowDown':
      case 'ArrowUp': {
        evento.preventDefault()
        const paso = evento.key === 'ArrowDown' ? 1 : -1
        if (!visible) {
          abrir()
          return
        }
        moverActiva(indiceActivo < 0 ? (paso > 0 ? 0 : lista.length - 1) : indiceActivo + paso)
        return
      }
      case 'Home':
      case 'End':
        if (!visible || lista.length === 0) return
        evento.preventDefault()
        moverActiva(evento.key === 'Home' ? 0 : lista.length - 1)
        return
      case 'Enter':
        if (!visible) return
        evento.preventDefault()
        if (opcionActiva !== null) elegir(opcionActiva)
        else cerrar()
        return
      case 'Escape':
        if (visible) evento.stopPropagation()
        cerrar()
        alCancelar?.()
        return
    }
  }

  const mensajeSinResultados = typeof textoSinResultados === 'function' ? textoSinResultados(buscado) : textoSinResultados
  const mensajeVacio = lista.length === 0 && !buscando ? (remotoVigente?.error ?? mensajeSinResultados) : null
  const anuncio = lista.length > 0 ? `${lista.length} ${lista.length === 1 ? 'resultado' : 'resultados'}.` : ''

  return (
    <div className="position-relative">
      <input
        ref={cajaRef}
        id={id}
        type="text"
        role="combobox"
        className={`form-select${className ? ` ${className}` : ''}`}
        aria-label={nombreAccesible}
        aria-autocomplete="list"
        aria-haspopup="listbox"
        aria-expanded={visible}
        aria-controls={visible ? idLista : undefined}
        aria-activedescendant={visible && opcionActiva !== null ? `${idLista}-${opcionActiva.id}` : undefined}
        autoComplete="off"
        placeholder={placeholder}
        title={mostrado === '' ? undefined : mostrado}
        value={mostrado}
        disabled={disabled}
        onFocus={(evento) => evento.currentTarget.select()}
        onClick={abrir}
        onChange={(evento) => {
          setTexto(evento.target.value)
          setAbierta(true)
          setIdActivo(null)
        }}
        onKeyDown={alTeclear}
        onBlur={cerrar}
      />
      {visible && (
        <div
          ref={listaRef}
          className="list-group shadow"
          style={posicion?.estilo ?? { visibility: 'hidden', position: 'fixed' }}
          onMouseDown={(evento) => evento.preventDefault()}
        >
          <ul id={idLista} role="listbox" aria-label={nombreAccesible} className="list-unstyled mb-0">
            {lista.map((o) => (
              <li
                key={o.id}
                id={`${idLista}-${o.id}`}
                role="option"
                aria-selected={opcionActiva !== null && o.id === opcionActiva.id}
                className={`list-group-item list-group-item-action py-1 px-2 small${opcionActiva !== null && o.id === opcionActiva.id ? ' active' : ''}${o.id === valor ? ' fw-semibold' : ''}`}
                style={{ cursor: 'pointer' }}
                onMouseMove={() => {
                  if (opcionActiva?.id !== o.id) setIdActivo(o.id)
                }}
                onClick={() => elegir(o)}
              >
                {o.etiqueta}
              </li>
            ))}
          </ul>
          {buscando && <div className="list-group-item small text-muted py-1 px-2">Buscando…</div>}
          {mensajeVacio !== null && (
            <div role="status" className="list-group-item small text-muted py-1 px-2">
              {mensajeVacio}
            </div>
          )}
        </div>
      )}
      <div role="status" aria-live="polite" className="visually-hidden">
        {visible && !buscando ? anuncio : ''}
      </div>
    </div>
  )
}
