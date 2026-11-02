import { useMemo } from 'react'
import { create } from 'qrcode'

type PropsCodigoQr = {
  valor: string
  /** Lado en píxeles del dibujo; el QR se escala al contenedor. */
  tamano?: number
}

/**
 * Dibuja `valor` como QR en un SVG, sin `innerHTML` ni imágenes `data:` (la CSP del POS de escritorio las bloquea). Los módulos
 * oscuros salen en un solo `path`; la zona silenciosa es de cuatro módulos, como pide el estándar. Un contenido que no entra en
 * un QR no dibuja nada y avisa en texto.
 */
export function CodigoQr({ valor, tamano = 280 }: PropsCodigoQr) {
  const dibujo = useMemo(() => {
    try {
      const { modules } = create(valor, { errorCorrectionLevel: 'M' })
      const lado = modules.size
      let trazo = ''
      for (let fila = 0; fila < lado; fila += 1) {
        for (let columna = 0; columna < lado; columna += 1) {
          if (modules.get(fila, columna)) trazo += `M${columna + 4} ${fila + 4}h1v1h-1z`
        }
      }
      return { lado: lado + 8, trazo }
    } catch {
      return null
    }
  }, [valor])

  if (dibujo === null) return <p className="text-danger mb-0">No se pudo dibujar el código QR.</p>

  return (
    <svg
      role="img"
      aria-label="Código QR de Mercado Pago"
      width={tamano}
      height={tamano}
      viewBox={`0 0 ${dibujo.lado} ${dibujo.lado}`}
      shapeRendering="crispEdges"
      style={{ maxWidth: '100%', height: 'auto', background: '#fff' }}
    >
      <rect width={dibujo.lado} height={dibujo.lado} fill="#fff" />
      <path d={dibujo.trazo} fill="#000" />
    </svg>
  )
}
