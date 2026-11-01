import { useEffect, useState } from 'react'
import { ErrorApi } from '../api/cliente'
import { clienteDeOrganizacion } from '../api/organizacion'
import type { SucursalListado } from '../api/tipos'

/**
 * Las sucursales visibles para el usuario, cargadas una vez. Es la fuente de los selectores de las pantallas de stock
 * (el stock es de la sucursal): `sucursales` es `null` mientras carga y `[]` si falló o no hay ninguna; `error` trae
 * el mensaje de la falla.
 */
export function useSucursales(): { sucursales: SucursalListado[] | null; error: string } {
  const [sucursales, setSucursales] = useState<SucursalListado[] | null>(null)
  const [error, setError] = useState('')

  useEffect(() => {
    let vigente = true
    clienteDeOrganizacion
      .listarSucursales()
      .then((lista) => {
        if (vigente) setSucursales(lista)
      })
      .catch((e) => {
        if (!vigente) return
        setSucursales([])
        setError(e instanceof ErrorApi ? e.message : 'No se pudieron cargar las sucursales.')
      })

    return () => {
      vigente = false
    }
  }, [])

  return { sucursales, error }
}
