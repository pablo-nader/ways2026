import { useEffect, useMemo, useState } from 'react'
import { clienteDePersonal } from '../api/personal'

export type OpcionDeEmpleado = { id: number; nombre: string }

type FilaConEmpleado = { idEmpleado: number | null; empleado: string | null }

/**
 * Opciones del filtro "Empleado" de las grillas de administración. El empleado de una venta o de un retiro es el usuario que la
 * cargó. Quien puede listar usuarios (Admin) ve a todos; Supervisor no tiene ese permiso y ve a los empleados que aparecieron en
 * las páginas que fue cargando, sin repetirlos. `error` avisa que el listado de usuarios no se pudo leer.
 */
export function useOpcionesDeEmpleado(puedeListarUsuarios: boolean, vistos: readonly FilaConEmpleado[]) {
  const [usuarios, setUsuarios] = useState<OpcionDeEmpleado[]>([])
  const [error, setError] = useState(false)
  const [acumulados, setAcumulados] = useState<Record<number, string>>({})

  useEffect(() => {
    if (!puedeListarUsuarios) return
    let vigente = true
    clienteDePersonal
      .usuariosParaVincular()
      .then((pagina) => {
        if (vigente) setUsuarios(pagina.items.map((u) => ({ id: u.id, nombre: u.usuario })))
      })
      .catch(() => {
        if (vigente) setError(true)
      })
    return () => {
      vigente = false
    }
  }, [puedeListarUsuarios])

  useEffect(() => {
    if (puedeListarUsuarios) return
    setAcumulados((previos) => {
      let cambio = false
      const siguientes = { ...previos }
      for (const fila of vistos) {
        if (fila.idEmpleado === null || fila.empleado === null || siguientes[fila.idEmpleado] === fila.empleado) continue
        siguientes[fila.idEmpleado] = fila.empleado
        cambio = true
      }
      return cambio ? siguientes : previos
    })
  }, [puedeListarUsuarios, vistos])

  const opciones = useMemo(() => {
    if (puedeListarUsuarios) return usuarios
    return Object.entries(acumulados)
      .map(([id, nombre]) => ({ id: Number(id), nombre }))
      .sort((a, b) => a.nombre.localeCompare(b.nombre, 'es'))
  }, [puedeListarUsuarios, usuarios, acumulados])

  return { opciones, error }
}
