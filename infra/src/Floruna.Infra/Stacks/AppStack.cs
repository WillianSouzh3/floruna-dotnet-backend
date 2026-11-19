using Amazon.CDK;
using Amazon.CDK.AWS.CloudWatch;
using Amazon.CDK.AWS.CloudWatch.Actions;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.ECR;
using Amazon.CDK.AWS.ECS;
using Amazon.CDK.AWS.ElasticLoadBalancingV2;
using Amazon.CDK.AWS.Logs;
using Amazon.CDK.AWS.S3;
using Amazon.CDK.AWS.SNS;
using Amazon.CDK.AWS.SNS.Subscriptions;
using Amazon.CDK.AWS.WAFv2;
using Constructs;
using EcsSecret = Amazon.CDK.AWS.ECS.Secret;
using HealthCheck = Amazon.CDK.AWS.ElasticLoadBalancingV2.HealthCheck;
using Protocol = Amazon.CDK.AWS.ElasticLoadBalancingV2.Protocol;

namespace Floruna.Infra.Stacks;

/// <summary>
/// A aplicação: UMA imagem, TRÊS papéis (Bulkhead):
/// <list type="bullet">
/// <item><b>api</b>: atende HTTP atrás do ALB, jobs desligados, escala por CPU e requisições.</item>
/// <item><b>worker</b>: outbox, expirações, reembolsos pendentes. Escala separado e não disputa CPU com o checkout.</item>
/// <item><b>migrator</b>: na <see cref="MigrationStack"/>, rodado pelo pipeline ANTES desta pilha.</item>
/// </list>
/// </summary>
public sealed class AppStack : Stack
{
    private const int ContainerPort = 4000;
    private const string CollectorVersion = "v0.51.0";

    public AppStack(Construct scope, string id, FlorunaEnvironment env, IVpc vpc, ISecurityGroup appSecurityGroup, ISecurityGroup albSecurityGroup, DataStack data, IRepository repository, IStackProps props)
        : base(scope, id, props)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(data);

        var cluster = new Cluster(this, "Cluster", new ClusterProps
        {
            Vpc = vpc,
            ContainerInsightsV2 = ContainerInsights.ENHANCED, // métricas por task/container no CloudWatch
        });

        var image = ContainerImage.FromEcrRepository(repository, env.ImageTag);
        var secrets = Secrets(data);

        // ── API ──
        var apiTask = TaskDefinition("ApiTask", env.ApiCpu, env.ApiMemoryMiB);
        apiTask.AddContainer("api", new ContainerDefinitionOptions
        {
            Image = image,
            Logging = Logs("api", env),
            Environment = EnvironmentVariables(env, vpc, backgroundJobs: false, serviceName: "floruna-api"),
            Secrets = secrets,
            PortMappings = [new PortMapping { ContainerPort = ContainerPort }],
            StopTimeout = Duration.Seconds(30), // SIGTERM → o ASP.NET termina as requisições em andamento
        });
        AddTelemetryCollector(apiTask, "api", env);
        var api = new FargateService(this, "ApiService", new FargateServiceProps
        {
            Cluster = cluster,
            TaskDefinition = apiTask,
            DesiredCount = env.ApiMinTasks,
            AssignPublicIp = false,
            VpcSubnets = new SubnetSelection { SubnetType = SubnetType.PRIVATE_WITH_EGRESS },
            SecurityGroups = [appSecurityGroup],
            // Deploy que não fica saudável volta sozinho para a versão anterior.
            CircuitBreaker = new DeploymentCircuitBreaker { Enable = true, Rollback = true },
            MinHealthyPercent = 100, // rolling sem perder capacidade
            MaxHealthyPercent = 200,
            HealthCheckGracePeriod = Duration.Seconds(60),
        });

