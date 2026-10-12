/** Los tres pasos de una compra. El segundo depende de que haya un WhatsApp al que escribir. */
function pasosDeCompra(conWhatsapp: boolean): readonly string[] {
  return [
    'Elegí tus productos en la tienda online',
    conWhatsapp ? 'Confirmá tu pedido por WhatsApp' : 'Confirmá tu pedido con el comercio',
    'Retiralo en el local o coordiná la entrega',
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
