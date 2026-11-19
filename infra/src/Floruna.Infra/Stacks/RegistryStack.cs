using Amazon.CDK;
using Amazon.CDK.AWS.ECR;
using Constructs;

namespace Floruna.Infra.Stacks;

/// <summary>
/// Repositório de imagens, COMPARTILHADO entre ambientes: a mesma imagem (mesmo digest) que
/// passou em staging é a que vai para produção ("build once, deploy many").
/// </summary>
public sealed class RegistryStack : Stack
{
    public RegistryStack(Construct scope, string id, IStackProps props)
        : base(scope, id, props)
    {
        Repository = new Repository(this, "ApiRepository", new RepositoryProps
        {
            RepositoryName = "floruna-api",
            ImageScanOnPush = true, // CVEs da imagem aparecem no console (Inspector básico)
            ImageTagMutability = TagMutability.IMMUTABLE, // a tag (SHA do commit) nunca aponta para outra imagem
            LifecycleRules = [new LifecycleRule { Description = "Mantém as 50 imagens mais recentes", MaxImageCount = 50 }],
            RemovalPolicy = RemovalPolicy.RETAIN,
        });
        _ = new CfnOutput(this, "RepositoryUri", new CfnOutputProps { Value = Repository.RepositoryUri });
    }

    public IRepository Repository { get; }
}
