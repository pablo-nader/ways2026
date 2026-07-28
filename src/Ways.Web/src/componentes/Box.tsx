import type { ReactNode } from 'react'

type Variante = 'default' | 'inverse' | 'primary' | 'success' | 'warning' | 'danger' | 'info'

type Props = {
  titulo?: ReactNode
  iconos?: ReactNode
  herramientas?: ReactNode
  variante?: Variante
  children: ReactNode
}

/**
 * Caja del template actual de Ways (metisAdmin). Mantiene las mismas clases CSS
 * que el sistema viejo para que la interfaz se vea igual.
 */
export function Box({ titulo, iconos, herramientas, variante = 'default', children }: Props) {
  const clase = variante === 'default' ? 'box' : `box ${variante}`

  return (
    <div className={clase}>
      {(titulo || iconos || herramientas) && (
        <header>
          {iconos}
          {titulo && <h5>{titulo}</h5>}
          {herramientas && <div className="toolbar">{herramientas}</div>}
        </header>
      )}
      <div className="body p-3">{children}</div>
    </div>
  )
}
