import { fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ConfirmacionDeBaja } from './ConfirmacionDeBaja'

// stage-20-organizacion-relaciones-y-bajas, slice 5, judgment-day ronda 1 (C1 y C5). La puerta es
// COMPARTIDA por las cuatro pantallas raíz, así que su disciplina modal se prueba UNA vez acá y
// vale para las cuatro (`react-async-state` regla 10 por construcción).

/** Anfitrión mínimo: la puerta se monta y se desmonta como en las pantallas —`{abierta && …}`— y el
 * botón "Baja" es el disparador cuyo foco hay que devolver al cerrarla. */
function Anfitrion({ ocupado = false, alConfirmar = () => {} }: { ocupado?: boolean; alConfirmar?: () => void }) {
  const [abierta, setAbierta] = useState(false)

  return (
    <>
      <button type="button" onClick={() => setAbierta(true)} disabled={abierta}>
        Baja
      </button>
      <input aria-label="otro control" disabled={abierta} />
      {abierta && (
        <ConfirmacionDeBaja
          titulo={'el tenant "Comercio Sur"'}
          ocupado={ocupado}
          onConfirmar={alConfirmar}
          onCancelar={() => setAbierta(false)}
        />
      )}
    </>
  )
}

describe('ConfirmacionDeBaja (slice 5, ronda 1 — disciplina modal)', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
  })

  /**
   * Cláusula bajo prueba: `aria-modal="true"` sobre el `role="alertdialog"`. Sin él la puerta se
   * anuncia como un aviso más y no como algo que reclama la pantalla, que es exactamente lo que
   * hace: mientras está abierta, todo lo demás queda inerte.
   */
  it('se anuncia como modal, no como un aviso al costado', async () => {
    const usuario = userEvent.setup()
    render(<Anfitrion />)

    await usuario.click(screen.getByRole('button', { name: 'Baja' }))

    expect(screen.getByRole('alertdialog', { name: 'Confirmar baja' })).toHaveAttribute('aria-modal', 'true')
  })

  /**
   * Cláusula bajo prueba: el `cancelarRef.current?.focus()` del efecto de apertura. Sin él el foco
   * se queda en el disparador —o en el `body`, si la fila desapareció— y quien navega por teclado
   * tiene que buscar a ciegas una puerta que acaba de reclamar la pantalla.
   */
  it('al abrirse se lleva el foco a Cancelar', async () => {
    const usuario = userEvent.setup()
    render(<Anfitrion />)

    await usuario.click(screen.getByRole('button', { name: 'Baja' }))

    expect(screen.getByRole('button', { name: 'Cancelar' })).toHaveFocus()
  })

  /**
   * Cláusula bajo prueba: el `return () => disparadorRef.current?.focus()` de ese mismo efecto.
   * Cerrar sin devolver el foco lo manda al `body` y pierde el lugar de la tabla.
   */
  it('al cerrarse devuelve el foco al disparador', async () => {
    const usuario = userEvent.setup()
    render(<Anfitrion />)

    const disparador = screen.getByRole('button', { name: 'Baja' })
    await usuario.click(disparador)
    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(disparador).toHaveFocus()
  })

  /**
   * Cláusula bajo prueba: el listener de `Escape` sobre `document`. Es la salida que todo diálogo
   * modal debe tener; sin ella la puerta solo se cierra con el mouse.
   */
  it('Escape equivale a Cancelar', async () => {
    const usuario = userEvent.setup()
    const alConfirmar = vi.fn()
    render(<Anfitrion alConfirmar={alConfirmar} />)

    await usuario.click(screen.getByRole('button', { name: 'Baja' }))
    fireEvent.keyDown(document, { key: 'Escape' })

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(alConfirmar).not.toHaveBeenCalled()
  })

  /**
   * Cláusula bajo prueba: el `|| ocupado` del listener de `Escape`. Con la escritura en vuelo los
   * dos botones están inertes; que el teclado siga cerrando la puerta la sacaría de la pantalla
   * mientras el DELETE sigue viajando, y el operador perdería de vista qué se está borrando.
   */
  it('con la escritura en vuelo, Escape no cierra nada', async () => {
    const usuario = userEvent.setup()
    render(<Anfitrion ocupado />)

    await usuario.click(screen.getByRole('button', { name: 'Baja' }))
    fireEvent.keyDown(document, { key: 'Escape' })

    expect(screen.getByRole('alertdialog')).toBeInTheDocument()
  })

  /**
   * Cláusula bajo prueba: el preámbulo de `arrastra`. Decía "Se dan de baja junto con él", que es
   * masculino y miente en `Empresas` ("la empresa"). El sujeto no se nombra: la lista ya lo dice.
   */
  it('el preámbulo del arrastre no le pone género al sujeto', () => {
    render(
      <ConfirmacionDeBaja
        titulo={'la empresa "Sur SRL"'}
        arrastra={['Sus puntos de venta activos']}
        ocupado={false}
        onConfirmar={() => {}}
        onCancelar={() => {}}
      />,
    )

    const puerta = screen.getByRole('alertdialog')
    expect(puerta).toHaveTextContent('También se dan de baja:')
    expect(puerta).not.toHaveTextContent(/junto con él/)
  })

  /**
   * Cláusula bajo prueba: los defaults de `pregunta`/`nota`/`etiquetaConfirmar` frente a los
   * valores que le pasa `Tenants` para suspender. El panel dejó de ser específico de la baja
   * (`Tenants` lo reusa para suspender y reactivar) y la nota de la baja lógica no puede colarse
   * en una acción que no borra nada.
   */
  it('reusada para otra acción, cambia verbo y botón y NO habla de baja lógica', () => {
    render(
      <ConfirmacionDeBaja
        titulo={'el tenant "Comercio Sur"'}
        pregunta="Suspender"
        nota={null}
        etiquetaConfirmar="Confirmar suspensión"
        etiquetaEnCurso="Suspendiendo…"
        ocupado={false}
        onConfirmar={() => {}}
        onCancelar={() => {}}
      />,
    )

    const puerta = screen.getByRole('alertdialog', { name: 'Confirmar suspensión' })
    expect(puerta).toHaveTextContent('¿Suspender el tenant "Comercio Sur"?')
    expect(puerta).not.toHaveTextContent(/baja es lógica/)
    expect(screen.getByRole('button', { name: 'Confirmar suspensión' })).toBeInTheDocument()
  })
})
