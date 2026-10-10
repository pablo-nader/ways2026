import { useRef, useState } from 'react'
import type { ChangeEvent, FocusEvent, InputHTMLAttributes } from 'react'
import { dentroDeRango, formatearTipeo, isoATexto, textoAIso } from '../formato/fechas'

export interface PropsCampoFecha
  extends Omit<InputHTMLAttributes<HTMLInputElement>, 'value' | 'onChange' | 'type' | 'defaultValue' | 'inputMode'> {
  /** `YYYY-MM-DD` o `''` — el mismo contrato que un `<input type="date">` controlado. */
  value: string
  /** Recibe `YYYY-MM-DD` solo para una fecha real dentro de `min`/`max`; `''` si se vació o el texto no es válido. */
  onChange: (valor: string) => void
  min?: string
  max?: string
}

type Estado = {
  texto: string
  /** Último valor ISO que este campo emitió o recibió; si `value` difiere, vino de afuera. */
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
export function CampoFecha({ value, onChange, min, max, className, disabled, onBlur, ...resto }: PropsCampoFecha) {
  const [estado, setEstado] = useState<Estado>(() => estadoDesde(value))
  const selectorRef = useRef<HTMLInputElement>(null)

  if (value !== estado.valorVisto) setEstado(estadoDesde(value))

  function emitir(iso: string, texto: string, validar: boolean) {
    setEstado({ texto, valorVisto: iso, validar })
    if (iso !== estado.valorVisto) onChange(iso)
  }

  function manejarCambio(e: ChangeEvent<HTMLInputElement>) {
    const texto = formatearTipeo(e.target.value)
    emitir(isoDelTexto(texto, min, max, true) ?? '', texto, texto.length === 10)
  }

  function manejarBlur(e: FocusEvent<HTMLInputElement>) {
    const iso = isoDelTexto(estado.texto, min, max)
    if (iso !== null) emitir(iso, isoATexto(iso), false)
    else setEstado({ ...estado, validar: true })
    onBlur?.(e)
  }

  function abrirCalendario() {
    const selector = selectorRef.current
    if (!selector) return
    try {
      selector.showPicker()
    } catch {
      selector.focus()
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
          min={min}
          max={max}
          disabled={disabled}
          onChange={manejarCambio}
          onBlur={manejarBlur}
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
        aria-hidden="true"
        className="visually-hidden"
        style={{ left: 0, bottom: 0 }}
        value={estado.valorVisto}
        min={min}
        max={max}
        disabled={disabled}
        onChange={(e) => emitir(e.target.value, isoATexto(e.target.value), false)}
      />
    </div>
  )
}
