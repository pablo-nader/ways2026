/**
 * Punto de venta de la sesión, persistido en `localStorage` para sobrevivir al refresco de la
 * página. Se guarda junto al id del usuario: otra cuenta en el mismo navegador nunca hereda la
 * elección de la anterior (`leerPuntoVentaDeSesion` devuelve `null` si el usuario no coincide).
 */

export const CLAVE_PUNTO_VENTA_DE_SESION = 'ways.sesion.puntoVenta'

type PuntoVentaDeSesion = { idUsuario: number; idPuntoVenta: number }

export function leerPuntoVentaDeSesion(idUsuario: number): number | null {
  try {
    const crudo = localStorage.getItem(CLAVE_PUNTO_VENTA_DE_SESION)
    if (!crudo) return null

    const guardado = JSON.parse(crudo) as Partial<PuntoVentaDeSesion> | null
    if (!guardado || guardado.idUsuario !== idUsuario) return null

    return typeof guardado.idPuntoVenta === 'number' && Number.isFinite(guardado.idPuntoVenta)
      ? guardado.idPuntoVenta
      : null
  } catch {
    return null
  }
}

export function guardarPuntoVentaDeSesion(idUsuario: number, idPuntoVenta: number): void {
  try {
    localStorage.setItem(CLAVE_PUNTO_VENTA_DE_SESION, JSON.stringify({ idUsuario, idPuntoVenta }))
  } catch {
    // Sin almacenamiento (modo privado, cuota agotada) la elección no sobrevive al refresco;
    // la sesión sigue funcionando igual.
  }
}

export function olvidarPuntoVentaDeSesion(): void {
  try {
    localStorage.removeItem(CLAVE_PUNTO_VENTA_DE_SESION)
  } catch {
    // Mismo criterio que al guardar: no hay nada que olvidar si el almacenamiento no existe.
  }
}
