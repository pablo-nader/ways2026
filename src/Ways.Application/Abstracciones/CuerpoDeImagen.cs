using Ways.Domain.Common;

namespace Ways.Application.Abstracciones;

/// <summary>Lectura del cuerpo crudo de una request que sube una imagen (el logo de la vitrina, la imagen de
/// un artículo): comparten el tipo declarado y el tope de lectura.</summary>
public static class CuerpoDeImagen
{
    /// <summary><c>image/png; charset=binary</c> es <c>image/png</c>.</summary>
    public static string? TipoSinParametros(string? contentType)
    {
        var tipo = contentType?.Split(';')[0].Trim().ToLowerInvariant();

        return string.IsNullOrEmpty(tipo) ? null : tipo;
    }

    /// <summary>Lee a lo sumo un byte más que el máximo: alcanza para saber que se pasó sin traer el
    /// resto del cuerpo.</summary>
    public static async Task<byte[]> LeerAcotadoAsync(Stream cuerpo, CancellationToken ct)
    {
        using var memoria = new MemoryStream();
        var buffer = new byte[16 * 1024];
        var restante = ValidadorDeImagen.TamanoMaximo + 1;

        while (restante > 0)
        {
            var leidos = await cuerpo.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, restante)), ct);

            if (leidos == 0)
            {
                break;
            }

            memoria.Write(buffer, 0, leidos);
            restante -= leidos;
        }

        return memoria.ToArray();
    }
}
