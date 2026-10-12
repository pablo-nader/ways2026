/**
 * Los tres pasos de una compra, tal como funciona hoy: la tienda online solo muestra el catálogo (el carrito no
 * envía pedidos) y no hay entrega a domicilio, así que el pedido se coordina con el comercio. El segundo paso
 * depende de que haya un WhatsApp al que escribir.
 */
function pasosDeCompra(conWhatsapp: boolean): readonly string[] {
  return [
    'Mirá lo que hay en la tienda online',
    conWhatsapp ? 'Escribinos por WhatsApp con lo que necesitás' : 'Consultá al comercio por lo que necesitás',
    'Coordiná con el comercio el retiro en el local',
  ]
}

export function ComoComprar({ conWhatsapp }: { conWhatsapp: boolean }) {
  return (
    <section className="publico-seccion" aria-labelledby="como-comprar-titulo">
      <h2 id="como-comprar-titulo" className="publico-titulo publico-seccion-titulo">
        Cómo comprar
      </h2>
      <div className="publico-barra publico-barra-fina" aria-hidden="true" />
      <ol className="publico-pasos">
        {pasosDeCompra(conWhatsapp).map((paso, indice) => (
          <li key={paso} className="publico-paso">
            <span className="publico-titulo publico-paso-numero" aria-hidden="true">
              {String(indice + 1).padStart(2, '0')}
            </span>
            <h3 className="publico-paso-texto">{paso}</h3>
          </li>
        ))}
      </ol>
    </section>
  )
}
