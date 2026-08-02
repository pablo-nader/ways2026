import { BrowserRouter, Navigate, Route, Routes } from 'react-router'
import { AuthProvider } from './auth/AuthContext'
import { RutaProtegida } from './auth/RutaProtegida'
import { Layout } from './componentes/Layout'
import { Categorias } from './paginas/Categorias'
import { CatalogosFiscales } from './paginas/CatalogosFiscales'
import { Inicio } from './paginas/Inicio'
import { Login } from './paginas/Login'
import { NuevoTenant } from './paginas/NuevoTenant'
import { Parametros } from './paginas/Parametros'
import { RutaCatalogo } from './paginas/RutaCatalogo'
import { Usuarios } from './paginas/Usuarios'
import { ROL } from './api/tipos'

export function App() {
  return (
    <BrowserRouter>
      <AuthProvider>
        <Routes>
          {/* Única ruta sin sesión. */}
          <Route path="/login" element={<Login />} />

          <Route
            element={
              <RutaProtegida>
                <Layout />
              </RutaProtegida>
            }
          >
            <Route path="/" element={<Inicio />} />
            <Route
              path="/usuarios"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Root, ROL.Admin]}>
                  <Usuarios />
                </RutaProtegida>
              }
            />

            {/* Escape hatch de ADR-11: árbol propio, no la máquina genérica. react-router
                prioriza segmentos literales sobre ":recurso" sin importar el orden de
                declaración, pero queda declarada antes por legibilidad. */}
            <Route
              path="/catalogos/categorias"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Admin]}>
                  <Categorias />
                </RutaProtegida>
              }
            />
            {/* Los 4 catálogos restantes pasan por la máquina genérica (ADR-11). */}
            <Route
              path="/catalogos/:recurso"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Admin]}>
                  <RutaCatalogo />
                </RutaProtegida>
              }
            />
            <Route
              path="/catalogos-fiscales"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Root, ROL.Admin]}>
                  <CatalogosFiscales />
                </RutaProtegida>
              }
            />
            <Route
              path="/parametros"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Admin]}>
                  <Parametros />
                </RutaProtegida>
              }
            />
            <Route
              path="/organizacion/nuevo-tenant"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Root]}>
                  <NuevoTenant />
                </RutaProtegida>
              }
            />
          </Route>

          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </AuthProvider>
    </BrowserRouter>
  )
}
