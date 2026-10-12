import { ErrorApi } from '../api/cliente'
import type { OrigenFondosGasto } from '../api/tipos'
import type { CampoDeCuenta, ErrorDeCuenta } from './cuentaDePersonalFormularios'

export type { CampoDeCuenta, ErrorDeCuenta }

/** Cuál de las tres escrituras falló: un 403 y un 404 significan cosas distintas en cada una. */
export type Escritura = 'horas' | 'ajuste' | 'pago'

const COPIA_GENERICA = 'No se pudo registrar.'

/** Código que `pedir` le pone a un error cuando la respuesta no trae un cuerpo de error de la API
 * (`problema.codigo ?? 'error'` en `api/cliente.ts`): un 4xx con este código lo pudo producir un intermediario. */
const CODIGO_GENERICO_DE_ERROR = 'error'

/** Un 408 y un 429 son 4xx que no dicen que la escritura se rechazó: el pedido pudo llegar y aplicarse. */
const ESTADOS_4XX_INCIERTOS: readonly number[] = [408, 429]

/** `true` solo para un rechazo que emitió NUESTRA API: un 4xx (salvo 408 y 429) con el código de error del
 * cuerpo. Todo lo demás es incierto: un 5xx, un fallo sin respuesta (red cortada, tiempo agotado), una
 * respuesta 2xx ilegible y un 4xx sin cuerpo de la API (proxy, balanceador). Como las tres escrituras no
 * llevan clave de idempotencia, reintentar a ciegas una incierta puede registrarla dos veces. */
export function esRechazoCierto(e: unknown): e is ErrorApi {
  return (
    e instanceof ErrorApi &&
    e.estado >= 400 &&
    e.estado < 500 &&
    !ESTADOS_4XX_INCIERTOS.includes(e.estado) &&
    e.codigo !== '' &&
    e.codigo !== CODIGO_GENERICO_DE_ERROR
  )
}

/** Se elige por `codigo`, nunca por `mensaje`. Es un `Map` porque el código viene del servidor: sobre un
 * objeto, `'constructor'` resolvería contra el prototipo. Los códigos son los de `ReglaDeMovimientoDePersonal`,
 * `ReglaDePagoDePersonal`, `ServicioDePagoDePersonal` y los de gastos y turnos que el pago reutiliza. */
