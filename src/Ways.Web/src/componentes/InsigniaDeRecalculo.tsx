type Props = {
  /** Momento del último recálculo administrativo del arqueo; `null` si nunca hubo uno. */
  fechaRecalculo: string | null
}

/** Marca "Recalculado" de un arqueo que un administrador recalculó tras editar o eliminar un
 * gasto de un turno cerrado. Sin recálculo no renderiza nada. */
export function InsigniaDeRecalculo({ fechaRecalculo }: Props) {
  if (fechaRecalculo === null) return null

  const cuando = new Date(fechaRecalculo).toLocaleString('es-AR')

  return (
    <span className="badge text-bg-warning ms-2" title={`Arqueo recalculado el ${cuando}`}>
      Recalculado
    </span>
  )
}
