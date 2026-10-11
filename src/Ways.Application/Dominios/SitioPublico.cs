namespace Ways.Application.Dominios;

/// <summary>Lo que la web pública necesita saber del host antes de iniciar sesión.
/// <paramref name="AdmiteUsuario"/>: el login por nombre de usuario solo existe bajo el dominio de un
/// tenant.</summary>
public record SitioPublico(bool EsPlataforma, string? NombreComercio, bool AdmiteUsuario);
