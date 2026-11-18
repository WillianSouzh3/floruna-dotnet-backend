namespace Floruna.SharedKernel;

/// <summary>
/// Identificador fortemente tipado da loja. Impede trocar um TenantId por
/// um CustomerId por engano: os dois são Guid, mas o compilador não deixa
/// passar um no lugar do outro (cura da "primitive obsession").
/// </summary>
public readonly record struct TenantId(Guid Value)
{
    public static TenantId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Marca toda entidade que pertence a UMA loja. A infraestrutura usa este
/// contrato para (1) filtrar toda consulta pelo tenant atual e (2) carimbar
/// o TenantId em toda inserção. O código de negócio nunca filtra à mão.
/// </summary>
public interface ITenantOwned
{
    TenantId TenantId { get; }
}
