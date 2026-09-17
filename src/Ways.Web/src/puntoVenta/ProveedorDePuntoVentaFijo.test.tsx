import { act, render, screen } from '@testing-library/react'
import { useState } from 'react'
import { describe, expect, it } from 'vitest'
import { ProveedorDePuntoVentaFijo } from './ProveedorDePuntoVentaFijo'
import { usePuntoVenta } from './usePuntoVenta'
import type { PuntoVentaListado } from '../api/tipos'

function puntoVentaFixture(sobrescribir: Partial<PuntoVentaListado> = {}): PuntoVentaListado {
  return {
    id: 7,
    idTenant: 1,
    idEmpresa: 3,
    nombre: 'Local Centro',
    domicilio: null,
    horario: null,
    whatsapp: null,
    instagram: null,
    facebook: null,
    web: null,
    nombreTenant: 'Tenant Demo',
    razonSocialEmpresa: 'Empresa Demo',
    ...sobrescribir,
  }
}

function Consumidor() {
  const { puntoVenta, puntosVenta, elegir, recargar } = usePuntoVenta()
  const [recargado, setRecargado] = useState(false)

  return (
    <div>
      <span>puntoVenta:{puntoVenta?.id ?? 'ninguno'}</span>
      <span>cantidad:{puntosVenta.length}</span>
      <span>recargado:{recargado ? 'si' : 'no'}</span>
      <button type="button" onClick={() => elegir(999)}>
        elegir otro
      </button>
      <button type="button" onClick={() => void recargar().then(() => setRecargado(true))}>
        recargar
      </button>
    </div>
  )
}

describe('ProveedorDePuntoVentaFijo', () => {
  it('expone el punto de venta fijo como único elemento de puntosVenta', () => {
    render(
      <ProveedorDePuntoVentaFijo puntoVenta={puntoVentaFixture()}>
        <Consumidor />
      </ProveedorDePuntoVentaFijo>,
    )

    expect(screen.getByText('puntoVenta:7')).toBeInTheDocument()
    expect(screen.getByText('cantidad:1')).toBeInTheDocument()
  })

  it('elegir() es un no-op: nunca hay otro punto de venta al que cambiar', () => {
    render(
      <ProveedorDePuntoVentaFijo puntoVenta={puntoVentaFixture()}>
        <Consumidor />
      </ProveedorDePuntoVentaFijo>,
    )

    act(() => screen.getByRole('button', { name: 'elegir otro' }).click())

    expect(screen.getByText('puntoVenta:7')).toBeInTheDocument()
  })

  it('recargar() resuelve sin pedir nada al servidor', async () => {
    render(
      <ProveedorDePuntoVentaFijo puntoVenta={puntoVentaFixture()}>
        <Consumidor />
      </ProveedorDePuntoVentaFijo>,
    )

    await act(async () => {
      screen.getByRole('button', { name: 'recargar' }).click()
    })

    expect(screen.getByText('recargado:si')).toBeInTheDocument()
  })
})
