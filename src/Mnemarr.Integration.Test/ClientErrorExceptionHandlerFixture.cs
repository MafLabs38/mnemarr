using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Mnemarr.Host.Http;

namespace Mnemarr.Integration.Test;

public sealed class ClientErrorExceptionHandlerFixture
{
    private static async Task<(DefaultHttpContext Context, bool Handled, string Body)> Handle(Exception exception)
    {
        var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        var handler = new ClientErrorExceptionHandler(services.GetRequiredService<IProblemDetailsService>());

        var handled = await handler.TryHandleAsync(context, exception, TestContext.Current.CancellationToken);

        context.Response.Body.Position = 0;
        return (context, handled, await new StreamReader(context.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(413)]
    [InlineData(400)]
    public async Task should_answer_a_request_the_server_could_not_read_with_the_status_it_found_and_never_its_cause(int status)
    {
        var (context, handled, body) = await Handle(new BadHttpRequestException("secret detail of the parser", status));

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(status);
        body.Should().Contain("The request cannot be read.").And.NotContain("secret detail");
    }

    [Fact]
    public async Task should_leave_every_other_exception_to_the_default_handler()
    {
        var (_, handled, _) = await Handle(new InvalidOperationException("a bug"));

        handled.Should().BeFalse();
    }
}
