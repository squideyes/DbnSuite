using DbnClient;
using Shouldly;

namespace DbnSync.UnitTests.ClientTests;

public sealed class ResultTests
{
    private const string Code = "input.bad_thing";
    private const string Message = "That was a bad thing.";
    private const int Answer = 42;

    private static readonly Error Failure = new(Code, Message);

    [Fact]
    public void A_value_is_a_success()
    {
        Result<int> result = Answer;

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(Answer);
        result.Error.ShouldBeNull();
    }

    [Fact]
    public void An_error_is_not()
    {
        Result<int> result = Failure;

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(Failure);
    }

    [Fact]
    public void Reading_the_value_of_a_failure_says_what_failed()
    {
        Result<int> result = Failure;

        var thrown = Should.Throw<InvalidOperationException>(() => result.Value);

        thrown.Message.ShouldContain(Message);
    }

    [Fact]
    public void An_error_reads_as_its_message()
    {
        Failure.ToString().ShouldBe(Message);
    }
}
