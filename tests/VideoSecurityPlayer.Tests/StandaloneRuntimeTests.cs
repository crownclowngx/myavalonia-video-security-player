using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.UI;
using VideoSecurityPlayer.Standalone;
using Xunit;

namespace VideoSecurityPlayer.Tests;

public sealed class StandaloneRuntimeTests
{
    [Fact]
    public async Task InitializationFailureDisposesScopeAndCancelsLifetime()
    {
        var probe = new Probe();
        await using var runtime = new PreviewRuntime(configure: services => services.AddSingleton(probe), module: new FailingModule());
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.OpenAsync(runtime.Registration.Documents[0], false));
        Assert.True(probe.Disposed);
        Assert.True(probe.Token.IsCancellationRequested);
    }
    [Fact]
    public async Task ClosingDocumentIsIdempotentAndDoesNotCloseOtherScope()
    {
        await using var runtime = new PreviewRuntime(module: new ProbeModule());
        var a = await runtime.OpenAsync(runtime.Registration.Documents[0], false);
        var b = await runtime.OpenAsync(runtime.Registration.Documents[0], false);
        await runtime.CloseAsync(a);
        await runtime.CloseAsync(a);
        Assert.True(a.ClosingToken.IsCancellationRequested);
        Assert.False(b.ClosingToken.IsCancellationRequested);
        await runtime.DisposeAsync();
        Assert.True(b.ClosingToken.IsCancellationRequested);
    }
    private sealed class Probe { public bool Disposed; public CancellationToken Token; }
    private sealed class FailingModule : IPluginModule
    {
        public void Configure(IPluginRegistration r) => r.AddDocument<FailingDocument, Border>(new(new("preview.document.failure"), "失败", "", "测试"));
    }
    private sealed class ProbeModule : IPluginModule
    {
        public void Configure(IPluginRegistration r) => r.AddDocument<ProbeDocument, Border>(new(new("preview.document.probe"), "测试", "", "测试"));
    }
    private sealed class FailingDocument(Probe probe, IDocumentLifetime lifetime) : IPluginDocument, IDisposable
    {
        public DocumentPresentationState Presentation => new("失败");
        public event EventHandler? PresentationChanged { add { } remove { } }
        public ValueTask InitializeAsync(DocumentActivation activation, CancellationToken token)
        { probe.Token = lifetime.ClosingToken; throw new InvalidOperationException("expected initialization failure"); }
        public void Dispose() => probe.Disposed = true;
    }
    private sealed class ProbeDocument : IPluginDocument
    {
        public DocumentPresentationState Presentation => new("测试");
        public event EventHandler? PresentationChanged { add { } remove { } }
        public ValueTask InitializeAsync(DocumentActivation activation, CancellationToken token) => ValueTask.CompletedTask;
    }
}
