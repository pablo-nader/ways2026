import { forwardRef, useEffect, useRef, useState } from 'react'
import type { ChangeEvent, FocusEvent, InputHTMLAttributes } from 'react'
import { formatearImporte, parsearImporte, redondearImporte } from '../formato/importes'

export interface PropsCampoImporte extends Omit<InputHTMLAttributes<HTMLInputElement>, 'value' | 'onChange' | 'type'> {
  /** Valor numérico actual. `null` representa "vacío". */
  valor: number | null
  /** Se dispara en cada tecleo y en el blur con el mismo número que `formatearImporte`
   * terminaría mostrando (`null` mientras el texto no es un importe válido todavía, ej.
   * "1234,") — nunca un valor sin redondear que el texto visible no respalde. */
  onChange: (valor: number | null) => void
  decimales?: number
  /** Permite tipear un "-" inicial. Default `false`: la mayoría de los importes adoptados
   * (fondos, costos, precios) no son negativos: un "-" tipeado en esos campos se descarta
   * en vez de dejar pasar un valor que la validación de todos modos va a rechazar. */
  admiteNegativos?: boolean
}

/** Elimina cualquier caracter que no sea dígito o ",", conserva un único signo "-" SOLO al
 * principio y solo si `admiteNegativos` (uno tipeado en cualquier otra posición se descarta,
 * no se cuela silenciosamente como si fuera parte del número), una única "," (el resto de
 * comas tipeadas se funden en los decimales), y descarta cualquier "." tipeado — se tolera
 * como separador de miles pero no se conserva mientras el campo está enfocado
 * (`react-async-state`: no reformatear en cada tecla evita que el caret salte). Los decimales
 * tipeados se recortan a `decimales` dígitos — nunca se deja tipear un tercer decimal cuando
 * `decimales` es 2. El reformateo con "." de miles ocurre recién en el blur. */
function sanearTipeo(bruto: string, decimales: number, admiteNegativos: boolean): string {
  const negativo = admiteNegativos && bruto.trimStart().startsWith('-')
  const sinSigno = bruto.replace(/-/g, '')
  const soloValidos = decimales > 0 ? sinSigno.replace(/[^0-9,]/g, '') : sinSigno.replace(/[^0-9]/g, '')
  const [antes, ...resto] = soloValidos.split(',')

  let cuerpo = antes
  if (decimales > 0 && resto.length > 0) {
    const decimalesTipeados = resto.join('').slice(0, decimales)
    cuerpo = `${antes},${decimalesTipeados}`
  }

  return negativo ? `-${cuerpo}` : cuerpo
}

/** Único punto donde un texto en edición se convierte en el valor que sale por `onChange`:
 * parsea y redondea con la MISMA `redondearImporte` que usa `formatearImporte`, así el número
 * emitido nunca difiere del que el texto reformateado en el blur va a mostrar. */
function valorEmitido(texto: string, decimales: number): number | null {
  const parseado = parsearImporte(texto)
  return parseado === null ? null : redondearImporte(parseado, decimales)
}

/**
 * Input de importe: mientras está enfocado se edita en crudo (dígitos + una ","); al perder
 * el foco se reformatea a "1.234,56". El valor numérico sale por `onChange`, nunca el texto.
 */
export const CampoImporte = forwardRef<HTMLInputElement, PropsCampoImporte>(function CampoImporte(
  { valor, onChange, decimales = 2, admiteNegativos = false, className, onFocus, onBlur, ...resto },
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
    const saneado = sanearTipeo(e.target.value, decimales, admiteNegativos)
    setTexto(saneado)
    onChange(saneado === '' || saneado === '-' ? null : valorEmitido(saneado, decimales))
  }

  function manejarFocus(e: FocusEvent<HTMLInputElement>) {
    enfocadoRef.current = true
    onFocus?.(e)
  }

  function manejarBlur(e: FocusEvent<HTMLInputElement>) {
    enfocadoRef.current = false
    const redondeado = valorEmitido(texto, decimales)
    setTexto(redondeado === null ? '' : formatearImporte(redondeado, { decimales }))
    onChange(redondeado)
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
