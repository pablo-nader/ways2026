import { NavLink, Outlet, useNavigate } from 'react-router'
import { useAuth } from '../auth/useAuth'
import { DESCRIPTORES_DE_CATALOGO } from '../api/catalogos'
import { ROL, puedeAprovisionarTenants, puedeGestionarCatalogos, puedeGestionarUsuarios, puedeOperarPos } from '../api/tipos'

export function Layout() {
  const { usuario, cerrarSesion } = useAuth()
  const navegar = useNavigate()

  async function salir() {
    await cerrarSesion()
    navegar('/login', { replace: true })
  }

  return (
    <div id="wrap" className="bg-dark dk">
      <div id="top">
        {/* Franja de color del punto de venta. Hoy es fija; cuando exista la tabla
            de puntos de venta vuelve a pintar según el local activo. */}
        <nav className="ways-nav color_1" />

        <nav className="navbar navbar-dark navbar-expand-lg bg-dark border-top-0">
          <div className="container">
            <NavLink className="navbar-brand ways-brand" to="/">
              Ways
            </NavLink>

            <div className="collapse navbar-collapse show">
              <ul className="navbar-nav">
                <li className="nav-item">
                  <NavLink className="nav-link" to="/">
                    Inicio
                  </NavLink>
                </li>
                {usuario && puedeOperarPos(usuario.rolId) && (
                  <li className="nav-item">
                    <NavLink className="nav-link" to="/pos">
                      POS
                    </NavLink>
                  </li>
                )}
                {usuario && puedeGestionarUsuarios(usuario.rolId) && (
                  <li className="nav-item">
                    <NavLink className="nav-link" to="/usuarios">
                      Usuarios
                    </NavLink>
                  </li>
                )}
                {usuario && puedeGestionarCatalogos(usuario.rolId) && (
                  <>
                    <li className="nav-item">
                      <NavLink className="nav-link" to="/clientes">
                        Clientes
                      </NavLink>
                    </li>
                    <li className="nav-item">
                      <NavLink className="nav-link" to="/proveedores">
                        Proveedores
                      </NavLink>
                    </li>
                    <li className="nav-item">
                      <NavLink className="nav-link" to="/articulos">
                        Artículos
                      </NavLink>
                    </li>
                    <li className="nav-item">
                      <NavLink className="nav-link" to="/listas-precio">
                        Listas de precio
                      </NavLink>
                    </li>
                    <li className="nav-item">
                      <NavLink className="nav-link" to="/ofertas">
                        Ofertas
                      </NavLink>
                    </li>
                    {Object.values(DESCRIPTORES_DE_CATALOGO).map((d) => (
                      <li className="nav-item" key={d.recurso}>
                        <NavLink className="nav-link" to={`/catalogos/${d.recurso}`}>
                          {d.titulo}
                        </NavLink>
                      </li>
                    ))}
                    <li className="nav-item">
                      <NavLink className="nav-link" to="/catalogos/categorias">
                        Categorías
                      </NavLink>
                    </li>
                    <li className="nav-item">
                      <NavLink className="nav-link" to="/parametros">
                        Parámetros
                      </NavLink>
                    </li>
                    <li className="nav-item">
                      <NavLink className="nav-link" to="/catalogos-fiscales">
                        Catálogos fiscales
                      </NavLink>
                    </li>
                  </>
                )}
                {usuario && (usuario.rolId === ROL.Root || usuario.rolId === ROL.Admin) && (
                  <>
                    <li className="nav-item">
                      <NavLink className="nav-link" to="/organizacion/empresas">
                        Empresas
                      </NavLink>
                    </li>
                    <li className="nav-item">
                      <NavLink className="nav-link" to="/organizacion/puntos-venta">
                        Puntos de venta
                      </NavLink>
                    </li>
                  </>
                )}
                {usuario && puedeAprovisionarTenants(usuario.rolId) && (
                  <>
                    <li className="nav-item">
                      <NavLink className="nav-link" to="/organizacion/tenants">
                        Tenants
                      </NavLink>
                    </li>
                    <li className="nav-item">
                      <NavLink className="nav-link" to="/organizacion/nuevo-tenant">
                        Nuevo tenant
                      </NavLink>
                    </li>
                  </>
                )}
              </ul>
            </div>

            <div className="d-flex align-items-center gap-3">
              <span className="text-light small">
                {usuario?.usuario} <span className="text-secondary">· {usuario?.rol}</span>
              </span>
              <button
                type="button"
                className="btn btn-danger rounded-0"
                title="Salir"
                onClick={salir}
              >
                Salir
              </button>
            </div>
          </div>
        </nav>
      </div>

      <div id="content">
        <div className="outer">
          <div className="inner bg-light lter">
            <Outlet />
          </div>
        </div>
      </div>
    </div>
  )
}
