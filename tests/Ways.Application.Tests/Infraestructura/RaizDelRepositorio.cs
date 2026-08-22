namespace Ways.Application.Tests.Infraestructura;

/// <summary>
/// stage-19a-slice2 (judgment ronda 2, juez A — SUGGESTION dedupe): resolver la raíz del
/// repositorio (el directorio que contiene <c>Ways.slnx</c>) es un helper idéntico que vivía
/// duplicado, literal, en cinco archivos de test nuevos de <c>Fiscal/</c>
/// (<c>ClienteWsaaTests</c>, <c>GeneradorDeTraTests</c>, <c>SinMaterialDeClaveTests</c>,
/// <c>SobreSoapAislamientoTests</c>, <c>SobreSoapTests</c>). Extraído acá para tener una única
/// fuente de verdad; <c>ContencionDelExportadorTests</c> (el precedente pre-existente de este
/// patrón) mantiene su propia copia porque no fue parte de los hallazgos de esta ronda.
/// </summary>
public static class RaizDelRepositorio
{
    public static string Resolver()
    {
        var directorio = AppContext.BaseDirectory;

        while (directorio is not null && !File.Exists(Path.Combine(directorio, "Ways.slnx")))
        {
            directorio = Path.GetDirectoryName(directorio.TrimEnd(Path.DirectorySeparatorChar));
        }

        return directorio ?? throw new InvalidOperationException("No se encontró la raíz del repositorio (Ways.slnx).");
    }
}
