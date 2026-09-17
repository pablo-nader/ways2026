import { forwardRef, useEffect, useRef, useState } from 'react'
import type { ChangeEvent, FocusEvent, InputHTMLAttributes } from 'react'
import { formatearImporte, parsearImporte } from '../formato/importes'

export interface PropsCampoImporte extends Omit<InputHTMLAttributes<HTMLInputElement>, 'value' | 'onChange' | 'type'> {
  /** Valor numérico actual. `null` representa "vacío". */
  valor: number | null
  /** Se dispara en cada tecleo con el valor parseado (`null` mientras el texto no es un
   * importe válido todavía, ej. "1234,"). El commit "definitivo" ocurre en el blur, que
   * además reformatea el texto visible. */
  onChange: (valor: number | null) => void
  decimales?: number
}

/** Elimina cualquier caracter que no sea dígito, ",", "-", conserva un único signo "-" al
 * principio, una única "," (el resto de comas tipeadas se funden en los decimales), y
 * descarta cualquier "." tipeado — se tolera como separador de miles pero no se conserva
 * mientras el campo está enfocado (`react-async-state`: no reformatear en cada tecla evita
 * que el caret salte). El reformateo con "." de miles ocurre recién en el blur. */
function sanearTipeo(bruto: string): string {
  const negativo = bruto.trim().startsWith('-')
  const sinSigno = bruto.replace(/-/g, '')
  const soloValidos = sinSigno.replace(/[^0-9,]/g, '')
  const [antes, ...resto] = soloValidos.split(',')
  const cuerpo = resto.length > 0 ? `${antes},${resto.join('')}` : antes
  return negativo ? `-${cuerpo}` : cuerpo
}

/**
 * Input de importe: mientras está enfocado se edita en crudo (dígitos + una ","); al perder
 * el foco se reformatea a "1.234,56". El valor numérico sale por `onChange`, nunca el texto.
 */
export const CampoImporte = forwardRef<HTMLInputElement, PropsCampoImporte>(function CampoImporte(
  { valor, onChange, decimales = 2, className, onFocus, onBlur, ...resto },
  ref,
) {
  const [texto, setTexto] = useState(() => (valor === null ? '' : formatearImporte(valor, { decimales })))
  const enfocadoRef = useRef(false)

  // Sincroniza con `valor` cuando cambia desde afuera (ej. el padre lo resetea) — pero nunca
  // mientras el usuario está tecleando, para no pisarle el texto en crudo a mitad de edición.
  useEffect(() => {
    if (enfocadoRef.current) return
    setTexto(valor === null ? '' : formatearImporte(valor, { decimales }))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [valor, decimales])

  function manejarCambio(e: ChangeEvent<HTMLInputElement>) {
    const saneado = sanearTipeo(e.target.value)
    setTexto(saneado)
    onChange(saneado === '' || saneado === '-' ? null : parsearImporte(saneado))
  }

  function manejarFocus(e: FocusEvent<HTMLInputElement>) {
    enfocadoRef.current = true
    onFocus?.(e)
  }

  function manejarBlur(e: FocusEvent<HTMLInputElement>) {
    enfocadoRef.current = false
    const parseado = parsearImporte(texto)
    setTexto(parseado === null ? '' : formatearImporte(parseado, { decimales }))
    onChange(parseado)
    onBlur?.(e)
  }

  return (
    <input
      {...resto}
      ref={ref}
      type="text"
      inputMode="decimal"
      className={['text-end', className].filter(Boolean).join(' ')}
      value={texto}
      onChange={manejarCambio}
      onFocus={manejarFocus}
      onBlur={manejarBlur}
    />
  )
})
