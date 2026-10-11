import { useRef, useState } from 'react'
import type { ChangeEvent, FocusEvent, InputHTMLAttributes, KeyboardEvent } from 'react'
import { dentroDeRango, formatearTipeoOPegado, isoATexto, textoAIso } from '../formato/fechas'

export interface PropsCampoFecha
  extends Omit<InputHTMLAttributes<HTMLInputElement>, 'value' | 'onChange' | 'type' | 'defaultValue' | 'inputMode'> {
  /** `YYYY-MM-DD` o `''` — el mismo contrato que un `<input type="date">` controlado. */
  value: string
  /**
   * Recibe `YYYY-MM-DD` al completarse una fecha real dentro de `min`/`max`, y `''` al vaciar el texto.
   * Un texto parcial o inválido no emite mientras se tipea: se resuelve a `''` al salir del campo o con Enter.
   */
  onChange: (valor: string) => void
  min?: string
  max?: string
}

type Estado = {
  texto: string
  /** Último valor ISO que el padre tiene (el que este campo emitió o recibió); si `value` difiere, vino de afuera. */
  valorVisto: string
  /** Se muestra el error recién al salir del campo o al completar los diez caracteres. */
  validar: boolean
}

function estadoDesde(valor: string): Estado {
  return { texto: isoATexto(valor), valorVisto: valor, validar: false }
}

function isoDelTexto(texto: string, min?: string, max?: string, enVivo = false): string | null {
  const iso = textoAIso(texto, undefined, !enVivo)
  return iso !== null && dentroDeRango(iso, min, max) ? iso : null
}

/**
 * Campo de fecha que muestra y acepta `DD/MM/AAAA` sin depender de la configuración regional del
 * navegador. Hacia afuera se comporta como un `<input type="date">` controlado.
 */
export function CampoFecha({ value, onChange, min, max, className, disabled, onBlur, onKeyDown, ...resto }: PropsCampoFecha) {
  const [estado, setEstado] = useState<Estado>(() => estadoDesde(value))
  const selectorRef = useRef<HTMLInputElement>(null)

  if (value !== estado.valorVisto) setEstado(estadoDesde(value))

  function emitir(iso: string, texto: string, validar: boolean) {
    setEstado({ texto, valorVisto: iso, validar })
    if (iso !== estado.valorVisto) onChange(iso)
  }

  function manejarCambio(e: ChangeEvent<HTMLInputElement>) {
    const texto = formatearTipeoOPegado(e.target.value)
    const iso = isoDelTexto(texto, min, max, true)
    if (iso !== null) emitir(iso, texto, false)
    else if (texto === '') emitir('', '', false)
    else setEstado({ ...estado, texto, validar: texto.length === 10 })
  }

  function confirmarTexto() {
    const iso = isoDelTexto(estado.texto, min, max)
    if (iso !== null) emitir(iso, isoATexto(iso), false)
    else if (estado.texto === '') emitir('', '', false)
    else emitir('', estado.texto, true)
  }

  function manejarBlur(e: FocusEvent<HTMLInputElement>) {
    confirmarTexto()
    onBlur?.(e)
  }

  function manejarTecla(e: KeyboardEvent<HTMLInputElement>) {
    if (e.key === 'Enter') confirmarTexto()
    onKeyDown?.(e)
  }

  function manejarSelector(e: ChangeEvent<HTMLInputElement>) {
    if (e.target.value === '') {
      emitir('', '', false)
      return
    }
    const iso = isoDelTexto(isoATexto(e.target.value), min, max)
    if (iso !== null) emitir(iso, isoATexto(iso), false)
  }

  function abrirCalendario() {
    try {
      selectorRef.current?.showPicker()
    } catch {
      // Sin selector nativo disponible, el texto sigue siendo la vía de carga.
    }
  }

  const invalido = estado.validar && estado.texto !== '' && isoDelTexto(estado.texto, min, max) === null
  const pequeno = className?.split(' ').includes('form-control-sm') ?? false

  return (
    <div className="position-relative">
      <div className={`input-group${pequeno ? ' input-group-sm' : ''}`}>
        <input
          {...resto}
          type="text"
          inputMode="numeric"
          autoComplete="off"
          maxLength={10}
          placeholder="dd/mm/aaaa"
          className={[className, invalido ? 'is-invalid' : ''].filter(Boolean).join(' ')}
          aria-invalid={invalido ? true : resto['aria-invalid']}
          value={estado.texto}
          disabled={disabled}
          onChange={manejarCambio}
          onBlur={manejarBlur}
          onKeyDown={manejarTecla}
        />
        <button
          type="button"
          className="btn btn-outline-secondary"
          aria-label="Abrir calendario"
          title="Abrir calendario"
          disabled={disabled}
          onClick={abrirCalendario}
        >
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true">
            <rect x="3" y="5" width="18" height="16" rx="2" />
            <path d="M3 10h18M8 3v4M16 3v4" />
          </svg>
        </button>
      </div>
      <input
        ref={selectorRef}
        type="date"
        tabIndex={-1}
        inert
        aria-hidden="true"
        className="visually-hidden"
        style={{ left: 0, bottom: 0 }}
        value={estado.valorVisto}
        min={min}
        max={max}
        disabled={disabled}
        onChange={manejarSelector}
      />
    </div>
  )
}