const ERROR_POR_CODIGO: ReadonlyMap<string, ErrorDeCuenta> = new Map<string, ErrorDeCuenta>([
  ['fecha_requerida', { campo: 'fecha', mensaje: 'Ingresá la fecha.' }],
  ['horas_invalidas', { campo: 'horas', mensaje: 'Las horas deben ser mayores que 0 y hasta 24, con hasta dos decimales.' }],
  [
    'valor_hora_invalido',
    { campo: 'valorHora', mensaje: 'El valor hora debe estar entre 0 y 999.999.999.999,99, con hasta dos decimales.' },
  ],
  ['importe_fuera_de_rango', { campo: 'general', mensaje: 'El importe supera el máximo admitido.' }],
  ['detalle_muy_largo', { campo: 'detalle', mensaje: 'El detalle no puede superar los 500 caracteres.' }],
  ['ajuste_importe_invalido', { campo: 'importe', mensaje: 'El importe del ajuste no puede ser cero y admite hasta dos decimales.' }],
  [
    'ajuste_detalle_requerido',
    { campo: 'detalle', mensaje: 'El detalle es obligatorio y tiene que tener al menos 5 caracteres.' },
  ],
  ['pago_personal_origen_invalido', { campo: 'origen', mensaje: 'El origen de los fondos elegido no existe.' }],
  [
    'pago_personal_vacio',
    { campo: 'general', mensaje: 'Cargá un descuento de cuenta corriente o al menos una línea de dinero.' },
  ],
  [
    'pago_personal_descuento_invalido',
    { campo: 'descuento', mensaje: 'El descuento debe ser mayor que 0, con hasta dos decimales.' },
  ],
  ['pago_personal_demasiadas_lineas', { campo: 'lineas', mensaje: 'Un pago admite hasta 20 líneas de dinero.' }],
  [
    'pago_personal_importe_invalido',
    { campo: 'lineas', mensaje: 'El importe de cada línea debe ser mayor que 0, con hasta dos decimales.' },
  ],
  ['pago_personal_referencia_muy_larga', { campo: 'lineas', mensaje: 'La referencia no puede superar los 100 caracteres.' }],
  ['pago_personal_origen_requerido', { campo: 'origen', mensaje: 'Elegí de dónde sale el dinero.' }],
  ['pago_personal_origen_sin_lineas', { campo: 'origen', mensaje: 'Sin líneas de dinero no hay origen de fondos que elegir.' }],
  ['pago_personal_empresa_requerida', { campo: 'empresa', mensaje: 'Elegí la empresa que paga.' }],
  [
    'pago_personal_empresa_no_aplica',
    { campo: 'empresa', mensaje: 'La empresa solo se elige cuando el dinero sale de la tesorería.' },
  ],
  [
    'pago_personal_punto_venta_requerido',
    {
      campo: 'general',
      mensaje: 'Elegí un punto de venta desde el menú: el cajón del turno y el descuento de cuenta corriente lo necesitan.',
    },
  ],
  [
    'turno_no_abierto',
    {
      campo: 'origen',
      mensaje: 'No hay un turno abierto en el punto de venta de la sesión. Abrí el turno desde Caja y volvé a intentar.',
    },
  ],
  // Va arriba del formulario: si el empleado ya no tiene cliente vinculado, el campo del descuento desaparece.
  [
    'personal_sin_cliente_vinculado',
    {
      campo: 'general',
      mensaje:
        'El empleado no tiene un cliente vinculado vigente para descontar de su cuenta corriente. Sacá el descuento o revisá el cliente vinculado.',
    },
  ],
  [
    'descuento_supera_la_deuda',
    {
      campo: 'descuento',
      mensaje: 'El descuento supera la deuda actual del cliente vinculado. Revisá la deuda que muestra este formulario.',
    },
  ],
  ['gasto_fecha_futura', { campo: 'fecha', mensaje: 'La fecha del pago no puede ser futura.' }],
  [
    'gasto_medio_pago_cuenta_corriente_invalido',
    { campo: 'lineas', mensaje: 'La cuenta corriente no se puede usar como medio de pago de una línea de dinero.' },
  ],
  [
    'punto_venta_no_pertenece_a_la_empresa',
    { campo: 'empresa', mensaje: 'El punto de venta de la sesión no pertenece a la empresa elegida.' },
  ],
])

/** Los rechazos que dejan vieja la deuda del cliente o el cliente vinculado del empleado: la pantalla los relee. */
export function desactualizaLaDeuda(e: ErrorApi): boolean {
  return e.codigo === 'descuento_supera_la_deuda' || e.codigo === 'personal_sin_cliente_vinculado'
}

/** El rechazo de una escritura de la cuenta, ubicado en el campo que corresponde. `origenDelPago` es el
 * origen de fondos que se envió (`null` si el pago no llevaba líneas): un 403 solo se explica con la
 * tesorería cuando fue la tesorería lo que se pidió. */
export function errorDeEscritura(e: ErrorApi, escritura: Escritura, origenDelPago: OrigenFondosGasto | null = null): ErrorDeCuenta {
  const propio = ERROR_POR_CODIGO.get(e.codigo)
  if (propio) return propio

  if (e.estado === 403) {
    return escritura === 'pago' && origenDelPago === 'Tesoreria'
      ? { campo: 'origen', mensaje: 'Pagar desde la tesorería requiere ser administrador.' }
      : { campo: 'general', mensaje: 'No tenés permiso para registrar esto.' }
  }

  if (e.estado === 404) {
    return {
      campo: 'general',
      mensaje:
        escritura === 'pago'
          ? 'No se encontró el empleado, el medio de pago, la empresa o el punto de venta elegido. Actualizá la pantalla e intentá de nuevo.'
          : 'El empleado ya no existe. Volvé al personal y actualizá el listado.',
    }
  }

  return { campo: 'general', mensaje: e.message.trim() || COPIA_GENERICA }
}
