using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="Cliente"/> (design decision 1, Table Shapes): entidad dedicada, no
/// <c>ConfiguracionDeCatalogo&lt;T&gt;</c> — su índice único es por <c>numero</c> (asignado
/// por el contador), no por <c>nombre</c>.
/// </summary>
public class ClienteConfiguration : IEntityTypeConfiguration<Cliente>
{
    public void Configure(EntityTypeBuilder<Cliente> builder)
    {
        // Design decision 4 / backstop map: la constraint de esquema que cierra la baja
        // irreversible del Consumidor Final — ReglaDeClientes.ValidarNoConsumidorFinal ya
        // bloquea el camino normal (edición + baja), esto es el backstop ante un bypass
        // directo del servicio.
        builder.ToTable("clientes", t => t.HasCheckConstraint(
            "ck_clientes_cf_protegido", "numero <> 1 OR deleted_at IS NULL"));

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Id)
            .HasColumnName("id_cliente")
            .UseIdentityByDefaultColumn();

        builder.Property(c => c.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        builder.Property(c => c.IdEmpresa)
            .HasColumnName("id_empresa");

        builder.Property(c => c.Numero)
            .HasColumnName("numero")
            .IsRequired();

        builder.Property(c => c.Nombre)
            .HasColumnName("nombre")
            .HasColumnType("citext")
            .HasMaxLength(150)
            .IsRequired();

        builder.Property(c => c.Apellido)
            .HasColumnName("apellido")
            .HasColumnType("citext")
            .HasMaxLength(150);

        builder.Property(c => c.RazonSocial)
            .HasColumnName("razon_social")
            .HasColumnType("citext")
            .HasMaxLength(150);

        builder.Property(c => c.TipoDocumento)
            .HasColumnName("tipo_documento")
            .HasColumnType("tipo_documento");

        builder.Property(c => c.NumeroDocumento)
            .HasColumnName("numero_documento")
            .HasColumnType("citext")
            .HasMaxLength(30);

        builder.Property(c => c.IdCondicionFiscal)
            .HasColumnName("id_condicion_fiscal")
            .IsRequired();

        builder.Property(c => c.Nacimiento)
            .HasColumnName("nacimiento")
            .HasColumnType("date");

        builder.Property(c => c.Domicilio)
            .HasColumnName("domicilio")
            .HasColumnType("citext")
            .HasMaxLength(255);

        builder.Property(c => c.Telefono)
            .HasColumnName("telefono")
            .HasColumnType("citext")
            .HasMaxLength(50);

        builder.Property(c => c.Celular)
            .HasColumnName("celular")
            .HasColumnType("citext")
            .HasMaxLength(50);

        builder.Property(c => c.Email)
            .HasColumnName("email")
            .HasColumnType("citext")
            .HasMaxLength(255);

        builder.Property(c => c.Observaciones)
            .HasColumnName("observaciones")
            .HasColumnType("text");

        builder.Property(c => c.IdListaPrecio)
            .HasColumnName("id_lista_precio")
            .IsRequired();

        builder.Property(c => c.LimiteCredito)
            .HasColumnName("limite_credito")
            .HasColumnType("numeric(14,2)")
            .HasDefaultValue(0m)
            .IsRequired();

        builder.Property(c => c.CreditoIlimitado)
            .HasColumnName("credito_ilimitado")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(c => c.Saldo)
            .HasColumnName("saldo")
            .HasColumnType("numeric(14,2)")
            .HasDefaultValue(0m)
            .IsRequired();

        builder.Property(c => c.Activo)
            .HasColumnName("activo")
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(c => c.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(c => c.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(c => c.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(c => c.EstaEliminada);
        builder.Ignore(c => c.EsConsumidorFinal);

        // Design decision 2 / spec "Atomic Per-Tenant Numero Assignment": backstop del
        // contador — bajo operación normal el contador atómico nunca choca acá, esto cubre
        // un bypass directo del camino de escritura.
        builder.HasIndex(c => new { c.IdTenant, c.Numero })
            .HasDatabaseName("ux_clientes_numero")
            .HasFilter("deleted_at IS NULL")
            .IsUnique();

        builder.HasIndex(c => c.IdTenant).HasDatabaseName("ix_clientes_tenant");
        builder.HasIndex(c => new { c.IdEmpresa, c.IdTenant }).HasDatabaseName("ix_clientes_empresa");

        // Nombre explícito en snake_case (doc 10): sin esto, EF nombra el índice de soporte
        // de la FK con su convención propia (IX_clientes_id_condicion_fiscal), rompiendo la
        // convención que el resto del esquema mantiene sin excepciones (p.ej.
        // ix_categorias_padre para id_categoria_padre).
        builder.HasIndex(c => c.IdCondicionFiscal).HasDatabaseName("ix_clientes_condicion_fiscal");

        // (IdListaPrecio, IdTenant), no solo IdListaPrecio: mismo criterio que
        // ix_clientes_empresa, columnas en el mismo orden que la FK compuesta de abajo, para
        // que EF no genere un segundo índice de soporte con su propia convención de nombre.
        builder.HasIndex(c => new { c.IdListaPrecio, c.IdTenant }).HasDatabaseName("ix_clientes_lista_precio");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(c => c.IdTenant)
            .HasConstraintName("fk_clientes_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Empresa>()
            .WithMany()
            .HasForeignKey(c => new { c.IdEmpresa, c.IdTenant })
            .HasPrincipalKey(e => new { e.Id, e.IdTenant })
            .HasConstraintName("fk_clientes_empresa")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<CondicionFiscal>()
            .WithMany()
            .HasForeignKey(c => c.IdCondicionFiscal)
            .HasConstraintName("fk_clientes_condicion_fiscal")
            .OnDelete(DeleteBehavior.Restrict);

        // DB CHANGE GATE aprobado 2026-08-02 (judgment-day ronda 1, hardening de esquema):
        // FK compuesta (IdListaPrecio, IdTenant) contra la clave alterna de ListaPrecioConfiguration
        // -- una FK simple sobre id_lista_precio (PK global, única entre tenants) dejaba pasar el
        // id de una lista de OTRO tenant sin violar la constraint, y solo RLS lo frenaba en runtime.
        builder.HasOne<ListaPrecio>()
            .WithMany()
            .HasForeignKey(c => new { c.IdListaPrecio, c.IdTenant })
            .HasPrincipalKey(l => new { l.Id, l.IdTenant })
            .HasConstraintName("fk_clientes_lista_precio")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
