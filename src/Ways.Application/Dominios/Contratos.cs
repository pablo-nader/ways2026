namespace Ways.Application.Dominios;

/// <summary>Alta de un dominio de un tenant. <paramref name="EsPrincipal"/> mueve la marca de
/// principal desde el dominio que la tenía.</summary>
public record SolicitudDeDominio(string Host, bool EsPrincipal);

public record DominioListado(
    int Id,
    int IdTenant,
    string Host,
    bool EsPrincipal,
    DateTimeOffset? VerificadoEn,
    DateTimeOffset CreatedAt);
