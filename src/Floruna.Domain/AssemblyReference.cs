namespace Floruna.Domain;

/// <summary>
/// Âncora para localizar o assembly do domínio (testes de arquitetura, scan).
/// Os bounded contexts (Catalog, Inventory, Ordering...) entram a partir da Fase 1.
/// </summary>
public static class AssemblyReference
{
    public static readonly System.Reflection.Assembly Assembly = typeof(AssemblyReference).Assembly;
}
