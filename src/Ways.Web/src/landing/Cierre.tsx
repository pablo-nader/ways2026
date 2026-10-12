import { PREFIJO_ONLINE } from '../config/rutasPublicas'
import { Chevrones, GlifoWhatsapp } from '../publico/Glifos'

/**
 * Banda final sobre el color de acento: repite los dos botones de la portada. Es lo último de la página, no hay pie.
 * Invita a pedir solo si hay un WhatsApp, que es el canal real para hacerlo; sin él, invita a conocer la tienda.
 */
export function Cierre({ whatsapp }: { whatsapp: string | null }) {
  return (
    <section className="publico-cierre" aria-labelledby="cierre-titulo">
      <div className="publico-pagina publico-cierre-contenido">
        <Chevrones className="publico-cierre-chevrones" />
        <h2 id="cierre-titulo" className="publico-titulo publico-cierre-titulo">
          {whatsapp ? '¿Listo para pedir?' : '¿Querés saber más?'}
        </h2>
        <div className="publico-cierre-acciones">
          <a className="publico-boton publico-boton-tinta" href={PREFIJO_ONLINE}>
            Comprar online
          </a>
          {whatsapp && (
            <a className="publico-boton publico-boton-contorno-tinta" href={whatsapp} target="_blank" rel="noopener">
              <GlifoWhatsapp />
              Escribinos por WhatsApp
            </a>
          )}
        </div>
      </div>
    </section>
  )
}
