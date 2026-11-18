using Floruna.Application.Behaviors;
using Floruna.Application.Messaging;
using Floruna.SharedKernel;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace Floruna.Application.UnitTests.Behaviors;

public sealed class ValidationBehaviorTests
{
    public sealed record CreateThing(string Name, int Quantity) : ICommand<Guid>;

    public sealed record RenameThing(string Name) : ICommand;

    internal sealed class CreateThingValidator : AbstractValidator<CreateThing>
    {
        public CreateThingValidator()
        {
            RuleFor(x => x.Name).NotEmpty().WithMessage("Nome é obrigatório.");
            RuleFor(x => x.Quantity).GreaterThan(0).WithMessage("Quantidade deve ser maior que zero.");
        }
    }

    internal sealed class RenameThingValidator : AbstractValidator<RenameThing>
    {
        public RenameThingValidator() =>
            RuleFor(x => x.Name).MaximumLength(5).WithMessage("Nome muito longo.");
    }

    internal sealed class Calls
    {
        public int Count { get; set; }
    }

    internal sealed class CreateThingHandler(Calls calls) : ICommandHandler<CreateThing, Guid>
    {
        public Task<Result<Guid>> Handle(CreateThing request, CancellationToken cancellationToken)
        {
            calls.Count++;
            return Task.FromResult<Result<Guid>>(Guid.NewGuid());
        }
    }

    internal sealed class RenameThingHandler(Calls calls) : ICommandHandler<RenameThing>
    {
        public Task<Result> Handle(RenameThing request, CancellationToken cancellationToken)
        {
            calls.Count++;
            return Task.FromResult(Result.Success());
        }
    }

    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Calls>();
        services.AddMessaging(typeof(ValidationBehaviorTests).Assembly);
        services.AddPipelineBehavior(typeof(ValidationBehavior<,>));
        services.AddValidatorsFromAssembly(typeof(ValidationBehaviorTests).Assembly, includeInternalTypes: true);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Invalid_command_returns_validation_error_with_all_issues_and_skips_handler()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CreateThing("", 0));

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("VALIDATION_ERROR");
        result.Error.Type.ShouldBe(ErrorType.Validation);
        result.Error.Issues.ShouldBe(["Nome é obrigatório.", "Quantidade deve ser maior que zero."]);
        provider.GetRequiredService<Calls>().Count.ShouldBe(0);
    }

    [Fact]
    public async Task Valid_command_reaches_the_handler()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CreateThing("Rosa", 2));

        result.IsSuccess.ShouldBeTrue();
        provider.GetRequiredService<Calls>().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Non_generic_result_commands_are_validated_too()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new RenameThing("Girassol"));

        result.Error.Issues.ShouldBe(["Nome muito longo."]);
        provider.GetRequiredService<Calls>().Count.ShouldBe(0);
    }
}
