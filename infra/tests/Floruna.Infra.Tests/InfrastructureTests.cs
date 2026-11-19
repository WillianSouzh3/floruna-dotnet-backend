using System.Text.Json;
using Amazon.CDK;
using Amazon.CDK.Assertions;
using Floruna.Infra;
using Annotations = Amazon.CDK.Assertions.Annotations;

namespace Floruna.Infra.Tests;

/// <summary>
/// App de teste com o MESMO contexto que o CLI leria de <c>cdk.json</c>/<c>cdk.context.json</c>
/// (lista de AZs da conta fictícia, força das referências entre pilhas).
/// </summary>
internal static class TestApp
{
    public static App Create() => new(new AppProps
    {
        Context = new Dictionary<string, object>
        {
            ["availability-zones:account=123456789012:region=sa-east-1"] = new[] { "sa-east-1a", "sa-east-1b", "sa-east-1c" },
            ["@aws-cdk/core:defaultCrossStackReferences"] = "strong",
        },
    });
}

/// <summary>Monta e sintetiza as pilhas UMA vez por ambiente (jsii é caro para subir).</summary>
public sealed class ProductionFixture
{
    public ProductionFixture()
    {
        System.Environment.SetEnvironmentVariable("JSII_RUNTIME_PACKAGE_CACHE", "disabled");
        Stacks = FlorunaApp.Build(TestApp.Create(), FlorunaEnvironment.Production("123456789012", "abc1234", "arn:aws:acm:sa-east-1:123456789012:certificate/test"));
        Network = Template.FromStack(Stacks.Network);
        Data = Template.FromStack(Stacks.Data);
        App = Template.FromStack(Stacks.App);
        Migration = Template.FromStack(Stacks.Migration);
    }

    public FlorunaApp.Stacks Stacks { get; }

    public Template Network { get; }

    public Template Data { get; }

    public Template App { get; }

    public Template Migration { get; }
}

public sealed class ProductionInfrastructureTests(ProductionFixture prod) : IClassFixture<ProductionFixture>
{
    [Fact]
    public void Database_is_private_encrypted_multi_az_backed_up_and_protected()
    {
        prod.Data.HasResourceProperties("AWS::RDS::DBInstance", new Dictionary<string, object>
        {
            ["Engine"] = "postgres",
            ["StorageEncrypted"] = true,
            ["PubliclyAccessible"] = false,
            ["MultiAZ"] = true,
            ["DeletionProtection"] = true,
            ["BackupRetentionPeriod"] = 14,
        });
        prod.Stacks.Data.TerminationProtection.ShouldBe(true);
    }

    [Fact]
    public void Database_accepts_only_the_app_tasks_on_5432()
    {
        var data = Json(prod.Data);
        var ingress = Resources(data, "AWS::EC2::SecurityGroupIngress").ToList();

        var rule = ingress.ShouldHaveSingleItem();
        rule.GetProperty("FromPort").GetInt32().ShouldBe(5432);
        rule.TryGetProperty("SourceSecurityGroupId", out _).ShouldBeTrue();
        rule.TryGetProperty("CidrIp", out _).ShouldBeFalse(); // nunca por faixa de IP
    }

    [Fact]
    public void Secrets_reach_the_tasks_only_through_secrets_manager()
    {
        foreach (var container in AppContainers(prod.App).Concat(Containers(prod.Migration)))
        {
            var names = Names(container, "Environment");
            names.ShouldNotContain(n => n.Contains("Password", StringComparison.OrdinalIgnoreCase)
                                        || n.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                                        || n.Contains("EncryptionKey", StringComparison.OrdinalIgnoreCase));
            Names(container, "Secrets").ShouldContain("Database__Password");
        }
    }

    [Fact]
    public void One_image_three_roles_api_worker_and_migrator()
    {
        var containers = AppContainers(prod.App).Concat(Containers(prod.Migration)).ToDictionary(c => c.GetProperty("Name").GetString()!);

        EnvValue(containers["api"], "BackgroundJobs__Enabled").ShouldBe("false");
        EnvValue(containers["worker"], "BackgroundJobs__Enabled").ShouldBe("true");
        containers["api"].GetProperty("PortMappings")[0].GetProperty("ContainerPort").GetInt32().ShouldBe(4000);

        var migrator = containers["migrator"];
        migrator.GetProperty("Command")[0].GetString().ShouldBe("migrate");
        Names(migrator, "Secrets").ShouldNotContain("Jwt__Secret"); // menor privilégio: o migrator só vê o banco
        containers.Values.Select(c => c.GetProperty("Image").ToString()).ShouldAllBe(i => i.Contains("abc1234")); // a MESMA tag nos três papéis
    }

