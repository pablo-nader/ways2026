export function Cargando({ texto = 'Cargando…' }: { texto?: string }) {
  return (
    <div className="d-flex justify-content-center align-items-center py-5">
      <div className="spinner-border text-secondary me-3" role="status" aria-hidden="true" />
      <span className="text-muted">{texto}</span>
    </div>
  )
}
