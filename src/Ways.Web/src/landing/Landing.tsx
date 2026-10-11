import { hrefDeAdmin } from '../config/rutasPublicas'

export function Landing() {
  return (
    <main className="min-vh-100 d-flex flex-column align-items-center justify-content-center gap-3 p-4 text-center">
      <h1 className="display-3 fw-bold text-primary m-0">Ways</h1>
      <p className="lead text-body-secondary m-0">La página de este comercio se está preparando.</p>
      <a className="btn btn-primary" href={hrefDeAdmin('/login')}>
        Ingresar
      </a>
    </main>
  )
}
