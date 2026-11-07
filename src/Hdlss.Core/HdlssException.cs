namespace Hdlss.Core;

/// <summary>An expected, user-facing failure. The message is shown as-is in the UI.</summary>
public class HdlssException : Exception
{
    public HdlssException(string message) : base(message) { }
    public HdlssException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>A precondition that blocks an operation (game running, conflicting injector…).</summary>
public class BlockedException(string message) : HdlssException(message);

/// <summary>A file to restore was changed by another program since install; removal can proceed only by leaving it as is.</summary>
public sealed class ModifiedByOthersException(string message) : BlockedException(message);
