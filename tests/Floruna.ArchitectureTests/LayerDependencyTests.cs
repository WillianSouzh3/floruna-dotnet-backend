using System.Reflection;
using Floruna.Application.Messaging;
using NetArchTest.Rules;

namespace Floruna.ArchitectureTests;

/// <summary>
/// A regra de dependência da Clean Architecture, verificada por máquina
/// (equivale ao <c>pnpm architecture:check</c> do projeto atual).
/// Se alguém fizer o Domain depender do EF Core, o CI quebra.
/// </summary>
public sealed class LayerDependencyTests
{
    private const string SharedKernel = "Floruna.SharedKernel";
    private const string Domain = "Floruna.Domain";
    private const string Application = "Floruna.Application";
    private const string Infrastructure = "Floruna.Infrastructure";
    private const string Api = "Floruna.Api";

    private static readonly Assembly SharedKernelAssembly = typeof(SharedKernel.Result).Assembly;
    private static readonly Assembly DomainAssembly = Domain_.AssemblyReference.Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(Application_.DependencyInjection).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(Infrastructure_.DependencyInjection).Assembly;

    public static TheoryData<string, string> ForbiddenDependencies => new()
    {
        // assembly analisado, namespace proibido
        { SharedKernel, Domain },
        { SharedKernel, Application },
        { SharedKernel, "Microsoft" },
        { Domain, Application },
        { Domain, Infrastructure },
        { Domain, Api },
        { Domain, "Microsoft.EntityFrameworkCore" },
        { Domain, "Microsoft.AspNetCore" },
        { Domain, "Microsoft.Extensions" },
        { Application, Infrastructure },
        { Application, Api },
        { Application, "Microsoft.EntityFrameworkCore" },
        { Application, "Microsoft.AspNetCore" },
        { Infrastructure, Api },
    };

    [Theory]
    [MemberData(nameof(ForbiddenDependencies))]
    public void Layer_does_not_depend_on_forbidden_namespace(string layer, string forbidden)
    {
        var result = Types.InAssembly(AssemblyOf(layer))
            .ShouldNot().HaveDependencyOn(forbidden)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"{layer} não pode depender de {forbidden}. Violações: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void Request_handlers_are_sealed_and_not_public()
    {
        // Handlers são detalhe de implementação: ninguém deve chamá-los
        // diretamente, só pelo ISender. "internal sealed" garante isso.
        var result = Types.InAssembly(ApplicationAssembly)
            .That().AreClasses().And().ImplementInterface(typeof(IRequestHandler<,>))
            .Should().BeSealed().And().NotBePublic()
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Domain_and_shared_kernel_never_use_floating_point()
    {
        // RNF-13: dinheiro e quantidades não usam double/float.
        var offenders =
            from assembly in new[] { SharedKernelAssembly, DomainAssembly }
            from type in assembly.GetTypes()
            from member in type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            let memberType = member switch
            {
                PropertyInfo p => p.PropertyType,
                FieldInfo f => f.FieldType,
                _ => null,
            }
            where memberType == typeof(double) || memberType == typeof(float)
            select $"{type.Name}.{member.Name}";

        offenders.ShouldBeEmpty();
    }

    private static Assembly AssemblyOf(string layer) => layer switch
    {
        SharedKernel => SharedKernelAssembly,
        Domain => DomainAssembly,
        Application => ApplicationAssembly,
        Infrastructure => InfrastructureAssembly,
        _ => throw new ArgumentOutOfRangeException(nameof(layer), layer, null),
    };
}
