import { fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ConfirmacionDeBaja } from './ConfirmacionDeBaja'

// stage-20-organizacion-relaciones-y-bajas, slice 5, judgment-day ronda 1 (C1 y C5). La puerta es
// COMPARTIDA por las cuatro pantallas raíz, así que su disciplina modal se prueba UNA vez acá y
// vale para las cuatro (`react-async-state` regla 10 por construcción).

/** Anfitrión mínimo: la puerta se monta y se desmonta como en las pantallas —`{abierta && …}`— y el
 * botón "Baja" es el disparador cuyo foco hay que devolver al cerrarla. El disparador se captura
 * SÍNCRONAMENTE en el `onClick`, antes de cualquier `setState`, igual que en las cuatro pantallas
 * raíz: cuando la puerta se monta, ese botón ya está `disabled`.
 *
 * `conFixup` reproduce la *focus fixup rule* del navegador: deshabilitar el elemento enfocado le
 * saca el foco y lo manda al `<body>`. jsdom NO la aplica, y tampoco se la puede forzar en su
 * momento real —una vez que el botón quedó `disabled`, jsdom trata `blur()` y `document.body.
 * focus()` como no-ops sobre un área no focusable, así que `document.activeElement` se queda
 * pegado al botón—. Por eso el blur se ADELANTA un tick, al `onClick`, cuando el botón todavía
 * está habilitado: el mecanismo difiere, pero el estado observable es exactamente el que importa
 * —el foco ya está en el `<body>` cuando corre el efecto de montaje de la puerta—, que es donde
 * la captura tardía leía `document.activeElement`. */
function Anfitrion({
  ocupado = false,
  alConfirmar = () => {},
  conFixup = false,
}: {
  ocupado?: boolean
  alConfirmar?: () => void
  conFixup?: boolean
}) {
  const [abierta, setAbierta] = useState(false)
  const [disparador, setDisparador] = useState<HTMLElement | null>(null)

  return (
    <>
      <button
        type="button"
        onClick={(evento) => {
          setDisparador(evento.currentTarget)
          if (conFixup) evento.currentTarget.blur()
          setAbierta(true)
        }}
        disabled={abierta}
      >
        Baja
      </button>
      <input aria-label="otro control" disabled={abierta} />
      {abierta && (
        <ConfirmacionDeBaja
          titulo={'el tenant "Comercio Sur"'}
          ocupado={ocupado}
          disparador={disparador}
          onConfirmar={alConfirmar}
          onCancelar={() => setAbierta(false)}
        />
      )}
    </>
  )
}

/** Anfitrión con la forma real de una pantalla (`Box`: `div.box > header > h5`) y una fila que
 * DESAPARECE al confirmar, que es lo que pasa cuando la baja sale bien. */
function AnfitrionQuePierdeLaFila() {
  const [fila, setFila] = useState(true)
  const [abierta, setAbierta] = useState(false)
  const [disparador, setDisparador] = useState<HTMLElement | null>(null)

  return (
    <div className="box">
      <header>
        <h5>Tenants</h5>
      </header>
      <div className="body p-3">
        {fila && (
          <button
            type="button"
            onClick={(evento) => {
              setDisparador(evento.currentTarget)
              setAbierta(true)
            }}
            disabled={abierta}
          >
            Baja
          </button>
        )}
        {abierta && (
          <ConfirmacionDeBaja
            titulo={'el tenant "Comercio Sur"'}
            ocupado={false}
            disparador={disparador}
            onConfirmar={() => {
              setFila(false)
              setAbierta(false)
            }}
            onCancelar={() => setAbierta(false)}
          />
        )}
      </div>
    </div>
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
   * Cláusula bajo prueba: el `return` del efecto de apertura, que devuelve el foco al disparador
   * recibido por prop. Cerrar sin devolverlo lo manda al `body` y pierde el lugar de la tabla.
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
   * Cláusula bajo prueba (ronda 2, R2-2): que el disparador llegue por PROP —capturado en el
   * `onClick`, antes de cualquier `setState`— y no de un `document.activeElement` leído dentro del
   * efecto de montaje. Ese efecto pasivo corre DESPUÉS del commit que dejó el disparador
   * `disabled`, y un navegador real ya aplicó ahí la *focus fixup rule*: el foco está en el
   * `<body>`, así que la captura tardía se quedaba con el `<body>` y "devolver el foco" era un
   * no-op.
   *
   * Honestidad sobre el entorno: jsdom NO implementa esa corrección de foco, y por eso el defecto
   * pasaba verde. Tampoco se la puede reproducir en su instante real —sobre un botón ya `disabled`,
   * `blur()` y `document.body.focus()` son no-ops en jsdom—, así que el anfitrión (`conFixup`)
   * adelanta el blur al `onClick`. Lo que el test observa es idéntico: cuando la puerta monta, el
   * foco está en el `<body>` y no en el disparador.
   */
  it('captura el disparador antes del render que lo deshabilita, no después', async () => {
    const usuario = userEvent.setup()
    render(<Anfitrion conFixup />)

    const disparador = screen.getByRole('button', { name: 'Baja' })
    await usuario.click(disparador)

    // El navegador ya se llevó el foco al `<body>`: lo único que sabe quién abrió la puerta es la
    // prop capturada en el `onClick`.
    expect(disparador).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancelar' })).toHaveFocus()

    await usuario.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(disparador).toHaveFocus()
    expect(document.body).not.toHaveFocus()
  })

  /**
   * Cláusula bajo prueba (ronda 2, R2-2): la rama de respaldo del cierre —`esAlcanzable` en `false`
   * → `regresoRef`—. Cuando la baja sale bien, la fila que tenía el disparador ya no existe:
   * enfocar un nodo desprendido no hace nada y el foco se queda en el `<body>`, o sea al principio
   * de todo el documento. El respaldo es un punto de referencia estable de la pantalla.
   */
  it('si la fila del disparador desapareció, el foco cae en el título de la pantalla', async () => {
    const usuario = userEvent.setup()
    render(<AnfitrionQuePierdeLaFila />)

    await usuario.click(screen.getByRole('button', { name: 'Baja' }))
    await usuario.click(screen.getByRole('button', { name: 'Confirmar baja' }))

    expect(screen.queryByRole('button', { name: 'Baja' })).not.toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Tenants' })).toHaveFocus()
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
