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

/** Un importe ya agrupado en miles: con coma decimal ("1.234,56") o, sin coma, con dos o más grupos ("1.234.567"). Un solo
 * grupo sin coma ("1.234") no entra: ahí el "." es decimal. */
const PATRON_FORMATEADO = /^\d{1,3}((\.\d{3})+,\d*|(\.\d{3}){2,})$/

/** Elimina cualquier caracter que no sea dígito, "." o ",", conserva un único signo "-" SOLO al
 * principio y solo si `admiteNegativos` (uno tipeado en cualquier otra posición se descarta,
 * no se cuela silenciosamente como si fuera parte del número). "." y "," son el mismo separador
 * decimal: el primero que se tipea queda como "," y los siguientes (de cualquiera de los dos
 * tipos) se funden en los decimales. Los decimales tipeados se recortan a `decimales` dígitos —
 * nunca se deja tipear un tercer decimal cuando `decimales` es 2; con `decimales` en 0 se
 * conservan para que el blur los redondee en vez de pegarlos a los enteros. Un importe ya
 * agrupado en miles (`PATRON_FORMATEADO`, p. ej. pegado) conserva la lectura es-AR: sus "." son
 * de miles. El agrupado de miles con "." ocurre recién en el blur
 * (`react-async-state`: no reformatear en cada tecla evita que el caret salte). */
function sanearTipeo(bruto: string, decimales: number, admiteNegativos: boolean): string {
  const negativo = admiteNegativos && bruto.trimStart().startsWith('-')
  const sinSigno = bruto.replace(/-/g, '')
  const filtrado = sinSigno.replace(/[^0-9.,]/g, '')
  const soloValidos = PATRON_FORMATEADO.test(filtrado) ? filtrado.replace(/\./g, '') : filtrado
  const [antes, ...resto] = soloValidos.split(/[.,]/)

  let cuerpo = antes
  if (resto.length > 0) {
    const decimalesTipeados = decimales > 0 ? resto.join('').slice(0, decimales) : resto.join('')
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

/** Texto de edición de un valor existente: sin miles y con "," decimal ("1234,56"). */
function textoCrudo(valor: number | null, decimales: number): string {
  if (valor === null) return ''
  const redondeado = redondearImporte(valor, decimales)
  return Number.isFinite(redondeado) ? String(redondeado).replace('.', ',') : ''
}

/**
 * Input de importe: mientras está enfocado se edita en crudo, sin miles (dígitos + un separador
 * decimal, "." o ","; al enfocar un valor existente aparece como "1234,56"); al perder el foco se
 * reformatea a "1.234,56". El valor numérico sale por `onChange`, nunca el texto.
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
    setTexto(textoCrudo(valor, decimales))
    onFocus?.(e)
  }

  function manejarBlur(e: FocusEvent<HTMLInputElement>) {
    enfocadoRef.current = false
    // Un separador final sin decimales ("1234,") vale como el entero al salir del campo.
    const redondeado = valorEmitido(texto.replace(/,$/, ''), decimales)
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
