using Microsoft.EntityFrameworkCore.Migrations;
using Ways.Infrastructure.Multitenancy;

namespace Ways.Application.Tests.Persistencia;

/// <summary>
/// Guard de identificador de <see cref="RlsMigrationBuilderExtensions.HabilitarRlsDeTenant"/>
/// (INFO cargada de judgment-day, slice 1→3): el helper interpola el nombre de tabla directo
/// en SQL crudo, así que valida antes de reusarlo por primera vez para los catálogos de
/// tenant (slice 3).
/// </summary>
public class RlsMigrationBuilderExtensionsTests
{
    private static MigrationBuilder CrearMigrationBuilder() => new("Npgsql.EntityFrameworkCore.PostgreSQL");

    [Theory]
    [InlineData("areas")]
    [InlineData("medios_pago")]
    [InlineData("tenants")]
    [InlineData("_tabla_con_guion_bajo_al_inicio")]
    public void AceptaIdentificadoresValidos(string tabla)
    {
        var builder = CrearMigrationBuilder();

        builder.HabilitarRlsDeTenant(tabla);

        Assert.Equal(3, builder.Operations.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("areas; DROP TABLE tenants;--")]
    [InlineData("areas'")]
    [InlineData("Areas")] // mayúsculas: fuera de convención, no un identificador Postgres sin comillas
    [InlineData("123areas")] // no puede empezar con un dígito
    [InlineData("areas cascade")]
    public void RechazaIdentificadoresInvalidos(string tabla)
    {
        var builder = CrearMigrationBuilder();

        Assert.Throws<ArgumentException>(() => builder.HabilitarRlsDeTenant(tabla));
    }

    [Fact]
    public void RechazaNull()
    {
        var builder = CrearMigrationBuilder();

        Assert.Throws<ArgumentException>(() => builder.HabilitarRlsDeTenant(null!));
    }
}
