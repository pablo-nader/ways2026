import { BrowserRouter, Navigate, Route, Routes } from 'react-router'
import { AuthProvider } from './auth/AuthContext'
import { RutaProtegida } from './auth/RutaProtegida'
import { Layout } from './componentes/Layout'
import { Categorias } from './paginas/Categorias'
import { CatalogosFiscales } from './paginas/CatalogosFiscales'
import { Clientes } from './paginas/Clientes'
import { Empresas } from './paginas/Empresas'
import { Inicio } from './paginas/Inicio'
import { Login } from './paginas/Login'
import { NuevoTenant } from './paginas/NuevoTenant'
import { Parametros } from './paginas/Parametros'
import { Proveedores } from './paginas/Proveedores'
import { PuntosVenta } from './paginas/PuntosVenta'
import { RutaCatalogo } from './paginas/RutaCatalogo'
import { Tenants } from './paginas/Tenants'
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

            {/* Entidad dedicada (stage-2-clientes-proveedores, design decision 1): árbol
                propio, no la máquina genérica de catálogos. */}
            <Route
              path="/clientes"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Admin]}>
                  <Clientes />
                </RutaProtegida>
              }
            />

            {/* Entidad dedicada (stage-2-clientes-proveedores, design decision 1): árbol
                propio, no la máquina genérica de catálogos — mismo criterio que /clientes. */}
            <Route
              path="/proveedores"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Admin]}>
                  <Proveedores />
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
              path="/organizacion/tenants"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Root]}>
                  <Tenants />
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
            <Route
              path="/organizacion/empresas"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Root, ROL.Admin]}>
                  <Empresas />
                </RutaProtegida>
              }
            />
            <Route
              path="/organizacion/puntos-venta"
              element={
                <RutaProtegida rolesPermitidos={[ROL.Root, ROL.Admin]}>
                  <PuntosVenta />
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