    [Fact]
    public void Migrator_lives_in_its_own_stack_so_it_runs_before_the_app_rolls()
    {
        AppContainers(prod.App).Select(c => c.GetProperty("Name").GetString()).ShouldBe(["api", "worker"], ignoreOrder: true);
        prod.Stacks.App.Dependencies.ShouldNotContain(prod.Stacks.Migration); // o pipeline decide a ordem, não um acoplamento entre pilhas
        prod.Migration.ResourceCountIs("AWS::ECS::Cluster", 1);
    }

    [Fact]
    public void Api_and_worker_ship_telemetry_through_a_non_essential_adot_sidecar()
    {
        foreach (var task in Resources(Json(prod.App), "AWS::ECS::TaskDefinition"))
        {
            var containers = task.GetProperty("ContainerDefinitions").EnumerateArray().ToList();
            var collector = containers.Where(c => c.GetProperty("Name").GetString() == "otel-collector").ShouldHaveSingleItem();
            collector.GetProperty("Essential").GetBoolean().ShouldBeFalse(); // telemetria caindo não derruba vendas
            collector.GetProperty("Image").GetString()!.ShouldStartWith("public.ecr.aws/aws-observability/aws-otel-collector:v");

            var app = containers.Single(c => c.GetProperty("Name").GetString() != "otel-collector");
            EnvValue(app, "OTEL_EXPORTER_OTLP_ENDPOINT").ShouldBe("http://localhost:4317");
        }

        var policies = Resources(Json(prod.App), "AWS::IAM::Policy").Select(p => p.GetProperty("PolicyDocument").ToString());
        policies.ShouldContain(p => p.Contains("xray:PutTraceSegments", StringComparison.Ordinal));
    }

    [Fact]
    public void Tasks_run_in_private_subnets_on_graviton_with_automatic_rollback()
    {
        var app = Json(prod.App);
        foreach (var service in Resources(app, "AWS::ECS::Service"))
        {
            service.GetProperty("NetworkConfiguration").GetProperty("AwsvpcConfiguration").GetProperty("AssignPublicIp").GetString().ShouldBe("DISABLED");
            var breaker = service.GetProperty("DeploymentConfiguration").GetProperty("DeploymentCircuitBreaker");
            breaker.GetProperty("Rollback").GetBoolean().ShouldBeTrue();
        }

        foreach (var task in Resources(app, "AWS::ECS::TaskDefinition"))
        {
            task.GetProperty("RuntimePlatform").GetProperty("CpuArchitecture").GetString().ShouldBe("ARM64");
        }
    }

    [Fact]
    public void Https_only_with_modern_tls_and_http_redirects()
    {
        prod.App.HasResourceProperties("AWS::ElasticLoadBalancingV2::Listener", new Dictionary<string, object>
        {
            ["Port"] = 443,
            ["Protocol"] = "HTTPS",
            ["SslPolicy"] = "ELBSecurityPolicy-TLS13-1-2-2021-06",
        });
        prod.App.HasResourceProperties("AWS::ElasticLoadBalancingV2::Listener", new Dictionary<string, object>
        {
            ["Port"] = 80,
            ["DefaultActions"] = new object[] { new Dictionary<string, object> { ["Type"] = "redirect" } },
        });
        prod.App.HasResourceProperties("AWS::ElasticLoadBalancingV2::LoadBalancer", Match.ObjectLike(new Dictionary<string, object>
        {
            ["LoadBalancerAttributes"] = Match.ArrayWith(new object[]
            {
                new Dictionary<string, object> { ["Key"] = "routing.http.drop_invalid_header_fields.enabled", ["Value"] = "true" },
            }),
        }));
    }

    [Fact]
    public void Api_scales_between_the_configured_bounds_on_cpu_and_requests()
    {
        prod.App.HasResourceProperties("AWS::ApplicationAutoScaling::ScalableTarget", new Dictionary<string, object>
        {
            ["MinCapacity"] = 2,
            ["MaxCapacity"] = 8,
        });
        prod.App.ResourceCountIs("AWS::ApplicationAutoScaling::ScalingPolicy", 2);
    }

