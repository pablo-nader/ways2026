namespace Ways.Domain.Catalogos;

/// <summary>
/// Cómo se comporta un medio de pago en caja (doc 10 §1). Enum nativo de Postgres
/// (<c>comportamiento_medio_pago</c>), mismo criterio que <c>estado_tenant</c>/<c>estado_usuario</c>.
/// </summary>
public enum ComportamientoMedioPago
{
    /// <summary>Participa del arqueo físico y admite vuelto.</summary>
    Efectivo,

    /// <summary>No admite vuelto, pide referencia (nro de cupón/operación).</summary>
    Electronico,

    /// <summary>Exige cliente identificado y mueve su saldo.</summary>
    CuentaCorriente
}
