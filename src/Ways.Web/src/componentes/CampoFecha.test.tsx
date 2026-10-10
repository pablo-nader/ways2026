import { useState } from 'react'
import { fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { CampoFecha } from './CampoFecha'

function Controlado({
  inicial = '',
  alCambiar,
  ...resto
}: {
  inicial?: string
  alCambiar?: (valor: string) => void
  min?: string
  max?: string
  disabled?: boolean
}) {
  const [valor, setValor] = useState(inicial)
  return (
    <>
      <label htmlFor="f">Fecha</label>
      <CampoFecha
        id="f"
        className="form-control"
        value={valor}
        onChange={(v) => {
          setValor(v)
          alCambiar?.(v)
        }}
        {...resto}
      />
      <output data-testid="valor">{valor}</output>
    </>
  )
}

function ConReinicio() {
  const [v, setV] = useState('2026-08-05')
  return (
    <>
      <label htmlFor="f">Fecha</label>
      <CampoFecha id="f" value={v} onChange={setV} />
      <button type="button" onClick={() => setV('')}>
        Reiniciar
      </button>
    </>
  )
}

function EnFormulario({ inicial, alEnviar }: { inicial: string; alEnviar: (valor: string) => void }) {
  const [v, setV] = useState(inicial)
  return (
    <form
      onSubmit={(e) => {
        e.preventDefault()
        alEnviar(v)
      }}
    >
      <label htmlFor="f">Fecha</label>
      <CampoFecha id="f" value={v} onChange={setV} />
      <button type="submit">Enviar</button>
    </form>
  )
}

const campo = () => screen.getByLabelText('Fecha')
const valor = () => screen.getByTestId('valor').textContent

describe('CampoFecha', () => {
  it('muestra el valor ISO como DD/MM/AAAA y deja el placeholder', () => {
    render(<Controlado inicial="2026-08-05" />)
    expect(campo()).toHaveValue('05/08/2026')
    expect(campo()).toHaveAttribute('placeholder', 'dd/mm/aaaa')
  })

  it('un valor vacío se muestra vacío', () => {
    render(<Controlado />)
    expect(campo()).toHaveValue('')
  })

  it('inserta las barras mientras se tipean dígitos y emite el ISO al completar la fecha', async () => {
    const alCambiar = vi.fn()
    render(<Controlado alCambiar={alCambiar} />)

    await userEvent.type(campo(), '05082026')

    expect(campo()).toHaveValue('05/08/2026')
    expect(valor()).toBe('2026-08-05')
    expect(alCambiar).toHaveBeenCalledTimes(1)
    expect(alCambiar).toHaveBeenCalledWith('2026-08-05')
  })

  it('no emite nada mientras la fecha está incompleta', async () => {
    const alCambiar = vi.fn()
    render(<Controlado alCambiar={alCambiar} />)

    await userEvent.type(campo(), '0508')

    expect(campo()).toHaveValue('05/08')
    expect(alCambiar).not.toHaveBeenCalled()
  })

  it('al salir completa día y mes de un dígito y el año de dos', async () => {
    render(<Controlado />)

    await userEvent.type(campo(), '5/8/26')
    await userEvent.tab()

    expect(campo()).toHaveValue('05/08/2026')
    expect(valor()).toBe('2026-08-05')
  })

  it('una fecha inexistente de diez caracteres se marca is-invalid sin salir del campo y no emite', async () => {
    const alCambiar = vi.fn()
    render(<Controlado alCambiar={alCambiar} />)

    await userEvent.type(campo(), '31/02/2026')

    expect(campo()).toHaveClass('is-invalid')
    expect(campo()).toHaveAttribute('aria-invalid', 'true')
    expect(valor()).toBe('')
    expect(alCambiar).not.toHaveBeenCalled()
  })

  it('una fecha inexistente con año corto se marca is-invalid recién al salir del campo', async () => {
    render(<Controlado />)

    await userEvent.type(campo(), '31/02/26')
    expect(campo()).not.toHaveClass('is-invalid')

    await userEvent.tab()
    expect(campo()).toHaveClass('is-invalid')
    expect(campo()).toHaveAttribute('aria-invalid', 'true')
    expect(valor()).toBe('')
  })

  it('un texto incompleto no se marca inválido hasta salir del campo', async () => {
    render(<Controlado />)

    await userEvent.type(campo(), '05/08')
    expect(campo()).not.toHaveClass('is-invalid')

    await userEvent.tab()
    expect(campo()).toHaveClass('is-invalid')
  })

  it('vaciar el texto emite vacío', async () => {
    render(<Controlado inicial="2026-08-05" />)

    await userEvent.clear(campo())

    expect(valor()).toBe('')
    expect(campo()).not.toHaveClass('is-invalid')
  })

  it('editar una fecha válida a una incompleta no emite mientras se tipea', async () => {
    const alCambiar = vi.fn()
    render(<Controlado inicial="2026-08-05" alCambiar={alCambiar} />)

    await userEvent.type(campo(), '{Backspace}')

    expect(campo()).toHaveValue('05/08/202')
    expect(alCambiar).not.toHaveBeenCalled()
    expect(valor()).toBe('2026-08-05')
  })

  it('al salir con el texto incompleto emite vacío, conserva el texto y marca is-invalid', async () => {
    const alCambiar = vi.fn()
    render(<Controlado inicial="2026-08-05" alCambiar={alCambiar} />)

    await userEvent.type(campo(), '{Backspace}')
    await userEvent.tab()

    expect(alCambiar).toHaveBeenCalledTimes(1)
    expect(alCambiar).toHaveBeenCalledWith('')
    expect(campo()).toHaveValue('05/08/202')
    expect(campo()).toHaveClass('is-invalid')
  })

  it('al salir con el texto incompleto y el valor ya vacío no vuelve a emitir', async () => {
    const alCambiar = vi.fn()
    render(<Controlado alCambiar={alCambiar} />)

    await userEvent.type(campo(), '05/08')
    await userEvent.tab()

    expect(alCambiar).not.toHaveBeenCalled()
    expect(campo()).toHaveClass('is-invalid')
  })

  it('Enter confirma el texto pendiente, con el año de dos dígitos expandido, antes del envío del formulario', async () => {
    const enviado = vi.fn()
    render(<EnFormulario inicial="" alEnviar={enviado} />)

    await userEvent.type(campo(), '5/8/26{Enter}')

    expect(enviado).toHaveBeenCalledWith('2026-08-05')
    expect(campo()).toHaveValue('05/08/2026')
  })

  it('Enter con un texto inválido envía vacío y marca is-invalid', async () => {
    const enviado = vi.fn()
    render(<EnFormulario inicial="2026-08-05" alEnviar={enviado} />)

    await userEvent.type(campo(), '{Backspace}{Enter}')

    expect(enviado).toHaveBeenCalledWith('')
    expect(campo()).toHaveClass('is-invalid')
  })

  it('un ISO pegado se muestra como DD/MM/AAAA y emite el valor', async () => {
    render(<Controlado />)

    await userEvent.click(campo())
    await userEvent.paste('2026-08-05')

    expect(campo()).toHaveValue('05/08/2026')
    expect(valor()).toBe('2026-08-05')
  })

  it('rechaza años anteriores a 1900', async () => {
    render(<Controlado />)

    await userEvent.type(campo(), '05/08/1899')

    expect(campo()).toHaveClass('is-invalid')
    expect(valor()).toBe('')
  })

  it('un cambio externo del valor reemplaza el texto', () => {
    const { rerender } = render(<CampoFecha value="2026-08-05" onChange={() => {}} aria-label="f" />)
    rerender(<CampoFecha value="2027-01-02" onChange={() => {}} aria-label="f" />)
    expect(screen.getByLabelText('f')).toHaveValue('02/01/2027')
  })

  it('un reinicio externo con el texto incompleto (el foco sigue en el campo) lo vacía y no queda is-invalid', async () => {
    render(<ConReinicio />)

    await userEvent.type(campo(), '{Backspace}{Backspace}{Backspace}')
    fireEvent.click(screen.getByRole('button', { name: 'Reiniciar' }))

    expect(campo()).toHaveValue('')
    expect(campo()).not.toHaveClass('is-invalid')
  })

  it('un rerender del padre con el mismo valor no borra lo que el usuario sigue tipeando', async () => {
    const { rerender } = render(<CampoFecha value="" onChange={() => {}} aria-label="f" />)
    await userEvent.type(screen.getByLabelText('f'), '12/05')

    rerender(<CampoFecha value="" onChange={() => {}} aria-label="f" />)

    expect(screen.getByLabelText('f')).toHaveValue('12/05')
  })

  it('respeta min y max: una fecha fuera de rango es inválida y no se emite', async () => {
    render(<Controlado min="2026-08-01" max="2026-08-31" />)

    await userEvent.type(campo(), '01/09/2026')
    expect(campo()).toHaveClass('is-invalid')
    expect(valor()).toBe('')

    await userEvent.clear(campo())
    await userEvent.type(campo(), '31/07/2026')
    expect(campo()).toHaveClass('is-invalid')
    expect(valor()).toBe('')

    await userEvent.clear(campo())
    await userEvent.type(campo(), '15/08/2026')
    expect(campo()).not.toHaveClass('is-invalid')
    expect(valor()).toBe('2026-08-15')
  })

  it('reenvía id y deshabilita también el botón del calendario; min y max quedan solo en el selector nativo', () => {
    const { container } = render(<Controlado disabled min="2026-01-01" max="2026-12-31" />)
    const selector = container.querySelector('input[type="date"]') as HTMLInputElement

    expect(campo()).toBeDisabled()
    expect(campo()).not.toHaveAttribute('max')
    expect(campo()).not.toHaveAttribute('min')
    expect(selector).toHaveAttribute('max', '2026-12-31')
    expect(selector).toHaveAttribute('min', '2026-01-01')
    expect(screen.getByRole('button', { name: 'Abrir calendario' })).toBeDisabled()
  })

  it('el botón del calendario abre el selector nativo con showPicker', async () => {
    const showPicker = vi.fn()
    const original = HTMLInputElement.prototype.showPicker
    HTMLInputElement.prototype.showPicker = showPicker
    try {
      render(<Controlado />)
      await userEvent.click(screen.getByRole('button', { name: 'Abrir calendario' }))
      expect(showPicker).toHaveBeenCalledTimes(1)
    } finally {
      HTMLInputElement.prototype.showPicker = original
    }
  })

  it('el selector nativo queda fuera del árbol de accesibilidad y no es enfocable', () => {
    const { container } = render(<Controlado />)
    const selector = container.querySelector('input[type="date"]') as HTMLInputElement

    expect(selector).toHaveAttribute('aria-hidden', 'true')
    expect(selector).toHaveAttribute('inert')
  })

  it('elegir una fecha en el selector nativo actualiza el texto y emite el ISO', () => {
    const { container } = render(<Controlado />)
    const selector = container.querySelector('input[type="date"]') as HTMLInputElement

    fireEvent.change(selector, { target: { value: '2026-12-24' } })

    expect(campo()).toHaveValue('24/12/2026')
    expect(valor()).toBe('2026-12-24')
  })

  it('una fecha del selector nativo fuera de min y max no se emite', () => {
    const { container } = render(<Controlado min="2026-08-01" max="2026-08-31" />)
    const selector = container.querySelector('input[type="date"]') as HTMLInputElement

    fireEvent.change(selector, { target: { value: '2026-09-15' } })

    expect(campo()).toHaveValue('')
    expect(valor()).toBe('')
  })

  it('una fecha del selector nativo anterior a 1900 no se emite', () => {
    const { container } = render(<Controlado />)
    const selector = container.querySelector('input[type="date"]') as HTMLInputElement

    fireEvent.change(selector, { target: { value: '0001-01-01' } })

    expect(valor()).toBe('')
  })

  it('vaciar el selector nativo vacía el campo', () => {
    const { container } = render(<Controlado inicial="2026-08-05" />)
    const selector = container.querySelector('input[type="date"]') as HTMLInputElement

    fireEvent.change(selector, { target: { value: '' } })

    expect(campo()).toHaveValue('')
    expect(valor()).toBe('')
  })
})
