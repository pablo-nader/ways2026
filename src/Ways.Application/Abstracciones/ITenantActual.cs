using System.Data.Common;

namespace Ways.Application.Abstracciones;

/// <summary>
/// Modo de acceso vigente en la conexión actual (doc 09). Viaja como GUC de Postgres
/// (<c>app.acceso</c>) y es lo que las policies de RLS evalúan.
/// </summary>
public enum ModoDeAcceso
{
    /// <summary>Sin contexto resuelto. Las policies de RLS no ven nada: falla cerrado.</summary>
    Ninguno,

    /// <summary>Sesión de un usuario de tenant: ve solo las filas de su tenant.</summary>
    Tenant,

    /// <summary>Sesión de staff de plataforma (root): ve todos los tenants.</summary>
    Plataforma,

    /// <summary>Único modo permitido antes de resolver la sesión: <c>POST /api/auth/login</c>,
    /// que solo puede leer y actualizar <c>usuarios</c>.</summary>
    Login
}

/// <summary>
/// Tenant resuelto para la operación en curso. Se puebla una sola vez, en
/// <c>OnValidatePrincipal</c> (ADR-2): nunca viaja como parámetro editable por el cliente.
/// </summary>
public interface ITenantActual
{
    /// <summary><c>null</c> cuando <see cref="Modo"/> no es <see cref="ModoDeAcceso.Tenant"/>.</summary>
    int? Id { get; }

    ModoDeAcceso Modo { get; }

    bool EsPlataforma => Modo == ModoDeAcceso.Plataforma;

    /// <summary>Suplanta temporalmente el contexto a <see cref="ModoDeAcceso.Tenant"/> /
    /// <paramref name="idTenant"/> (ADR-16, aprovisionamiento): mientras el scope devuelto
    /// no se libera, el filtro/estampado de EF de <c>WaysDbContext</c> ve ese tenant en vez
    /// del modo anterior. Al liberarse (<c>Dispose</c>), restaura el modo/id previos. Solo
    /// tiene sentido en un contexto HTTP mutable (<c>TenantActualDeSesion</c>); un contexto
    /// inmutable (<c>TenantActualFijo</c>, usado por semilla/design-time/tests) no lo
    /// soporta — nada en esos puntos de entrada aprovisiona tenants.</summary>
    IDisposable Suplantar(int idTenant);

    /// <summary>Reaplica el GUC de tenant sobre una conexión ya abierta (ADR-3, pieza
    /// diferida hasta ADR-16): dentro de la transacción explícita de aprovisionamiento,
    /// <c>InterceptorDeContextoDeTenant</c> no vuelve a dispararse porque la conexión ya
    /// estaba abierta antes de <see cref="Suplantar"/> — sin este reaplicado, RLS seguiría
    /// evaluando el modo con el que la conexión se abrió originalmente. El GUC se aplica con
    /// <c>is_local = true</c>: vuelve solo al valor de sesión cuando la transacción termina
    /// (commit o rollback), sin dejar nada que revertir a mano.</summary>
    Task ReaplicarSobreConexionAsync(DbConnection conexion, CancellationToken ct = default);
}
