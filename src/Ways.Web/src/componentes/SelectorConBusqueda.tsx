import { useEffect, useId, useLayoutEffect, useMemo, useRef, useState } from 'react'
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
/** Tope de opciones dibujadas: una copia local completa de clientes no se vuelca entera en la lista. */
export const MAXIMO_DE_OPCIONES_VISIBLES = 50

/** Las opciones cuya etiqueta contiene `texto`, sin distinguir mayúsculas ni acentos. Un texto vacío no filtra. */
export function filtrarOpciones<T extends OpcionDeSelector>(opciones: readonly T[], texto: string): T[] {
  return opciones.filter((o) => contieneSinAcentos(o.etiqueta, texto))
}

/** Las `primeras` en su orden y, a continuación, las `siguientes` que no estén ya: lo ya mostrado nunca se reordena. */
export function combinarResultados<T extends OpcionDeSelector>(primeras: readonly T[], siguientes: readonly T[]): T[] {
  const vistos = new Set<T['id']>()
  const resultado: T[] = []
  for (const opcion of [...primeras, ...siguientes]) {
    if (vistos.has(opcion.id)) continue
    vistos.add(opcion.id)
    resultado.push(opcion)
  }
  return resultado
}

/** Las coincidencias locales en su orden, con las remotas detrás. Un id que viene de las dos fuentes
 * conserva su posición local pero toma los datos de la copia remota. */
export function sumarRemotas<T extends OpcionDeSelector>(locales: readonly T[], remotas: readonly T[]): T[] {
  if (remotas.length === 0) return locales as T[]
  const remotaPorId = new Map(remotas.map((o) => [o.id, o]))
  return combinarResultados(
    locales.map((o) => remotaPorId.get(o.id) ?? o),
    remotas,
  )
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
  /** Consulta remota con el texto tipeado, con espera de tipeo y respuesta tardía descartada. Lo que trae se
   * agrega detrás de lo ya mostrado, sin reordenarlo. Si rechaza, el mensaje del
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
  /** Marca la caja como inválida para tecnología asistiva; la clase visual va aparte, en `className`. */
  'aria-invalid'?: boolean
  /** Id del elemento que explica el error de la caja. */
  'aria-describedby'?: string
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
 * "contiene" (sin distinguir mayúsculas ni acentos). Combobox WAI-ARIA con listbox: flechas mueven la opción
 * activa; Enter elige; Escape cierra y restaura la etiqueta elegida; perder el
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
  'aria-invalid': ariaInvalid,
  'aria-describedby': ariaDescribedBy,
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

  // Sin consulta remota (por ejemplo, sin red) no queda nada de lo que trajo antes.
  useEffect(() => {
    if (!hayBusquedaRemota) setRemoto(null)
  }, [hayBusquedaRemota])

  // Cerrada la lista o deshabilitado el control no queda nada abierto ni tipeado: al volver no reaparece.
  useEffect(() => {
    if (disabled) cerrar()
  }, [disabled])

  const remotoVigente = remoto !== null && remoto.texto === buscado ? remoto : null
  const buscando = visible && buscado !== '' && hayBusquedaRemota && remotoVigente === null

  const locales = useMemo(
    () =>
      buscado === '' ? opciones : combinarResultados(buscarLocal ? buscarLocal(buscado) : [], filtrarOpciones(opciones, buscado)),
    [buscado, opciones, buscarLocal],
  )
  const coincidencias = useMemo(() => sumarRemotas(locales, remotoVigente?.opciones ?? []), [locales, remotoVigente])
  const hayMas = coincidencias.length > MAXIMO_DE_OPCIONES_VISIBLES
  const lista = useMemo(() => coincidencias.slice(0, MAXIMO_DE_OPCIONES_VISIBLES), [coincidencias])

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
    setRemoto(null)
  }

  function abrir() {
    if (disabled || abierta) return
    setAbierta(true)
    setIdActivo(valor)
    cajaRef.current?.select()
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
      case 'Enter':
        // Enter que confirma una composición (IME, tecla muerta) no es una elección.
        if (!visible || evento.nativeEvent.isComposing || evento.keyCode === 229) return
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
        aria-invalid={ariaInvalid}
        aria-describedby={ariaDescribedBy}
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
          <ul id={idLista} role="listbox" aria-label={`Opciones de ${nombreAccesible}`} className="list-unstyled mb-0">
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
          {hayMas && (
            <div className="list-group-item small text-muted py-1 px-2">
              Se muestran las primeras {MAXIMO_DE_OPCIONES_VISIBLES} coincidencias: seguí escribiendo para acotar.
            </div>
          )}
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
