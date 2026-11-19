using Amazon.CDK;
using Cdklabs.CdkNag;
using Floruna.Infra.Stacks;

namespace Floruna.Infra;

/// <summary>Monta as pilhas de um ambiente. Separado do Program para os testes montarem igual.</summary>
public static class FlorunaApp
{
    public sealed record Stacks(RegistryStack Registry, NetworkStack Network, DataStack Data, MigrationStack Migration, AppStack App);

    /// <param name="githubRepository">dono/repo para a confiança OIDC; null = não monta a pilha (ela é implantada uma vez, à parte).</param>
    public static Stacks Build(App app, FlorunaEnvironment env, string? githubRepository = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(env);
        var awsEnv = new Amazon.CDK.Environment { Account = env.Account, Region = env.Region };

        var registry = new RegistryStack(app, "Floruna-Registry", new StackProps { Env = awsEnv, Description = "Floruna: imagens da API (compartilhado)" });
        var network = new NetworkStack(app, $"{env.Prefix}-Network", env, new StackProps { Env = awsEnv, Description = $"Floruna {env.Name}: rede" });
        var data = new DataStack(app, $"{env.Prefix}-Data", env, network.Vpc, network.AppSecurityGroup, new StackProps { Env = awsEnv, Description = $"Floruna {env.Name}: banco e segredos" });
        var migration = new MigrationStack(app, $"{env.Prefix}-Migration", env, network.Vpc, network.AppSecurityGroup, data, registry.Repository,
            new StackProps { Env = awsEnv, Description = $"Floruna {env.Name}: task de migrations (roda antes da app)" });
        var application = new AppStack(app, $"{env.Prefix}-App", env, network.Vpc, network.AppSecurityGroup, network.AlbSecurityGroup, data, registry.Repository,
            new StackProps { Env = awsEnv, Description = $"Floruna {env.Name}: ECS Fargate, ALB, WAF, alarmes" });

        if (githubRepository is not null)
        {
            var github = new GitHubDeployStack(app, "Floruna-GitHubDeploy", githubRepository,
                new StackProps { Env = awsEnv, Description = "Floruna: confiança OIDC do GitHub Actions (implantada uma vez)" });
            Tags.Of(github).Add("environment", "shared");
            NagSuppressions.AddStackSuppressions(github,
            [
                new NagPackSuppression { Id = "AwsSolutions-IAM5", Reason = "ecr:GetAuthorizationToken não aceita recurso; RunTask/DescribeTasks restritos por condição ao cluster Floruna-*; curinga nas roles do bootstrap do CDK é o padrão documentado." },
            ]);
        }

        Tags.Of(app).Add("project", "floruna");
        Tags.Of(app).Add("managed-by", "cdk");
        foreach (var stack in new Stack[] { network, data, migration, application })
        {
            Tags.Of(stack).Add("environment", env.Name); // custo por ambiente no Cost Explorer
        }

        Tags.Of(registry).Add("environment", "shared");

        // Regras de segurança/boas práticas (AWS Solutions) checadas em TODO synth. Exceções ficam
        // documentadas no código, com o motivo (Suppressions).
        Aspects.Of(app).Add(new AwsSolutionsChecks(new NagPackProps { Verbose = true }));
        Suppressions.Apply(env, network, data, migration, application);
        return new Stacks(registry, network, data, migration, application);
    }
}
