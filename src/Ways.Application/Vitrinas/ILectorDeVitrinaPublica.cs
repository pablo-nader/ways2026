namespace Ways.Application.Vitrinas;

/// <summary>
/// Lee lo que un comercio publica en su dominio, sin sesión. Lo único que lo separa de una lectura
/// de tenant es el modo de acceso: corre en <c>publico</c>, que solo puede hacer <c>SELECT</c> sobre
/// <c>vitrinas</c> (publicada), <c>empresas</c> y <c>puntos_venta</c> del tenant que se le pasa.
/// </summary>
public interface ILectorDeVitrinaPublica
{
    /// <summary><c>null</c> cuando el tenant no tiene una vitrina publicada.</summary>
    Task<VitrinaPublica?> ObtenerAsync(int idTenant, CancellationToken ct = default);

    /// <summary>Tipo y marca de carga del logo, sin leer la columna <c>logo</c>. <c>null</c> cuando no hay
    /// vitrina publicada o no tiene logo.</summary>
    Task<VersionDeLogo?> ObtenerVersionDelLogoAsync(int idTenant, CancellationToken ct = default);

    /// <summary>Los bytes del logo. <c>null</c> cuando no hay vitrina publicada o no tiene logo.</summary>
    Task<LogoPublico?> ObtenerLogoAsync(int idTenant, CancellationToken ct = default);
}
