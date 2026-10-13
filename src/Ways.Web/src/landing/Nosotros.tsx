/** Quiénes somos: el título a la izquierda y el texto del comercio (texto plano, con sus saltos de línea) a la derecha. */
export function Nosotros({ texto }: { texto: string | null }) {
  const contenido = texto?.trim()
  if (!contenido) return null

  return (
    <section className="publico-seccion publico-nosotros" aria-labelledby="nosotros-titulo">
      <div>
        <h2 id="nosotros-titulo" className="publico-titulo publico-seccion-titulo">
          Quiénes somos
        </h2>
        <div className="publico-barra publico-barra-fina" aria-hidden="true" />
      </div>
      <p className="publico-descripcion">{contenido}</p>
    </section>
  )
}
