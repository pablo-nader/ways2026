import type { ReactNode } from 'react'

type Variante = 'default' | 'inverse' | 'primary' | 'success' | 'warning' | 'danger' | 'info'

type Props = {
  titulo?: ReactNode
  iconos?: ReactNode
  herramientas?: ReactNode
  /** Contenido propio de la cabecera, en lugar del título de texto. Con `etiqueta` la caja pasa a ser una región con ese nombre. */
  encabezado?: ReactNode
  etiqueta?: string
  variante?: Variante
  children: ReactNode
}

/**
 * Caja del template actual de Ways (metisAdmin). Mantiene las mismas clases CSS
 * que el sistema viejo para que la interfaz se vea igual.
 */
export function Box({ titulo, iconos, herramientas, encabezado, etiqueta, variante = 'default', children }: Props) {
  const clase = variante === 'default' ? 'box' : `box ${variante}`

  return (
    <div className={clase} role={etiqueta ? 'region' : undefined} aria-label={etiqueta}>
      {(titulo || iconos || herramientas || encabezado) && (
        <header>
          {iconos}
          {titulo && <h5>{titulo}</h5>}
          {encabezado && <div className="encabezado">{encabezado}</div>}
          {herramientas && <div className="toolbar">{herramientas}</div>}
        </header>
      )}
      <div className="body p-3">{children}</div>
    </div>
  )
}
