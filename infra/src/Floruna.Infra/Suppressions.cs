using Cdklabs.CdkNag;
using Floruna.Infra.Stacks;

namespace Floruna.Infra;

/// <summary>
/// Exceções às regras do cdk-nag. Cada uma é uma DECISÃO, com motivo: suprimir sem explicar é
/// esconder dívida. Só entra aqui o que o relatório realmente apontou (supressão sem alvo é ruído).
/// Revisadas em code review como qualquer outra mudança de segurança.
/// </summary>
internal static class Suppressions
{
    public static void Apply(FlorunaEnvironment env, NetworkStack network, DataStack data, MigrationStack migration, AppStack app)
    {
        NagSuppressions.AddStackSuppressions(network,
        [
            new NagPackSuppression { Id = "AwsSolutions-EC23", Reason = "SG do ALB público: aceita 80/443 de qualquer IP por definição (vitrine da loja). O WAF filtra na frente." },
        ]);

        NagSuppressions.AddStackSuppressions(data,
        [
            new NagPackSuppression { Id = "AwsSolutions-SMG4", Reason = "Rotação automática fica para depois: exige a app reler a senha sem reiniciar (UsePeriodicPasswordProvider do Npgsql). Backlog na ADR 0011." },
            new NagPackSuppression { Id = "AwsSolutions-RDS11", Reason = "Porta padrão do PostgreSQL: o banco fica em subnet isolada e o SG só aceita as tasks; trocar a porta não agrega segurança real." },
            new NagPackSuppression { Id = "AwsSolutions-IAM4", Reason = "Políticas gerenciadas da AWS em roles criadas pelo CDK: Enhanced Monitoring do RDS e a função que ajusta a retenção dos logs do RDS." },
            new NagPackSuppression { Id = "AwsSolutions-IAM5", Reason = "Função do CDK (LogRetention) que ajusta a retenção dos logs do RDS precisa de logs:* em log groups criados pelo próprio RDS." },
        ]);
        if (!env.IsProduction)
        {
            NagSuppressions.AddStackSuppressions(data,
            [
                new NagPackSuppression { Id = "AwsSolutions-RDS3", Reason = "Staging é single-AZ por custo; produção é Multi-AZ (coberto por teste)." },
                new NagPackSuppression { Id = "AwsSolutions-RDS10", Reason = "Staging sem proteção contra exclusão para poder ser recriado; produção protegida (coberto por teste)." },
            ]);
        }

        NagSuppressions.AddStackSuppressions(migration,
        [
            new NagPackSuppression { Id = "AwsSolutions-ECS2", Reason = "Única variável de ambiente é o formato do log; a senha do banco vem por 'secrets'." },
            new NagPackSuppression { Id = "AwsSolutions-IAM5", Reason = "Curingas gerados pelo CDK: ecr:GetAuthorizationToken (não aceita recurso) e logs no log group da própria task." },
        ]);

        NagSuppressions.AddStackSuppressions(app,
        [
            new NagPackSuppression { Id = "AwsSolutions-ECS2", Reason = "Variáveis de ambiente só com configuração NÃO secreta; segredos vão por 'secrets' (Secrets Manager), coberto por teste." },
            new NagPackSuppression { Id = "AwsSolutions-S1", Reason = "Bucket de access logs do ALB não precisa de log de acesso de si mesmo (recursão)." },
            new NagPackSuppression { Id = "AwsSolutions-IAM5", Reason = "Curingas gerados pelo CDK: ecr:GetAuthorizationToken (não aceita recurso), logs no log group da própria task e a limpeza do bucket de staging (autoDeleteObjects)." },
        ]);
    }
}
