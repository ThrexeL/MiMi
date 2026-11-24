using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using MiMi.Codex;
using Xunit;

namespace MiMi.Tests;

public class CodexProtocolTests
{
    private async Task<Fixture> CreateAsync()
    {
        var inputServer = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        var inputClient = new AnonymousPipeClientStream(PipeDirection.In, inputServer.GetClientHandleAsString());
        var outputServer = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        var outputClient = new AnonymousPipeClientStream(PipeDirection.In, outputServer.GetClientHandleAsString());
        var inputWriter = new StreamWriter(inputServer, new UTF8Encoding(false)) { AutoFlush = true };
        var outputReader = new StreamReader(outputClient, new UTF8Encoding(false));
        var connection = new CodexRpcConnection(new StreamReader(inputClient, new UTF8Encoding(false)),
            new StreamWriter(outputServer, new UTF8Encoding(false)) { AutoFlush = true });
        connection.Start();
        return await Task.FromResult(new Fixture(connection, inputWriter, outputReader,
            inputServer, inputClient, outputServer, outputClient));
    }

    private sealed record Fixture(CodexRpcConnection Connection, StreamWriter Input, StreamReader Output,
        IDisposable InputServer, IDisposable InputClient, IDisposable OutputServer, IDisposable OutputClient) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            Input.Dispose();
            Output.Dispose();
            InputServer.Dispose();
            InputClient.Dispose();
            OutputServer.Dispose();
            OutputClient.Dispose();
        }
    }

    [Fact]
    public async Task RequestResponse_CorrelatesById_AndKeepsJsonLineTransport()
    {
        await using var fixture = await CreateAsync();
        var request = fixture.Connection.RequestAsync("initialize", new { clientInfo = new { name = "MiMi" } },
            TimeSpan.FromSeconds(2));

        var sent = JsonDocument.Parse((await fixture.Output.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)))!);
        Assert.Equal("initialize", sent.RootElement.GetProperty("method").GetString());
        var id = sent.RootElement.GetProperty("id").GetString();
        await fixture.Input.WriteLineAsync($"{{\"id\":{JsonSerializer.Serialize(id)},\"result\":{{\"platformOs\":\"windows\"}}}}");

        var result = await request.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("windows", result.GetProperty("platformOs").GetString());
    }

    [Fact]
    public async Task ServerRequest_PreservesNumericId_InResponse()
    {
        await using var fixture = await CreateAsync();
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Connection.RequestReceived += (request, _) =>
        {
            received.TrySetResult(request);
            return fixture.Connection.RespondAsync(request.GetProperty("id"), new { decision = "decline" });
        };

        await fixture.Input.WriteLineAsync("{\"id\":42,\"method\":\"item/commandExecution/requestApproval\",\"params\":{}}");
        var request = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(42, request.GetProperty("id").GetInt32());

        var response = JsonDocument.Parse((await fixture.Output.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)))!);
        Assert.Equal(42, response.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("decline", response.RootElement.GetProperty("result").GetProperty("decision").GetString());
    }

    [Fact]
    public async Task UnsupportedServerRequest_ReturnsMethodNotFound()
    {
        await using var fixture = await CreateAsync();
        await fixture.Input.WriteLineAsync("{\"id\":7,\"method\":\"unsupported/request\",\"params\":{}}");
        var response = JsonDocument.Parse((await fixture.Output.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)))!);
        Assert.Equal(7, response.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(-32601, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }
}
