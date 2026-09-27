import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useContext, useState } from 'react'
import { describe, expect, it } from 'vitest'
import { BorradorDeTicketContext, ProveedorDeBorradoresDeTicket } from './BorradorDeTicketContext'
import type { BorradorDeTicket } from './BorradorDeTicketContext'

const borradorDeEjemplo: BorradorDeTicket = {
  lineas: [],
  precios: {},
  cantidadesEnEdicion: {},
  filasPago: [],
  proximoIdFilaPago: 3,
  clienteSeleccionado: null,
}

/** Consumidor mínimo del contexto — expone `obtener`/`guardar`/`limpiar` por botones para poder
 * ejercitar el almacén desde tests sin depender de `Pos.tsx`. El almacén vive en un `ref` a
 * propósito (nunca fuerza un re-render por sí solo — ver el doc-comment del `Provider`), así que
 * este consumidor se re-renderiza con un contador local cada vez que muta el store, para poder
 * leerlo de nuevo. */
function Consumidor({ clave }: { clave: string }) {
  const almacen = useContext(BorradorDeTicketContext)
  const [, forzarRerender] = useState(0)
  if (!almacen) return <span>sin-provider</span>

  const encontrado = almacen.obtener(clave)

  return (
    <div>
      <span data-testid="estado">{encontrado ? `proximoId:${encontrado.proximoIdFilaPago}` : 'vacio'}</span>
      <button
        type="button"
        onClick={() => {
          almacen.guardar(clave, borradorDeEjemplo)
          forzarRerender((n) => n + 1)
        }}
      >
        guardar
      </button>
      <button
        type="button"
        onClick={() => {
          almacen.limpiar(clave)
          forzarRerender((n) => n + 1)
        }}
      >
        limpiar
      </button>
    </div>
  )
}

describe('BorradorDeTicketContext', () => {
  it('sin Provider en el árbol, el contexto es null (no-op)', () => {
    render(<Consumidor clave="libre:1" />)
    expect(screen.getByText('sin-provider')).toBeInTheDocument()
  })

  it('guardar y luego obtener devuelve exactamente el borrador guardado', async () => {
    render(
      <ProveedorDeBorradoresDeTicket>
        <Consumidor clave="libre:1" />
      </ProveedorDeBorradoresDeTicket>,
    )
    expect(screen.getByTestId('estado')).toHaveTextContent('vacio')

    await userEvent.click(screen.getByRole('button', { name: 'guardar' }))
    expect(screen.getByTestId('estado')).toHaveTextContent('proximoId:3')
  })

  it('limpiar borra el borrador de esa clave', async () => {
    render(
      <ProveedorDeBorradoresDeTicket>
        <Consumidor clave="libre:1" />
      </ProveedorDeBorradoresDeTicket>,
    )
    await userEvent.click(screen.getByRole('button', { name: 'guardar' }))
    expect(screen.getByTestId('estado')).toHaveTextContent('proximoId:3')

    await userEvent.click(screen.getByRole('button', { name: 'limpiar' }))
    expect(screen.getByTestId('estado')).toHaveTextContent('vacio')
  })

  it('dos claves distintas no se pisan entre sí (aislamiento por clave)', async () => {
    function DosConsumidores() {
      return (
        <>
          <div data-testid="a">
            <Consumidor clave="libre:1" />
          </div>
          <div data-testid="b">
            <Consumidor clave="libre:2" />
          </div>
        </>
      )
    }

    render(
      <ProveedorDeBorradoresDeTicket>
        <DosConsumidores />
      </ProveedorDeBorradoresDeTicket>,
    )

    const contenedorA = screen.getByTestId('a')
    const contenedorB = screen.getByTestId('b')

    await userEvent.click(contenedorA.querySelector('button')!)

    expect(contenedorA.querySelector('[data-testid="estado"]')).toHaveTextContent('proximoId:3')
    expect(contenedorB.querySelector('[data-testid="estado"]')).toHaveTextContent('vacio')
  })
})