        // ── Worker ──
        var workerTask = TaskDefinition("WorkerTask", env.WorkerCpu, env.WorkerMemoryMiB);
        workerTask.AddContainer("worker", new ContainerDefinitionOptions
        {
            Image = image,
            Logging = Logs("worker", env),
            Environment = EnvironmentVariables(env, vpc, backgroundJobs: true, serviceName: "floruna-worker"),
            Secrets = secrets,
            StopTimeout = Duration.Seconds(60), // termina o lote da outbox em andamento
        });
        AddTelemetryCollector(workerTask, "worker", env);
        _ = new FargateService(this, "WorkerService", new FargateServiceProps
        {
            Cluster = cluster,
            TaskDefinition = workerTask,
            DesiredCount = 1, // jobs são seguros com várias réplicas (SKIP LOCKED, leases): escalar é só mudar o número
            AssignPublicIp = false,
            VpcSubnets = new SubnetSelection { SubnetType = SubnetType.PRIVATE_WITH_EGRESS },
            SecurityGroups = [appSecurityGroup],
            CircuitBreaker = new DeploymentCircuitBreaker { Enable = true, Rollback = true },
            MinHealthyPercent = 100,
            MaxHealthyPercent = 200,
        });

        // ── Load balancer ──
        var alb = new ApplicationLoadBalancer(this, "Alb", new ApplicationLoadBalancerProps
        {
            Vpc = vpc,
            InternetFacing = true,
            SecurityGroup = albSecurityGroup,
            VpcSubnets = new SubnetSelection { SubnetType = SubnetType.PUBLIC },
            DropInvalidHeaderFields = true, // headers malformados não chegam na aplicação (request smuggling)
        });
        var accessLogs = new Bucket(this, "AlbAccessLogs", new BucketProps
        {
            Encryption = BucketEncryption.S3_MANAGED,
            BlockPublicAccess = BlockPublicAccess.BLOCK_ALL,
            EnforceSSL = true,
            LifecycleRules = [new Amazon.CDK.AWS.S3.LifecycleRule { Expiration = Duration.Days(90) }],
            RemovalPolicy = env.IsProduction ? RemovalPolicy.RETAIN : RemovalPolicy.DESTROY,
            AutoDeleteObjects = !env.IsProduction,
        });
        alb.LogAccessLogs(accessLogs, "alb");

        ApplicationListener listener;
        if (env.CertificateArn is { } certificateArn)
        {
            listener = alb.AddListener("Https", new BaseApplicationListenerProps
            {
                Port = 443,
                Protocol = ApplicationProtocol.HTTPS,
                Certificates = [ListenerCertificate.FromArn(certificateArn)],
                SslPolicy = SslPolicy.RECOMMENDED_TLS, // TLS 1.2+
                Open = false,
            });
            alb.AddRedirect(new ApplicationLoadBalancerRedirectConfig { SourcePort = 80, TargetPort = 443, Open = false });
        }
        else
        {
            listener = alb.AddListener("Http", new BaseApplicationListenerProps { Port = 80, Protocol = ApplicationProtocol.HTTP, Open = false });
        }

        var targets = listener.AddTargets("Api", new AddApplicationTargetsProps
        {
            Port = ContainerPort,
            Protocol = ApplicationProtocol.HTTP,
            Targets = [api],
            HealthCheck = new HealthCheck
            {
                // Pronto = processo de pé E banco respondendo. Se o banco cair, TODOS ficam "unhealthy" e o
                // ALB faz fail-open (continua enviando), o que é o comportamento desejado: a API devolve 503 limpo.
                Path = "/api/v1/health/ready",
                Protocol = Protocol.HTTP,
                HealthyHttpCodes = "200",
                Interval = Duration.Seconds(15),
                HealthyThresholdCount = 2,
                UnhealthyThresholdCount = 3,
            },
            DeregistrationDelay = Duration.Seconds(30),
        });

        // ── Autoscaling (target tracking) ──
        var scaling = api.AutoScaleTaskCount(new Amazon.CDK.AWS.ApplicationAutoScaling.EnableScalingProps { MinCapacity = env.ApiMinTasks, MaxCapacity = env.ApiMaxTasks });
        scaling.ScaleOnCpuUtilization("Cpu", new CpuUtilizationScalingProps
        {
            TargetUtilizationPercent = 60,
            ScaleOutCooldown = Duration.Seconds(60),
            ScaleInCooldown = Duration.Seconds(300), // sobe rápido, desce devagar (Dia das Mães não avisa)
        });
        scaling.ScaleOnRequestCount("Requests", new RequestCountScalingProps { RequestsPerTarget = 1000, TargetGroup = targets });

