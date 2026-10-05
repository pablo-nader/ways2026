import type { LineaDeTicket } from '../impresion/escpos'

/**
 * Ticket térmico en texto monoespaciado, a 48 columnas como la Fuente A. Oculto en pantalla: solo
 * se muestra al imprimir con `body.imprimiendo-ticket` (ver `estilos/impresion.css`), y entonces
 * es lo único que sale en el papel.
 */
export function VistaDeTicket({ lineas }: { lineas: LineaDeTicket[] }) {
  return (
    <div className="vista-de-ticket" data-testid="vista-de-ticket">
      {lineas.map((linea, indice) => (
        <div
          key={indice}
          className="vista-de-ticket__linea"
          style={{ textAlign: linea.alineacion === 'izquierda' ? 'left' : linea.alineacion === 'centro' ? 'center' : 'right', fontWeight: linea.negrita ? 700 : 400 }}
        >
          {linea.texto || ' '}
        </div>
      ))}
    </div>
  )
}
