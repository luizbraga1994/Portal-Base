using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace PortalBase.Infrastructure.Persistencia;

/// <summary>
/// Armazena <see cref="DateOnly"/> como <c>date</c> no MySQL. O provider Oracle
/// (<c>MySql.EntityFrameworkCore</c>) não materializa <see cref="DateOnly"/> nativamente (retorna
/// <see cref="DateTime"/>), então esta conversão explícita é necessária para as colunas de
/// data (sem hora) do domínio. A hora é sempre meia-noite.
/// </summary>
internal sealed class ConversorDateOnly : ValueConverter<DateOnly, DateTime>
{
    public ConversorDateOnly()
        : base(
            modelo => modelo.ToDateTime(TimeOnly.MinValue),
            provedor => DateOnly.FromDateTime(provedor))
    {
    }
}
