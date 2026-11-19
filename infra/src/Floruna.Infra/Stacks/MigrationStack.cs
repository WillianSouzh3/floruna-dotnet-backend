using Amazon.CDK;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.ECR;
using Amazon.CDK.AWS.ECS;
using Amazon.CDK.AWS.Logs;
using Constructs;

namespace Floruna.Infra.Stacks;

/// <summary>
/// A task "migrator" numa pilha PRÓPRIA. Motivo: se ela morasse na pilha da aplicação, atualizar a
/// imagem trocaria a API e o migrator ao MESMO tempo, e as tasks novas subiriam antes do schema.
/// Separada, o pipeline faz: deploy desta pilha (imagem nova) → <c>ecs run-task</c> → espera
/// exit code 0 → só então deploy da aplicação. Migrations precisam ser compatíveis com a versão
/// anterior (expand/contract): durante o rollout, as duas versões convivem com o schema novo.
/// </summary>
public sealed class MigrationStack : Stack
{
    public MigrationStack(Construct scope, string id, FlorunaEnvironment env, IVpc vpc, ISecurityGroup appSecurityGroup, DataStack data, IRepository repository, IStackProps props)
        : base(scope, id, props)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(data);

        // Cluster só para tarefas avulsas (migrations hoje; scripts de manutenção amanhã). Cluster ECS não custa nada parado.
        var cluster = new Cluster(this, "OpsCluster", new ClusterProps { Vpc = vpc, ContainerInsightsV2 = ContainerInsights.ENHANCED });

        var task = new FargateTaskDefinition(this, "MigratorTask", new FargateTaskDefinitionProps
        {
            Cpu = 256,
            MemoryLimitMiB = 512,
            RuntimePlatform = new RuntimePlatform { CpuArchitecture = CpuArchitecture.ARM64, OperatingSystemFamily = OperatingSystemFamily.LINUX },
        });
        task.AddContainer("migrator", new ContainerDefinitionOptions
        {
            Image = ContainerImage.FromEcrRepository(repository, env.ImageTag),
            Command = ["migrate"],
            Logging = LogDriver.AwsLogs(new AwsLogDriverProps
            {
                StreamPrefix = "migrator",
                LogGroup = new LogGroup(this, "MigratorLogs", new LogGroupProps
                {
                    Retention = env.LogRetention,
                    RemovalPolicy = env.IsProduction ? RemovalPolicy.RETAIN : RemovalPolicy.DESTROY,
                }),
            }),
            Environment = new Dictionary<string, string> { ["Logging__Console__FormatterName"] = "json" },
            Secrets = AppStack.DatabaseSecrets(data), // só o banco: o migrator não precisa (nem deve ver) os outros segredos
        });

        // O pipeline lê estes valores para montar o "aws ecs run-task".
        _ = new CfnOutput(this, "OpsClusterName", new CfnOutputProps { Value = cluster.ClusterName });
        _ = new CfnOutput(this, "MigratorTaskDefinition", new CfnOutputProps { Value = task.TaskDefinitionArn });
        _ = new CfnOutput(this, "AppSecurityGroup", new CfnOutputProps { Value = appSecurityGroup.SecurityGroupId });
        _ = new CfnOutput(this, "AppSubnets", new CfnOutputProps
        {
            Value = Fn.Join(",", vpc.SelectSubnets(new SubnetSelection { SubnetType = SubnetType.PRIVATE_WITH_EGRESS }).SubnetIds),
        });
    }
}
