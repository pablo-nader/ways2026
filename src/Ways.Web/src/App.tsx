import { BrowserRouter, Navigate, Route, Routes } from 'react-router'
import { AuthProvider } from './auth/AuthContext'
import { RutaProtegida } from './auth/RutaProtegida'
import { Layout } from './componentes/Layout'
import { Inicio } from './paginas/Inicio'
import { Login } from './paginas/Login'
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
          </Route>

          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </AuthProvider>
    </BrowserRouter>
  )
}
