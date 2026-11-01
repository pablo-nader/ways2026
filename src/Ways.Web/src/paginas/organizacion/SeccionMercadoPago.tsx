import { useEffect, useState } from 'react'
import { clienteDeCuentasTesoreria, type CuentaTesoreriaListado } from '../../api/cuentasTesoreria'
import {
  AVISO_DE_CONEXION_CANCELADA,
  AVISO_DE_CONEXION_COMPLETADA,
  clienteDeMercadoPago,
  copiaDeFalloDeMercadoPago,
  leerRetornoDeMercadoPago,
  type RetornoDeMercadoPago,
} from '../../api/mercadoPago'
import type { EmpresaListado } from '../../api/tipos'
import { Box } from '../../componentes/Box'
import { ConexionMercadoPago } from './ConexionMercadoPago'
import { limpiarFragmento } from './fragmentoMercadoPago'

type Resultado = { tipo: 'exito' | 'error' | 'info'; texto: string }

const CLASE_DE_AVISO: Readonly<Record<Resultado['tipo'], string>> = {
  exito: 'alert alert-success',
  error: 'alert alert-danger',
  info: 'alert alert-info',
}

const COPIA_CUENTAS_NO_CARGADAS = 'No se pudieron cargar las cuentas de tesorería.'

/**
 * Mercado Pago de las empresas del tenant (solo Admin), debajo de la grilla de empresas. Decidido acá y no en el modal de
 * edición porque la conexión tiene su propio ciclo de vida (OAuth, redirecciones, desconexión) y no se guarda con el botón
 * "Guardar" de la empresa.
 *
 * Al montarse lee el fragmento de la URL: si es el retorno de Mercado Pago, lo borra de la barra ANTES de la primera espera
 * y completa el canje con un POST autenticado (el servidor lo ata a la sesión que inició el flujo). El borrado es
 * síncrono, así que la segunda corrida del efecto bajo StrictMode ya no encuentra nada que canjear: un `code` es de un solo
 * uso y no se manda dos veces.
 */
export function SeccionMercadoPago({ empresas }: { empresas: readonly EmpresaListado[] }) {
  const [cuentas, setCuentas] = useState<readonly CuentaTesoreriaListado[]>([])
  const [errorDeCuentas, setErrorDeCuentas] = useState('')
  const [resultado, setResultado] = useState<Resultado | null>(null)
  const [completando, setCompletando] = useState(false)
  const [versiones, setVersiones] = useState<Readonly<Record<number, number>>>({})

  useEffect(() => {
    let vigente = true
    clienteDeCuentasTesoreria
      .listar()
      .then((filas) => {
        if (!vigente) return
        setCuentas(filas)
        setErrorDeCuentas('')
      })
      .catch(() => {
        if (vigente) setErrorDeCuentas(COPIA_CUENTAS_NO_CARGADAS)
      })

    return () => {
      vigente = false
    }
  }, [])

  useEffect(() => {
    const retorno = leerRetornoDeMercadoPago(window.location.hash)
    if (!retorno) return

    limpiarFragmento()
    void procesar(retorno)
  }, [])

  async function procesar(retorno: RetornoDeMercadoPago) {
    if (retorno.tipo === 'cancelado') {
      setResultado({ tipo: 'info', texto: AVISO_DE_CONEXION_CANCELADA })

      return
    }

    setCompletando(true)
    setResultado(null)
    try {
      await clienteDeMercadoPago.completar(retorno.idEmpresa, { code: retorno.code, state: retorno.state })
      setResultado({ tipo: 'exito', texto: AVISO_DE_CONEXION_COMPLETADA })
    } catch (e) {
      setResultado({ tipo: 'error', texto: copiaDeFalloDeMercadoPago(e, 'completar') })
    } finally {
      setVersiones((previas) => ({ ...previas, [retorno.idEmpresa]: (previas[retorno.idEmpresa] ?? 0) + 1 }))
      setCompletando(false)
    }
  }

  return (
    <Box titulo="Mercado Pago" variante="inverse">
      <div aria-live="polite">
        {completando && <div className="alert alert-info">Completando la conexión con Mercado Pago…</div>}
        {resultado && <div className={CLASE_DE_AVISO[resultado.tipo]}>{resultado.texto}</div>}
      </div>

      {empresas.length === 0 && <p className="text-muted mb-0">No hay empresas para conectar.</p>}
      {empresas.map((empresa) => (
        <ConexionMercadoPago
          key={empresa.id}
          empresa={empresa}
          cuentas={cuentas.filter((c) => c.idEmpresa === empresa.id && c.activo)}
          errorDeCuentas={errorDeCuentas}
          version={versiones[empresa.id] ?? 0}
          bloqueado={completando}
          onAviso={(texto) => setResultado({ tipo: 'exito', texto })}
        />
      ))}
    </Box>
  )
}
