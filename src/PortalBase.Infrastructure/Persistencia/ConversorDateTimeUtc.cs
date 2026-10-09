using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace PortalBase.Infrastructure.Persistencia;

/// <summary>Garante que valores <see cref="DateTime"/> voltem do banco marcados como UTC.</summary>
internal sealed class ConversorDateTimeUtc : ValueConverter<DateTime, DateTime>
{
    public ConversorDateTimeUtc()
        : base(
            modelo => modelo,
            provedor => DateTime.SpecifyKind(provedor, DateTimeKind.Utc))
    {
    }
}
