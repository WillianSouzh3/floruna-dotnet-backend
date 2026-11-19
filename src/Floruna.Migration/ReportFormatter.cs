using System.Text;

namespace Floruna.Migration;

/// <summary>Relatório legível do corte: é o que a pessoa que opera a migração lê antes de virar a chave.</summary>
public static class ReportFormatter
{
    public static string Format(MigrationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var text = new StringBuilder();
        text.AppendLine($"== Migração da loja {report.Tenant} ({report.TenantId}) ==");
        text.AppendLine(report.Committed ? "RESULTADO: gravado." : report.Blockers.Count > 0 ? "RESULTADO: NÃO migrado." : "RESULTADO: ensaio sem gravar.");
        foreach (var blocker in report.Blockers)
        {
            text.AppendLine($"  [BLOQUEIO] {blocker}");
        }

        foreach (var (what, count) in report.Imported)
        {
            text.AppendLine($"  {what}: {count}");
        }

        foreach (var (check, source, target) in report.Reconciliation)
        {
            text.AppendLine($"  conferência {check}: origem {source} / destino {target} {(source == target ? "OK" : "DIVERGENTE")}");
        }

        foreach (var rejected in report.Rejected)
        {
            text.AppendLine($"  [recusado] {rejected}");
        }

        foreach (var warning in report.Warnings)
        {
            text.AppendLine($"  [aviso] {warning}");
        }

        return text.ToString();
    }
}