    [Fact]
    public void Waf_with_rate_limit_and_managed_rules_guards_the_load_balancer()
    {
        var acl = Resources(Json(prod.App), "AWS::WAFv2::WebACL").ShouldHaveSingleItem();
        var rules = acl.GetProperty("Rules").EnumerateArray().ToList();

        rules.Count(r => r.GetProperty("Statement").TryGetProperty("RateBasedStatement", out var _)).ShouldBe(1);
        rules.Select(r => r.GetProperty("Name").GetString()).ShouldContain("AWSManagedRulesCommonRuleSet");
        prod.App.ResourceCountIs("AWS::WAFv2::WebACLAssociation", 1);
    }

    [Fact]
    public void Every_log_group_has_retention()
    {
        foreach (var template in new[] { prod.App, prod.Network })
        {
            foreach (var logGroup in Resources(Json(template), "AWS::Logs::LogGroup"))
            {
                logGroup.TryGetProperty("RetentionInDays", out _).ShouldBeTrue();
            }
        }
    }

    [Fact]
    public void Production_uses_one_nat_per_az_slice_and_private_endpoints()
    {
        prod.Network.ResourceCountIs("AWS::EC2::NatGateway", 2);
        Resources(Json(prod.Network), "AWS::EC2::VPCEndpoint").Count().ShouldBe(5); // S3 (gateway) + ECR x2, Logs, Secrets Manager
    }

    [Fact]
    public void No_unsuppressed_cdk_nag_findings()
    {
        foreach (var stack in new Stack[] { prod.Stacks.Registry, prod.Stacks.Network, prod.Stacks.Data, prod.Stacks.Migration, prod.Stacks.App })
        {
            Annotations.FromStack(stack).FindError("*", Match.StringLikeRegexp("AwsSolutions-.*")).ShouldBeEmpty(stack.StackName);
            Annotations.FromStack(stack).FindWarning("*", Match.StringLikeRegexp("AwsSolutions-.*")).ShouldBeEmpty(stack.StackName);
        }
    }

    [Fact]
    public void Synth_is_deterministic_without_aws_credentials()
    {
        // Contexto faltando (ex.: lista de AZs) faria o CLI chamar a AWS no synth: no CI sem credencial, quebra;
        // com credencial, o template dependeria da conta. Tudo que é lookup precisa estar em cdk.context.json.
        var assembly = Stage.Of(prod.Stacks.App)!.Synth();
        (assembly.Manifest.Missing ?? []).Select(m => m.Key).ShouldBeEmpty();
    }

    [Fact]
    public void Synth_has_no_errors_or_warnings_of_any_kind()
    {
        foreach (var stack in new Stack[] { prod.Stacks.Registry, prod.Stacks.Network, prod.Stacks.Data, prod.Stacks.Migration, prod.Stacks.App })
        {
            Annotations.FromStack(stack).FindError("*", Match.AnyValue()).ShouldBeEmpty(stack.StackName);
            Annotations.FromStack(stack).FindWarning("*", Match.AnyValue()).ShouldBeEmpty(stack.StackName);
        }
    }

    internal static JsonElement Json(Template template) => JsonSerializer.SerializeToElement(template.ToJSON());

    internal static IEnumerable<JsonElement> Resources(JsonElement template, string type) =>
        template.GetProperty("Resources").EnumerateObject()
            .Where(r => r.Value.GetProperty("Type").GetString() == type)
            .Select(r => r.Value.TryGetProperty("Properties", out var p) ? p : default);

    /// <summary>Containers da aplicação (sem o sidecar de telemetria).</summary>
    private static IEnumerable<JsonElement> AppContainers(Template template) =>
        Containers(template).Where(c => c.GetProperty("Name").GetString() != "otel-collector");

    private static IEnumerable<JsonElement> Containers(Template template) =>
        Resources(Json(template), "AWS::ECS::TaskDefinition").SelectMany(t => t.GetProperty("ContainerDefinitions").EnumerateArray());

    private static List<string> Names(JsonElement container, string property) =>
        container.TryGetProperty(property, out var list) ? [.. list.EnumerateArray().Select(e => e.GetProperty("Name").GetString()!)] : [];

