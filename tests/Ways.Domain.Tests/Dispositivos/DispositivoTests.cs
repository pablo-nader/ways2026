using Ways.Domain.Dispositivos;

namespace Ways.Domain.Tests.Dispositivos;

public class DispositivoTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private static Dispositivo Nuevo() => new()
    {
        IdPuntoVenta = 1,
        Nombre = "Caja 1",
        TokenHash = new string('a', 64),
        IdUsuarioAlta = 1
    };

    [Fact]
    public void RegistrarUsoEstampaUltimoUsoAtYUpdatedAt()
    {
        var dispositivo = Nuevo();

        dispositivo.RegistrarUso(Ahora);

        Assert.Equal(Ahora, dispositivo.UltimoUsoAt);
        Assert.Equal(Ahora, dispositivo.UpdatedAt);
        Assert.Null(dispositivo.DeletedAt);
    }

    [Fact]
    public void RevocarEsBajaLogicaNuncaFisica()
    {
        var dispositivo = Nuevo();

        dispositivo.Revocar(Ahora);

        Assert.Equal(Ahora, dispositivo.DeletedAt);
        Assert.Equal(Ahora, dispositivo.UpdatedAt);
        Assert.True(dispositivo.EstaEliminada);
    }
}
