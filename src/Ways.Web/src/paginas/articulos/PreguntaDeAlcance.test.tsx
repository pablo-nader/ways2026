import { createRef } from 'react'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { PreguntaDeAlcance } from './PreguntaDeAlcance'

const CONTEXTO = 'Este artículo es parte de la familia "Sabores" (3 artículos).'

function montar(props: Partial<Parameters<typeof PreguntaDeAlcance>[0]> = {}) {
  const onElegir = vi.fn()
  const onCancelar = vi.fn()
  render(<PreguntaDeAlcance contexto={CONTEXTO} ocupado={false} onElegir={onElegir} onCancelar={onCancelar} {...props} />)
  return { onElegir, onCancelar }
}

describe('PreguntaDeAlcance', () => {
  it('abre con el contexto y hace la pregunta', () => {
    montar()

    expect(screen.getByText(CONTEXTO, { exact: false })).toBeInTheDocument()
    expect(screen.getByText('¿Aplicar el cambio a toda la familia?')).toBeInTheDocument()
  })

  it('explica qué hace cada respuesta', () => {
    montar()

    expect(
      screen.getByText(
        'Con "Toda la familia", el cambio llega a todos sus artículos. Con "Solo este artículo", este sale de la familia y el cambio queda solo en él.',
      ),
    ).toBeInTheDocument()
  })

  it('sin campos que cambian (un precio) no lista nada y no habla de campos propios', () => {
    montar()

    expect(screen.queryByText(/Campos compartidos que cambian/)).not.toBeInTheDocument()
    expect(screen.queryByText(/Los campos propios/)).not.toBeInTheDocument()
  })

  it('con campos que cambian los lista y aclara que los propios se guardan solo en este artículo', () => {
    montar({ cambios: ['Costo de lista', 'Controla lote'] })

    expect(screen.getByText('Campos compartidos que cambian: Costo de lista, Controla lote.')).toBeInTheDocument()
    expect(
      screen.getByText(/Los campos propios \(nombre, descripción, códigos, marca, activo y disponibilidad por empresa\) se guardan siempre solo en este artículo\./),
    ).toBeInTheDocument()
  })

  it('"Toda la familia" responde Familia, y "Solo este artículo" responde SoloEste: por nombre, nunca por posición', async () => {
    const { onElegir } = montar()

    await userEvent.click(screen.getByRole('button', { name: 'Toda la familia' }))
    await userEvent.click(screen.getByRole('button', { name: 'Solo este artículo (sale de la familia)' }))

    expect(onElegir.mock.calls).toEqual([['Familia'], ['SoloEste']])
  })

  /** Cláusula bajo prueba: `ref={refDeCancelar}` del botón "Cancelar": quien aloja la pregunta lo enfoca al abrirla.
   * Evidencia de mutación (mutation-proof-tests): sacar el `ref` hace fallar este test y la prueba de foco al abrir de
   * `EditorDePrecios.test.tsx`; revertido, vuelven a verde. */
  it('expone su botón "Cancelar" por refDeCancelar', () => {
    const refDeCancelar = createRef<HTMLButtonElement>()

    montar({ refDeCancelar })

    expect(refDeCancelar.current).toBe(screen.getByRole('button', { name: 'Cancelar' }))
  })

  it('"Cancelar" cancela y no elige nada', async () => {
    const { onElegir, onCancelar } = montar()

    await userEvent.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(onCancelar).toHaveBeenCalledTimes(1)
    expect(onElegir).not.toHaveBeenCalled()
  })

  it('con la escritura en vuelo las tres respuestas quedan inertes', async () => {
    const { onElegir, onCancelar } = montar({ ocupado: true })

    for (const boton of screen.getAllByRole('button')) {
      expect(boton).toBeDisabled()
      await userEvent.click(boton)
    }

    expect(onElegir).not.toHaveBeenCalled()
    expect(onCancelar).not.toHaveBeenCalled()
  })
})
