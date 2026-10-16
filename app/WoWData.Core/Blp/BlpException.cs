namespace WoWData.Core.Blp;

/// <summary>A BLP file cannot be read or written; the message is plain language (e.g. "not a BLP2 file").</summary>
public class BlpException : Exception
{
    public BlpException(string message) : base(message) { }
    public BlpException(string message, Exception inner) : base(message, inner) { }
}
