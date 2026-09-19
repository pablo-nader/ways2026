import { createContext } from 'react'

/**
 * stage-pos-caja-en-cabecera: seam para que `Pos.tsx` porte sus controles de caja (badge +
 * "Abrir caja"/"Cerrar caja") al header oscuro de `ShellPos` sin que la lógica de turno (hardened
 * por varias rondas de judgment-day) se mueva de lugar — `ShellPos` expone acá el nodo del
 * contenedor vacío que reserva en su header (vía un callback ref + estado, para que los
 * consumidores se vuelvan a renderizar cuando el nodo exista) y `Pos.tsx` hace `createPortal` ahí
 * cuando el valor no es `null`. En la app web (bajo `Layout.tsx`, sin `ShellPos`) no hay ningún
 * `Provider` que sobrescriba el default — el valor queda en `null` y `Pos.tsx` renderiza los
 * mismos controles en una franja propia en vez de portalearlos.
 */
export const RanuraHeaderPosContext = createContext<HTMLDivElement | null>(null)
