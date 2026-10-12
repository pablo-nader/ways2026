export function Descripcion({ texto }: { texto: string | null }) {
  const contenido = texto?.trim()
  if (!contenido) return null

  return (
    <section className="publico-seccion" aria-label="Sobre el comercio">
      <p className="publico-descripcion">{contenido}</p>
    </section>
  )
}
