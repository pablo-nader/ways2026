namespace Ways.Api.ConectorMcp;

/// <summary>
/// Lo que devuelve <see cref="RegistroDelConectorMcp.AgregarConectorMcp"/> y consultan los otros
/// dos pasos del arranque. Un conector que falla al inicializarse se desactiva en caliente: el
/// middleware del conector consulta <see cref="Activo"/> en cada request.
/// </summary>
public sealed class EstadoDelConectorMcp
{
    public bool Activo { get; private set; }

    /// <summary>Por qué el conector quedó deshabilitado estando <c>Mcp:Habilitado</c> encendido;
    /// <c>null</c> si está activo o si el flag está apagado.</summary>
    public string? Motivo { get; private set; }

    public static EstadoDelConectorMcp Apagado() => new();

    public static EstadoDelConectorMcp Deshabilitado(string motivo) => new() { Motivo = motivo };

    public static EstadoDelConectorMcp Registrado() => new() { Activo = true };

    public void Desactivar(string motivo)
    {
        Activo = false;
        Motivo = motivo;
    }
}
