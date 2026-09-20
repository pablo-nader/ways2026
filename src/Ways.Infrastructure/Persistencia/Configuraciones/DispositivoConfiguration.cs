using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Dispositivos;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="Dispositivo"/> (stage-desktop-pos, DB CHANGE GATE aprobado): scoping
/// <c>[operativa]</c> (<c>id_tenant</c> + <c>id_punto_venta</c>, doc 09) — un dispositivo nace
/// atado a un punto de venta y nunca se comparte entre puntos de venta.
/// </summary>
public class DispositivoConfiguration : IEntityTypeConfiguration<Dispositivo>
{
    public void Configure(EntityTypeBuilder<Dispositivo> builder)
    {
        builder.ToTable("dispositivos");

        builder.HasKey(d => d.Id).HasName("pk_dispositivos");

        builder.Property(d => d.Id)
            .HasColumnName("id_dispositivo")
            .UseIdentityByDefaultColumn();

        builder.Property(d => d.IdTenant).HasColumnName("id_tenant").IsRequired();
        builder.Property(d => d.IdPuntoVenta).HasColumnName("id_punto_venta").IsRequired();

        // stage-pos-reserva-de-numeracion (DB CHANGE GATE aprobado): por si el día de mañana algo
        // cuelga de un dispositivo con FK compuesta — mismo motivo que
        // ak_puntos_venta_id_punto_venta_id_tenant. Primer consumidor: reservas_numeracion
        // (ReservaNumeracionConfiguration.fk_reservas_numeracion_dispositivo), ADR-9.
        builder.HasAlternateKey(d => new { d.Id, d.IdTenant })
            .HasName("ak_dispositivos_id_dispositivo_id_tenant");

        builder.Property(d => d.Nombre)
            .HasColumnName("nombre")
            .HasMaxLength(NombreDeDispositivo.LargoMaximo)
            .IsRequired();

        // SHA-256 hex: 64 caracteres fijos. El secreto en texto plano nunca se persiste — solo
        // este hash, y solo viaja una vez en la cookie ways.dispositivo.
        builder.Property(d => d.TokenHash)
            .HasColumnName("token_hash")
            .HasColumnType("char(64)")
            .IsFixedLength()
            .IsRequired();

        builder.Property(d => d.IdUsuarioAlta).HasColumnName("id_usuario_alta").IsRequired();
        builder.Property(d => d.UltimoUsoAt).HasColumnName("ultimo_uso_at");

        builder.Property(d => d.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(d => d.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(d => d.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(d => d.EstaEliminada);

        builder.HasIndex(d => d.TokenHash).HasDatabaseName("ux_dispositivos_token_hash").IsUnique();

        // stage-desktop-pos (DB CHANGE GATE aprobado): invariante "una PC-caja = un punto de
        // venta" — a lo sumo un dispositivo ACTIVO por punto de venta. Reemplaza al
        // ix_dispositivos_tenant_punto_venta no-único de abajo: toda consulta de este par de
        // columnas en el código pasa por el filtro global de baja lógica (deleted_at IS NULL,
        // WaysDbContext.AplicarFiltroDeBajaLogica) salvo ResolverDispositivoVigenteAsync, que
        // ignora el filtro de TENANT pero deja el de baja lógica activo — así que ningún camino de
        // lectura necesita ya un índice no-parcial sobre filas revocadas.
        builder.HasIndex(d => new { d.IdTenant, d.IdPuntoVenta })
            .HasDatabaseName("ux_dispositivos_punto_venta_activo")
            .IsUnique()
            .HasFilter("deleted_at IS NULL");

        // Soporte de las otras dos FKs, con nombre propio en vez del "IX_..." autogenerado por
        // EF — mismo criterio que ix_puntos_venta_empresa/ix_certificados_fiscales_empresa.
        builder.HasIndex(d => new { d.IdPuntoVenta, d.IdTenant })
            .HasDatabaseName("ix_dispositivos_punto_venta");
        builder.HasIndex(d => d.IdUsuarioAlta).HasDatabaseName("ix_dispositivos_usuario_alta");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(d => d.IdTenant)
            .HasConstraintName("fk_dispositivos_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        // FK compuesta (id_punto_venta, id_tenant) → puntos_venta: un dispositivo de un tenant
        // no puede vincularse al punto de venta de otro tenant ni por bug (ADR-9), misma forma
        // que fk_certificados_fiscales_empresa/fk_puntos_venta_empresa.
        builder.HasOne<PuntoVenta>()
            .WithMany()
            .HasForeignKey(d => new { d.IdPuntoVenta, d.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_dispositivos_punto_venta")
            .OnDelete(DeleteBehavior.Restrict);

        // Simple: Usuario no tiene alternate key (id_usuario, id_tenant) para una FK compuesta
        // (su IdTenant es nullable = plataforma, doc 08) — igual que cualquier otra referencia a
        // usuarios en el esquema.
        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(d => d.IdUsuarioAlta)
            .HasConstraintName("fk_dispositivos_usuario_alta")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
