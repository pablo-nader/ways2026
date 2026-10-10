import { useState } from 'react'
import {
  DIGITOS_NUMERO,
  DIGITOS_PUNTO_DE_VENTA,
  dividirNumero,
  rellenarConCeros,
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

type Estado = PartesDeNumero & { valorVisto: string; editado: boolean }

function estadoDesde(valor: string): Estado {
  return { ...dividirNumero(valor), valorVisto: valor, editado: false }
}

export function CampoNumeroDeComprobante({ idBase, valor, onChange, disabled = false }: Props) {
  const [estado, setEstado] = useState<Estado>(() => estadoDesde(valor))

  // Un valor que no emitió este campo (carga de la compra, pre-carga desde un gasto) reemplaza lo mostrado.
  if (valor !== estado.valorVisto) setEstado(estadoDesde(valor))

  function aplicar(partes: PartesDeNumero) {
    const nuevo = unirNumero(partes)
    setEstado({ ...partes, valorVisto: nuevo, editado: true })
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

  const heredado = !estado.editado && !tieneFormatoEstandar(valor)

  return (
    <>
      <div className="input-group" role="group" aria-labelledby={`${idBase}-etiqueta`}>
        <input
          id={`${idBase}-punto-venta`}
          type="text"
          inputMode="numeric"
          autoComplete="off"
          maxLength={DIGITOS_PUNTO_DE_VENTA}
          className="form-control"
          style={{ maxWidth: '5.5rem' }}
          aria-label="Punto de venta del comprobante"
          placeholder="0000"
          value={estado.puntoVenta}
          disabled={disabled}
          onChange={(e) => aplicar({ puntoVenta: soloDigitos(e.target.value, DIGITOS_PUNTO_DE_VENTA), numero: estado.numero })}
          onBlur={completar}
        />
        <span className="input-group-text">-</span>
        <input
          id={`${idBase}-numero`}
          type="text"
          inputMode="numeric"
          autoComplete="off"
          maxLength={DIGITOS_NUMERO}
          className="form-control"
          aria-label="Número del comprobante"
          placeholder="00000000"
          value={estado.numero}
          disabled={disabled}
          onChange={(e) => aplicar({ puntoVenta: estado.puntoVenta, numero: soloDigitos(e.target.value, DIGITOS_NUMERO) })}
          onBlur={completar}
        />
      </div>
      {heredado && <div className="form-text">Número original: {valor}</div>}
    </>
  )
}
