import { useState } from 'react'
import type { ClipboardEvent, FocusEvent } from 'react'
import {
  DIGITOS_NUMERO,
  DIGITOS_PUNTO_DE_VENTA,
  dividirNumero,
  estaMedioLleno,
  rellenarConCeros,
  repartirPegado,
  soloDigitos,
  tieneFormatoEstandar,
  unirNumero,
  type PartesDeNumero,
} from './numeroDeComprobante'

type Props = {
  idBase: string
  /** Valor único `PPPP-NNNNNNNN` (o vacío) que viaja a la API. */
  valor: string
  onChange: (valor: string) => void
  disabled?: boolean
}

type Estado = PartesDeNumero & {
  valorVisto: string
  editado: boolean
  /** El foco ya salió del grupo después de editar: recién ahí se señala la parte que falta. */
  salio: boolean
}

function estadoDesde(valor: string): Estado {
  return { ...dividirNumero(valor), valorVisto: valor, editado: false, salio: false }
}

export function CampoNumeroDeComprobante({ idBase, valor, onChange, disabled = false }: Props) {
  const [estado, setEstado] = useState<Estado>(() => estadoDesde(valor))

  // Un valor que no emitió este campo (carga de la compra, pre-carga desde un gasto) reemplaza lo mostrado.
  if (valor !== estado.valorVisto) setEstado(estadoDesde(valor))

  function aplicar(partes: PartesDeNumero) {
    const nuevo = unirNumero(partes)
    setEstado({ ...partes, valorVisto: nuevo, editado: true, salio: estado.salio })
    onChange(nuevo)
  }

  function completar() {
    if (!estado.editado) return
    const partes = {
      puntoVenta: rellenarConCeros(estado.puntoVenta, DIGITOS_PUNTO_DE_VENTA),
      numero: rellenarConCeros(estado.numero, DIGITOS_NUMERO),
    }
    if (partes.puntoVenta !== estado.puntoVenta || partes.numero !== estado.numero) aplicar(partes)
  }

  function pegar(e: ClipboardEvent<HTMLInputElement>) {
    const partes = repartirPegado(e.clipboardData.getData('text'))
    if (partes === null) return
    e.preventDefault()
    aplicar(partes)
  }

  function salirDelGrupo(e: FocusEvent<HTMLDivElement>) {
    if (e.currentTarget.contains(e.relatedTarget)) return
    setEstado((prev) => ({ ...prev, salio: true }))
  }

  const faltaPuntoVenta = estado.editado && estado.salio && estaMedioLleno(estado) && estado.puntoVenta === ''
  const faltaNumero = estado.editado && estado.salio && estaMedioLleno(estado) && estado.numero === ''
  const heredado = !estado.editado && !tieneFormatoEstandar(valor)

  return (
    <>
      <div className="input-group has-validation" role="group" aria-labelledby={`${idBase}-etiqueta`} onBlur={salirDelGrupo}>
        <input
          id={`${idBase}-punto-venta`}
          type="text"
          inputMode="numeric"
          autoComplete="off"
          className={`form-control${faltaPuntoVenta ? ' is-invalid' : ''}`}
          style={{ maxWidth: '5.5rem' }}
          aria-label="Punto de venta del comprobante"
          aria-invalid={faltaPuntoVenta || undefined}
          placeholder="0000"
          value={estado.puntoVenta}
          disabled={disabled}
          onChange={(e) => aplicar({ puntoVenta: soloDigitos(e.target.value, DIGITOS_PUNTO_DE_VENTA), numero: estado.numero })}
          onBlur={completar}
          onPaste={pegar}
        />
        <span className="input-group-text">-</span>
        <input
          id={`${idBase}-numero`}
          type="text"
          inputMode="numeric"
          autoComplete="off"
          className={`form-control${faltaNumero ? ' is-invalid' : ''}`}
          aria-label="Número del comprobante"
          aria-invalid={faltaNumero || undefined}
          placeholder="00000000"
          value={estado.numero}
          disabled={disabled}
          onChange={(e) => aplicar({ puntoVenta: estado.puntoVenta, numero: soloDigitos(e.target.value, DIGITOS_NUMERO) })}
          onBlur={completar}
          onPaste={pegar}
        />
      </div>
      {(faltaPuntoVenta || faltaNumero) && (
        <div className="invalid-feedback d-block">
          {faltaPuntoVenta ? 'Falta el punto de venta.' : 'Falta el número.'} Complete ambas partes o deje las dos vacías.
        </div>
      )}
      {heredado && <div className="form-text">Número original: {valor}</div>}
    </>
  )
}
