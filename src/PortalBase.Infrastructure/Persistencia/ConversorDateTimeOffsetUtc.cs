using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace PortalBase.Infrastructure.Persistencia;

/// <summary>
/// Armazena <see cref="DateTimeOffset"/> como <c>datetime</c> em UTC (MySQL não possui
/// tipo com offset). Grava o instante UTC e o materializa de volta com offset zero.
/// </summary>
internal sealed class ConversorDateTimeOffsetUtc : ValueConverter<DateTimeOffset, DateTime>
{
    public ConversorDateTimeOffsetUtc()
        : base(
            modelo => modelo.UtcDateTime,
            provedor => new DateTimeOffset(DateTime.SpecifyKind(provedor, DateTimeKind.Utc), TimeSpan.Zero))
    {
    }
}
