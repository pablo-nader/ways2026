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

    /// <summary>judgment-day, slice 1 ronda 2, finding 3 (juez B): el tipo NO impide
    /// <c>new AccionAuditada(...)</c> inline (el <c>record</c> posicional genera un constructor
    /// público) — este test es el único freno real contra un typo en el catálogo, congelando los
    /// 12 pares exactos que el resto del código asume.</summary>
    [Fact]
    public void ElCatalogoTieneExactamenteLosDoceParesEsperados()
    {
        (string Accion, string Entidad)[] esperado =
        [
            ("precio.cambio", "articulo"),
            ("venta.anulacion", "comprobante_venta"),
            ("compra.anulacion", "comprobante_compra"),
            ("stock.ajuste", "articulo"),
            ("stock.decomiso", "articulo"),
            ("stock.conteo", "articulo"),
            ("cc.reliquidacion", "cliente"),
            ("usuario.alta", "usuario"),
            ("usuario.actualizacion", "usuario"),
            ("usuario.baja", "usuario"),
            ("usuario.desbloqueo", "usuario"),
            ("usuario.password", "usuario")
        ];

        var real = AccionAuditada.Todas.Select(a => (a.Accion, a.Entidad));

        Assert.Equal(esperado, real);
    }
}
