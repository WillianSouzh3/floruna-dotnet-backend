using Amazon.CDK;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.Logs;
using Amazon.CDK.AWS.RDS;
using Amazon.CDK.AWS.SecretsManager;
using Constructs;
using InstanceType = Amazon.CDK.AWS.EC2.InstanceType;

namespace Floruna.Infra.Stacks;

/// <summary>
/// Estado durável: PostgreSQL (RDS) e os segredos da aplicação. Pilha separada de propósito: um
/// deploy da aplicação nunca tem como derrubar o banco, e a pilha tem proteção contra exclusão em produção.
/// </summary>
public sealed class DataStack : Stack
{
    public DataStack(Construct scope, string id, FlorunaEnvironment env, IVpc vpc, ISecurityGroup appSecurityGroup, IStackProps props)
        : base(scope, id, props)
    {
        ArgumentNullException.ThrowIfNull(env);
        DatabaseSecurityGroup = new SecurityGroup(this, "DatabaseSg", new SecurityGroupProps
        {
            Vpc = vpc,
            Description = "PostgreSQL: entrada so a partir das tasks da aplicacao",
            AllowAllOutbound = false,
        });
        // Banco: SÓ a partir das tasks, e só na porta do PostgreSQL.
        DatabaseSecurityGroup.AddIngressRule(appSecurityGroup, Port.Tcp(5432), "PostgreSQL a partir das tasks");

        Database = new DatabaseInstance(this, "Database", new DatabaseInstanceProps
        {
            Engine = DatabaseInstanceEngine.Postgres(new PostgresInstanceEngineProps { Version = PostgresEngineVersion.VER_17 }),
            InstanceType = new InstanceType(env.DbInstanceType), // Graviton (t4g): ~20% mais barato que x86
            Vpc = vpc,
            VpcSubnets = new SubnetSelection { SubnetType = SubnetType.PRIVATE_ISOLATED },
            SecurityGroups = [DatabaseSecurityGroup],
            PubliclyAccessible = false,
            // Senha gerada no Secrets Manager; o segredo tem host, porta, banco, usuário e senha (o ECS injeta campo a campo).
            Credentials = Credentials.FromGeneratedSecret("floruna", new CredentialsBaseOptions { SecretName = $"floruna/{env.Name}/database" }),
            DatabaseName = "floruna",
            MultiAz = env.DbMultiAz, // standby síncrono em outra AZ: failover automático em ~1-2 min
            AllocatedStorage = env.DbAllocatedStorageGb,
            MaxAllocatedStorage = env.DbAllocatedStorageGb * 4, // storage autoscaling
            StorageType = StorageType.GP3,
            StorageEncrypted = true,
            BackupRetention = Duration.Days(env.DbBackupRetentionDays), // PITR: restaura para qualquer segundo da janela
            DeleteAutomatedBackups = false,
            DeletionProtection = env.DeletionProtection,
            RemovalPolicy = RemovalPolicy.SNAPSHOT, // mesmo excluindo a pilha, sobra um snapshot
            EnablePerformanceInsights = true,
            MonitoringInterval = Duration.Seconds(60),
            CloudwatchLogsExports = ["postgresql"],
            CloudwatchLogsRetention = env.LogRetention,
            AutoMinorVersionUpgrade = true,
            PreferredMaintenanceWindow = "Mon:06:00-Mon:07:00", // 03:00 em São Paulo
            PreferredBackupWindow = "05:00-05:45",
            CaCertificate = CaCertificate.RDS_CA_RSA2048_G1,
        });

        // Segredos da aplicação, gerados pela AWS (ninguém digita, ninguém vê). 64 caracteres
        // aleatórios: o protetor deriva a chave AES com HKDF (EncryptionKeyMaterial).
        JwtSecret = GeneratedSecret("JwtSecret", $"floruna/{env.Name}/jwt", "Segredo HMAC dos tokens (o mesmo AUTH_SECRET do BFF)");
        EncryptionKey = GeneratedSecret("EncryptionKey", $"floruna/{env.Name}/encryption-key", "Chave dos tokens de gateway das lojas (AES-256-GCM)");

        if (env.IsProduction)
        {
            TerminationProtection = true;
        }
    }

    public DatabaseInstance Database { get; }

    public SecurityGroup DatabaseSecurityGroup { get; }

    public ISecret JwtSecret { get; }

    public ISecret EncryptionKey { get; }

    private Amazon.CDK.AWS.SecretsManager.Secret GeneratedSecret(string id, string name, string description) =>
        new(this, id, new SecretProps
        {
            SecretName = name,
            Description = description,
            GenerateSecretString = new SecretStringGenerator { PasswordLength = 64, ExcludePunctuation = true },
            RemovalPolicy = RemovalPolicy.RETAIN, // perder a chave = perder os tokens cifrados das lojas
        });
}
