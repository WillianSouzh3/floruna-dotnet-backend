using Amazon.CDK;
using Amazon.CDK.AWS.IAM;
using Constructs;

namespace Floruna.Infra.Stacks;

/// <summary>
/// Confiança GitHub Actions → AWS por OIDC. Nenhuma chave de acesso fica guardada no GitHub: a cada
/// execução, o job troca um token assinado pelo GitHub por credenciais TEMPORÁRIAS desta role.
/// Implantada UMA vez, por um administrador (<c>cdk deploy Floruna-GitHubDeploy -c githubRepo=dono/repo</c>).
/// </summary>
public sealed class GitHubDeployStack : Stack
{
    public GitHubDeployStack(Construct scope, string id, string githubRepository, IStackProps props)
        : base(scope, id, props)
    {
        // Recurso nativo do CloudFormation (sem a Lambda de custom resource da versão L2).
        var provider = new CfnOIDCProvider(this, "GitHubOidc", new CfnOIDCProviderProps
        {
            Url = "https://token.actions.githubusercontent.com",
            ClientIdList = ["sts.amazonaws.com"],
        });

        // Só ESTE repositório, e só a branch main ou os ambientes do GitHub (prod exige aprovação lá).
        // Um fork ou outra branch não consegue assumir a role.
        var role = new Role(this, "DeployRole", new RoleProps
        {
            RoleName = "floruna-github-deploy",
            Description = "Assumida pelo GitHub Actions (OIDC) para publicar imagem e implantar",
            MaxSessionDuration = Duration.Hours(1),
            AssumedBy = new WebIdentityPrincipal(provider.AttrArn, new Dictionary<string, object>
            {
                ["StringEquals"] = new Dictionary<string, object> { ["token.actions.githubusercontent.com:aud"] = "sts.amazonaws.com" },
                ["StringLike"] = new Dictionary<string, object>
                {
                    ["token.actions.githubusercontent.com:sub"] = new[]
                    {
                        $"repo:{githubRepository}:ref:refs/heads/main",
                        $"repo:{githubRepository}:environment:*",
                    },
                },
            }),
        });

        // Deploy de verdade é feito pelas roles do bootstrap do CDK (cdk-hnb659fds-*): esta role só as assume.
        role.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Sid = "AssumeCdkBootstrapRoles",
            Actions = ["sts:AssumeRole"],
            Resources = [$"arn:aws:iam::{Account}:role/cdk-hnb659fds-*-{Account}-{Region}"],
        }));
        role.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Sid = "EcrLogin",
            Actions = ["ecr:GetAuthorizationToken"],
            Resources = ["*"], // a API não aceita recurso
        }));
        role.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Sid = "PushApiImage",
            Actions = ["ecr:BatchCheckLayerAvailability", "ecr:InitiateLayerUpload", "ecr:UploadLayerPart", "ecr:CompleteLayerUpload", "ecr:PutImage", "ecr:BatchGetImage", "ecr:DescribeImages"],
            Resources = [$"arn:aws:ecr:{Region}:{Account}:repository/floruna-api"],
        }));
        role.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Sid = "RunMigrations",
            Actions = ["ecs:RunTask", "ecs:DescribeTasks"],
            Resources = ["*"],
            Conditions = new Dictionary<string, object> { ["ArnLike"] = new Dictionary<string, object> { ["ecs:cluster"] = $"arn:aws:ecs:{Region}:{Account}:cluster/Floruna-*" } },
        }));
        role.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Sid = "PassMigratorRoles",
            Actions = ["iam:PassRole"],
            Resources = [$"arn:aws:iam::{Account}:role/Floruna-*-Migration-*"],
            Conditions = new Dictionary<string, object> { ["StringEquals"] = new Dictionary<string, object> { ["iam:PassedToService"] = "ecs-tasks.amazonaws.com" } },
        }));

        _ = new CfnOutput(this, "DeployRoleArn", new CfnOutputProps { Value = role.RoleArn, Description = "Secret AWS_DEPLOY_ROLE_ARN do repositório" });
    }
}
