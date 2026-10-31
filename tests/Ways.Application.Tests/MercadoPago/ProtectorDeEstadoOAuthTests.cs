using Microsoft.AspNetCore.DataProtection;
using Ways.Application.Abstracciones;
using Ways.Application.MercadoPago;
using Ways.Infrastructure.MercadoPago;

namespace Ways.Application.Tests.MercadoPago;

public class ProtectorDeEstadoOAuthTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly EstadoOAuth Estado = new(7, 21, 33, 5, "verificador-de-prueba", "https://ways.test");

    private sealed class RelojMutable(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; set; } = ahora;
    }

    private static (ProtectorDeEstadoOAuth Protector, RelojMutable Reloj, IDataProtectionProvider Proveedor) Crear()
    {
        var reloj = new RelojMutable(DateTimeOffset.UtcNow);
        var proveedor = new EphemeralDataProtectionProvider();
        return (new ProtectorDeEstadoOAuth(proveedor, reloj), reloj, proveedor);
    }

    [Fact]
    public void ElEstadoProtegidoVuelveIgualAntesDelVencimiento()
    {
        var (protector, reloj, _) = Crear();

        var valor = protector.Proteger(Estado);
        reloj.Ahora = reloj.Ahora.AddMinutes(9);

        Assert.Equal(Estado, protector.Desproteger(valor));
    }

    [Fact]
    public void ElEstadoVenceALosDiezMinutos()
    {
        var (protector, reloj, _) = Crear();

        var valor = protector.Proteger(Estado);
        reloj.Ahora = reloj.Ahora.Add(ServicioDeConexionMercadoPago.VigenciaDelEstado).AddSeconds(1);

        Assert.Null(protector.Desproteger(valor));
    }

    [Fact]
    public void ElValorNoLlevaEnClaroNingunDatoDelEstado()
    {
        var (protector, _, _) = Crear();

        var valor = protector.Proteger(Estado);

        Assert.DoesNotContain("verificador-de-prueba", valor);
        Assert.DoesNotContain("ways.test", valor);
    }

    [Fact]
    public void UnValorAlteradoOBasuraNoSeAcepta()
    {
        var (protector, _, _) = Crear();
        var valor = protector.Proteger(Estado);
        var medio = valor.Length / 2;
        var alterado = valor[..medio] + (valor[medio] == 'A' ? 'B' : 'A') + valor[(medio + 1)..];

        Assert.Null(protector.Desproteger(alterado));
        Assert.Null(protector.Desproteger("no-es-un-state"));
        Assert.Null(protector.Desproteger(string.Empty));
    }

    [Fact]
    public void UnValorProtegidoConOtroPropositoNoSeAcepta()
    {
        var (protector, _, proveedor) = Crear();
        var ajeno = proveedor.CreateProtector("Ways.OtroProposito").Protect("{}");

        Assert.Null(protector.Desproteger(ajeno));
    }

    [Fact]
    public void UnValorDeOtroLlaveroNoSeAcepta()
    {
        var (protector, _, _) = Crear();
        var (otro, _, _) = Crear();

        Assert.Null(otro.Desproteger(protector.Proteger(Estado)));
    }
}
