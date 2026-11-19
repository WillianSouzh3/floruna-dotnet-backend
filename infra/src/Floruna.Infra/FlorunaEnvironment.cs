using Amazon.CDK.AWS.Logs;

namespace Floruna.Infra;

/// <summary>
/// Tudo que muda entre ambientes, num lugar só (o resto do código não tem "if prod").
/// Staging é barato (1 NAT, banco single-AZ, sem endpoints de interface); produção é resiliente.
/// </summary>
public sealed record FlorunaEnvironment(
    string Name,
    string Account,
    string Region,
    string[] AvailabilityZones,
    string VpcCidr,
    int NatGateways,
    bool InterfaceEndpoints,
    string DbInstanceType,
    bool DbMultiAz,
    int DbAllocatedStorageGb,
    int DbBackupRetentionDays,
    bool DeletionProtection,
    int ApiCpu,
    int ApiMemoryMiB,
    int ApiMinTasks,
    int ApiMaxTasks,
    int WorkerCpu,
    int WorkerMemoryMiB,
    RetentionDays LogRetention,
    string PlatformDomain,
    string ApiPublicUrl,
    string[] CorsOrigins,
    string? CertificateArn,
    string? AlarmEmail,
    string ImageTag)
{
    public string Prefix => $"Floruna-{Name}";

    public bool IsProduction => Name == "prod";

    public static FlorunaEnvironment Staging(string account, string imageTag) => new(
        "staging", account, "sa-east-1", ["sa-east-1a", "sa-east-1b"], "10.20.0.0/16",
        NatGateways: 1, InterfaceEndpoints: false,
        DbInstanceType: "t4g.micro", DbMultiAz: false, DbAllocatedStorageGb: 20, DbBackupRetentionDays: 3, DeletionProtection: false,
        ApiCpu: 256, ApiMemoryMiB: 512, ApiMinTasks: 1, ApiMaxTasks: 2, WorkerCpu: 256, WorkerMemoryMiB: 512,
        LogRetention: RetentionDays.TWO_WEEKS,
        PlatformDomain: "staging.floruna.com.br", ApiPublicUrl: "https://api.staging.floruna.com.br", CorsOrigins: ["https://staging.floruna.com.br"],
        CertificateArn: null, AlarmEmail: null, ImageTag: imageTag);

    public static FlorunaEnvironment Production(string account, string imageTag, string certificateArn) => new(
        "prod", account, "sa-east-1", ["sa-east-1a", "sa-east-1b", "sa-east-1c"], "10.30.0.0/16",
        NatGateways: 2, InterfaceEndpoints: true,
        DbInstanceType: "t4g.medium", DbMultiAz: true, DbAllocatedStorageGb: 50, DbBackupRetentionDays: 14, DeletionProtection: true,
        ApiCpu: 512, ApiMemoryMiB: 1024, ApiMinTasks: 2, ApiMaxTasks: 8, WorkerCpu: 256, WorkerMemoryMiB: 512,
        LogRetention: RetentionDays.THREE_MONTHS,
        PlatformDomain: "floruna.com.br", ApiPublicUrl: "https://api.floruna.com.br", CorsOrigins: ["https://floruna.com.br", "https://www.floruna.com.br"],
        CertificateArn: certificateArn, AlarmEmail: null, ImageTag: imageTag);

    /// <summary>
    /// Lido do contexto do CDK: <c>cdk synth -c env=prod -c imageTag=abc123 -c certificateArn=arn:...</c>.
    /// Produção sem certificado é recusada: API de produção não serve HTTP puro.
    /// </summary>
    public static FlorunaEnvironment FromContext(Func<string, string?> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var account = context("account") ?? Environment.GetEnvironmentVariable("CDK_DEFAULT_ACCOUNT") ?? "000000000000";
        var imageTag = context("imageTag") ?? "latest";
        var env = context("env") ?? "staging";
        var built = env switch
        {
            "staging" => Staging(account, imageTag),
            "prod" => Production(account, imageTag,
                context("certificateArn") ?? throw new InvalidOperationException("Produção exige -c certificateArn=... (HTTPS obrigatório).")),
            _ => throw new InvalidOperationException($"Ambiente desconhecido: {env} (use staging ou prod)."),
        };
        return built with { AlarmEmail = context("alarmEmail") };
    }
}