    private static string? EnvValue(JsonElement container, string name) =>
        container.GetProperty("Environment").EnumerateArray().FirstOrDefault(e => e.GetProperty("Name").GetString() == name).GetProperty("Value").GetString();
}

public sealed class StagingFixture
{
    public StagingFixture()
    {
        System.Environment.SetEnvironmentVariable("JSII_RUNTIME_PACKAGE_CACHE", "disabled");
        Stacks = FlorunaApp.Build(TestApp.Create(), FlorunaEnvironment.Staging("123456789012", "abc1234"));
        Network = Template.FromStack(Stacks.Network);
        Data = Template.FromStack(Stacks.Data);
        App = Template.FromStack(Stacks.App);
    }

    public FlorunaApp.Stacks Stacks { get; }

    public Template Network { get; }

    public Template Data { get; }

    public Template App { get; }
}

public sealed class StagingInfrastructureTests(StagingFixture staging) : IClassFixture<StagingFixture>
{
    [Fact]
    public void Staging_trades_resilience_for_cost()
    {
        staging.Data.HasResourceProperties("AWS::RDS::DBInstance", new Dictionary<string, object> { ["MultiAZ"] = false, ["StorageEncrypted"] = true });
        staging.Network.ResourceCountIs("AWS::EC2::NatGateway", 1);
        ProductionInfrastructureTests.Resources(ProductionInfrastructureTests.Json(staging.Network), "AWS::EC2::VPCEndpoint").Count().ShouldBe(1); // só o S3 (gateway, grátis)
        staging.App.HasResourceProperties("AWS::ElasticLoadBalancingV2::Listener", new Dictionary<string, object> { ["Port"] = 80, ["Protocol"] = "HTTP" });
    }

    [Fact]
    public void No_unsuppressed_cdk_nag_findings_in_staging()
    {
        foreach (var stack in new Stack[] { staging.Stacks.Network, staging.Stacks.Data, staging.Stacks.App })
        {
            Annotations.FromStack(stack).FindError("*", Match.StringLikeRegexp("AwsSolutions-.*")).ShouldBeEmpty(stack.StackName);
        }
    }
}

public sealed class EnvironmentConfigTests
{
    [Fact]
    public void Production_without_a_certificate_is_refused()
    {
        var context = new Dictionary<string, string> { ["env"] = "prod", ["imageTag"] = "abc" };
        Should.Throw<InvalidOperationException>(() => FlorunaEnvironment.FromContext(k => context.GetValueOrDefault(k)))
            .Message.ShouldContain("certificateArn");
    }

    [Fact]
    public void Unknown_environment_is_refused()
    {
        Should.Throw<InvalidOperationException>(() => FlorunaEnvironment.FromContext(k => k == "env" ? "qa" : null));
    }
}

public sealed class GitHubDeployTests
{
    [Fact]
    public void Only_this_repository_main_or_its_environments_can_assume_the_deploy_role_without_admin_rights()
    {
        System.Environment.SetEnvironmentVariable("JSII_RUNTIME_PACKAGE_CACHE", "disabled");
        var app = TestApp.Create();
        FlorunaApp.Build(app, FlorunaEnvironment.Staging("123456789012", "abc1234"), githubRepository: "acme/floruna");
        var stack = (Stack)app.Node.FindChild("Floruna-GitHubDeploy");
        var template = Template.FromStack(stack);

        var role = ProductionInfrastructureTests.Resources(ProductionInfrastructureTests.Json(template), "AWS::IAM::Role").ShouldHaveSingleItem();
        var trust = role.GetProperty("AssumeRolePolicyDocument").GetProperty("Statement")[0].GetProperty("Condition");
        trust.GetProperty("StringEquals").GetProperty("token.actions.githubusercontent.com:aud").GetString().ShouldBe("sts.amazonaws.com");
        trust.GetProperty("StringLike").GetProperty("token.actions.githubusercontent.com:sub").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(["repo:acme/floruna:ref:refs/heads/main", "repo:acme/floruna:environment:*"]);
        role.TryGetProperty("ManagedPolicyArns", out _).ShouldBeFalse(); // nada de AdministratorAccess
        Annotations.FromStack(stack).FindError("*", Match.AnyValue()).ShouldBeEmpty();
    }
}
