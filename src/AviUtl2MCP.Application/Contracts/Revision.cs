namespace AviUtl2MCP.Application.Contracts;

public readonly record struct Revision
{
    public Revision(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (value.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value.Length, "Revision must not exceed 256 characters.");
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
