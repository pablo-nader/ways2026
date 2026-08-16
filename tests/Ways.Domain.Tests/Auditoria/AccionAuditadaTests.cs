using System.Text.RegularExpressions;
using Ways.Domain.Auditoria;

namespace Ways.Domain.Tests.Auditoria;

/// <summary>
/// stage-14-auditoria-trazabilidad, Slice 1 (task 1.13, design decisión 4): el catálogo genérico
/// — 12 entradas, sin duplicados, naming <c>&lt;dominio&gt;.&lt;operacion&gt;</c>, <c>Entidad</c>
/// no vacía.
/// </summary>
public partial class AccionAuditadaTests
{
    [GeneratedRegex(@"^[a-z]+\.[a-z]+$")]
    private static partial Regex FormatoDeAccion();

    [Fact]
    public void TieneDoceEntradas()
    {
        Assert.Equal(12, AccionAuditada.Todas.Count);
    }

    [Fact]
    public void NoHayDuplicados()
    {
        Assert.Equal(AccionAuditada.Todas.Count, AccionAuditada.Todas.Distinct().Count());
    }

    [Fact]
    public void CadaAccionRespetaElFormatoDominioPuntoOperacion()
    {
        foreach (var accion in AccionAuditada.Todas)
        {
            Assert.True(
                FormatoDeAccion().IsMatch(accion.Accion),
                $"'{accion.Accion}' no respeta el formato '<dominio>.<operacion>'.");
        }
    }

    [Fact]
    public void CadaEntidadEsNoVacia()
    {
        foreach (var accion in AccionAuditada.Todas)
        {
            Assert.False(string.IsNullOrWhiteSpace(accion.Entidad));
        }
    }
}
