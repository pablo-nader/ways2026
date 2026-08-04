using Ways.Domain.Ventas;

namespace Ways.Domain.Tests.Ventas;

/// <summary>
/// stage-5-pos-ventas, Slice 3 (task 3.17, design decisión 8, spec: comprobantes-venta /
/// Comprobante-Letter Resolution Stays Dormant, ambos escenarios) — pura, sin base de datos,
/// dormant (ningún endpoint la invoca en esta etapa).
/// </summary>
public class ResolvedorDeLetraComprobanteTests
{
    public static TheoryData<CondicionFiscalCodigo, CondicionFiscalCodigo, char> Cruces => new()
    {
        // Emisor RI: RI -> A, cualquier otra cosa -> B (doc 10 §1 "Regla de la letra").
        { CondicionFiscalCodigo.ResponsableInscripto, CondicionFiscalCodigo.ResponsableInscripto, 'A' },
        { CondicionFiscalCodigo.ResponsableInscripto, CondicionFiscalCodigo.Monotributo, 'B' },
        { CondicionFiscalCodigo.ResponsableInscripto, CondicionFiscalCodigo.Exento, 'B' },
        { CondicionFiscalCodigo.ResponsableInscripto, CondicionFiscalCodigo.ConsumidorFinal, 'B' },
        { CondicionFiscalCodigo.ResponsableInscripto, CondicionFiscalCodigo.NoResponsable, 'B' },

        // Emisor Monotributo: C a todos.
        { CondicionFiscalCodigo.Monotributo, CondicionFiscalCodigo.ResponsableInscripto, 'C' },
        { CondicionFiscalCodigo.Monotributo, CondicionFiscalCodigo.Monotributo, 'C' },
        { CondicionFiscalCodigo.Monotributo, CondicionFiscalCodigo.Exento, 'C' },
        { CondicionFiscalCodigo.Monotributo, CondicionFiscalCodigo.ConsumidorFinal, 'C' },
        { CondicionFiscalCodigo.Monotributo, CondicionFiscalCodigo.NoResponsable, 'C' },

        // Emisor Exento: misma rama que Monotributo (extensión conservadora, ver el doc de
        // ResolvedorDeLetraComprobante).
        { CondicionFiscalCodigo.Exento, CondicionFiscalCodigo.ResponsableInscripto, 'C' },
        { CondicionFiscalCodigo.Exento, CondicionFiscalCodigo.Monotributo, 'C' },
        { CondicionFiscalCodigo.Exento, CondicionFiscalCodigo.Exento, 'C' },
        { CondicionFiscalCodigo.Exento, CondicionFiscalCodigo.ConsumidorFinal, 'C' },
        { CondicionFiscalCodigo.Exento, CondicionFiscalCodigo.NoResponsable, 'C' },

        // Emisor ConsumidorFinal: idem.
        { CondicionFiscalCodigo.ConsumidorFinal, CondicionFiscalCodigo.ResponsableInscripto, 'C' },
        { CondicionFiscalCodigo.ConsumidorFinal, CondicionFiscalCodigo.Monotributo, 'C' },
        { CondicionFiscalCodigo.ConsumidorFinal, CondicionFiscalCodigo.Exento, 'C' },
        { CondicionFiscalCodigo.ConsumidorFinal, CondicionFiscalCodigo.ConsumidorFinal, 'C' },
        { CondicionFiscalCodigo.ConsumidorFinal, CondicionFiscalCodigo.NoResponsable, 'C' },

        // Emisor NoResponsable: idem.
        { CondicionFiscalCodigo.NoResponsable, CondicionFiscalCodigo.ResponsableInscripto, 'C' },
        { CondicionFiscalCodigo.NoResponsable, CondicionFiscalCodigo.Monotributo, 'C' },
        { CondicionFiscalCodigo.NoResponsable, CondicionFiscalCodigo.Exento, 'C' },
        { CondicionFiscalCodigo.NoResponsable, CondicionFiscalCodigo.ConsumidorFinal, 'C' },
        { CondicionFiscalCodigo.NoResponsable, CondicionFiscalCodigo.NoResponsable, 'C' }
    };

    [Theory]
    [MemberData(nameof(Cruces))]
    public void ResuelveLaLetraCorrectaParaCadaCruce(
        CondicionFiscalCodigo emisor, CondicionFiscalCodigo receptor, char letraEsperada)
    {
        var letra = ResolvedorDeLetraComprobante.Resolver(emisor, receptor);

        Assert.Equal(letraEsperada, letra);
    }

    [Fact]
    public void EsUnaFuncionPuraSinEfectosDeLado()
    {
        // No hay estado mutable ni parámetros por referencia — llamarla dos veces con los
        // mismos inputs da siempre el mismo resultado (spec: "no database read or write").
        var primera = ResolvedorDeLetraComprobante.Resolver(
            CondicionFiscalCodigo.ResponsableInscripto, CondicionFiscalCodigo.ResponsableInscripto);
        var segunda = ResolvedorDeLetraComprobante.Resolver(
            CondicionFiscalCodigo.ResponsableInscripto, CondicionFiscalCodigo.ResponsableInscripto);

        Assert.Equal(primera, segunda);
    }
}
