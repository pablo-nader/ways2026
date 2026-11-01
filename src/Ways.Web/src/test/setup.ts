import { cleanup } from '@testing-library/react'
import { afterEach } from 'vitest'
import '@testing-library/jest-dom/vitest'
import { reiniciarToasts } from '../toasts/toasts'

// Sin `test.globals` en la config de Vitest, @testing-library/react no detecta el framework
// de test y no registra su limpieza automática entre tests — se hace explícita acá.
afterEach(() => {
  cleanup()
  reiniciarToasts()
})
