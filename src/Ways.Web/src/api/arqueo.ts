/**
 * Reglas puras del arqueo de cierre (stage-6-turnos-caja, Slice 7): vista previa de `diferencia`
 * y armado del cuerpo de `POST …/cierre` a partir de los conteos tipeados en pantalla. Mismo
 * criterio que `caja.ts`/`pagos.ts` (stage 5/6): estas funciones son solo feedback instantáneo,
 * nunca autoritativas — el servidor vuelve a derivar todo (`arqueos_turno.diferencia` es una
 * columna `GENERATED ALWAYS`, design decisión 6).
 */
import type { ConteoDeclarado, LineaDeResumen, SolicitudDeCierre } from './tipos'

/** Espejo de `diferencia = importe_esperado − importe_declarado` (doc 10; design: The
 * Derivation). Positivo = faltante, negativo = sobrante. */
export function diferenciaPrevia(importeEsperado: number, importeDeclarado: number): number {
  return importeEsperado - importeDeclarado
}

/** Un conteo declarado es válido si es un número finito mayor o igual a 0 — declarar "nada" es
 * un acto deliberado del cajero (tipear 0), nunca un default silencioso. */
export function conteoValido(valor: string): boolean {
  const numero = Number(valor)
  return valor.trim() !== '' && Number.isFinite(numero) && numero >= 0
}

/** Todos los medios arqueables (`resumen.medios`, la fuente de verdad del servidor sobre qué
 * declarar) tienen que tener un conteo válido antes de habilitar "Finalizar cierre". */
export function conteosCompletos(medios: LineaDeResumen[], valores: Record<number, string>): boolean {
  return medios.length > 0 && medios.every((m) => conteoValido(valores[m.idMedioPago] ?? ''))
}

/** Arma `SolicitudDeCierre.conteos` — exactamente un conteo por medio arqueable, en el mismo
 * orden que `resumen.medios` (el servidor exige el set exacto: ni de más ni de menos, spec:
 * arqueo-de-cierre / Arqueo Incompleto, Medio Sin Actividad En El Turno). */
export function aSolicitudDeCierre(
  medios: LineaDeResumen[],
  valores: Record<number, string>,
  observaciones: string,
): SolicitudDeCierre {
  const conteos: ConteoDeclarado[] = medios.map((m) => ({
    idMedioPago: m.idMedioPago,
    importeDeclarado: Number(valores[m.idMedioPago] ?? '0'),
  }))
  return { conteos, observaciones: observaciones.trim() === '' ? null : observaciones.trim() }
}
