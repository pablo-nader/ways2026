import { useCallback, useState } from 'react'
import { aplicarTema, guardarTema, leerTemaGuardado } from './tema'
import type { Tema } from './tema'

export function useTema(): [Tema, () => void] {
  const [tema, setTema] = useState<Tema>(leerTemaGuardado)

  const alternarTema = useCallback(() => {
    const siguiente: Tema = tema === 'oscuro' ? 'claro' : 'oscuro'
    aplicarTema(siguiente)
    guardarTema(siguiente)
    setTema(siguiente)
  }, [tema])

  return [tema, alternarTema]
}
