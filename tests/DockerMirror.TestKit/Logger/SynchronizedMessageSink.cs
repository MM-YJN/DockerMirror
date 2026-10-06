using Xunit.Sdk;

namespace DockerMirror.TestKit.Logger;

/// <summary>
/// A thread-safe wrapper around <see cref="IMessageSink"/> to ensure that output from multiple threads does not interleave and remains coherent.
/// </summary>
public sealed class SynchronizedMessageSink : IMessageSink
{
    private readonly IMessageSink _inner;
    private readonly Lock _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SynchronizedMessageSink"/> class that wraps the specified <see cref="IMessageSink"/> instance.
    /// </summary>
    /// <param name="inner">The <see cref="IMessageSink"/> instance to wrap. Must not be null.</param>
    public SynchronizedMessageSink(IMessageSink inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <inheritdoc />
    public bool OnMessage(IMessageSinkMessage message)
    {
        lock (_lock)
        {
            return _inner.OnMessage(message);
        }
    }
}
