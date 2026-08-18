import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router'
import { describe, expect, it } from 'vitest'
import { esSaldoAFavor, ResumenSaldoDeProveedor } from './ResumenSaldoDeProveedor'
import type { ProveedorListado } from '../api/tipos'

function renderResumen(saldo: number, idProveedor = 1) {
  return render(<ResumenSaldoDeProveedor saldo={saldo} idProveedor={idProveedor} />, {
    wrapper: ({ children }) => <MemoryRouter>{children}</MemoryRouter>,
  })
}

function proveedorFixture(sobrescribir: Partial<ProveedorListado> = {}): ProveedorListado {
  return {
    id: 42,
    razonSocial: 'Proveedor Cuarenta y Dos SA',
    nombreFantasia: null,
    cuit: null,
    idCondicionFiscal: 1,
    domicilio: null,
    telefono: null,
    email: null,
    vendedor: null,
    celularVendedor: null,
    supervisor: null,
    celularSupervisor: null,
    margen: null,
    observaciones: null,
    activo: true,
    idEmpresa: null,
    ...sobrescribir,
  }
}

// Stub de la pantalla destino real (`/proveedores/:id/cuenta-corriente`) — solo lee
// `location.state.proveedor` y lo vuelca a texto, para discriminar si el LINK REAL (el único
// punto de entrada de producción) efectivamente propagó `state` o no.
function EstadoDeCuentaStub() {
  const location = useLocation()
  const proveedor = (location.state as { proveedor?: ProveedorListado } | null)?.proveedor
  return <div>{proveedor ? `state:${proveedor.razonSocial}` : 'sin-state'}</div>
}

function renderResumenConNavegacionReal(proveedor?: ProveedorListado) {
  return render(
    <MemoryRouter initialEntries={['/origen']}>
      <Routes>
        <Route
          path="/origen"
          element={<ResumenSaldoDeProveedor saldo={0} idProveedor={proveedor?.id ?? 1} proveedor={proveedor} />}
        />
        <Route path="/proveedores/:id/cuenta-corriente" element={<EstadoDeCuentaStub />} />
      </Routes>
    </MemoryRouter>,
  )
}

// ---- esSaldoAFavor: helper puro, sin DOM (web-descriptor-tests) -------------------------------

describe('esSaldoAFavor', () => {
  it('un saldo negativo es "a favor"', () => {
    expect(esSaldoAFavor(-1)).toBe(true)
  })

  it('cero y un saldo positivo NO son "a favor"', () => {
    expect(esSaldoAFavor(0)).toBe(false)
    expect(esSaldoAFavor(1)).toBe(false)
  })
})

// ---- ResumenSaldoDeProveedor -------------------------------------------------------------------

describe('ResumenSaldoDeProveedor', () => {
  it('un saldo positivo muestra el importe, sin el callout de saldo a favor', () => {
    renderResumen(500)
    expect(screen.getByText('$500,00')).toBeInTheDocument()
    expect(screen.queryByText('Saldo a favor.')).not.toBeInTheDocument()
  })

  // mutation target #28 (design.md, tasks.md 6.13): la rama de saldo a favor en
  // `ResumenSaldoDeProveedor.tsx` → borrarla → este test tiene que fallar.
  it('un saldo negativo muestra el importe con signo y el callout "Saldo a favor."', () => {
    renderResumen(-500)
    expect(screen.getByText('-$500,00')).toBeInTheDocument()
    expect(screen.getByText('Saldo a favor.')).toBeInTheDocument()
  })

  it('siempre linkea al estado de cuenta completo del proveedor', () => {
    renderResumen(0, 42)
    expect(screen.getByRole('link', { name: 'Ver estado de cuenta completo' })).toHaveAttribute(
      'href',
      '/proveedores/42/cuenta-corriente',
    )
  })

  // judgment-day stage-15 Slice 6, hallazgo CRITICAL: este Link es el ÚNICO punto de entrada real
  // a `/proveedores/:id/cuenta-corriente` — probarlo con un click real (no con `state` inyectado a
  // mano en el destino) es lo único que discrimina si la navegación real propaga `state` o no.
  it('con el proveedor completo disponible, el click real en el link propaga location.state.proveedor', async () => {
    const proveedor = proveedorFixture()
    renderResumenConNavegacionReal(proveedor)

    await userEvent.click(screen.getByRole('link', { name: 'Ver estado de cuenta completo' }))

    expect(await screen.findByText(`state:${proveedor.razonSocial}`)).toBeInTheDocument()
  })

  it('sin el proveedor completo, el click real en el link navega sin location.state (degrada con gracia)', async () => {
    renderResumenConNavegacionReal(undefined)

    await userEvent.click(screen.getByRole('link', { name: 'Ver estado de cuenta completo' }))

    expect(await screen.findByText('sin-state')).toBeInTheDocument()
  })
})
