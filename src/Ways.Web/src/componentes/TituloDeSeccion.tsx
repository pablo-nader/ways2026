import type { ReactNode, Ref } from 'react'

type Props = {
  nivel?: 2 | 3 | 4
  id?: string
  ref?: Ref<HTMLHeadingElement>
  tabIndex?: number
  className?: string
  children: ReactNode
}

/** Título de una sección de formulario o de pantalla, siempre como encabezado real. */
export function TituloDeSeccion({ nivel = 3, className, children, ...resto }: Props) {
  const Etiqueta = `h${nivel}` as const
  const clase = className ? `titulo-de-seccion ${className}` : 'titulo-de-seccion'

  return (
    <Etiqueta className={clase} {...resto}>
      {children}
    </Etiqueta>
  )
}
