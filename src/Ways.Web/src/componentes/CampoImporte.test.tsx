import { createRef } from 'react'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { CampoImporte } from './CampoImporte'

function CampoControlado({
  valorInicial = null,
  disabled = false,
  admiteNegativos = false,
  onChange,
}: {
  valorInicial?: number | null
  disabled?: boolean
  admiteNegativos?: boolean
  onChange?: (valor: number | null) => void
}) {
  // Envoltorio mínimo que reproduce el uso real: el padre guarda el valor y se lo devuelve
  // al campo (controlado), en vez de simular estado dentro del propio test.
  const ref = createRef<HTMLInputElement>()
  return (
    <CampoImporte
      ref={ref}
      valor={valorInicial}
      onChange={onChange ?? (() => {})}
      disabled={disabled}
      admiteNegativos={admiteNegativos}
      aria-label="importe"
    />
  )
}

describe('CampoImporte', () => {
  it('muestra el valor inicial ya formateado', () => {
    render(<CampoControlado valorInicial={1234.5} />)
    expect(screen.getByLabelText('importe')).toHaveValue('1.234,50')
  })

  it('un valor null se muestra vacío', () => {
    render(<CampoControlado valorInicial={null} />)
    expect(screen.getByLabelText('importe')).toHaveValue('')
  })

  it('al tipear, deja pasar dígitos y una coma, y descarta letras', async () => {
    const usuario = userEvent.setup()
    render(<CampoControlado />)
    const campo = screen.getByLabelText('importe')
    await usuario.type(campo, '1234,5a')
    expect(campo).toHaveValue('1234,5')
  })

  it('al perder el foco, reformatea a "1.234,56"', async () => {
    const usuario = userEvent.setup()
    render(<CampoControlado />)
    const campo = screen.getByLabelText('importe')
    await usuario.type(campo, '1234,5')
    await usuario.tab()
    expect(campo).toHaveValue('1.234,50')
  })

  it('tolera "." tipeado como miles: se descarta mientras edita y no rompe el número final', async () => {
    const usuario = userEvent.setup()
    render(<CampoControlado />)
    const campo = screen.getByLabelText('importe')
    await usuario.type(campo, '1.234,56')
    expect(campo).toHaveValue('1234,56')
    await usuario.tab()
    expect(campo).toHaveValue('1.234,56')
  })

  it('onChange recibe el número parseado en cada tecleo', async () => {
    const onChange = vi.fn()
    const usuario = userEvent.setup()
    render(<CampoControlado onChange={onChange} />)
    const campo = screen.getByLabelText('importe')
    await usuario.type(campo, '5')
    expect(onChange).toHaveBeenLastCalledWith(5)
    await usuario.type(campo, '0')
    expect(onChange).toHaveBeenLastCalledWith(50)
  })

  it('onChange recibe null mientras el texto no es un importe completo (ej. coma sin decimales)', async () => {
    const onChange = vi.fn()
    const usuario = userEvent.setup()
    render(<CampoControlado onChange={onChange} />)
    const campo = screen.getByLabelText('importe')
    await usuario.type(campo, '1234,')
    expect(onChange).toHaveBeenLastCalledWith(null)
  })

  it('onChange recibe null cuando el campo queda vacío', async () => {
    const onChange = vi.fn()
    const usuario = userEvent.setup()
    render(<CampoControlado valorInicial={10} onChange={onChange} />)
    const campo = screen.getByLabelText('importe')
    await usuario.clear(campo)
    expect(onChange).toHaveBeenLastCalledWith(null)
  })

  it('no deja tipear más dígitos decimales que `decimales` (default 2): el tercer dígito se descarta', async () => {
    const usuario = userEvent.setup()
    render(<CampoControlado />)
    const campo = screen.getByLabelText('importe')
    await usuario.type(campo, '1234,567')
    expect(campo).toHaveValue('1234,56')
  })

  it('en el blur, el texto y el valor emitido SIEMPRE coinciden (nunca "1.234,57" en pantalla con 1234.567 emitido)', async () => {
    const onChange = vi.fn()
    const usuario = userEvent.setup()
    render(<CampoControlado onChange={onChange} />)
    const campo = screen.getByLabelText('importe')
    await usuario.type(campo, '1234,567')
    await usuario.tab()
    // El tope de dígitos decimales ya recortó "567" a "56" — no hay margen para que blur y
    // onChange diverjan: los dos leen la MISMA `redondearImporte` (formato/importes.ts).
    expect(onChange).toHaveBeenLastCalledWith(1234.56)
    expect(campo).toHaveValue('1.234,56')
  })

  it('cada tecleo emite el mismo número que `formatearImporte` reproduciría del texto en pantalla en ese instante', async () => {
    const valores: (number | null)[] = []
    const onChange = vi.fn((v: number | null) => valores.push(v))
    const usuario = userEvent.setup()
    render(<CampoControlado onChange={onChange} />)
    const campo = screen.getByLabelText('importe') as HTMLInputElement

    for (const caracter of '1234,567') {
      await usuario.type(campo, caracter)
      const ultimo = valores.at(-1)
      if (ultimo === null || ultimo === undefined) continue
      // Reformatear lo emitido debe reproducir el número que hay tipeado en ese momento (sin
      // agrupado de miles, que solo se aplica en el blur) — nunca una precisión que el campo
      // todavía no muestra.
      const sinMiles = (n: number) => n.toString().replace('.', ',')
      expect(sinMiles(ultimo)).toBe(campo.value)
    }
  })

  it('si `decimales` cambia mientras el campo sigue enfocado, el blur re-redondea a la nueva precisión (mutation target: quitar `redondearImporte` del blur)', async () => {
    const onChange = vi.fn()
    const usuario = userEvent.setup()
    const { rerender } = render(<CampoImporte valor={null} decimales={2} onChange={onChange} aria-label="importe-decimales" />)
    const campo = screen.getByLabelText('importe-decimales')
    await usuario.type(campo, '1234,56')
    // El padre cambia `decimales` a 0 mientras el usuario sigue con el foco puesto — el texto
    // tipeado con la precisión VIEJA no se toca (el efecto de sincronización no pisa mientras
    // está enfocado), pero el blur tiene que reconciliarlo con la precisión NUEVA.
    rerender(<CampoImporte valor={null} decimales={0} onChange={onChange} aria-label="importe-decimales" />)
    await usuario.tab()
    expect(onChange).toHaveBeenLastCalledWith(1235)
    expect(campo).toHaveValue('1.235')
  })

  it('un texto inválido al perder el foco limpia el campo (—> vacío) y onChange recibe null', async () => {
    const onChange = vi.fn()
    const usuario = userEvent.setup()
    render(<CampoControlado onChange={onChange} admiteNegativos />)
    const campo = screen.getByLabelText('importe')
    await usuario.type(campo, '-')
    await usuario.tab()
    expect(campo).toHaveValue('')
    expect(onChange).toHaveBeenLastCalledWith(null)
  })

  describe('admiteNegativos (default false)', () => {
    it('sin admiteNegativos, un "-" tipeado se descarta por completo (no llega a mostrarse)', async () => {
      const usuario = userEvent.setup()
      render(<CampoControlado admiteNegativos={false} />)
      const campo = screen.getByLabelText('importe')
      await usuario.type(campo, '-123,45')
      expect(campo).toHaveValue('123,45')
    })

    it('con admiteNegativos, un "-" al principio se conserva', async () => {
      const usuario = userEvent.setup()
      render(<CampoControlado admiteNegativos />)
      const campo = screen.getByLabelText('importe')
      await usuario.type(campo, '-123,45')
      expect(campo).toHaveValue('-123,45')
    })

    it('con admiteNegativos, un "-" que NO está al principio se descarta (solo se reconoce como signo inicial)', async () => {
      const usuario = userEvent.setup()
      render(<CampoControlado admiteNegativos />)
      const campo = screen.getByLabelText('importe')
      await usuario.type(campo, '123-45')
      expect(campo).toHaveValue('12345')
    })

    it('sin admiteNegativos, blur nunca puede dejar un importe negativo', async () => {
      const onChange = vi.fn()
      const usuario = userEvent.setup()
      render(<CampoControlado admiteNegativos={false} onChange={onChange} />)
      const campo = screen.getByLabelText('importe')
      await usuario.type(campo, '-50')
      await usuario.tab()
      expect(onChange).toHaveBeenLastCalledWith(50)
      expect(campo).toHaveValue('50,00')
    })
  })

  it('es de solo alineación derecha e inputMode decimal', () => {
    render(<CampoControlado />)
    const campo = screen.getByLabelText('importe')
    expect(campo).toHaveAttribute('inputMode', 'decimal')
    expect(campo.className).toContain('text-end')
  })

  it('pasa disabled', () => {
    render(<CampoControlado disabled />)
    expect(screen.getByLabelText('importe')).toBeDisabled()
  })

  it('ref.focus() funciona (flujo de teclado)', () => {
    const ref = createRef<HTMLInputElement>()
    render(<CampoImporte ref={ref} valor={null} onChange={() => {}} aria-label="importe-ref" />)
    ref.current?.focus()
    expect(ref.current).toHaveFocus()
  })

  it('no reformatea mientras está enfocado, aunque el padre re-renderice con el mismo valor', async () => {
    const usuario = userEvent.setup()
    const { rerender } = render(<CampoImporte valor={null} onChange={() => {}} aria-label="importe" />)
    const campo = screen.getByLabelText('importe')
    await usuario.type(campo, '1234')
    // Simula al padre re-renderizando con la prop `valor` sin cambios reales
    rerender(<CampoImporte valor={null} onChange={() => {}} aria-label="importe" />)
    expect(campo).toHaveValue('1234')
  })
})
