using System.Runtime.InteropServices;
using OpcBridge.Da;
using Xunit;

namespace OpcBridge.LoadTest;

public class DaConnectErrorClassifierTests
{
    [Fact]
    public void RemoteRpcUnavailable_IsRetryable()
    {
        COMException ex = new("The RPC server is unavailable.", unchecked((int)0x800706BA));
        Assert.True(DaConnectErrorClassifier.IsRetryable(ex, isRemote: true));
    }

    [Fact]
    public void RemoteClassNotRegistered_IsTerminal()
    {
        COMException ex = new("Class not registered", unchecked((int)0x80040154));
        Assert.False(DaConnectErrorClassifier.IsRetryable(ex, isRemote: true));
    }

    [Fact]
    public void RemoteNonComException_IsRetryable()
    {
        // e.g. Type.GetTypeFromProgID returning null for an unreachable host:
        // wrapped as SourceConnectionLostException so the coordinator retries.
        Assert.True(DaConnectErrorClassifier.IsRetryable(
            new InvalidOperationException("not available on host"), isRemote: true));
    }

    [Fact]
    public void LocalFailures_AreTerminal()
    {
        // Local type lookup failure (ProgID not registered) is a configuration error
        // surfaced as Faulted, never retried.
        Assert.False(DaConnectErrorClassifier.IsRetryable(
            new COMException("x", unchecked((int)0x800706BA)), isRemote: false));
        Assert.False(DaConnectErrorClassifier.IsRetryable(
            new InvalidOperationException("not registered"), isRemote: false));
    }

    [Fact]
    public void LocalActivationFailure_IsRetryable()
    {
        // Registered-but-dead local server (killed process): CreateInstance's
        // RPC-unavailable failure is transient and must be retried.
        Assert.True(DaConnectErrorClassifier.IsActivationRetryable(
            new COMException("x", unchecked((int)0x800706BA))));
        Assert.True(DaConnectErrorClassifier.IsActivationRetryable(
            new InvalidOperationException("create failed")));
        Assert.False(DaConnectErrorClassifier.IsActivationRetryable(
            new COMException("x", unchecked((int)0x80040154))));
    }
}
