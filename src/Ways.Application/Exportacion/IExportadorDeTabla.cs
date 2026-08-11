namespace Ways.Application.Exportacion;

/// <summary>
/// Puerto de exportación de <see cref="TablaExportable"/> a bytes de un formato de archivo
/// concreto. Es el mismo seam que <c>IHasheadorDeContrasenas</c> → <c>HasheadorPbkdf2</c>: si el
/// audit de licencias de la librería adoptada fallara, el swap es un archivo de Infrastructure y
/// un <c>PackageReference</c>, sin tocar Application.
/// </summary>
public interface IExportadorDeTabla
{
    /// <summary>Content-Type de la respuesta HTTP para el archivo generado.</summary>
    string TipoDeContenido { get; }

    byte[] Generar(TablaExportable tabla);
}
