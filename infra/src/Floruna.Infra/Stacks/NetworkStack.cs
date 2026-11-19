using Amazon.CDK;
using Amazon.CDK.AWS.EC2;
using Constructs;

namespace Floruna.Infra.Stacks;

/// <summary>
/// VPC em três camadas: pública (só o load balancer e os NAT), privada com saída (tasks do ECS)
/// e ISOLADA (banco: sem rota para a internet, nem de saída).
/// </summary>
public sealed class NetworkStack : Stack
{
    public NetworkStack(Construct scope, string id, FlorunaEnvironment env, IStackProps props)
        : base(scope, id, props)
    {
        ArgumentNullException.ThrowIfNull(env);
        Vpc = new Vpc(this, "Vpc", new VpcProps
        {
            IpAddresses = IpAddresses.Cidr(env.VpcCidr),
            AvailabilityZones = env.AvailabilityZones,
            // Um NAT por AZ em produção (a queda de uma AZ não derruba a saída das outras); um só em staging (custo).
            NatGateways = env.NatGateways,
            SubnetConfiguration =
            [
                new SubnetConfiguration { Name = "public", SubnetType = SubnetType.PUBLIC, CidrMask = 24 },
                new SubnetConfiguration { Name = "app", SubnetType = SubnetType.PRIVATE_WITH_EGRESS, CidrMask = 22 },
                new SubnetConfiguration { Name = "data", SubnetType = SubnetType.PRIVATE_ISOLATED, CidrMask = 24 },
            ],
            // Tráfego REJEITADO vai para o CloudWatch: investigação de incidente sem pagar por todo o tráfego.
            FlowLogs = new Dictionary<string, IFlowLogOptions>
            {
                ["Rejected"] = new FlowLogOptions { Destination = FlowLogDestination.ToCloudWatchLogs(), TrafficType = FlowLogTrafficType.REJECT },
            },
        });

        AppSecurityGroup = new SecurityGroup(this, "AppSg", new SecurityGroupProps
        {
            Vpc = Vpc,
            Description = "Tasks da aplicacao (api, worker, migrator)",
            AllowAllOutbound = true, // gateway de pagamento, SMTP, APIs da AWS (via NAT/endpoints)
        });

        AlbSecurityGroup = new SecurityGroup(this, "AlbSg", new SecurityGroupProps
        {
            Vpc = Vpc,
            Description = "ALB publico",
            AllowAllOutbound = false,
        });
        AlbSecurityGroup.AddIngressRule(Peer.AnyIpv4(), Port.Tcp(443), "HTTPS");
        AlbSecurityGroup.AddIngressRule(Peer.AnyIpv4(), Port.Tcp(80), "HTTP (redireciona para HTTPS; em staging sem certificado, atende)");
        // Tasks só aceitam tráfego do ALB, na porta da aplicação.
        AppSecurityGroup.AddIngressRule(AlbSecurityGroup, Port.Tcp(4000), "API a partir do ALB");
        AlbSecurityGroup.AddEgressRule(AppSecurityGroup, Port.Tcp(4000), "ALB para as tasks");

        // S3 por gateway é grátis (camadas das imagens do ECR moram no S3).
        Vpc.AddGatewayEndpoint("S3", new GatewayVpcEndpointOptions { Service = GatewayVpcEndpointAwsService.S3 });
        if (env.InterfaceEndpoints)
        {
            // Em produção, puxar imagem, gravar log e ler segredo não passam pelo NAT (menos custo por GB e
            // menos superfície). Em staging o custo fixo dos endpoints não compensa.
            foreach (var (name, service) in new[]
            {
                ("EcrApi", InterfaceVpcEndpointAwsService.ECR),
                ("EcrDocker", InterfaceVpcEndpointAwsService.ECR_DOCKER),
                ("Logs", InterfaceVpcEndpointAwsService.CLOUDWATCH_LOGS),
                ("SecretsManager", InterfaceVpcEndpointAwsService.SECRETS_MANAGER),
            })
            {
                Vpc.AddInterfaceEndpoint(name, new InterfaceVpcEndpointOptions { Service = service, PrivateDnsEnabled = true });
            }
        }
    }

    public IVpc Vpc { get; }

    /// <summary>
    /// SG das tasks (api, worker, migrator). Mora AQUI, e não na pilha da aplicação, para as dependências
    /// irem num sentido só (rede → dados → app): o banco libera a porta para ele sem ciclo entre pilhas.
    /// </summary>
    public SecurityGroup AppSecurityGroup { get; }

    /// <summary>SG do ALB público. Também aqui: as regras entre SGs (ALB → tasks:4000) ficam todas numa pilha só.</summary>
    public SecurityGroup AlbSecurityGroup { get; }
}