        Waf(alb);
        Alarms(env, alb, targets, api, data);

        _ = new CfnOutput(this, "ApiUrl", new CfnOutputProps { Value = $"{(env.CertificateArn is null ? "http" : "https")}://{alb.LoadBalancerDnsName}" });
        _ = new CfnOutput(this, "ClusterName", new CfnOutputProps { Value = cluster.ClusterName });
    }

    private FargateTaskDefinition TaskDefinition(string id, int cpu, int memory) => new(this, id, new FargateTaskDefinitionProps
    {
        Cpu = cpu,
        MemoryLimitMiB = memory,
        // Graviton (ARM64): ~20% mais barato por vCPU. A imagem é multi-arquitetura (buildx no CI).
        RuntimePlatform = new RuntimePlatform { CpuArchitecture = CpuArchitecture.ARM64, OperatingSystemFamily = OperatingSystemFamily.LINUX },
    });

    /// <summary>
    /// Sidecar do AWS Distro for OpenTelemetry: recebe OTLP da aplicação e envia traces ao X-Ray e métricas
    /// ao CloudWatch (EMF). NÃO essencial: se o coletor cair, a aplicação continua atendendo (perde telemetria,
    /// não vendas).
    /// </summary>
    private void AddTelemetryCollector(FargateTaskDefinition task, string role, FlorunaEnvironment env)
    {
        task.AddContainer($"{role}-otel-collector", new ContainerDefinitionOptions
        {
            ContainerName = "otel-collector",
            Image = ContainerImage.FromRegistry($"public.ecr.aws/aws-observability/aws-otel-collector:{CollectorVersion}"),
            Command = ["--config=/etc/ecs/ecs-default-config.yaml"], // OTLP → X-Ray + CloudWatch EMF
            Essential = false,
            MemoryReservationMiB = 64,
            Logging = Logs($"{role}-otel", env),
        });
        task.AddToTaskRolePolicy(new Amazon.CDK.AWS.IAM.PolicyStatement(new Amazon.CDK.AWS.IAM.PolicyStatementProps
        {
            Sid = "XRayTraces",
            Actions = ["xray:PutTraceSegments", "xray:PutTelemetryRecords", "xray:GetSamplingRules", "xray:GetSamplingTargets", "xray:GetSamplingStatisticSummaries"],
            Resources = ["*"], // o X-Ray não aceita recurso nessas ações
        }));
        task.AddToTaskRolePolicy(new Amazon.CDK.AWS.IAM.PolicyStatement(new Amazon.CDK.AWS.IAM.PolicyStatementProps
        {
            Sid = "EmbeddedMetrics",
            Actions = ["logs:CreateLogGroup", "logs:CreateLogStream", "logs:PutLogEvents", "logs:DescribeLogStreams", "logs:DescribeLogGroups"],
            Resources = [$"arn:aws:logs:{Region}:{Account}:log-group:/aws/ecs/application/metrics*"],
        }));
    }

    private LogDriver Logs(string role, FlorunaEnvironment env) => LogDriver.AwsLogs(new AwsLogDriverProps
    {
        StreamPrefix = role,
        LogGroup = new LogGroup(this, $"{role}Logs", new LogGroupProps
        {
            Retention = env.LogRetention,
            RemovalPolicy = env.IsProduction ? RemovalPolicy.RETAIN : RemovalPolicy.DESTROY,
        }),
    });

    /// <summary>Configuração NÃO secreta. Nada aqui é senha: segredos vão em <see cref="Secrets"/>.</summary>
    private static Dictionary<string, string> EnvironmentVariables(FlorunaEnvironment env, IVpc vpc, bool backgroundJobs, string serviceName)
    {
        var variables = new Dictionary<string, string>
        {
            // OpenTelemetry → coletor ADOT no MESMO task (sidecar, localhost): a app não conhece X-Ray nem CloudWatch.
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4317",
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "grpc",
            ["OTEL_SERVICE_NAME"] = serviceName,
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["Logging__Console__FormatterName"] = "json", // uma linha JSON por log: consultável no Logs Insights
            ["BackgroundJobs__Enabled"] = backgroundJobs ? "true" : "false",
            ["Database__MigrateOnStartup"] = "false", // quem migra é a task migrator
            ["Platform__Domain"] = env.PlatformDomain,
            ["Platform__ApiPublicUrl"] = env.ApiPublicUrl,
            // X-Forwarded-For só é aceito vindo de dentro da VPC (o ALB): o cliente não forja o IP do rate limit.
            ["Network__TrustedProxies__0"] = vpc.VpcCidrBlock,
        };
        for (var i = 0; i < env.CorsOrigins.Length; i++)
        {
            variables[$"Cors__AllowedOrigins__{i}"] = env.CorsOrigins[i];
        }

        return variables;
    }

    /// <summary>Injetados pelo ECS na partida da task, lidos do Secrets Manager: nunca no template nem no console.</summary>
    private static Dictionary<string, EcsSecret> Secrets(DataStack data)
    {
        var secrets = DatabaseSecrets(data);
        secrets["Jwt__Secret"] = EcsSecret.FromSecretsManager(data.JwtSecret);
        secrets["Secrets__EncryptionKey"] = EcsSecret.FromSecretsManager(data.EncryptionKey);
        return secrets;
    }

    internal static Dictionary<string, EcsSecret> DatabaseSecrets(DataStack data)
    {
        var db = data.Database.Secret!;
        return new Dictionary<string, EcsSecret>
        {
            ["Database__Host"] = EcsSecret.FromSecretsManager(db, "host"),
            ["Database__Port"] = EcsSecret.FromSecretsManager(db, "port"),
            ["Database__Name"] = EcsSecret.FromSecretsManager(db, "dbname"),
            ["Database__Username"] = EcsSecret.FromSecretsManager(db, "username"),
            ["Database__Password"] = EcsSecret.FromSecretsManager(db, "password"),
        };
    }

    /// <summary>
    /// WAF na frente do ALB: regras gerenciadas da AWS + limite por IP. Defesa em profundidade:
    /// a API já tem rate limit próprio, mas aqui o abuso é barrado ANTES de gastar CPU das tasks.
    /// </summary>
    private void Waf(ApplicationLoadBalancer alb)
    {
        var acl = new CfnWebACL(this, "Waf", new CfnWebACLProps
        {
            Scope = "REGIONAL",
            DefaultAction = new CfnWebACL.DefaultActionProperty { Allow = new CfnWebACL.AllowActionProperty() },
            VisibilityConfig = Visibility("floruna-waf"),
            Rules = new object[]
            {
                new CfnWebACL.RuleProperty
                {
                    Name = "RateLimitPerIp",
                    Priority = 0,
                    Action = new CfnWebACL.RuleActionProperty { Block = new CfnWebACL.BlockActionProperty() },
                    Statement = new CfnWebACL.StatementProperty
                    {
                        RateBasedStatement = new CfnWebACL.RateBasedStatementProperty { Limit = 2000, AggregateKeyType = "IP" }, // por 5 minutos
                    },
                    VisibilityConfig = Visibility("rate-limit"),
                },
                ManagedRule("AWSManagedRulesCommonRuleSet", 1, countOnly: ["SizeRestrictions_BODY"]), // descrições longas de produto passam de 8 KB
                ManagedRule("AWSManagedRulesKnownBadInputsRuleSet", 2),
                ManagedRule("AWSManagedRulesSQLiRuleSet", 3),
            },
        });
        _ = new CfnWebACLAssociation(this, "WafAlb", new CfnWebACLAssociationProps { ResourceArn = alb.LoadBalancerArn, WebAclArn = acl.AttrArn });
    }

    private static CfnWebACL.RuleProperty ManagedRule(string name, int priority, string[]? countOnly = null) => new()
    {
        Name = name,
        Priority = priority,
        OverrideAction = new CfnWebACL.OverrideActionProperty { None = new Dictionary<string, object>() },
        Statement = new CfnWebACL.StatementProperty
        {
            ManagedRuleGroupStatement = new CfnWebACL.ManagedRuleGroupStatementProperty
            {
                VendorName = "AWS",
                Name = name,
                RuleActionOverrides = countOnly?.Select(rule => (object)new CfnWebACL.RuleActionOverrideProperty
                {
                    Name = rule,
                    ActionToUse = new CfnWebACL.RuleActionProperty { Count = new CfnWebACL.CountActionProperty() },
                }).ToArray(),
            },
        },
        VisibilityConfig = Visibility(name),
    };

    private static CfnWebACL.VisibilityConfigProperty Visibility(string metric) => new()
    {
        CloudWatchMetricsEnabled = true,
        MetricName = metric,
        SampledRequestsEnabled = true,
    };

    /// <summary>Alarmes dos sintomas que o cliente sente (erros, lentidão) e das causas mais comuns.</summary>
    private void Alarms(FlorunaEnvironment env, ApplicationLoadBalancer alb, ApplicationTargetGroup targets, FargateService api, DataStack data)
    {
        // Alarmes do CloudWatch não publicam em tópico cifrado com a chave GERENCIADA do SNS (aws/sns);
        // exigiria KMS próprio com política para o CloudWatch. Tópico só com alarmes, sem dado sensível.
        var topic = new Topic(this, "Alarms", new TopicProps { DisplayName = $"Floruna {env.Name} alarmes", EnforceSSL = true });
        if (env.AlarmEmail is { } email)
        {
            topic.AddSubscription(new EmailSubscription(email));
        }

        var action = new SnsAction(topic);
        var five = Duration.Minutes(5);
        foreach (var alarm in new[]
        {
            new Alarm(this, "Target5xx", new AlarmProps
            {
                AlarmDescription = "Erros 5xx da API acima do normal",
                Metric = alb.Metrics.HttpCodeTarget(HttpCodeTarget.TARGET_5XX_COUNT, new MetricOptions { Period = five, Statistic = "Sum" }),
                Threshold = 20, EvaluationPeriods = 1, TreatMissingData = TreatMissingData.NOT_BREACHING,
            }),
            new Alarm(this, "LatencyP95", new AlarmProps
            {
                AlarmDescription = "p95 da API acima de 2 s por 15 minutos",
                Metric = targets.Metrics.TargetResponseTime(new MetricOptions { Period = five, Statistic = "p95" }),
                Threshold = 2, EvaluationPeriods = 3, TreatMissingData = TreatMissingData.NOT_BREACHING,
            }),
            new Alarm(this, "UnhealthyTargets", new AlarmProps
            {
                AlarmDescription = "Task da API falhando no health check",
                Metric = targets.Metrics.UnhealthyHostCount(new MetricOptions { Period = Duration.Minutes(1), Statistic = "Maximum" }),
                Threshold = 1, ComparisonOperator = ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD, EvaluationPeriods = 3,
            }),
            new Alarm(this, "ApiCpu", new AlarmProps
            {
                AlarmDescription = "CPU da API alta mesmo com autoscaling (chegou no máximo de tasks?)",
                Metric = api.MetricCpuUtilization(new MetricOptions { Period = five }),
                Threshold = 85, EvaluationPeriods = 3,
            }),
            new Alarm(this, "DatabaseCpu", new AlarmProps
            {
                AlarmDescription = "CPU do PostgreSQL alta",
                Metric = data.Database.MetricCPUUtilization(new MetricOptions { Period = five }),
                Threshold = 80, EvaluationPeriods = 3,
            }),
            new Alarm(this, "DatabaseStorage", new AlarmProps
            {
                AlarmDescription = "Menos de 5 GB livres no banco",
                Metric = data.Database.MetricFreeStorageSpace(new MetricOptions { Period = five }),
                Threshold = 5L * 1024 * 1024 * 1024, ComparisonOperator = ComparisonOperator.LESS_THAN_THRESHOLD, EvaluationPeriods = 1,
            }),
        })
        {
            alarm.AddAlarmAction(action);
            alarm.AddOkAction(action);
        }
    }
}
